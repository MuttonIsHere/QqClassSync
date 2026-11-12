using QqClassSync.Models;
using QqClassSync.Services;

namespace QqSync.Tests;

internal static class Program
{
    private static int _failed;

    private static int Main()
    {
        var parser = new TiaokeParser();

        Console.WriteLine("=== 单条调课 ===");
        Check(parser, "内卷背刺班 班主任王老师：#调课 第3节 语文改数学", (3, "语文", "数学"));
        Check(parser, "群聊 李老师：#调课 第三节 数学改英语", (3, "数学", "英语"));
        Check(parser, "群聊 李老师：#调课第12节 物理改体育", (12, "物理", "体育"));
        Check(parser, "群聊 李老师：#调课 第5节 化学换成生物", (5, "化学", "生物"));
        Check(parser, "群聊 李老师：#调课 第2节 历史 改 地理", (2, "历史", "地理"));
        Check(parser, "李云芳 #调课 第三节 数学改为语文", (3, "数学", "语文"));
        CheckNoFrom(parser, "群聊 李老师：#调课 第3节 换成自习", (3, "自习"));

        Console.WriteLine();
        Console.WriteLine("=== 批量调课 ===");
        CheckBatch(parser, "群聊 李老师：#调课 第3节 语文改数学，第5节 英语改物理",
            (3, "数学"), (5, "物理"));
        CheckBatch(parser, "群聊 李老师：#调课 第1节和第2节都换成自习",
            (1, "自习"), (2, "自习"));
        CheckBatch(parser, "群聊 李老师：#调课 第1节语文改数学；第2节数学改英语；第4节音乐改美术",
            (1, "数学"), (2, "英语"), (4, "美术"));

        Console.WriteLine();
        Console.WriteLine("=== 互换 ===");
        CheckSwap(parser, "群聊 李老师：#调课 第3节和第5节互换", 3, 5);

        Console.WriteLine();
        Console.WriteLine("=== 撤销 ===");
        CheckUndo(parser, "群聊 李老师：#撤销调课", 0);
        CheckUndo(parser, "李云芳 #撤销调课 第3节", 3);
        CheckUndoBatch(parser, "群聊 李老师：#撤销调课 第3,5节", 3, 5);
        CheckUndoBatch(parser, "群聊 李老师：#撤销调课 第3节和第5节", 3, 5);

        Console.WriteLine();
        Console.WriteLine("=== 拒绝误判 ===");
        CheckFail(parser, "内卷背刺班 夏沐夕：今天作业好多");
        CheckFail(parser, "随便聊聊");

        Console.WriteLine();
        Console.WriteLine("=== 指令识别 ===");
        CheckMentions(parser, "李云芳 #撤销调课", true);
        CheckMentions(parser, "李云芳 今天作业好多", false);

        Console.WriteLine();
        Console.WriteLine("=== 调课改动日志 ===");
        _failed += JournalTests.Run();

        Console.WriteLine();
        Console.WriteLine("=== AI 解析配置判定 ===");
        _failed += AiParsePolicyTests.Run();

        Console.WriteLine();
        Console.WriteLine("=== AI 返回内容解析 ===");
        _failed += AiResponseParserTests.Run();

        Console.WriteLine();
        Console.WriteLine("=== AI HTTP 调用（本地假服务器） ===");
        _failed += AiHttpIntegrationTests.Run();

        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? "全部用例通过" : $"有 {_failed} 个用例失败");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(TiaokeParser parser, string text, (int Lesson, string From, string To) expected)
    {
        var result = parser.Parse(text, "#调课");
        var ok = result.Success
                 && result.Commands.Count == 1
                 && result.Commands[0].Kind == TiaokeCommandKind.Apply
                 && result.Commands[0].Lesson == expected.Lesson
                 && result.Commands[0].FromSubject == expected.From
                 && result.Commands[0].ToSubject == expected.To;
        Report(ok, text, Dump(result));
    }

    private static void CheckNoFrom(TiaokeParser parser, string text, (int Lesson, string To) expected)
    {
        var result = parser.Parse(text, "#调课");
        var ok = result.Success
                 && result.Commands.Count == 1
                 && result.Commands[0].Lesson == expected.Lesson
                 && result.Commands[0].FromSubject == string.Empty
                 && result.Commands[0].ToSubject == expected.To;
        Report(ok, text, Dump(result));
    }

    private static void CheckBatch(TiaokeParser parser, string text, params (int Lesson, string To)[] expected)
    {
        var result = parser.Parse(text, "#调课");
        var ok = result.Success && result.Commands.Count == expected.Length;
        if (ok)
        {
            for (var i = 0; i < expected.Length; i++)
            {
                if (result.Commands[i].Kind != TiaokeCommandKind.Apply
                    || result.Commands[i].Lesson != expected[i].Lesson
                    || result.Commands[i].ToSubject != expected[i].To)
                {
                    ok = false;
                    break;
                }
            }
        }

        Report(ok, text, Dump(result));
    }

    private static void CheckSwap(TiaokeParser parser, string text, int a, int b)
    {
        var result = parser.Parse(text, "#调课");
        var ok = result.Success
                 && result.Commands.Count == 1
                 && result.Commands[0].Kind == TiaokeCommandKind.Swap
                 && result.Commands[0].Lesson == a
                 && result.Commands[0].SwapWith == b;
        Report(ok, text, Dump(result));
    }

    private static void CheckUndo(TiaokeParser parser, string text, int expectedLesson)
    {
        var result = parser.Parse(text, "#调课");
        var ok = result.Success
                 && result.Commands.Count == 1
                 && result.Commands[0].Kind == TiaokeCommandKind.Undo
                 && result.Commands[0].Lesson == expectedLesson;
        Report(ok, text, Dump(result));
    }

    private static void CheckUndoBatch(TiaokeParser parser, string text, params int[] lessons)
    {
        var result = parser.Parse(text, "#调课");
        var ok = result.Success
                 && result.Commands.Count == lessons.Length
                 && result.Commands.All(c => c.Kind == TiaokeCommandKind.Undo)
                 && result.Commands.Select(c => c.Lesson).OrderBy(x => x).SequenceEqual(lessons.OrderBy(x => x));
        Report(ok, text, Dump(result));
    }

    private static void CheckFail(TiaokeParser parser, string text)
    {
        var result = parser.Parse(text, "#调课");
        Report(!result.Success, "应拒绝: " + text, result.Success ? "被误判为 " + Dump(result) : result.Reason);
    }

    private static void CheckMentions(TiaokeParser parser, string text, bool expected)
    {
        var actual = parser.MentionsCommand(text, "#调课");
        Report(actual == expected, "指令识别: " + text, actual.ToString());
    }

    private static string Dump(TiaokeParseResult result) =>
        result.Success ? string.Join("；", result.Commands.Select(c => c.ToString())) : $"失败（{result.Reason}）";

    private static void Report(bool ok, string text, string detail)
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {text} => {detail}");
        if (!ok)
        {
            _failed++;
        }
    }
}
