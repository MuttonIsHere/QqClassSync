using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.Logging;
using QqClassSync.Models;

namespace QqClassSync.Services;

/// <summary>
/// 把调课指令落到 ClassIsland 的「临时层课表」上。
///
/// 关键实现依据（读 ClassIsland 源码得出的结论）：
///   - 今天该用哪张课表，不能直接查 OrderedSchedules：正常日子那里是空的，
///     真正生效的课表由 ILessonsService.GetClassPlanByDate() 按
///     「预定课表 → 临时课表 → 课表组/时间规则匹配」的顺序选出。
///   - CreateTempClassPlan 会复制源课表、标记 IsOverlay=true，并挂到
///     OrderedSchedules[今天]，所以补丁只影响今天。
///   - ClassPlan.Classes 与时间表里 TimeType==0 的「上课」时间点一一对应，
///     因此「第 X 节」就是 Classes[X-1]。
///   - 改完必须 SaveProfile()，否则只在内存里生效。
/// </summary>
public sealed class TempClassPlanPatcher(
    IProfileService profileService,
    ILessonsService lessonsService,
    IExactTimeService timeService,
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

        classInfo.SubjectId = target.Value.Key;
        classInfo.IsChangedClass = true;
        profileService.SaveProfile();

        logger.LogInformation(
            "调课已写入临时层：第{Lesson}节 {Before} -> {After}",
            command.Lesson, beforeName, target.Value.Value.Name);

        return PatchOutcome.Ok($"第 {command.Lesson} 节：{beforeName} → {target.Value.Value.Name}");
    }

    /// <summary>
    /// 撤销调课：清除今天的临时层课表，恢复到原课表。
    /// 注意 ClearTempClassPlan() 是「整天」级别的撤销，不能只撤销某一节。
    /// </summary>
    public PatchOutcome Undo()
    {
        var profile = profileService.Profile;
        if (profile.OverlayClassPlanId is null)
        {
            return PatchOutcome.Fail("今天没有临时层课表，无需撤销");
        }

        profileService.ClearTempClassPlan();
        logger.LogInformation("已清除今天的临时层课表（撤销调课）");
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

        return $"「{plan.Name}」共 {plan.Classes.Count} 节{(plan.IsOverlay ? "（临时层）" : string.Empty)}";
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
