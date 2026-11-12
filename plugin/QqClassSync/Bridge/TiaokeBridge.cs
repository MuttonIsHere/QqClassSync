using QqClassSync.Services;

namespace QqClassSync.Bridge;

/// <summary>
/// 跨插件桥接：其它插件（如「小橙看班联动」）把消息正文投递给 QQ 调课同步的解析管线。
///
/// 约定（对方插件通过反射按类型名调用，**不需要**编译期引用本程序集，避免两个插件互相锁死版本）：
///   类型名：<c>QqClassSync.Bridge.TiaokeBridge</c>
///   方法： <c>public static bool Submit(string text, string source)</c>
///
/// 返回 false 表示本插件尚未就绪或文本为空；调用方应静默跳过、下次再试。
/// </summary>
public static class TiaokeBridge
{
    /// <summary>「小橙看班」来源标识（对方调用时传入，用于提醒里标注来源）。</summary>
    public const string SourceXiaoCheng = "小橙看班";

    /// <summary>
    /// 桥接契约版本。对方（XiaoChengLink）启动时会读取并核对；
    /// 2 = 支持批量指令（多节 / 互换）。
    /// </summary>
    public static string ContractVersion => "2";

    /// <summary>由 <see cref="TiaokeNotificationProvider"/> 在构造时赋值。</summary>
    internal static TiaokeNotificationProvider? Provider { get; set; }

    /// <summary>
    /// 提交一条消息正文。线程安全，可在任意线程调用。
    /// </summary>
    public static bool Submit(string? text, string? source)
    {
        try
        {
            return Provider?.SubmitExternal(text, source) ?? false;
        }
        catch
        {
            // 桥接失败绝不能影响调用方插件。
            return false;
        }
    }
}
