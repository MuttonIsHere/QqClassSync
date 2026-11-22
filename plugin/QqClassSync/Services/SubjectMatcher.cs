using System.Text;

namespace QqClassSync.Services;

/// <summary>
/// 科目候选（从 ClassIsland 档案里抽出来的最小信息，不依赖宿主类型，方便单测）。
/// </summary>
public sealed record SubjectCandidate(Guid Id, string Name, string Initial = "", string TeacherName = "");

/// <summary>科目匹配方式，按可信度从高到低排列。</summary>
public enum SubjectMatchKind
{
    /// <summary>没匹配上。</summary>
    None,

    /// <summary>科目名完全相同（忽略全半角/空白）。</summary>
    ExactName,

    /// <summary>简称相同（如「语」「数」）。</summary>
    ExactInitial,

    /// <summary>常见同义说法（如「道法」→ 道德与法治）。</summary>
    Alias,

    /// <summary>按任课老师匹配（如「王老师」→ 王老师任教的那一科）。</summary>
    Teacher,

    /// <summary>包含关系（如「语文阅读」→「语文」）。</summary>
    Contains,

    /// <summary>错别字容忍（编辑距离）。</summary>
    Fuzzy
}

/// <summary>
/// 一次匹配的结果。
/// <para><see cref="Match"/> 非空且 <see cref="Candidates"/> 为空 → 唯一匹配，可以执行。</para>
/// <para><see cref="Candidates"/> 有 2 个及以上 → 存在歧义，调用方不得猜，必须让老师补充更精确的说法。</para>
/// </summary>
public sealed record SubjectMatchResult(
    SubjectMatchKind Kind,
    SubjectCandidate? Match,
    IReadOnlyList<SubjectCandidate> Candidates,
    string Detail)
{
    public bool IsUnique => Match is not null && Candidates.Count == 0;

    public bool IsAmbiguous => Candidates.Count > 1;

    public static SubjectMatchResult Unique(SubjectMatchKind kind, SubjectCandidate match, string detail) =>
        new(kind, match, Array.Empty<SubjectCandidate>(), detail);

    public static SubjectMatchResult Ambiguous(SubjectMatchKind kind, IReadOnlyList<SubjectCandidate> candidates, string detail) =>
        new(kind, null, candidates, detail);

    public static SubjectMatchResult None(string detail) =>
        new(SubjectMatchKind.None, null, Array.Empty<SubjectCandidate>(), detail);
}

