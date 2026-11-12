using QqClassSync.Services;

namespace QqSync.Tests;

/// <summary>
/// 大模型返回内容解析（批量数组）的单元测试（不联网）。
/// </summary>
internal static class AiResponseParserTests
{
    public static int Run()
    {
        var failed = 0;

        // 批量数组
        var list = AiResponseParser.TryParseList(
            "[{\"Lesson\":3,\"OriginalSubject\":\"语文\",\"NewSubject\":\"数学\"},{\"Lesson\":5,\"OriginalSubject\":\"英语\",\"NewSubject\":\"物理\"}]",
            out _);
        failed += Check(list is { Count: 2 }
                        && list[0].Lesson == 3 && list[0].NewSubject == "数学"
                        && list[1].Lesson == 5 && list[1].NewSubject == "物理",
            "标准数组解析");

        // 带 Markdown 围栏 + 前后说明
        list = AiResponseParser.TryParseList(
            "好的：\n```json\n[{\"Lesson\":1,\"NewSubject\":\"自习\"},{\"Lesson\":2,\"NewSubject\":\"自习\"}]\n```",
            out _);
        failed += Check(list is { Count: 2 } && list![0].Lesson == 1, "带围栏的数组");

        // 互换
        list = AiResponseParser.TryParseList("[{\"Lesson\":3,\"SwapWith\":5}]", out _);
        failed += Check(list is { Count: 1 } && list![0].SwapWith == 5, "互换项解析");

        // 对象包一层
        list = AiResponseParser.TryParseList("{\"commands\":[{\"Lesson\":4,\"NewSubject\":\"美术\"}]}", out _);
        failed += Check(list is { Count: 1 } && list![0].Lesson == 4, "commands 包裹数组");

        // 单个对象也容忍
        list = AiResponseParser.TryParseList("{\"Lesson\":2,\"NewSubject\":\"英语\"}", out _);
        failed += Check(list is { Count: 1 }, "单个对象容忍");

        // 空数组 = 无调课意图
        list = AiResponseParser.TryParseList("[]", out var reason1);
        failed += Check(list is { Count: 0 } && reason1 == "no_intent", "空数组识别为无调课意图");

        // 异常输入 → null，不抛异常
        foreach (var bad in new[] { "", "   ", "null", "我不知道", "[不是JSON]", "[{\"Lesson\":99,\"NewSubject\":\"数学\"}]", "[{\"NewSubject\":\"数学\"}]" })
        {
            try
            {
                var r = AiResponseParser.TryParseList(bad, out _);
                failed += Check(r is null, $"异常输入应返回 null: \"{bad}\"");
            }
            catch (Exception)
            {
                failed += Check(false, $"异常输入不应抛异常: \"{bad}\"");
            }
        }

        // 响应体 content 提取
        var body = "{\"choices\":[{\"message\":{\"content\":\"[{\\\"Lesson\\\":1,\\\"NewSubject\\\":\\\"数学\\\"}]\"}}]}";
        var content = AiResponseParser.ExtractMessageContent(body);
        failed += Check(content is not null && content.Contains("Lesson"), "从 chat/completions 响应提取 content");
        failed += Check(AiResponseParser.ExtractMessageContent("not json") is null, "非法响应体返回 null");

        return failed;
    }

    private static int Check(bool ok, string message)
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {message}");
        return ok ? 0 : 1;
    }
}
