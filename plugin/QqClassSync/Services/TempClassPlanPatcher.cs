using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.Logging;
using QqClassSync.Models;

namespace QqClassSync.Services;

/// <summary>
/// 把调课指令落到 ClassIsland 的「临时层课表」上。
///
/// 实现依据（ClassIsland 源码 ProfileService.cs）：
///   - CreateTempClassPlan 会复制源课表、标记 IsOverlay=true，并把它挂到
///     OrderedSchedules[今天]，所以补丁只影响今天。
///   - ClassPlan.Classes 与时间表里 TimeType==0 的「上课」时间点一一对应，
///     因此「第 X 节」就是 Classes[X-1]。
///   - 改完必须 SaveProfile()，否则只在内存里生效。
/// </summary>
public sealed class TempClassPlanPatcher(
    IProfileService profileService,
    IExactTimeService timeService,
    ILogger<TempClassPlanPatcher> logger)
{
    public PatchOutcome Apply(TiaokeCommand command)
    {
        var profile = profileService.Profile;
        var today = timeService.GetCurrentLocalDateTime().Date;

        if (!profile.OrderedSchedules.TryGetValue(today, out var orderedSchedule)
            || orderedSchedule.ClassPlanId == Guid.Empty
            || !profile.ClassPlans.ContainsKey(orderedSchedule.ClassPlanId))
        {
            return PatchOutcome.Fail("今天没有生效的课表，无法调课");
        }

        var planId = orderedSchedule.ClassPlanId;
        var plan = profile.ClassPlans[planId];

        if (!plan.IsOverlay)
        {
            var overlayId = profileService.CreateTempClassPlan(planId);
            if (overlayId is null)
            {
                return PatchOutcome.Fail("创建临时层课表失败（可能今天已经有临时层）");
            }

            planId = overlayId.Value;
            plan = profile.ClassPlans[planId];
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
