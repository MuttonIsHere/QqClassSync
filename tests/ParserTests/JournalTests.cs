using QqClassSync.Models;
using QqClassSync.Services;

namespace QqSync.Tests;

/// <summary>
/// 调课改动日志（单节课撤销的数据基础）的纯逻辑测试。
/// </summary>
internal static class JournalTests
{
    public static int Run()
    {
        var failed = 0;
        var dir = Path.Combine(AppContext.BaseDirectory, "journal-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "TempPatches.json");
        var today = new DateTime(2026, 11, 12);

        try
        {
            var journal = LessonUndoJournal.Load(path);
            var chinese = Guid.NewGuid();
            var math = Guid.NewGuid();
            var english = Guid.NewGuid();

            journal.Add(new LessonPatchRecord(today, 3, chinese, "语文", math, "数学", today.AddHours(8)));
            journal.Add(new LessonPatchRecord(today, 5, math, "数学", english, "英语", today.AddHours(9)));
            journal.Save();

            failed += Assert(journal.ForDate(today).Count == 2, "追加两条记录后应为 2 条");
            failed += Assert(journal.ForLesson(today, 3).Count == 1, "第 3 节应有 1 条记录");

            // 持久化：重新加载后记录还在
            var reloaded = LessonUndoJournal.Load(path);
            failed += Assert(reloaded.ForDate(today).Count == 2, "重新加载后记录应保留");

            // 栈式撤销：同节改两次，先撤最近一次
            reloaded.Add(new LessonPatchRecord(today, 3, math, "数学", english, "英语", today.AddHours(10)));
            reloaded.Save();
            var last = reloaded.TakeLast(today, 3);
            failed += Assert(last is not null && last.OriginalSubjectName == "数学", "TakeLast 应返回最近一条（原科目=数学）");
            var second = reloaded.TakeLast(today, 3);
            failed += Assert(second is not null && second.OriginalSubjectName == "语文", "再取一次应返回更早一条（原科目=语文）");
            var empty = reloaded.TakeLast(today, 3);
            failed += Assert(empty is null, "记录取完后应返回 null");

            // 清空某天
            reloaded.Add(new LessonPatchRecord(today, 7, chinese, "语文", math, "数学", today));
            reloaded.Clear(today);
            failed += Assert(reloaded.ForDate(today).Count == 0, "Clear 后当天记录应为 0");

            // 过期清理：只保留最近 7 天
            var old = today.AddDays(-10);
            reloaded.Add(new LessonPatchRecord(old, 1, chinese, "语文", math, "数学", old));
            reloaded.Prune(today);
            failed += Assert(reloaded.ForDate(old).Count == 0, "超过 7 天的记录应被清理");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        return failed;
    }

    private static int Assert(bool ok, string message)
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {message}");
        return ok ? 0 : 1;
    }
}
