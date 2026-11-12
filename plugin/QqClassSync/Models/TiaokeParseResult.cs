namespace QqClassSync.Models;

/// <summary>调课指令类型。</summary>
public enum TiaokeCommandKind
{
    /// <summary>「第X节 A改B」：把第 X 节换成新科目。</summary>
    Apply,

    /// <summary>「#撤销调课」：清除今天的临时层课表，恢复原课表。</summary>
    Undo,

    /// <summary>「第X节和第Y节互换」：两节科目对调。</summary>
    Swap
}

/// <summary>
/// 一条调课指令（批量指令由多条组成）。
/// </summary>
/// <param name="Kind">指令类型。</param>
/// <param name="Lesson">第几节课（1 起）。撤销指令里可能为 0（表示整天撤销）。</param>
/// <param name="FromSubject">原科目名（撤销/互换为空）。</param>
/// <param name="ToSubject">目标科目名（撤销/互换为空）。</param>
/// <param name="SwapWith">互换的对端节次（仅 Swap 有效）。</param>
/// <param name="RawText">原始消息文本，用于提醒和排查。</param>
public sealed record TiaokeCommand(
    TiaokeCommandKind Kind,
    int Lesson,
    string FromSubject,
    string ToSubject,
    int SwapWith = 0,
    string RawText = "")
{
    public static TiaokeCommand Apply(int lesson, string from, string to, string raw) =>
        new(TiaokeCommandKind.Apply, lesson, from, to, 0, raw);

    public static TiaokeCommand Swap(int a, int b, string raw) =>
        new(TiaokeCommandKind.Swap, a, string.Empty, string.Empty, b, raw);

    public static TiaokeCommand Undo(int lesson, string raw) =>
        new(TiaokeCommandKind.Undo, lesson, string.Empty, string.Empty, 0, raw);

    public override string ToString() => Kind switch
    {
        TiaokeCommandKind.Undo => Lesson > 0 ? $"撤销调课（第{Lesson}节）" : "撤销调课",
        TiaokeCommandKind.Swap => $"第{Lesson}节 ↔ 第{SwapWith}节 互换",
        _ => $"第{Lesson}节 {FromSubject}→{ToSubject}"
    };
}

/// <summary>
/// 解析结果。支持一条消息里包含多条指令（批量调课）。
/// </summary>
public sealed record TiaokeParseResult(bool Success, IReadOnlyList<TiaokeCommand> Commands, string Reason)
{
    public static TiaokeParseResult Ok(IReadOnlyList<TiaokeCommand> commands) =>
        new(true, commands, string.Empty);

    public static TiaokeParseResult Ok(params TiaokeCommand[] commands) =>
        new(true, commands, string.Empty);

    public static TiaokeParseResult Fail(string reason) =>
        new(false, Array.Empty<TiaokeCommand>(), reason);
}

/// <summary>
/// 批量执行结果：成功项与失败项分开，便于「部分成功」提示。
/// </summary>
public sealed record BatchPatchOutcome(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Failures)
{
    public bool AnyApplied => Applied.Count > 0;
    public bool AllSucceeded => Failures.Count == 0 && Applied.Count > 0;

    public string Summary()
    {
        if (Applied.Count == 0 && Failures.Count == 0)
        {
            return "没有需要执行的调课";
        }

        var parts = new List<string>();
        if (Applied.Count > 0)
        {
            parts.Add("已执行：" + string.Join("；", Applied));
        }

        if (Failures.Count > 0)
        {
            parts.Add("失败：" + string.Join("；", Failures));
        }

        return string.Join("。", parts);
    }
}