/// <summary>
/// 科目名匹配：把老师随手写的说法（简称、同义、错别字、老师姓名）落到课表里真实的科目上。
///
/// 设计红线：
///   - 只做确定性匹配，不猜；
///   - 同分/多候选一律返回歧义，由上层提示老师补充，绝不默认取第一个；
///   - 纯函数、无 IO、无宿主依赖，便于单测覆盖边界。
/// </summary>
public static class SubjectMatcher
{
    /// <summary>
    /// 常见同义说法 → 课表里可能使用的正式科目名（同一个说法可指向多个正式名，命中哪个取决于本校课表）。
    /// 只收录跨学校通用的说法，学校自建的别名由「科目简称」字段承担。
    /// </summary>
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.Ordinal)
    {
        ["语文"] = ["语文", "语文课"],
        ["中文"] = ["语文"],
        ["汉语"] = ["语文"],
        ["语"] = ["语文"],
        ["数学"] = ["数学"],
        ["数"] = ["数学"],
        ["英语"] = ["英语", "外语"],
        ["英文"] = ["英语"],
        ["外语"] = ["英语", "外语"],
        ["英"] = ["英语"],
        ["物理"] = ["物理"],
        ["物"] = ["物理"],
        ["化学"] = ["化学"],
        ["化"] = ["化学"],
        ["生物"] = ["生物"],
        ["生"] = ["生物"],
        ["历史"] = ["历史"],
        ["史"] = ["历史"],
        ["地理"] = ["地理"],
        ["地"] = ["地理"],
        ["政治"] = ["政治", "道德与法治", "思想政治", "思想品德", "道法"],
        ["道法"] = ["道法", "道德与法治", "政治", "思想政治", "思想品德"],
        ["道德与法治"] = ["道德与法治", "道法", "政治", "思想品德"],
        ["思想品德"] = ["思想品德", "政治", "道法", "道德与法治"],
        ["思政"] = ["思想政治", "政治", "道法", "道德与法治"],
        ["体育"] = ["体育", "体育与健康"],
        ["体育与健康"] = ["体育与健康", "体育"],
        ["体"] = ["体育", "体育与健康"],
        ["音乐"] = ["音乐"],
        ["音"] = ["音乐"],
        ["美术"] = ["美术"],
        ["美"] = ["美术"],
        ["信息技术"] = ["信息技术", "信息", "计算机"],
        ["信息"] = ["信息技术", "信息"],
        ["信技"] = ["信息技术"],
        ["电脑"] = ["信息技术", "计算机"],
        ["计算机"] = ["信息技术", "计算机"],
        ["科学"] = ["科学"],
        ["自然"] = ["科学", "自然"],
        ["自习"] = ["自习", "自修"],
        ["自修"] = ["自习", "自修"],
        ["班会"] = ["班会"],
        ["劳动"] = ["劳动", "劳动教育", "劳动技术"],
        ["劳动技术"] = ["劳动技术", "劳动教育", "通用技术"],
        ["劳技"] = ["劳动技术", "劳动教育", "通用技术", "劳动"],
        ["通用技术"] = ["通用技术"],
        ["心理健康"] = ["心理健康", "心理"],
        ["心理"] = ["心理健康", "心理"],
        ["综合"] = ["综合实践", "综合实践活动"],
        ["综合实践"] = ["综合实践", "综合实践活动"]
    };

    /// <summary>地点类说法：ClassIsland 的课表模型没有地点字段，命中后要给出明确解释，而不是当成科目。</summary>
    private static readonly string[] LocationTokens =
    [
        "操场", "实验室", "机房", "体育馆", "会议室", "报告厅", "录播室",
        "舞蹈房", "音乐教室", "美术教室", "教室", "户外", "走廊"
    ];

    public static SubjectMatchResult Resolve(string? raw, IReadOnlyList<SubjectCandidate> candidates)
    {
        var text = Normalize(raw);
        if (text.Length == 0)
        {
            return SubjectMatchResult.None("目标科目为空");
        }

        var usable = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .ToList();
        if (usable.Count == 0)
        {
            return SubjectMatchResult.None("课表里还没有任何科目");
        }

        // 1) 科目名精确匹配。
        var hits = usable.Where(c => Eq(Normalize(c.Name), text)).ToList();
        if (hits.Count == 1)
        {
            return SubjectMatchResult.Unique(SubjectMatchKind.ExactName, hits[0], "科目名精确匹配");
        }

        if (hits.Count > 1)
        {
            return SubjectMatchResult.Ambiguous(SubjectMatchKind.ExactName, hits, "有多个同名科目");
        }

        // 2) 简称精确匹配。
        hits = usable
            .Where(c => !string.IsNullOrWhiteSpace(c.Initial) && Eq(Normalize(c.Initial), text))
            .ToList();
        if (hits.Count == 1)
        {
            return SubjectMatchResult.Unique(SubjectMatchKind.ExactInitial, hits[0], "科目简称匹配");
        }

        if (hits.Count > 1)
        {
            return SubjectMatchResult.Ambiguous(SubjectMatchKind.ExactInitial, hits, "有多个科目共用同一个简称");
        }

        // 3) 常见同义说法（「道法」「英文」「电脑」等）。
        if (Aliases.TryGetValue(text, out var aliases))
        {
            hits = usable
                .Where(c => aliases.Any(alias => Eq(Normalize(c.Name), alias)))
                .ToList();
            if (hits.Count == 1)
            {
                return SubjectMatchResult.Unique(SubjectMatchKind.Alias, hits[0], $"同义说法「{raw!.Trim()}」");
            }

            if (hits.Count > 1)
            {
                return SubjectMatchResult.Ambiguous(SubjectMatchKind.Alias, hits, $"「{raw!.Trim()}」可能指多个科目");
            }

            // 同义表里没有对应科目：直接给出明确原因。
            // 不能再走「错别字容忍」——「英文」和「语文」、「化学」和「数学」编辑距离都只有 1，
            // 继续模糊匹配会把科目改错。
            return SubjectMatchResult.None(
                $"「{raw!.Trim()}」对应的科目（{string.Join("、", aliases)}）不在课表里");
        }

        // 4) 去掉「老师/教师」后缀再试一次科目名/简称/别名（「语文老师」→ 语文）。
        var stripped = StripTeacherSuffix(text);
        if (!Eq(stripped, text))
        {
            hits = usable.Where(c => Eq(Normalize(c.Name), stripped)).ToList();
            if (hits.Count == 1)
            {
                return SubjectMatchResult.Unique(SubjectMatchKind.ExactName, hits[0], "「科目+老师」说法");
            }

            if (hits.Count > 1)
            {
                return SubjectMatchResult.Ambiguous(SubjectMatchKind.ExactName, hits, "有多个同名科目");
            }

            if (Aliases.TryGetValue(stripped, out var strippedAliases))
            {
                hits = usable
                    .Where(c => strippedAliases.Any(alias => Eq(Normalize(c.Name), alias)))
                    .ToList();
                if (hits.Count == 1)
                {
                    return SubjectMatchResult.Unique(SubjectMatchKind.Alias, hits[0], $"同义说法「{raw!.Trim()}」");
                }

                if (hits.Count > 1)
                {
                    return SubjectMatchResult.Ambiguous(SubjectMatchKind.Alias, hits, $"「{raw!.Trim()}」可能指多个科目");
                }
            }
        }

        // 5) 老师姓名匹配：全名（李华）、姓氏+老师（王老师 → 王）、单姓（王）。
        hits = usable.Where(c => TeacherMatches(c.TeacherName, stripped)).ToList();
        if (hits.Count == 1)
        {
            return SubjectMatchResult.Unique(SubjectMatchKind.Teacher, hits[0], $"按任课老师「{stripped}」匹配");
        }

        if (hits.Count > 1)
        {
            return SubjectMatchResult.Ambiguous(SubjectMatchKind.Teacher, hits, $"「{stripped}」是多个科目的任课老师");
        }

        // 6) 包含关系：「语文阅读」→「语文」；「语文」→「语文阅读」时若只有它一个也算。
        var contains = usable
            .Where(c =>
            {
                var name = Normalize(c.Name);
                return name.Length >= 2
                       && (text.Contains(name, StringComparison.Ordinal)
                           || (text.Length >= 2 && name.Contains(text, StringComparison.Ordinal)));
            })
            .ToList();
        if (contains.Count == 1)
        {
            return SubjectMatchResult.Unique(SubjectMatchKind.Contains, contains[0], "包含关系匹配");
        }

        if (contains.Count > 1)
        {
            return SubjectMatchResult.Ambiguous(SubjectMatchKind.Contains, contains, $"「{raw!.Trim()}」匹配到多个科目");
        }

        // 7) 错别字容忍：编辑距离。
        //    约束（防止把科目改错）：
        //      - 至少 2 个字（单字缩写交给「简称」处理，不做模糊）；
        //      - 首字必须相同（「语交」→语文可以，「英文」→语文不行）；
        //      - 长度差不超过 1。
        if (text.Length >= 2)
        {
            var maxDistance = text.Length <= 4 ? 1 : 2;
            var scored = usable
                .Where(c =>
                {
                    var name = Normalize(c.Name);
                    return name.Length >= 2
                           && name[0] == text[0]
                           && Math.Abs(name.Length - text.Length) <= 1;
                })
                .Select(c => (Candidate: c, Distance: Levenshtein(Normalize(c.Name), text)))
                .Where(x => x.Distance <= maxDistance)
                .ToList();

            if (scored.Count > 0)
            {
                var best = scored.Min(x => x.Distance);
                var bestHits = scored.Where(x => x.Distance == best).Select(x => x.Candidate).ToList();
                if (bestHits.Count == 1)
                {
                    return SubjectMatchResult.Unique(SubjectMatchKind.Fuzzy, bestHits[0], "疑似错别字，按最接近的科目匹配");
                }

                return SubjectMatchResult.Ambiguous(SubjectMatchKind.Fuzzy, bestHits, $"「{raw!.Trim()}」有多个相近科目");
            }
        }

        // 8) 地点类说法：模型里没有地点字段，给明确解释。
        if (LocationTokens.Any(token => text.Contains(token, StringComparison.Ordinal)))
        {
            return SubjectMatchResult.None("课表模型没有地点字段，无法只改地点");
        }

        return SubjectMatchResult.None($"课表里找不到科目「{raw!.Trim()}」");
    }

    /// <summary>去掉全半角差异、空白和「课/课程」后缀，便于比较。</summary>
    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (!char.IsWhiteSpace(ch) && ch != '\u3000')
            {
                builder.Append(ch);
            }
        }

        var text = builder.ToString();
        if (text.EndsWith("课程", StringComparison.Ordinal) && text.Length > 2)
        {
            text = text[..^2];
        }
        else if (text.EndsWith('课') && text.Length > 1)
        {
            text = text[..^1];
        }

        return text;
    }

    private static string StripTeacherSuffix(string text)
    {
        foreach (var suffix in new[] { "老师", "教师" })
        {
            if (text.EndsWith(suffix, StringComparison.Ordinal) && text.Length > suffix.Length)
            {
                return text[..^suffix.Length];
            }
        }

        return text;
    }

    private static bool TeacherMatches(string? teacherName, string text)
    {
        var teacher = Normalize(teacherName);
        if (teacher.Length == 0 || text.Length == 0)
        {
            return false;
        }

        if (Eq(teacher, text))
        {
            return true;
        }

        // 「王」这类姓氏：只允许 1-2 个字的输入做前缀匹配，避免「王小明老师」这种全名被当成前缀。
        return text.Length <= 2 && teacher.StartsWith(text, StringComparison.Ordinal);
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>标准编辑距离（插入/删除/替换代价都为 1）。</summary>
    internal static int Levenshtein(string a, string b)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
