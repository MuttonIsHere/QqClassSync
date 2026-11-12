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
    /// <summary>批量执行调课（Apply / Swap）。返回成功项与失败项，支持「部分成功」。</summary>
    public BatchPatchOutcome ApplyBatch(IReadOnlyList<TiaokeCommand> commands)
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

        if (!plan.IsOverlay)
        {
            var overlayId = profileService.CreateTempClassPlan(planId.Value);
            if (overlayId is null)
            {
                failures.Add("创建临时层课表失败（今天可能已有临时层）");
                return new BatchPatchOutcome(applied, failures);
            }

            plan = profile.ClassPlans[overlayId.Value];
            logger.LogInformation("已为今天创建临时层课表：{PlanId}", overlayId);
        }

        foreach (var command in workItems)
        {
            if (command.Kind == TiaokeCommandKind.Swap)
            {
                ApplySwap(profile, plan, command, applied, failures);
            }
            else
            {
                ApplySingle(profile, plan, command, applied, failures);
            }
        }

        if (applied.Count > 0)
        {
            // 批量只落盘一次。
            profileService.SaveProfile();
            journal.Save();
        }

        return new BatchPatchOutcome(applied, failures);
    }

    private void ApplySingle(
        Profile profile,
        ClassPlan plan,
        TiaokeCommand command,
        List<string> applied,
        List<string> failures)
    {
        if (command.Lesson < 1 || command.Lesson > plan.Classes.Count)
        {
            failures.Add($"第{command.Lesson}节超出今天课表范围（共 {plan.Classes.Count} 节）");
            return;
        }

        var classInfo = plan.Classes[command.Lesson - 1];
        var beforeName = profile.Subjects.TryGetValue(classInfo.SubjectId, out var beforeSubject)
            ? beforeSubject.Name
            : "(空)";

        var target = FindSubject(profile, command.ToSubject);
        if (target is null)
        {
            var available = string.Join("、", profile.Subjects.Values
                .Select(s => s.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Take(12));
            logger.LogWarning(
                "课表里找不到科目「{Subject}」，已跳过第 {Lesson} 节。可用科目：{Available}",
                command.ToSubject,
                command.Lesson,
                available);
            failures.Add($"第{command.Lesson}节找不到科目「{command.ToSubject}」，已跳过");
            return;
        }

        if (classInfo.SubjectId == target.Value.Key)
        {
            applied.Add($"第{command.Lesson}节本来就是{target.Value.Value.Name}（未变更）");
            return;
        }

        journal.Add(new LessonPatchRecord(
            timeService.GetCurrentLocalDateTime().Date,
            command.Lesson,
            classInfo.SubjectId,
            beforeName,
            target.Value.Key,
            target.Value.Value.Name,
            DateTime.Now));

        classInfo.SubjectId = target.Value.Key;
        classInfo.IsChangedClass = true;
        applied.Add($"第{command.Lesson}节 {beforeName}→{target.Value.Value.Name}");
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
            logger.LogInformation("已清除今天的临时层课表（撤销全部调课）");
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
            logger.LogInformation("已撤销 {Count} 节的调课：{Items}", applied.Count, string.Join("；", applied));
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

    private static KeyValuePair<Guid, Subject>? FindSubject(Profile profile, string name)
    {
        var trimmed = name.Trim();
        foreach (var pair in profile.Subjects)
        {
            if (string.Equals(pair.Value.Name?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return pair;
            }
        }

        foreach (var pair in profile.Subjects)
        {
            if (string.Equals(pair.Value.Initial?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return pair;
            }
        }

        return null;
    }
}
