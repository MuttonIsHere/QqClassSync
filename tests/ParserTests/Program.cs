using QqClassSync.Services;

namespace QqSync.Tests;

internal static class Program
{
    private static int _failed;

    private static int Main()
    {
        var parser = new TiaokeParser();

        // 命中用例：(群名 发送者 内容) 合并后的文本, 期望节次, 期望原科目, 期望目标科目
        Check(parser, "内卷背刺班 班主任王老师：#调课 第3节 语文改数学", 3, "语文", "数学");
        Check(parser, "群聊 李老师：#调课 第三节 数学改英语", 3, "数学", "英语");
        Check(parser, "群聊 李老师：#调课第12节 物理改体育", 12, "物理", "体育");
        Check(parser, "群聊 李老师：#调课 第5节 化学换成生物", 5, "化学", "生物");
        Check(parser, "群聊 李老师：#调课 第2节 历史 改 地理", 2, "历史", "地理");

        // 失败用例：不能误判
        CheckFail(parser, "内卷背刺班 夏沐夕：今天作业好多");
        CheckFail(parser, "群聊 李老师：#调课 第3节 语文改数学 但是这是补充说明".Substring(0, 5) + "随便聊聊");

        Console.WriteLine(_failed == 0 ? "全部用例通过" : $"有 {_failed} 个用例失败");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(TiaokeParser parser, string text, int lesson, string from, string to)
    {
        var result = parser.Parse(text, "#调课");
        var ok = result.Success
                 && result.Command!.Lesson == lesson
                 && result.Command.FromSubject == from
                 && result.Command.ToSubject == to;
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {text} => "
                          + (result.Success ? result.Command!.ToString() : $"失败（{result.Reason}）"));
        if (!ok)
        {
            _failed++;
        }
    }

    private static void CheckFail(TiaokeParser parser, string text)
    {
        var result = parser.Parse(text, "#调课");
        var ok = !result.Success;
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] 应拒绝: {text} => {(ok ? result.Reason : "被误判为 " + result.Command)}");
        if (!ok)
        {
            _failed++;
        }
    }
}
