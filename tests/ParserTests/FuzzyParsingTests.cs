using QqClassSync.Models;
using QqClassSync.Services;

namespace QqSync.Tests;

/// <summary>
/// 模糊识别与冲突兜底的单元测试：科目别名/老师/错别字、时段换算、批量冲突、AI 返回扩展字段。
/// 全部为纯函数测试，不联网、不依赖 ClassIsland 宿主。
/// </summary>
internal static class FuzzyParsingTests
{
    private static readonly Guid ChineseId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MathId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid MoralsId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ItId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ReadingId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid PeId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private static readonly IReadOnlyList<SubjectCandidate> Candidates =
    [
        new(ChineseId, "语文", "语", "王小明"),
        new(MathId, "数学", "数", "李华"),
        new(MoralsId, "道德与法治", "道", "张伟"),
        new(ItId, "信息技术", "信", ""),
        new(ReadingId, "语文阅读", "语", "赵敏"),
        new(PeId, "体育与健康", "体", "")
    ];

    public static int Run()
    {
        var failed = 0;
        failed += SubjectMatching();
        failed += RealSchoolAliases();
        failed += TimeResolution();
        failed += Validation();
        failed += AiOutcome();
        failed += RegexDayPart();
        return failed;
    }

    private static int SubjectMatching()
    {
        var failed = 0;
        Console.WriteLine("-- 科目匹配 --");

        failed += CheckUnique("数学", SubjectMatchKind.ExactName, MathId, "科目名精确匹配");
        failed += CheckUnique("数", SubjectMatchKind.ExactInitial, MathId, "简称匹配");
        failed += CheckUnique("道法", SubjectMatchKind.Alias, MoralsId, "同义说法「道法」");
        failed += CheckUnique("英文", SubjectMatchKind.Alias, ChineseId, "「英文」不应匹配到语文", expectFail: true);
        failed += CheckUnique("王老师", SubjectMatchKind.Teacher, ChineseId, "按老师姓名匹配");
        failed += CheckUnique("李华", SubjectMatchKind.Teacher, MathId, "按老师全名匹配");
        failed += CheckUnique("语交", SubjectMatchKind.Fuzzy, ChineseId, "错别字容忍");
        failed += CheckUnique("语文老师", SubjectMatchKind.ExactName, ChineseId, "「科目+老师」");
        failed += CheckUnique("数学提高", SubjectMatchKind.Contains, MathId, "包含关系");

        // 「语」是两个科目的简称 → 必须判歧义，不能默认取第一个。
        var ambiguous = SubjectMatcher.Resolve("语", Candidates);
        failed += Check(ambiguous.IsAmbiguous && ambiguous.Match is null, "共用简称应判歧义，不能猜");

        // 「阅读」→ 只匹配语文阅读。
        failed += CheckUnique("阅读", SubjectMatchKind.Contains, ReadingId, "包含关系（短名）");

        // 找不到的科目 → None，且给出原因。
        var missing = SubjectMatcher.Resolve("化学", Candidates);
        failed += Check(!missing.IsUnique && !missing.IsAmbiguous && missing.Detail.Contains("化学"), "不存在的科目应明确失败");

        // 地点说法 → 明确提示模型里没有地点字段。
        var location = SubjectMatcher.Resolve("操场", Candidates);
        failed += Check(!location.IsUnique && location.Detail.Contains("地点"), "地点说法应给出明确解释");

        // 空输入/空课表不抛异常。
        failed += Check(!SubjectMatcher.Resolve("", Candidates).IsUnique, "空输入不匹配");
        failed += Check(!SubjectMatcher.Resolve("语文", Array.Empty<SubjectCandidate>()).IsUnique, "空科目表不匹配");

        return failed;
    }

    /// <summary>用本机真实课表的科目命名（道法 / 综合实践活动 / 劳动教育 / 通用技术）验证同义说法。</summary>
    private static int RealSchoolAliases()
    {
        var failed = 0;
        Console.WriteLine("-- 真实课表科目命名 --");

        var candidates = new List<SubjectCandidate>
        {
            new(Guid.NewGuid(), "道法", "道", ""),
            new(Guid.NewGuid(), "综合实践活动", "综", ""),
            new(Guid.NewGuid(), "劳动教育", "劳", ""),
            new(Guid.NewGuid(), "通用技术", "技", "")
        };

        var morals = SubjectMatcher.Resolve("道德与法治", candidates);
        failed += Check(morals.IsUnique && morals.Match!.Name == "道法", "「道德与法治」应匹配本校的「道法」");

        var practice = SubjectMatcher.Resolve("综合实践", candidates);
        failed += Check(practice.IsUnique && practice.Match!.Name == "综合实践活动", "「综合实践」应匹配「综合实践活动」");

        // 本校同时有「劳动教育」和「通用技术」时，「劳技」无法唯一确定 → 必须判歧义。
        var labor = SubjectMatcher.Resolve("劳技", candidates);
        failed += Check(labor.IsAmbiguous, "「劳技」对应多科时应判歧义，不能猜");

        return failed;
    }

