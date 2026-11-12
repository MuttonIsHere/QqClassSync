using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.Logging;
using QqClassSync.Models;

namespace QqClassSync.Services;

/// <summary>
/// 把调课指令落到 ClassIsland 的「临时层课表」上。
///
/// 关键实现依据（读 ClassIsland 源码得出）：
///   - 今天该用哪张课表，不能直接查 OrderedSchedules：正常日子那里是空的，
///     真正生效的课表由 ILessonsService.GetClassPlanByDate() 按
///     「预定课表 → 临时课表 → 课表组/时间规则匹配」的顺序选出。
///   - CreateTempClassPlan 会复制源课表、标记 IsOverlay=true，并挂到
///     OrderedSchedules[今天]，所以补丁只影响今天。
///   - ClassPlan.Classes 与时间表里 TimeType==0 的「上课」时间点一一对应，
///     因此「第 X 节」就是 Classes[X-1]。
///   - 改完必须 SaveProfile()，否则只在内存里生效。
///
/// 单节课撤销：临时层本身看不出「哪些节被改过」，所以每次改课都往
/// <see cref="LessonUndoJournal"/> 记一条「第几节 原科目→新科目」，
/// 撤销时只回滚指定节次，其余改动保留。
/// </summary>
public sealed class TempClassPlanPatcher(
    IProfileService profileService,
    ILessonsService lessonsService,
    IExactTimeService timeService,
    LessonUndoJournal journal,
    ILogger<TempClassPlanPatcher> logger)
{
    public PatchOutcome Apply(TiaokeCommand command)
    {
        var profile = profileService.Profile;
        var today = timeService.GetCurrentLocalDateTime().Date;

        var plan = lessonsService.GetClassPlanByDate(today, out var planId);
        if (plan is null || planId is null)
        {
            return PatchOutcome.Fail("今天没有生效的课表，无法调课");
        }

        if (!plan.IsOverlay)
        {
            var overlayId = profileService.CreateTempClassPlan(planId.Value);
            if (overlayId is null)
            {
                return PatchOutcome.Fail("创建临时层课表失败（可能今天已经有临时层）");
            }

            planId = overlayId;
            plan = profile.ClassPlans[planId.Value];
            logger.LogInformation("已为今天创建临时层课表：{PlanId}", planId);
        }

        if (command.Lesson > plan.Classes.Count)
        {
            return PatchOutcome.Fail($"第 {command.Lesson} 节超出今天课表范围（共 {plan.Classes.Count} 节）");
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
            return PatchOutcome.Fail($"课表里找不到科目「{command.ToSubject}」。可用科目：{available}");
        }

        if (classInfo.SubjectId == target.Value.Key)
        {
            return PatchOutcome.Ok($"第 {command.Lesson} 节本来就是 {target.Value.Value.Name}，无需改动");
        }

        // 先记日志再改：万一 SaveProfile 抛异常，日志里有一条无害的「未生效」记录，
        // 下次撤销会把它写回成同一个科目，不会造成数据错乱。
        journal.Add(new LessonPatchRecord(
            today,
            command.Lesson,
            classInfo.SubjectId,
            beforeName,
            target.Value.Key,
            target.Value.Value.Name,
            DateTime.Now));
        journal.Save();

        classInfo.SubjectId = target.Value.Key;
        classInfo.IsChangedClass = true;
        profileService.SaveProfile();

        logger.LogInformation(
            "调课已写入临时层：第{Lesson}节 {Before} -> {After}",
            command.Lesson, beforeName, target.Value.Value.Name);

        return PatchOutcome.Ok($"第 {command.Lesson} 节：{beforeName} → {target.Value.Value.Name}");
    }

    /// <summary>
    /// 撤销单节课：只把指定节次恢复成原科目，其他节次的调课保留。
    /// </summary>
    public PatchOutcome UndoLesson(int lesson)
    {
        var profile = profileService.Profile;
        var today = timeService.GetCurrentLocalDateTime().Date;

        if (profile.OverlayClassPlanId is null
            || !profile.ClassPlans.TryGetValue(profile.OverlayClassPlanId.Value, out var plan))
        {
            return PatchOutcome.Fail("今天没有临时层课表，无需撤销");
        }

        if (lesson <= 0 || lesson > plan.Classes.Count)
        {
            return PatchOutcome.Fail($"节次 {lesson} 超出今天课表范围（共 {plan.Classes.Count} 节）");
        }

        var record = journal.TakeLast(today, lesson);
        var fallbackId = Guid.Empty;
        var fallbackName = string.Empty;
        if (record is null && !TryReadOriginalFromSource(profile, plan, lesson, out fallbackId, out fallbackName))
        {
            return PatchOutcome.Fail($"找不到第 {lesson} 节的原课表信息，无法精准撤销（可改用整天撤销）");
        }

        var originalId = record?.OriginalSubjectId ?? fallbackId;
        var originalName = record?.OriginalSubjectName ?? fallbackName;

        var classInfo = plan.Classes[lesson - 1];
        classInfo.SubjectId = originalId;
        classInfo.IsChangedClass = false;

        journal.Save();
        profileService.SaveProfile();

        logger.LogInformation("已撤销第{Lesson}节的调课，恢复为 {Subject}", lesson, originalName);

        // 所有节次都和源课表一致了，就把临时层一起清掉，避免课表名一直挂着「（临时层）」。
        if (IsIdenticalToSource(profile, plan))
        {
            profileService.ClearTempClassPlan();
            journal.Clear(today);
            journal.Save();
            logger.LogInformation("所有调课都已撤销，临时层课表已清除。");
            return PatchOutcome.Ok($"第 {lesson} 节已恢复为 {originalName}；今天已无调课，临时层已清除");
        }

        return PatchOutcome.Ok($"第 {lesson} 节已恢复为 {originalName}，其余调课保留");
    }

    /// <summary>
    /// 撤销整天：清除今天的临时层课表，恢复到原课表。
    /// </summary>
    public PatchOutcome Undo()
    {
        var profile = profileService.Profile;
        if (profile.OverlayClassPlanId is null)
        {
            return PatchOutcome.Fail("今天没有临时层课表，无需撤销");
        }

        var today = timeService.GetCurrentLocalDateTime().Date;
        profileService.ClearTempClassPlan();
        journal.Clear(today);
        journal.Save();

        logger.LogInformation("已清除今天的临时层课表（撤销全部调课）");
        return PatchOutcome.Ok("已恢复原课表（今天的临时层已清除）");
    }

    /// <summary>
    /// 诊断用：描述今天解析出来的课表，便于确认「今天没有课表」这类问题。
    /// </summary>
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

    /// <summary>
    /// 没有日志记录时（例如日志被清空、插件重装），退回到「临时层的源课表」读取原科目。
    /// </summary>
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
            // 无法比较时保守保留临时层。
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
