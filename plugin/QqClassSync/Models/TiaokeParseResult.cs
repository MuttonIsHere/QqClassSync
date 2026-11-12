namespace QqClassSync.Models;

/// <summary>调课指令类型。</summary>
public enum TiaokeCommandKind
{
    /// <summary>「#调课 第X节 A改B」：把第 X 节换成新科目。</summary>
    Apply,

    /// <summary>「#撤销调课」：清除今天的临时层课表，恢复原课表。</summary>
    Undo
}

/// <summary>
/// 从群聊/私聊消息里解析出来的调课指令。
/// </summary>
/// <param name="Kind">指令类型。</param>
/// <param name="Lesson">第几节课（1 起）。撤销指令里只是附带信息，可能为 0。</param>
/// <param name="FromSubject">原科目名（撤销指令为空）。</param>
/// <param name="ToSubject">目标科目名（撤销指令为空）。</param>
/// <param name="RawText">原始消息文本，用于提醒和排查。</param>
public sealed record TiaokeCommand(
    TiaokeCommandKind Kind,
    int Lesson,
    string FromSubject,
    string ToSubject,
    string RawText)
{
    public static TiaokeCommand Apply(int lesson, string from, string to, string raw) =>
        new(TiaokeCommandKind.Apply, lesson, from, to, raw);

    public static TiaokeCommand Undo(int lesson, string raw) =>
        new(TiaokeCommandKind.Undo, lesson, string.Empty, string.Empty, raw);

    public override string ToString() => Kind switch
    {
        TiaokeCommandKind.Undo => Lesson > 0 ? $"撤销调课（第{Lesson}节）" : "撤销调课",
        _ => $"第{Lesson}节 {FromSubject}→{ToSubject}"
    };
}

/// <summary>
/// 解析结果。失败时 <see cref="Reason"/> 说明原因，用于给用户发提醒而不是静默丢弃。
/// </summary>
public sealed record TiaokeParseResult(bool Success, TiaokeCommand? Command, string Reason)
{
    public static TiaokeParseResult Ok(TiaokeCommand command) => new(true, command, string.Empty);

    public static TiaokeParseResult Fail(string reason) => new(false, null, reason);
}