    private static int TimeResolution()
    {
        var failed = 0;
        Console.WriteLine("-- 时段换算 --");

        var slots = new List<LessonSlot>
        {
            new(1, TimeSpan.FromHours(8), TimeSpan.FromHours(8.7)),
            new(2, TimeSpan.FromHours(9), TimeSpan.FromHours(9.7)),
            new(3, TimeSpan.FromHours(10), TimeSpan.FromHours(10.7)),
            new(4, TimeSpan.FromHours(11), TimeSpan.FromHours(11.7)),
            new(5, TimeSpan.FromHours(13.5), TimeSpan.FromHours(14.2)),
            new(6, TimeSpan.FromHours(14.5), TimeSpan.FromHours(15.2)),
            new(7, TimeSpan.FromHours(15.5), TimeSpan.FromHours(16.2)),
            new(8, TimeSpan.FromHours(16.5), TimeSpan.FromHours(17.2))
        };

        failed += Check(LessonTimeResolver.Resolve(1, "下午", slots) is { Success: true, Lesson: 5 }, "下午第1节 → 全天第5节");
        failed += Check(LessonTimeResolver.Resolve(2, "下午", slots) is { Success: true, Lesson: 6 }, "下午第2节 → 全天第6节");
        failed += Check(LessonTimeResolver.Resolve(2, "上午", slots) is { Success: true, Lesson: 2 }, "上午第2节 → 全天第2节");
        failed += Check(LessonTimeResolver.Resolve(3, "", slots) is { Success: true, Lesson: 3 }, "无时段时保持原节次");
        failed += Check(!LessonTimeResolver.Resolve(5, "下午", slots).Success, "下午没有第5节时应失败");
        failed += Check(LessonTimeResolver.Resolve(1, "中午", slots) is { Success: true, Lesson: 5 }, "中午第1节 → 全天第5节（13:30 属中午时段）");
        failed += Check(!LessonTimeResolver.Resolve(2, "下午", Array.Empty<LessonSlot>()).Success, "没有时间表时应失败");

        return failed;
    }

    private static int Validation()
    {
        var failed = 0;
        Console.WriteLine("-- 冲突校验 --");

        var plan = new PlanSnapshot(8,
        [
            new(1, "语文"), new(2, "数学"), new(3, "英语"), new(4, "物理"),
            new(5, "化学"), new(6, "生物"), new(7, "历史"), new(8, "地理")
        ]);

        var conflict = CommandValidator.Validate(
        [
            TiaokeCommand.Apply(3, "英语", "数学", ""),
            TiaokeCommand.Apply(3, "英语", "物理", "")
        ], plan);
        failed += Check(conflict.Any(i => i.Level == CommandIssueLevel.Block), "同一节改成两个科目应阻断");

        var swapConflict = CommandValidator.Validate(
        [
            TiaokeCommand.Apply(3, "英语", "数学", ""),
            TiaokeCommand.Swap(3, 5, "")
        ], plan);
        failed += Check(swapConflict.Any(i => i.Level == CommandIssueLevel.Block), "同一节既调课又互换应阻断");

        var crossSwap = CommandValidator.Validate(
        [
            TiaokeCommand.Swap(3, 5, ""),
            TiaokeCommand.Swap(5, 7, "")
        ], plan);
        failed += Check(crossSwap.Any(i => i.Level == CommandIssueLevel.Block), "互换交叉应阻断");

        var reverseDuplicate = CommandValidator.Validate(
        [
            TiaokeCommand.Swap(3, 5, ""),
            TiaokeCommand.Swap(5, 3, "")
        ], plan);
        failed += Check(reverseDuplicate.All(i => i.Level != CommandIssueLevel.Block), "反向重复的同一对互换应合并，不阻断");

        var outOfRange = CommandValidator.Validate([TiaokeCommand.Apply(99, "", "数学", "")], plan);
        failed += Check(outOfRange.Any(i => i.Level == CommandIssueLevel.Block), "越界应阻断");

        var duplicate = CommandValidator.Validate(
        [
            TiaokeCommand.Apply(3, "英语", "数学", ""),
            TiaokeCommand.Apply(3, "英语", "数学", "")
        ], plan);
        failed += Check(
            duplicate.All(i => i.Level != CommandIssueLevel.Block)
            && duplicate.Any(i => i.Level == CommandIssueLevel.Warning),
            "重复等价指令只提示、不阻断");

        return failed;
    }

