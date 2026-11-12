using System.Text.Json;

namespace QqClassSync.Services;

/// <summary>
/// 解析大模型返回的 JSON（批量数组）。抽成纯函数便于单元测试，也不在这里做任何日志（避免泄露内容）。
/// </summary>
public static class AiResponseParser
{
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
        if (!TryGetProperty(element, "Lesson", out var lessonElement)
            || !TryReadInt(lessonElement, out var lesson)
            || lesson is < 1 or > 20)
        {
            return null;
        }

        var swapWith = 0;
        if (TryGetProperty(element, "SwapWith", out var swapElement) && TryReadInt(swapElement, out var swap))
        {
            if (swap is >= 1 and <= 20 && swap != lesson)
            {
                swapWith = swap;
            }
        }

        var newSubject = TryGetProperty(element, "NewSubject", out var newElement)
            ? ReadString(newElement).Trim()
            : string.Empty;

        if (swapWith == 0 && string.IsNullOrWhiteSpace(newSubject))
        {
            return null;
        }

        var originalSubject = TryGetProperty(element, "OriginalSubject", out var originalElement)
            ? ReadString(originalElement).Trim()
            : string.Empty;

        return new AiParseOutcome(lesson, originalSubject, newSubject, swapWith);
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

/// <summary>模型解析出的一条调课意图（原科目可能为空；SwapWith &gt; 0 表示互换）。</summary>
public sealed record AiParseOutcome(int Lesson, string OriginalSubject, string NewSubject, int SwapWith = 0);
