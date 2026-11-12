using System.Text.Json;

namespace QqClassSync.Services;

/// <summary>
/// 解析大模型返回的 JSON。抽成纯函数便于单元测试，也不在这里做任何日志（避免泄露内容）。
/// </summary>
public static class AiResponseParser
{
    /// <summary>
    /// 从模型返回片段里解析调课意图。
    /// </summary>
    /// <param name="content">模型 message.content 原文（可能带 ```json 围栏或多余说明）。</param>
    /// <param name="failureReason">失败原因（内部用，不直接写日志正文）。</param>
    /// <returns>解析成功返回结果；明确「无调课意图」返回 null 且 failureReason = "no_intent"。</returns>
    public static AiParseOutcome? TryParse(string? content, out string failureReason)
    {
        failureReason = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            failureReason = "empty";
            return null;
        }

        var json = ExtractJsonObject(content);
        if (json is null)
        {
            failureReason = "no_json";
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                failureReason = "not_object";
                return null;
            }

            // 空对象 {} 表示「没有调课意图」，这是正常结果，不算失败。
            var hasLesson = TryGetProperty(root, "Lesson", out var lessonElement);
            var hasNew = TryGetProperty(root, "NewSubject", out var newElement);
            if (!hasLesson && !hasNew)
            {
                failureReason = "no_intent";
                return null;
            }

            if (!hasLesson || !TryReadInt(lessonElement, out var lesson) || lesson is < 1 or > 20)
            {
                failureReason = "bad_lesson";
                return null;
            }

            var newSubject = hasNew ? ReadString(newElement) : string.Empty;
            if (string.IsNullOrWhiteSpace(newSubject))
            {
                failureReason = "bad_subject";
                return null;
            }

            var originalSubject = TryGetProperty(root, "OriginalSubject", out var originalElement)
                ? ReadString(originalElement)
                : string.Empty;

            return new AiParseOutcome(lesson, originalSubject.Trim(), newSubject.Trim());
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
    /// 提取第一个 JSON 对象：去掉 ```json 围栏、忽略前后说明文字。
    /// </summary>
    internal static string? ExtractJsonObject(string content)
    {
        var start = content.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        // 从后往前找结尾大括号，兼容模型在 JSON 后面补充说明的情况。
        var end = content.LastIndexOf('}');
        if (end <= start)
        {
            return null;
        }

        return content[start..(end + 1)];
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

    private static string ReadString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Null => string.Empty,
        _ => element.ToString()
    };
}

/// <summary>模型解析出的调课意图（原科目可能为空，表示消息里没提）。</summary>
public sealed record AiParseOutcome(int Lesson, string OriginalSubject, string NewSubject);