    private static int AiOutcome()
    {
        var failed = 0;
        Console.WriteLine("-- AI 返回扩展字段 --");

        var list = AiResponseParser.TryParseList(
            "[{\"Period\":\"下午第二节\",\"Subject\":\"语交\",\"Confidence\":0.6,\"Ambiguity\":\"疑似错别字\"}]",
            out _);
        failed += Check(
            list is { Count: 1 }
            && list[0].Lesson == 2
            && list[0].NewSubject == "语交"
            && list[0].LessonText.Contains("下午")
            && Math.Abs(list[0].Confidence - 0.6) < 0.001
            && list[0].Ambiguity == "疑似错别字",
            "字段别名 + 置信度 + 歧义说明");

        list = AiResponseParser.TryParseList("[{\"Lesson\":3,\"To\":\"数学\"}]", out _);
        failed += Check(list is { Count: 1 } && list[0].NewSubject == "数学", "To 作为 NewSubject 别名");

        list = AiResponseParser.TryParseList("[{\"Lesson\":3,\"NewSubject\":\"数学\",\"Confidence\":\"0.9\"}]", out _);
        failed += Check(list is { Count: 1 } && Math.Abs(list[0].Confidence - 0.9) < 0.001, "字符串置信度");

        list = AiResponseParser.TryParseList("[{\"LessonText\":\"下午第二节\",\"NewSubject\":\"数学\"}]", out _);
        failed += Check(list is { Count: 1 } && list[0].Lesson == 2, "只有 LessonText 时提取节次");

        list = AiResponseParser.TryParseList("[{\"Lesson\":3,\"NewSubject\":\"数学\",\"Confidence\":9}]", out _);
        failed += Check(list is { Count: 1 } && Math.Abs(list[0].Confidence - 1) < 0.001, "置信度夹到 0~1");

        list = AiResponseParser.TryParseList("[{\"Lesson\":3,\"NewSubject\":\"数学\"}]", out _);
        failed += Check(list is { Count: 1 } && Math.Abs(list[0].Confidence - 1) < 0.001, "缺省置信度为 1");

        list = AiResponseParser.TryParseList("[{\"Lesson\":0,\"LessonText\":\"下午第二节\",\"NewSubject\":\"数学\"}]", out _);
        failed += Check(list is { Count: 1 } && list[0].Lesson == 2, "Lesson=0 时从 LessonText 兜底取节次");

        list = AiResponseParser.TryParseList("[{\"Lesson\":\"下午第二节\",\"NewSubject\":\"数学\"}]", out _);
        failed += Check(
            list is { Count: 1 }
            && list[0].Lesson == 2
            && list[0].LessonText.Contains("下午"),
            "Lesson 字段写整句时也能取节次并保留时段");

        list = AiResponseParser.TryParseList("[{\"LessonText\":\"14:30\",\"NewSubject\":\"数学\"}]", out _);
        failed += Check(list is null, "「14:30」不应被当成第 14 节");

        return failed;
    }

    private static int RegexDayPart()
    {
        var failed = 0;
        Console.WriteLine("-- 正则时段 --");

        var parser = new TiaokeParser();

        var single = parser.Parse("群聊 李老师：#调课 下午第2节 语文改数学", "#调课");
        failed += Check(
            single.Success && single.Commands.Count == 1
            && single.Commands[0].Lesson == 2
            && single.Commands[0].DayPart == "下午",
            "正则支持「下午第2节」");

        var pair = parser.Parse("群聊 李老师：#调课 上午第1节和第2节都换成自习", "#调课");
        failed += Check(
            pair.Success && pair.Commands.Count == 2
            && pair.Commands.All(c => c.DayPart == "上午"),
            "「上午第1节和第2节」两节都带时段");

        var swap = parser.Parse("群聊 李老师：#调课 上午第3节和下午第5节互换", "#调课");
        failed += Check(!swap.Success && swap.Reason.Contains("互换暂不支持"), "带时段的互换应明确拒绝");

        return failed;
    }

    private static int CheckUnique(
        string raw,
        SubjectMatchKind kind,
        Guid expectedId,
        string message,
        bool expectFail = false)
    {
        var result = SubjectMatcher.Resolve(raw, Candidates);
        var ok = expectFail
            ? !result.IsUnique
            : result.IsUnique && result.Match!.Id == expectedId && result.Kind == kind;
        if (!ok)
        {
            Console.WriteLine($"[FAIL] {message}：实际 Kind={result.Kind}，Match={(result.Match?.Name ?? "无")}，候选={string.Join("/", result.Candidates.Select(c => c.Name))}");
            return 1;
        }

        Console.WriteLine($"[PASS] {message}");
        return 0;
    }

    private static int Check(bool ok, string message)
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {message}");
        return ok ? 0 : 1;
    }
}
