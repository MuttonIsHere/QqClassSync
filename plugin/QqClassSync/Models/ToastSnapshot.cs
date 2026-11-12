namespace QqClassSync.Models;

/// <summary>
/// 一条来自 Windows 通知中心的 Toast 快照（只保留插件需要的字段）。
/// </summary>
public sealed record ToastSnapshot(
    uint Id,
    string AppName,
    string AppUserModelId,
    IReadOnlyList<string> Texts,
    DateTimeOffset CreatedAt)
{
    /// <summary>
    /// 合并后的完整文本。QQ 的 Toast 通常把「发送者」和「消息内容」拆成多个 text 元素。
    /// </summary>
    public string FullText => string.Join(" ", Texts);
}

/// <summary>
/// 补丁执行结果。
/// </summary>
public sealed record PatchOutcome(bool Applied, string Message)
{
    public static PatchOutcome Ok(string message) => new(true, message);

    public static PatchOutcome Fail(string message) => new(false, message);
}
