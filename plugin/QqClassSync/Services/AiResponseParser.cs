using System.Text.Json;
using System.Text.RegularExpressions;

namespace QqClassSync.Services;

/// <summary>
/// 解析大模型返回的 JSON（批量数组）。抽成纯函数便于单元测试，也不在这里做任何日志（避免泄露内容）。
/// </summary>
public static class AiResponseParser
{
    private static readonly Regex NumberToken = new("[0-9零〇一二两三四五六七八九十百]{1,3}", RegexOptions.Compiled);
    /// <summary>
    /// 从模型返回片段里解析调课意图列表。
    /// </summary>
    /// <param name="content">模型 message.content 原文（可能带 ```json 围栏或多余说明）。</param>
    /// <param name="failureReason">失败原因（内部用，不直接写日志正文）。</param>
    /// <returns>解析成功返回列表；明确「无调课意图」返回空列表（reason=no_intent）；格式错误返回 null。</returns>
    public static IReadOnlyList<AiParseOutcome>? TryParseList(string? content, out string failureReason)
    {
        failureReason = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            failureReason = "empty";
            return null;
        }

        var json = ExtractJson(content);
        if (json is null)
        {
            failureReason = "no_json";
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // 兼容：数组 / {"commands":[...]} / 单个对象
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryGetProperty(root, "commands", out var commandsElement) && commandsElement.ValueKind == JsonValueKind.Array)
                {
                    root = commandsElement;
                }
                else if (TryGetProperty(root, "Commands", out var commandsElement2) && commandsElement2.ValueKind == JsonValueKind.Array)
                {
                    root = commandsElement2;
                }
                else
                {
                    var single = TryReadItem(root);
                    if (single is null)
                    {
                        failureReason = "bad_item";
                        return null;
                    }

                    return new[] { single };
                }
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                failureReason = "not_array";
                return null;
            }

            var outcomes = new List<AiParseOutcome>();
            var totalItems = 0;
            foreach (var item in root.EnumerateArray())
            {
                totalItems++;
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var outcome = TryReadItem(item);
                if (outcome is not null)
                {
                    outcomes.Add(outcome);
                }
            }

            if (outcomes.Count == 0)
            {
                // 空数组 = 没有调课意图；非空但全部非法 = 模型输出格式不对（调用方应回退正则）。
                if (totalItems == 0)
                {
                    failureReason = "no_intent";
                    return new List<AiParseOutcome>();
                }

                failureReason = "bad_items";
                return null;
            }

            return outcomes;
        }
        catch (JsonException)
        {
            failureReason = "bad_json";
            return null;
        }
    }

    /// <summary>
    /// 从 /chat/completions 响应里取出 choices[0].message.content。
    /// </summary>
    public static string? ExtractMessageContent(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return null;
            }

            var message = choices[0].GetProperty("message");
            return message.GetProperty("content").GetString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 提取 JSON（数组或对象）：去掉 ```json 围栏、忽略前后说明文字。
    /// </summary>
    internal static string? ExtractJson(string content)
    {
        var startArray = content.IndexOf('[');
        var startObject = content.IndexOf('{');

        int start;
        char close;
        if (startArray >= 0 && (startObject < 0 || startArray < startObject))
        {
            start = startArray;
            close = ']';
        }
        else if (startObject >= 0)
        {
            start = startObject;
            close = '}';
        }
        else
        {
            return null;
        }

        var end = content.LastIndexOf(close);
        if (end <= start)
        {
            return null;
        }

        return content[start..(end + 1)];
    }

    private static AiParseOutcome? TryReadItem(JsonElement element)
    {
        // 兼容模型可能用的几种字段别名；优先级：标准字段 → 常见别名。
        var lessonText = TryGetAnyProperty(element, out var lessonTextElement, "LessonText", "TimeText", "Time", "PeriodText", "Period")
            ? ReadString(lessonTextElement).Trim()
            : string.Empty;

        var lesson = 0;
        string? lessonFieldText = null;
        var hasLesson = TryGetAnyProperty(element, out var lessonElement, "Lesson", "Period", "Index", "Jie")
                        && TryReadInt(lessonElement, out lesson);
        if (!hasLesson
            && TryGetAnyProperty(element, out var rawLessonElement, "Lesson", "Period", "Index", "Jie"))
        {
            // 模型偶尔把整句时间写进 Lesson（"下午第二节"），当成 LessonText 用。
            lessonFieldText = ReadString(rawLessonElement).Trim();
        }

        if (!hasLesson || lesson <= 0)
        {
            // 模型只给了「下午第二节」这类文本时，退一步从里面取数字（时段换算交给执行层）。
            if (string.IsNullOrWhiteSpace(lessonText))
            {
                lessonText = lessonFieldText ?? string.Empty;
            }

            lesson = ExtractLeadingNumber(lessonText);
        }

        if (lesson is < 1 or > 20)
        {
            return null;
        }

        var swapWith = 0;
        if (TryGetAnyProperty(element, out var swapElement, "SwapWith", "Swap", "ExchangeWith")
            && TryReadInt(swapElement, out var swap))
        {
            if (swap is >= 1 and <= 20 && swap != lesson)
            {
                swapWith = swap;
            }
        }

        var newSubject = TryGetAnyProperty(element, out var newElement, "NewSubject", "Subject", "To", "TargetSubject")
            ? ReadString(newElement).Trim()
            : string.Empty;

        if (swapWith == 0 && string.IsNullOrWhiteSpace(newSubject))
        {
            return null;
        }

        var originalSubject = TryGetAnyProperty(element, out var originalElement, "OriginalSubject", "From", "OldSubject")
            ? ReadString(originalElement).Trim()
            : string.Empty;

        // 置信度：缺省视为 1；模型偶尔给字符串（"0.8"），这里一并兼容并夹到 0~1。
        var confidence = 1.0;
        if (TryGetAnyProperty(element, out var confidenceElement, "Confidence", "Score")
            && TryReadDouble(confidenceElement, out var parsedConfidence))
        {
            confidence = Math.Clamp(parsedConfidence, 0, 1);
        }

        var ambiguity = TryGetAnyProperty(element, out var ambiguityElement, "Ambiguity", "Uncertain", "Uncertainty", "Reason")
            ? ReadString(ambiguityElement).Trim()
            : string.Empty;

        return new AiParseOutcome(lesson, originalSubject, newSubject, swapWith, confidence, ambiguity, lessonText);
    }

    /// <summary>从「下午第二节」这类文本里取第一个数字（中文/阿拉伯都支持）。</summary>
    private static int ExtractLeadingNumber(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        // 只认带「节」或带时段的说法；避免把「14:30」这类时间当成第 14 节。
        if (!text.Contains('节') && !LessonTimeResolver.TryParseDayPart(text, out _))
        {
            return 0;
        }

        var match = NumberToken.Match(text);
        if (!match.Success)
        {
            return 0;
        }

        var parsed = TiaokeParser.ParseNumber(match.Value);
        return parsed is >= 1 and <= 20 ? parsed.Value : 0;
    }

    private static bool TryGetAnyProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryReadInt(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(element.GetString(), out value),
            _ => false
        };
    }

    private static bool TryReadDouble(JsonElement element, out double value)
    {
        value = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(
                element.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value),
            _ => false
        };
    }

    private static string ReadString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Null => string.Empty,
        _ => element.ToString()
    };
}

/// <summary>模型解析出的一条调课意图（原科目可能为空；SwapWith &gt; 0 表示互换）。</summary>
/// <param name="Lesson">全天节次（1-20）。</param>
/// <param name="OriginalSubject">模型理解的原科目，可能为空。</param>
/// <param name="NewSubject">目标科目（可能是老师的说法，执行前还要做一次确定性匹配）。</param>
/// <param name="SwapWith">互换对端节次，0 表示不是互换。</param>
/// <param name="Confidence">模型自评置信度 0-1；缺省视为 1。</param>
/// <param name="Ambiguity">模型自报的歧义说明；非空时强制走「#确认」流程。</param>
/// <param name="LessonText">模型看到的原始时间说法（如「下午第二节」），用于时段换算。</param>
public sealed record AiParseOutcome(
    int Lesson,
    string OriginalSubject,
    string NewSubject,
    int SwapWith = 0,
    double Confidence = 1,
    string Ambiguity = "",
    string LessonText = "");
