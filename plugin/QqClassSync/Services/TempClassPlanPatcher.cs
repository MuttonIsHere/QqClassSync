using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.Logging;
using QqClassSync.Models;

namespace QqClassSync.Services;

/// <summary>
/// 把调课指令落到 ClassIsland 的「临时层课表」上。
///
/// 关键实现依据（读 ClassIsland 源码得出）：
///   - 今天该用哪张课表由 ILessonsService.GetClassPlanByDate() 选出
///     （预定课表 → 临时课表 → 课表组/时间规则匹配）。
///   - CreateTempClassPlan 会复制源课表并挂到 OrderedSchedules[今天]，补丁只影响今天。
///   - ClassPlan.Classes 与 TimeType==0 的时间点一一对应，第 X 节 = Classes[X-1]。
///   - 批量调课：全部改完后只 SaveProfile() 一次，避免连续落盘。
///   - 调用方必须保证在 UI 线程执行（否则会触发课表网格的跨线程异常）。
///
/// 单节课撤销依赖 <see cref="LessonUndoJournal"/> 记录「第几节 原科目→新科目」。
/// </summary>
public sealed class TempClassPlanPatcher(
    IProfileService profileService,
    ILessonsService lessonsService,
    IExactTimeService timeService,
    LessonUndoJournal journal,
    ILogger<TempClassPlanPatcher> logger)
{
    /// <summary>
    /// 并发保护：QQ 与小橙看班两个输入源同时来指令时，保证改课表/撤销串行执行。
    /// 目前所有调用点都在 UI 线程（单轮询循环 + InvokeAsync），这里是廉价保险，
    /// 防止以后新增调用路径（比如别的插件、定时任务）造成档案写冲突。
    /// </summary>
    private readonly object _sync = new();

    /// <summary>批量执行调课（Apply / Swap）。返回成功项与失败项，支持「部分成功」。</summary>
    public BatchPatchOutcome ApplyBatch(IReadOnlyList<TiaokeCommand> commands)
    {
        lock (_sync)
        {
            return ApplyBatchCore(commands);
        }
    }

    private BatchPatchOutcome ApplyBatchCore(IReadOnlyList<TiaokeCommand> commands)
    {
        var applied = new List<string>();
        var failures = new List<string>();

        var profile = profileService.Profile;
        var today = timeService.GetCurrentLocalDateTime().Date;

        var workItems = commands.Where(c => c.Kind is TiaokeCommandKind.Apply or TiaokeCommandKind.Swap).ToList();
        if (workItems.Count == 0)
        {
            failures.Add("没有可执行的调课指令");
            return new BatchPatchOutcome(applied, failures);
        }

        var plan = lessonsService.GetClassPlanByDate(today, out var planId);
        if (plan is null || planId is null)
        {
            failures.Add("今天没有生效的课表");
            return new BatchPatchOutcome(applied, failures);
        }

        // 1) 时段换算 + 科目落点：都基于「源课表」做，临时层是它的副本，结果一致。
        //    先算清楚每条指令要落到哪个科目，再决定要不要建临时层，
        //    避免「科目写错 → 建了个空临时层 → 大屏一直挂着（临时层）却什么都没改」。
        var candidates = BuildCandidates(profile);
        var prepared = PrepareCommands(profile, plan, workItems, candidates, failures);
        if (prepared.Count == 0)
        {
            if (failures.Count == 0)
            {
                failures.Add("没有可执行的调课指令");
            }

            return new BatchPatchOutcome(applied, failures);
        }

        // 2) 冲突/越界校验：有任何阻断项就整批不执行，避免写坏课表。
        var snapshot = BuildSnapshot(profile, plan);
        var issues = CommandValidator.Validate(prepared.Select(p => p.Command).ToList(), snapshot);
        var blocks = issues.Where(i => i.Level == CommandIssueLevel.Block).ToList();
        if (blocks.Count > 0)
        {
            failures.AddRange(blocks.Select(i => i.Message).Distinct(StringComparer.Ordinal));
            TiaokeLog.Warn(logger, "调课指令存在冲突，已整批取消：{Reasons}", string.Join("；", blocks.Select(i => i.Message)));
            return new BatchPatchOutcome(applied, failures);
        }

        foreach (var warning in issues.Where(i => i.Level == CommandIssueLevel.Warning))
        {
            TiaokeLog.Info(logger, "调课提示：{Message}", warning.Message);
        }

        // 3) 建临时层并执行（批量只落盘一次）。
        if (!plan.IsOverlay)
        {
            var overlayId = profileService.CreateTempClassPlan(planId.Value);
            if (overlayId is null)
            {
                failures.Add("创建临时层课表失败（今天可能已有临时层）");
                return new BatchPatchOutcome(applied, failures);
            }

            plan = profile.ClassPlans[overlayId.Value];
            TiaokeLog.Info(logger, "已为今天创建临时层课表：{PlanId}", overlayId);
        }

        foreach (var item in prepared)
        {
            if (item.Command.Kind == TiaokeCommandKind.Swap)
            {
                ApplySwap(profile, plan, item.Command, applied, failures);
            }
            else
            {
                ApplySingle(profile, plan, item, applied, failures);
            }
        }

        if (applied.Count > 0)
        {
            profileService.SaveProfile();
            journal.Save();
        }

        return new BatchPatchOutcome(applied, failures);
    }

    /// <summary>执行前的准备结果：指令 + 已确定的科目落点（互换时为空）。</summary>
    private sealed record PreparedCommand(
        TiaokeCommand Command,
        Guid TargetSubjectId,
        string TargetSubjectName,
        string MismatchNote);

    /// <summary>
    /// 执行前的准备：时段换算、科目落点、去重。任何一条无法唯一确定就跳过并记失败，
    /// 不影响同一条消息里其他能确定的条目（部分成功）。
    /// </summary>
    private List<PreparedCommand> PrepareCommands(
        Profile profile,
        ClassPlan plan,
        IReadOnlyList<TiaokeCommand> workItems,
        IReadOnlyList<SubjectCandidate> candidates,
        List<string> failures)
    {
        var slots = BuildSlots(plan);
        var prepared = new List<PreparedCommand>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var original in workItems)
        {
            var command = original;

            if (!string.IsNullOrWhiteSpace(command.DayPart))
            {
                var resolved = LessonTimeResolver.Resolve(command.Lesson, command.DayPart, slots);
                if (!resolved.Success)
                {
                    failures.Add($"{command.DayPart}第{command.Lesson}节无法换算：{resolved.Reason}");
                    TiaokeLog.Warn(logger,
                        "时段换算失败：{Part}第{Lesson}节（{Reason}）",
                        command.DayPart,
                        command.Lesson,
                        resolved.Reason);
                    continue;
                }

                if (resolved.Lesson != command.Lesson)
                {
                    TiaokeLog.Info(logger,
                        "时段换算：{Part}第{Lesson}节 → 全天第{Resolved}节",
                        command.DayPart,
                        command.Lesson,
                        resolved.Lesson);
                }

                command = command with { Lesson = resolved.Lesson, DayPart = string.Empty };
            }

            if (command.Kind == TiaokeCommandKind.Swap)
            {
                var swapKey = $"S:{Math.Min(command.Lesson, command.SwapWith)}:{Math.Max(command.Lesson, command.SwapWith)}";
                if (seen.Add(swapKey))
                {
                    prepared.Add(new PreparedCommand(command, Guid.Empty, string.Empty, string.Empty));
                }

                continue;
            }

            var match = SubjectMatcher.Resolve(command.ToSubject, candidates);
            if (match.IsAmbiguous)
            {
                var names = string.Join("、", match.Candidates.Select(c => c.Name).Distinct(StringComparer.Ordinal));
                TiaokeLog.Warn(logger,
                    "第 {Lesson} 节的目标科目「{Subject}」有多个候选：{Candidates}，已跳过",
                    command.Lesson,
                    command.ToSubject,
                    names);
                failures.Add($"{command.LessonLabel}的目标科目「{command.ToSubject}」有多个候选：{names}；请改用完整科目名重发");
                continue;
            }

            if (!match.IsUnique)
            {
                var reason = match.Detail;
                if (!reason.Contains("地点", StringComparison.Ordinal))
                {
                    var available = string.Join("、", candidates
                        .Select(c => c.Name)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Take(12));
                    reason = $"{reason}；可用科目：{available}";
                }

                TiaokeLog.Warn(logger, "第 {Lesson} 节无法确定目标科目：{Reason}", command.Lesson, reason);
                failures.Add($"{command.LessonLabel}：{reason}");
                continue;
            }

            var target = match.Match!;
            var applyKey = $"A:{command.Lesson}:{target.Id}";
            if (!seen.Add(applyKey))
            {
                continue;
            }

            prepared.Add(new PreparedCommand(
                command,
                target.Id,
                target.Name,
                BuildMismatchNote(profile, plan, command, candidates)));
        }

        return prepared;
    }

    /// <summary>
    /// 原科目只用于提示：老师可能记错，但「换成什么」是明确的，不因此拒绝执行。
    /// </summary>
    private string BuildMismatchNote(
        Profile profile,
        ClassPlan plan,
        TiaokeCommand command,
        IReadOnlyList<SubjectCandidate> candidates)
    {
        if (string.IsNullOrWhiteSpace(command.FromSubject)
            || command.Lesson < 1
            || command.Lesson > plan.Classes.Count)
        {
            return string.Empty;
        }

        var classInfo = plan.Classes[command.Lesson - 1];
        var fromMatch = SubjectMatcher.Resolve(command.FromSubject, candidates);
        if (!fromMatch.IsUnique || fromMatch.Match!.Id == classInfo.SubjectId)
        {
            return string.Empty;
        }

        var actualName = profile.Subjects.TryGetValue(classInfo.SubjectId, out var subject)
            ? subject.Name ?? "(空)"
            : "(空)";
        TiaokeLog.Warn(logger,
            "第 {Lesson} 节原科目不符：指令写的是 {From}，课表实际是 {Actual}（准备改为 {To}）",
            command.Lesson,
            fromMatch.Match!.Name,
            actualName,
            command.ToSubject);
        return $"（注意：你说的是{fromMatch.Match!.Name}，课表实际是{actualName}）";
    }

    private static IReadOnlyList<LessonSlot> BuildSlots(ClassPlan plan)
    {
        var slots = new List<LessonSlot>();
        var layout = plan.TimeLayout;
        if (layout is null)
        {
            return slots;
        }

        var points = layout.Layouts
            .Where(item => item.TimeType == 0)
            .OrderBy(item => item.StartTime)
            .ToList();

        for (var i = 0; i < points.Count; i++)
        {
            slots.Add(new LessonSlot(i + 1, points[i].StartTime, points[i].EndTime));
        }

        return slots;
    }

    private static PlanSnapshot BuildSnapshot(Profile profile, ClassPlan plan)
    {
        var lessons = new List<PlanLessonInfo>(plan.Classes.Count);
        for (var i = 0; i < plan.Classes.Count; i++)
        {
            var name = profile.Subjects.TryGetValue(plan.Classes[i].SubjectId, out var subject)
                ? subject.Name ?? "(空)"
                : "(空)";
            lessons.Add(new PlanLessonInfo(i + 1, name));
        }

        return new PlanSnapshot(plan.Classes.Count, lessons);
    }

    private static List<SubjectCandidate> BuildCandidates(Profile profile) =>
        profile.Subjects
            .Select(pair => new SubjectCandidate(
                pair.Key,
                pair.Value.Name ?? string.Empty,
                pair.Value.Initial ?? string.Empty,
                pair.Value.TeacherName ?? string.Empty))
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Name))
            .ToList();

    /// <summary>
    /// 给 AI 提示词用的科目表（只含名称/简称/老师）。每次调用现读档案，老师改了科目表立即生效。
    /// </summary>
    public IReadOnlyList<SubjectCandidate> GetSubjectCatalog() => BuildCandidates(profileService.Profile);

    private void ApplySingle(
        Profile profile,
        ClassPlan plan,
        PreparedCommand prepared,
        List<string> applied,
        List<string> failures)
    {
        var command = prepared.Command;
        if (command.Lesson < 1 || command.Lesson > plan.Classes.Count)
        {
            failures.Add($"第{command.Lesson}节超出今天课表范围（共 {plan.Classes.Count} 节）");
            return;
        }

        var classInfo = plan.Classes[command.Lesson - 1];
        var beforeName = profile.Subjects.TryGetValue(classInfo.SubjectId, out var beforeSubject)
            ? beforeSubject.Name
            : "(空)";

        if (classInfo.SubjectId == prepared.TargetSubjectId)
        {
            applied.Add($"{command.LessonLabel}本来就是{prepared.TargetSubjectName}（未变更）");
            return;
        }

        journal.Add(new LessonPatchRecord(
            timeService.GetCurrentLocalDateTime().Date,
            command.Lesson,
            classInfo.SubjectId,
            beforeName,
            prepared.TargetSubjectId,
            prepared.TargetSubjectName,
            DateTime.Now));

        classInfo.SubjectId = prepared.TargetSubjectId;
        classInfo.IsChangedClass = true;
        applied.Add($"{command.LessonLabel} {beforeName}→{prepared.TargetSubjectName}{prepared.MismatchNote}");
    }

    private void ApplySwap(
        Profile profile,
        ClassPlan plan,
        TiaokeCommand command,
        List<string> applied,
        List<string> failures)
    {
        var a = command.Lesson;
        var b = command.SwapWith;
        if (a == b)
        {
            failures.Add($"第{a}节不能和自己互换");
            return;
        }

        if (a < 1 || a > plan.Classes.Count || b < 1 || b > plan.Classes.Count)
        {
            failures.Add($"互换节次超出今天课表范围（第{a}节 / 第{b}节，共 {plan.Classes.Count} 节）");
            return;
        }

        var classA = plan.Classes[a - 1];
        var classB = plan.Classes[b - 1];
        var subjectA = classA.SubjectId;
        var subjectB = classB.SubjectId;
        if (subjectA == subjectB)
        {
            failures.Add($"第{a}节和第{b}节科目相同，无需互换");
            return;
        }

        var nameA = profile.Subjects.TryGetValue(subjectA, out var sa) ? sa.Name : "(空)";
        var nameB = profile.Subjects.TryGetValue(subjectB, out var sb) ? sb.Name : "(空)";
        var today = timeService.GetCurrentLocalDateTime().Date;

        journal.Add(new LessonPatchRecord(today, a, subjectA, nameA, subjectB, nameB, DateTime.Now));
        journal.Add(new LessonPatchRecord(today, b, subjectB, nameB, subjectA, nameA, DateTime.Now));

        classA.SubjectId = subjectB;
        classB.SubjectId = subjectA;
        classA.IsChangedClass = true;
        classB.IsChangedClass = true;

        applied.Add($"第{a}节↔第{b}节 互换（{nameA}↔{nameB}）");
    }

    /// <summary>单条执行（保留给设置页等旧调用方）。</summary>
    public PatchOutcome Apply(TiaokeCommand command)
    {
        var batch = ApplyBatch([command]);
        if (batch.Applied.Count > 0)
        {
            return PatchOutcome.Ok(batch.Applied[0]);
        }

        return PatchOutcome.Fail(batch.Failures.FirstOrDefault() ?? "调课失败");
    }

    /// <summary>
    /// 批量撤销：lessons 为空表示撤销今天全部；否则只恢复指定节次，其他调课保留。
    /// </summary>
    public BatchPatchOutcome UndoLessons(IReadOnlyList<int> lessons)
    {
        lock (_sync)
        {
            return UndoLessonsCore(lessons);
        }
    }

    private BatchPatchOutcome UndoLessonsCore(IReadOnlyList<int> lessons)
    {
        var applied = new List<string>();
        var failures = new List<string>();
        var profile = profileService.Profile;
        var today = timeService.GetCurrentLocalDateTime().Date;

        if (profile.OverlayClassPlanId is null
            || !profile.ClassPlans.TryGetValue(profile.OverlayClassPlanId.Value, out var plan))
        {
            failures.Add("今天没有临时层课表，无需撤销");
            return new BatchPatchOutcome(applied, failures);
        }

        if (lessons.Count == 0)
        {
            profileService.ClearTempClassPlan();
            journal.Clear(today);
            journal.Save();
            TiaokeLog.Info(logger, "已清除今天的临时层课表（撤销全部调课）");
            applied.Add("已撤销今天全部调课");
            return new BatchPatchOutcome(applied, failures);
        }

        foreach (var lesson in lessons.Distinct().OrderBy(x => x))
        {
            if (lesson < 1 || lesson > plan.Classes.Count)
            {
                failures.Add($"第{lesson}节超出今天课表范围（共 {plan.Classes.Count} 节）");
                continue;
            }

            var record = journal.TakeLast(today, lesson);
            var originalId = Guid.Empty;
            var originalName = string.Empty;
            if (record is not null)
            {
                originalId = record.OriginalSubjectId;
                originalName = record.OriginalSubjectName;
            }
            else if (!TryReadOriginalFromSource(profile, plan, lesson, out originalId, out originalName))
            {
                failures.Add($"第{lesson}节找不到原课表信息，无法撤销");
                continue;
            }

            var classInfo = plan.Classes[lesson - 1];
            classInfo.SubjectId = originalId;
            classInfo.IsChangedClass = false;
            applied.Add($"第{lesson}节→{originalName}");
        }

        if (applied.Count > 0)
        {
            journal.Save();
            profileService.SaveProfile();
            TiaokeLog.Info(logger, "已撤销 {Count} 节的调课：{Items}", applied.Count, string.Join("；", applied));
        }

        // 所有节次都回到源课表 → 清掉临时层，避免一直挂着「（临时层）」。
        if (applied.Count > 0 && IsIdenticalToSource(profile, plan))
        {
            profileService.ClearTempClassPlan();
            journal.Clear(today);
            journal.Save();
            applied.Add("（已无调课，临时层已清除）");
        }

        return new BatchPatchOutcome(applied, failures);
    }

    /// <summary>撤销单节课（保留给设置页）。</summary>
    public PatchOutcome UndoLesson(int lesson)
    {
        var batch = UndoLessons([lesson]);
        if (batch.Applied.Count > 0)
        {
            var extra = batch.Applied.Count > 1 ? "；" + string.Join("；", batch.Applied.Skip(1)) : string.Empty;
            return PatchOutcome.Ok(batch.Applied[0] + extra);
        }

        return PatchOutcome.Fail(batch.Failures.FirstOrDefault() ?? "撤销失败");
    }

    /// <summary>撤销整天（保留给设置页）。</summary>
    public PatchOutcome Undo()
    {
        var batch = UndoLessons([]);
        if (batch.Applied.Count > 0)
        {
            return PatchOutcome.Ok(batch.Applied[0]);
        }

        return PatchOutcome.Fail(batch.Failures.FirstOrDefault() ?? "撤销失败");
    }

    /// <summary>诊断用：描述今天解析出来的课表。</summary>
    public string DescribeTodayPlan()
    {
        var today = timeService.GetCurrentLocalDateTime().Date;
        var plan = lessonsService.GetClassPlanByDate(today, out _);
        if (plan is null)
        {
            return "无（今天没有生效的课表）";
        }

        var patches = journal.ForDate(today).Count;
        var suffix = plan.IsOverlay
            ? $"（临时层{(patches > 0 ? $"，{patches} 处调课" : string.Empty)}）"
            : string.Empty;

        return $"「{plan.Name}」共 {plan.Classes.Count} 节{suffix}";
    }

    private static bool TryReadOriginalFromSource(
        Profile profile,
        ClassPlan overlayPlan,
        int lesson,
        out Guid subjectId,
        out string subjectName)
    {
        subjectId = Guid.Empty;
        subjectName = string.Empty;

        if (overlayPlan.OverlaySourceId is not { } sourceId
            || !profile.ClassPlans.TryGetValue(sourceId, out var sourcePlan)
            || lesson > sourcePlan.Classes.Count)
        {
            return false;
        }

        subjectId = sourcePlan.Classes[lesson - 1].SubjectId;
        subjectName = profile.Subjects.TryGetValue(subjectId, out var subject) ? subject.Name : "(空)";
        return true;
    }

    private static bool IsIdenticalToSource(Profile profile, ClassPlan overlayPlan)
    {
        if (overlayPlan.OverlaySourceId is not { } sourceId
            || !profile.ClassPlans.TryGetValue(sourceId, out var sourcePlan)
            || overlayPlan.Classes.Count != sourcePlan.Classes.Count)
        {
            return false;
        }

        for (var i = 0; i < overlayPlan.Classes.Count; i++)
        {
            if (overlayPlan.Classes[i].SubjectId != sourcePlan.Classes[i].SubjectId)
            {
                return false;
            }
        }

        return true;
    }
}
