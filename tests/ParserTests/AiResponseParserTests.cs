using QqClassSync.Services;

namespace QqSync.Tests;

/// <summary>
/// 大模型返回内容解析的单元测试（不联网）。
/// 重点：模型胡说八道 / 带 Markdown 围栏 / 空对象 时都必须安全返回 null。
/// </summary>
internal static class AiResponseParserTests
{
    public static int Run()
    {
        var failed = 0;

        // 标准 JSON
        var outcome = AiResponseParser.TryParse("{\"Lesson\": 3, \"OriginalSubject\": \"语文\", \"NewSubject\": \"数学\"}", out _);
        failed += Check(outcome is { Lesson: 3, OriginalSubject: "语文", NewSubject: "数学" }, "标准 JSON 解析");

        // 带 Markdown 围栏 + 前后说明
        outcome = AiResponseParser.TryParse("好的，结果如下：\n```json\n{\"Lesson\": 5, \"OriginalSubject\": \"\", \"NewSubject\": \"体育\"}\n```\n", out _);
        failed += Check(outcome is { Lesson: 5, NewSubject: "体育", OriginalSubject: "" }, "带围栏与说明的 JSON");

        // 小写字段名
        outcome = AiResponseParser.TryParse("{\"lesson\": 2, \"newsubject\": \"英语\"}", out _);
        failed += Check(outcome is { Lesson: 2, NewSubject: "英语" }, "字段名大小写不敏感");

        // 数字字符串
        outcome = AiResponseParser.TryParse("{\"Lesson\": \"4\", \"NewSubject\": \"化学\"}", out _);
        failed += Check(outcome is { Lesson: 4 }, "节次是字符串数字");

        // 空对象 = 无调课意图（不算失败）
        outcome = AiResponseParser.TryParse("{}", out var reason1);
        failed += Check(outcome is null && reason1 == "no_intent", "空对象识别为「无调课意图」");

        // 各种异常输入都必须返回 null，不能抛
        foreach (var bad in new[] { "", "   ", "null", "我不知道", "{\"Lesson\": 3}", "{\"Lesson\": 99, \"NewSubject\": \"数学\"}", "{\"Lesson\": 0, \"NewSubject\": \"数学\"}", "{\"Lesson\": 3, \"NewSubject\": \"\"}", "{不是JSON}" })
        {
            try
            {
                var r = AiResponseParser.TryParse(bad, out _);
                failed += Check(r is null, $"异常输入应返回 null: \"{bad}\"");
            }
            catch (Exception)
            {
                failed += Check(false, $"异常输入不应抛异常: \"{bad}\"");
            }
        }

        // 提取响应体里的 content
        var body = "{\"choices\":[{\"message\":{\"content\":\"{\\\"Lesson\\\":1,\\\"NewSubject\\\":\\\"数学\\\"}\"}}]}";
        var content = AiResponseParser.ExtractMessageContent(body);
        failed += Check(content is not null && content.Contains("Lesson"), "从 chat/completions 响应提取 content");
        failed += Check(AiResponseParser.ExtractMessageContent("not json") is null, "非法响应体返回 null");
        failed += Check(AiResponseParser.ExtractMessageContent("{\"choices\":[]}") is null, "空 choices 返回 null");

        return failed;
    }

    private static int Check(bool ok, string message)
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {message}");
        return ok ? 0 : 1;
    }
}
