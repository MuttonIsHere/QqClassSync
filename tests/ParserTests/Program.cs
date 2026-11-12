using QqClassSync.Models;
using QqClassSync.Services;

namespace QqSync.Tests;

internal static class Program
{
    private static int _failed;

    private static int Main()
    {
        var parser = new TiaokeParser();

        // 调课用例：(合并后的文本, 期望节次, 期望原科目, 期望目标科目)
        Check(parser, "内卷背刺班 班主任王老师：#调课 第3节 语文改数学", 3, "语文", "数学");
        Check(parser, "群聊 李老师：#调课 第三节 数学改英语", 3, "数学", "英语");
        Check(parser, "群聊 李老师：#调课第12节 物理改体育", 12, "物理", "体育");
        Check(parser, "群聊 李老师：#调课 第5节 化学换成生物", 5, "化学", "生物");
        Check(parser, "群聊 李老师：#调课 第2节 历史 改 地理", 2, "历史", "地理");
        // 私聊形态：没有群名前缀
        Check(parser, "李云芳 #调课 第三节 数学改为语文", 3, "数学", "语文");

        // 撤销用例
        CheckUndo(parser, "群聊 李老师：#撤销调课", 0);
        CheckUndo(parser, "李云芳 #撤销调课 第3节", 3);
        CheckUndo(parser, "群聊 李老师：#取消调课", 0);

        // 失败用例：不能误判
        CheckFail(parser, "内卷背刺班 夏沐夕：今天作业好多");
        CheckFail(parser, "随便聊聊");

        // 指令识别：撤销指令不含「#调课」，也要能被识别
        CheckMentions(parser, "李云芳 #撤销调课", true);
        CheckMentions(parser, "李云芳 今天作业好多", false);

        Console.WriteLine();
        Console.WriteLine("=== 调课改动日志 ===");
        var journalFailures = JournalTests.Run();
        _failed += journalFailures;

        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? "全部用例通过" : $"有 {_failed} 个用例失败");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(TiaokeParser parser, string text, int lesson, string from, string to)
    {
        var result = parser.Parse(text, "#调课");
        var ok = result.Success
                 && result.Command!.Kind == TiaokeCommandKind.Apply
                 && result.Command.Lesson == lesson
                 && result.Command.FromSubject == from
                 && result.Command.ToSubject == to;
        Report(ok, text, result.Success ? result.Command!.ToString() : $"失败（{result.Reason}）");
    }

    private static void CheckUndo(TiaokeParser parser, string text, int expectedLesson)
    {
        var result = parser.Parse(text, "#调课");
        var ok = result.Success
                 && result.Command!.Kind == TiaokeCommandKind.Undo
                 && result.Command.Lesson == expectedLesson;
        Report(ok, text, result.Success ? result.Command!.ToString() : $"失败（{result.Reason}）");
    }

    private static void CheckFail(TiaokeParser parser, string text)
    {
        var result = parser.Parse(text, "#调课");
        Report(!result.Success, "应拒绝: " + text, result.Success ? "被误判为 " + result.Command : result.Reason);
    }

    private static void CheckMentions(TiaokeParser parser, string text, bool expected)
    {
        var actual = parser.MentionsCommand(text, "#调课");
        Report(actual == expected, "指令识别: " + text, actual.ToString());
    }

    private static void Report(bool ok, string text, string detail)
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {text} => {detail}");
        if (!ok)
        {
            _failed++;
        }
    }
}
