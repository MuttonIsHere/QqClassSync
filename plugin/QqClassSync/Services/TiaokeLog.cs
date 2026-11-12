using Microsoft.Extensions.Logging;

namespace QqClassSync.Services;

/// <summary>
/// 统一日志前缀：所有调课相关日志都带 <c>[调课中枢]</c>，
/// 方便在 ClassIsland 日志里一眼筛出「消息在哪一环丢了」。
/// </summary>
public static class TiaokeLog
{
    public const string Prefix = "[调课中枢] ";

    public static void Info(ILogger logger, string message, params object?[] args) =>
        logger.LogInformation(Prefix + message, args);

    public static void Info(ILogger logger, Exception ex, string message, params object?[] args) =>
        logger.LogInformation(ex, Prefix + message, args);

    public static void Warn(ILogger logger, string message, params object?[] args) =>
        logger.LogWarning(Prefix + message, args);

    public static void Warn(ILogger logger, Exception ex, string message, params object?[] args) =>
        logger.LogWarning(ex, Prefix + message, args);

    public static void Debug(ILogger logger, string message, params object?[] args) =>
        logger.LogDebug(Prefix + message, args);

    public static void Debug(ILogger logger, Exception ex, string message, params object?[] args) =>
        logger.LogDebug(ex, Prefix + message, args);

    public static void Error(ILogger logger, Exception? ex, string message, params object?[] args) =>
        logger.LogError(ex, Prefix + message, args);
}
