using Microsoft.Extensions.Logging;
using QqClassSync.Models;

namespace QqClassSync.Services;

/// <summary>
/// AI 模糊解析入口（本版本只做「配置判定 + 安全回退」骨架，模型调用后续接入）。
///
/// 红线：
///   - 未启用 / Key 为空 / BaseUrl 为空 → 直接返回 null，调用方静默回退正则解析；
///   - 任何异常都在内部吞掉并返回 null，绝不让老师发一条消息就把轮询循环打挂。
/// </summary>
public sealed class AiTiaokeParser(TiaokeSettings settings, ILogger<AiTiaokeParser> logger)
{
    private bool _notedNotImplemented;

    /// <summary>AI 解析是否可用（开关 + Key + BaseUrl 三者齐全）。</summary>
    public bool IsConfigured =>
        AiParsePolicy.IsConfigured(settings.AiEnabled, settings.AiApiKey, settings.AiBaseUrl);

    /// <summary>
    /// 尝试用 AI 解析。返回 null 表示「AI 不可用或未解析出结果」，调用方应回退正则解析。
    /// </summary>
    public Task<TiaokeParseResult?> TryParseAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsConfigured)
            {
                return Task.FromResult<TiaokeParseResult?>(null);
            }

            if (!_notedNotImplemented)
            {
                _notedNotImplemented = true;
                logger.LogInformation(
                    "AI 解析已配置（{BaseUrl}，Key={Masked}），但当前版本尚未接入模型调用；继续使用正则解析。",
                    settings.AiBaseUrl,
                    AiParsePolicy.MaskKey(settings.AiApiKey));
            }

            // TODO(下一版)：调用 DeepSeek /chat/completions，把自然语言解析成 TiaokeCommand。
            // 接入时注意：超时要短（如 8s）、失败一律返回 null 回退正则、日志不打印 Key 和消息正文。
            return Task.FromResult<TiaokeParseResult?>(null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI 解析异常，已回退正则解析。");
            return Task.FromResult<TiaokeParseResult?>(null);
        }
    }
}
