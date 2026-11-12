using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using QqClassSync.Models;

namespace QqClassSync.Services;

/// <summary>
/// AI 模糊解析：把自然语言调课交给 OpenAI 兼容接口（默认 DeepSeek）/chat/completions。
///
/// 隐私红线（务必保持）：
///   - 日志**不得**打印 QQ 消息原文，也不得打印模型返回原文；
///   - 只允许出现：「尝试调用 AI 解析」「AI 解析成功」「AI 调用失败（超时/网络错误/限流/...）」。
///
/// 稳定性红线和限流：
///   - 8 秒超时；超时 / HTTP 错误 / JSON 解析失败一律返回 null，由调用方回退正则解析；
///   - 任意异常都在这里吞掉，绝不把异常抛进轮询循环；
///   - 单实例串行（SemaphoreSlim）+ 最小调用间隔 1.5 秒，避免两个输入源同时触发把接口打爆；
///   - 收到 HTTP 429 进入 60 秒冷却，冷却期内直接回退正则，不再发请求。
/// </summary>
public sealed class AiTiaokeParser(TiaokeSettings settings, ILogger<AiTiaokeParser> logger)
{
    private const string Model = "deepseek-chat";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromSeconds(60);

    // 复用 HttpClient，避免每次请求都建连接池（后台轮询场景下这很重要）。
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    // 同一插件实例内串行调用 + 限流冷却（按实例隔离，测试之间互不影响）。
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastCallUtc = DateTime.MinValue;
    private DateTime _cooldownUntilUtc = DateTime.MinValue;

    private const string SystemPrompt = """
        你是课表指令解析器。用户会给你一条来自老师 QQ 群聊/私聊/班级通知的消息，请判断其中是否包含「调课指令」。
        只输出 JSON 数组，不要输出解释、Markdown 或代码块。
        每项格式：{"Lesson": 3, "OriginalSubject": "语文", "NewSubject": "数学"}
        互换格式：{"Lesson": 3, "SwapWith": 5}
        规则：
        1. Lesson / SwapWith 是节次（1-20 的整数）；
        2. NewSubject 是要换成的科目；
        3. 消息里没提原科目时，OriginalSubject 填空字符串 "";
        4. 一条消息里有多节课时，输出多个对象；
        5. 如果消息里没有明确的「第几节 + 换成什么科目」意图，输出 []；
        6. 不要臆测消息里没有的信息。
        示例输入：今天下午第1节和第2节都换成自习，另外第4节音乐改美术
        示例输出：[{"Lesson":1,"OriginalSubject":"","NewSubject":"自习"},{"Lesson":2,"OriginalSubject":"","NewSubject":"自习"},{"Lesson":4,"OriginalSubject":"音乐","NewSubject":"美术"}]
        """;

    /// <summary>AI 解析是否可用（开关 + Key + BaseUrl 三者齐全）。</summary>
    public bool IsConfigured =>
        AiParsePolicy.IsConfigured(settings.AiEnabled, settings.AiApiKey, settings.AiBaseUrl);

    /// <summary>
    /// 尝试用 AI 解析。返回 null 表示「AI 不可用 / 无调课意图 / 调用失败 / 限流冷却」，调用方应回退正则解析。
    /// </summary>
    public async Task<TiaokeParseResult?> TryParseAsync(string text, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        if (DateTime.UtcNow < _cooldownUntilUtc)
        {
            TiaokeLog.Debug(logger, "AI 处于限流冷却期，本次直接走正则解析");
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var sinceLast = DateTime.UtcNow - _lastCallUtc;
            if (sinceLast < MinInterval)
            {
                TiaokeLog.Debug(logger, "AI 调用过于频繁，本次直接走正则解析");
                return null;
            }

            _lastCallUtc = DateTime.UtcNow;
            TiaokeLog.Info(logger, "尝试调用 AI 解析");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(RequestTimeout);

            var (content, rateLimited) = await RequestCompletionAsync(text, includeResponseFormat: true, cts.Token);
            if (content is null && !rateLimited)
            {
                // 部分 OpenAI 兼容实现不支持 response_format，去掉该参数再试一次。
                (content, rateLimited) = await RequestCompletionAsync(text, includeResponseFormat: false, cts.Token);
            }

            if (rateLimited)
            {
                _cooldownUntilUtc = DateTime.UtcNow + RateLimitCooldown;
                TiaokeLog.Warn(logger, "AI 调用失败（限流），{Seconds} 秒内改走正则解析", (int)RateLimitCooldown.TotalSeconds);
                return null;
            }

            if (content is null)
            {
                TiaokeLog.Warn(logger, "AI 调用失败（HTTP 错误）");
                return null;
            }

            var outcomes = AiResponseParser.TryParseList(content, out var reason);
            if (outcomes is null)
            {
                if (reason != "no_intent")
                {
                    TiaokeLog.Warn(logger, "AI 调用失败（返回格式无法解析）");
                }

                return null;
            }

            if (outcomes.Count == 0)
            {
                // 明确没有调课意图：安静返回，交给调用方忽略。
                return null;
            }

            var commands = outcomes
                .Select(outcome => outcome.SwapWith > 0
                    ? TiaokeCommand.Swap(outcome.Lesson, outcome.SwapWith, text)
                    : TiaokeCommand.Apply(outcome.Lesson, outcome.OriginalSubject, outcome.NewSubject, text))
                .ToList();

            TiaokeLog.Info(logger, "AI 解析成功");
            return TiaokeParseResult.Ok(commands);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 插件停止导致的外部取消，不算 AI 失败，也不打日志。
            return null;
        }
        catch (OperationCanceledException)
        {
            TiaokeLog.Warn(logger, "AI 调用失败（超时）");
            return null;
        }
        catch (HttpRequestException)
        {
            TiaokeLog.Warn(logger, "AI 调用失败（网络错误）");
            return null;
        }
        catch (Exception ex)
        {
            // 只打异常类型，不打消息内容，避免泄露 QQ 文本或响应体。
            TiaokeLog.Warn(logger, ex, "AI 调用失败（{ErrorKind}）", ex.GetType().Name);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 发一次 /chat/completions，返回 message.content；HTTP 非 2xx 时返回 (null, rateLimited)。
    /// </summary>
    private async Task<(string? Content, bool RateLimited)> RequestCompletionAsync(
        string text,
        bool includeResponseFormat,
        CancellationToken cancellationToken)
    {
        var endpoint = $"{AiParsePolicy.NormalizeBaseUrl(settings.AiBaseUrl)}/chat/completions";

        var payload = new Dictionary<string, object?>
        {
            ["model"] = Model,
            ["temperature"] = 0,
            ["max_tokens"] = 400,
            ["messages"] = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = text }
            }
        };

        if (includeResponseFormat)
        {
            payload["response_format"] = new { type = "json_object" };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AiApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return (null, true);
        }

        if (response.StatusCode == HttpStatusCode.BadRequest && includeResponseFormat)
        {
            return (null, false);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, false);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return (AiResponseParser.ExtractMessageContent(body), false);
    }
}
