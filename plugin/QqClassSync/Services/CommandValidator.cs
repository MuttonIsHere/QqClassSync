using QqClassSync.Models;

namespace QqClassSync.Services;

/// <summary>当天课表的一节课（只读快照，供冲突校验使用）。</summary>
public sealed record PlanLessonInfo(int Lesson, string SubjectName);

/// <summary>当天课表快照。</summary>
public sealed record PlanSnapshot(int LessonCount, IReadOnlyList<PlanLessonInfo> Lessons)
{
    public string SubjectNameAt(int lesson) =>
        lesson >= 1 && lesson <= Lessons.Count ? Lessons[lesson - 1].SubjectName : string.Empty;
}

public enum CommandIssueLevel
{
    /// <summary>不阻断，只在日志/结果里提示。</summary>
    Warning,

    /// <summary>阻断整批执行（冲突、越界等会造成错误改课的情况）。</summary>
    Block
}

public sealed record CommandIssue(CommandIssueLevel Level, int Lesson, string Message);

/// <summary>
/// 批量指令的冲突与边界校验：在真正写档案之前把「不可能同时成立」的指令拦下来。
///
/// 原则：
///   - 结构冲突（同一节多个目标、互换交叉、越界）→ 整批阻断，一条都不写；
///   - 重复但等价的指令 → 只提示，由上层去重后执行；
///   - 纯函数，便于单测。
/// </summary>
public static class CommandValidator
{
    public const int MaxCommandsPerMessage = 10;

    public static IReadOnlyList<CommandIssue> Validate(IReadOnlyList<TiaokeCommand> commands, PlanSnapshot plan)
    {
        var issues = new List<CommandIssue>();
        if (commands.Count == 0)
        {
            issues.Add(new CommandIssue(CommandIssueLevel.Block, 0, "没有可执行的调课指令"));
            return issues;
        }

        if (commands.Count > MaxCommandsPerMessage)
        {
            issues.Add(new CommandIssue(
                CommandIssueLevel.Block,
                0,
                $"一条消息最多支持 {MaxCommandsPerMessage} 条调课指令（当前 {commands.Count} 条）"));
        }

        var applies = commands.Where(c => c.Kind == TiaokeCommandKind.Apply).ToList();
        var swaps = commands.Where(c => c.Kind == TiaokeCommandKind.Swap).ToList();

        // 1) 同一节被改成不同科目 → 无法判断哪个才是老师的意图。
        foreach (var group in applies.GroupBy(c => c.Lesson).Where(g => g.Count() > 1))
        {
            var targets = group
                .Select(c => c.ToSubject?.Trim() ?? string.Empty)
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (targets.Count > 1)
            {
                issues.Add(new CommandIssue(
                    CommandIssueLevel.Block,
                    group.Key,
                    $"第{group.Key}节在同一条消息里被改成多个科目（{string.Join("、", targets)}），无法判断以哪个为准"));
            }
            else if (targets.Count == 1)
            {
                issues.Add(new CommandIssue(
                    CommandIssueLevel.Warning,
                    group.Key,
                    $"第{group.Key}节出现了重复指令，已合并为一条"));
            }
        }

        // 2) 每条 Apply 的自身合法性。
        foreach (var apply in applies)
        {
            if (string.IsNullOrWhiteSpace(apply.ToSubject))
            {
                issues.Add(new CommandIssue(CommandIssueLevel.Block, apply.Lesson, $"第{apply.Lesson}节没有目标科目"));
            }

            if (apply.Lesson < 1 || apply.Lesson > plan.LessonCount)
            {
                issues.Add(new CommandIssue(
                    CommandIssueLevel.Block,
                    apply.Lesson,
                    $"第{apply.Lesson}节超出今天课表范围（共 {plan.LessonCount} 节）"));
            }
        }

        // 3) 互换自身合法性 + 互换交叉。
        foreach (var swap in swaps)
        {
            if (swap.Lesson == swap.SwapWith)
            {
                issues.Add(new CommandIssue(CommandIssueLevel.Block, swap.Lesson, $"第{swap.Lesson}节不能和自己互换"));
            }

            if (swap.Lesson < 1 || swap.Lesson > plan.LessonCount
                || swap.SwapWith < 1 || swap.SwapWith > plan.LessonCount)
            {
                issues.Add(new CommandIssue(
                    CommandIssueLevel.Block,
                    swap.Lesson,
                    $"互换节次超出今天课表范围（第{swap.Lesson}节 / 第{swap.SwapWith}节，共 {plan.LessonCount} 节）"));
            }
        }

        var swapPairs = new HashSet<string>(StringComparer.Ordinal);
        var swapLessons = new Dictionary<int, string>();
        foreach (var swap in swaps)
        {
            var low = Math.Min(swap.Lesson, swap.SwapWith);
            var high = Math.Max(swap.Lesson, swap.SwapWith);
            var pairKey = $"{low}:{high}";

            // 「3↔5」和「5↔3」是同一对互换，重复写只算一次。
            if (!swapPairs.Add(pairKey))
            {
                continue;
            }

            foreach (var lesson in new[] { low, high })
            {
                if (swapLessons.TryGetValue(lesson, out var otherPair) && otherPair != pairKey)
                {
                    issues.Add(new CommandIssue(
                        CommandIssueLevel.Block,
                        lesson,
                        $"第{lesson}节同时出现在多条互换指令里，互换顺序无法确定"));
                }
                else
                {
                    swapLessons[lesson] = pairKey;
                }
            }
        }

        // 4) 同一节既调课又互换 → 结果取决于执行顺序，不应猜。
        foreach (var apply in applies)
        {
            if (swapLessons.ContainsKey(apply.Lesson))
            {
                issues.Add(new CommandIssue(
                    CommandIssueLevel.Block,
                    apply.Lesson,
                    $"第{apply.Lesson}节既有调课又有互换，结果取决于执行顺序，无法安全执行"));
            }
        }

        return issues;
    }
}
