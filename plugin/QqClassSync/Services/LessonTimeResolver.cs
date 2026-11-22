namespace QqClassSync.Services;

/// <summary>一天中的时段（老师口中的「上午/下午」）。</summary>
public enum DayPart
{
    None,
    Morning,
    Noon,
    Afternoon,
    Evening
}

/// <summary>当天课表里的一个时间点（只保留解析「上午/下午」需要的字段）。</summary>
public sealed record LessonSlot(int Lesson, TimeSpan Start, TimeSpan End);

/// <summary>时段解析结果。</summary>
public sealed record LessonTimeResult(bool Success, int Lesson, string Reason)
{
    public static LessonTimeResult Ok(int lesson) => new(true, lesson, string.Empty);

    public static LessonTimeResult Fail(string reason) => new(false, 0, reason);
}

/// <summary>
/// 把「下午第二节」这类带时段的说法，换算成当天课表里的真实节次。
///
/// 为什么要做：ClassIsland 的节次是「全天第 N 节」，而老师习惯说「下午第二节」。
/// 直接当第 2 节会改错课；这里按当天时间表 12:00 / 14:00 / 18:00 分界换算，
/// 换算不出来就明确失败，绝不退回「第 N 节」硬猜。
///
/// 纯函数、无宿主依赖，便于单测。
/// </summary>
public static class LessonTimeResolver
{
    private static readonly TimeSpan NoonStart = TimeSpan.FromHours(12);
    private static readonly TimeSpan AfternoonEnd = TimeSpan.FromHours(18);
    private static readonly TimeSpan NoonEnd = TimeSpan.FromHours(14);

    /// <summary>从文本里识别时段词。</summary>
    public static bool TryParseDayPart(string? text, out DayPart part)
    {
        part = DayPart.None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value.Contains("上午", StringComparison.Ordinal) || value.Contains("早上", StringComparison.Ordinal))
        {
            part = DayPart.Morning;
            return true;
        }

        if (value.Contains("中午", StringComparison.Ordinal))
        {
            part = DayPart.Noon;
            return true;
        }

        if (value.Contains("下午", StringComparison.Ordinal))
        {
            part = DayPart.Afternoon;
            return true;
        }

        if (value.Contains("晚上", StringComparison.Ordinal) || value.Contains("晚自习", StringComparison.Ordinal))
        {
            part = DayPart.Evening;
            return true;
        }

        return false;
    }

    public static string ToText(DayPart part) => part switch
    {
        DayPart.Morning => "上午",
        DayPart.Noon => "中午",
        DayPart.Afternoon => "下午",
        DayPart.Evening => "晚上",
        _ => string.Empty
    };

    /// <summary>
    /// 把「时段 + 该时段内的第几节」换算成全天节次。
    /// </summary>
    /// <param name="lesson">时段内的序号（1 起）。</param>
    /// <param name="partText">原文里的时段词（可为空）。</param>
    /// <param name="slots">当天课表的时间点（按开始时间升序）。</param>
    public static LessonTimeResult Resolve(int lesson, string? partText, IReadOnlyList<LessonSlot> slots)
    {
        if (lesson <= 0)
        {
            return LessonTimeResult.Fail("节次必须是大于 0 的整数");
        }

        if (!TryParseDayPart(partText, out var part) || part == DayPart.None)
        {
            return LessonTimeResult.Ok(lesson);
        }

        if (slots.Count == 0)
        {
            return LessonTimeResult.Fail("当天课表没有时间表，无法按「上午/下午」换算");
        }

        var partName = ToText(part);
        var inPart = slots.Where(slot => InPart(slot.Start, part)).OrderBy(slot => slot.Start).ToList();
        if (inPart.Count == 0)
        {
            return LessonTimeResult.Fail($"当天课表里没有{partName}的课");
        }

        if (lesson > inPart.Count)
        {
            return LessonTimeResult.Fail(
                $"{partName}只有 {inPart.Count} 节（全天第 {inPart[0].Lesson}-{inPart[^1].Lesson} 节），没有「{partName}第{lesson}节」");
        }

        return LessonTimeResult.Ok(inPart[lesson - 1].Lesson);
    }

    private static bool InPart(TimeSpan start, DayPart part) => part switch
    {
        DayPart.Morning => start < NoonStart,
        DayPart.Noon => start >= NoonStart && start < NoonEnd,
        DayPart.Afternoon => start >= NoonStart && start < AfternoonEnd,
        DayPart.Evening => start >= AfternoonEnd,
        _ => true
    };
}
