namespace QqClassSync.Models;

/// <summary>
/// 从群聊消息里解析出来的调课指令，例如「#调课 第3节 语文改数学」。
/// </summary>
/// <param name="Lesson">第几节课（1 起）。</param>
/// <param name="FromSubject">原科目名。</param>
/// <param name="ToSubject">目标科目名。</param>
/// <param name="RawText">原始消息文本，用于提醒和排查。</param>
public sealed record TiaokeCommand(int Lesson, string FromSubject, string ToSubject, string RawText)
{
    public override string ToString() => $"第{Lesson}节 {FromSubject}→{ToSubject}";
}

/// <summary>
/// 解析结果。失败时 <see cref="Reason"/> 说明原因，用于给用户发提醒而不是静默丢弃。
/// </summary>
public sealed record TiaokeParseResult(bool Success, TiaokeCommand? Command, string Reason)
{
    public static TiaokeParseResult Ok(TiaokeCommand command) => new(true, command, string.Empty);

    public static TiaokeParseResult Fail(string reason) => new(false, null, reason);
}
