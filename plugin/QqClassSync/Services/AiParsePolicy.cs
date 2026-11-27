namespace QqClassSync.Services;

/// <summary>
/// AI 解析的「是否可用」判定与配置规整。抽成纯函数是为了能脱离插件宿主做单元测试，
/// 也避免各处重复写「开关 + Key + BaseUrl + Model」的四重判断。
///
/// 红线：
///   - 配置不完整时只返回 false，调用方必须静默回退到正则解析，不允许抛异常；
///   - 空值就是空值：Base URL / 模型名称留空时**不再回填默认值**，由用户决定接哪个厂商。
/// </summary>
public static class AiParsePolicy
{
    /// <summary>仅用于界面提示与旧配置迁移，不再作为空值的兜底。</summary>
    public const string DefaultBaseUrl = "https://api.deepseek.com/v1";

    /// <summary>旧版本没有模型字段时的迁移值（只在 Base URL 指向 DeepSeek 时使用）。</summary>
    public const string LegacyDeepSeekModel = "deepseek-chat";

    /// <summary>
    /// 便宜的「意图预筛」：只有像调课的消息才值得送进大模型，避免把整群闲聊都发出去。
    /// 命中任意一个词才继续；宁可漏判（回退正则/忽略），也不要滥发。
    /// </summary>
    private static readonly string[] IntentHints =
    [
        "调课", "换课", "代课", "改课", "调到", "换到", "换成", "改成",
        "不上", "来上", "来代", "来教", "顶课", "代上",
        "调一下", "换一下", "这节课", "上一节"
    ];

    /// <summary>这条消息看起来像不像「调课意图」（用于决定要不要调用 AI）。</summary>
    public static bool LooksLikeIntent(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && IntentHints.Any(hint => text.Contains(hint, StringComparison.Ordinal));

    /// <summary>
    /// 只有「开关打开 + API Key 非空 + Base URL 是有效 http(s) 地址 + 模型名称非空」四者同时满足，
    /// 才认为 AI 解析可用。
    /// </summary>
    public static bool IsConfigured(bool enabled, string? apiKey, string? baseUrl, string? model) =>
        enabled
        && !string.IsNullOrWhiteSpace(apiKey)
        && IsHttpUrl(baseUrl)
        && !string.IsNullOrWhiteSpace(model);

    /// <summary>Base URL 必须是完整的 http/https 地址，避免拿相对路径去发请求。</summary>
    public static bool IsHttpUrl(string? baseUrl)
    {
        var normalized = NormalizeBaseUrl(baseUrl);
        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>
    /// 给设置页显示的状态文案（不会暴露 Key 内容）。
    /// </summary>
    public static string DescribeStatus(bool enabled, string? apiKey, string? baseUrl, string? model)
    {
        if (!enabled)
        {
            return "未启用：仍使用正则解析（#调课 第X节 A改B）";
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "未填写 API Key：AI 解析已停用，自动回退正则（不会报错）";
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return "API Base URL 为空：AI 解析已停用，自动回退正则（填上服务商地址即可启用）";
        }

        if (!IsHttpUrl(baseUrl))
        {
            return "API Base URL 格式无效：需要以 http:// 或 https:// 开头的完整地址";
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return "模型名称为空：AI 解析已停用，自动回退正则（例如 glm-4、deepseek-chat）";
        }

        return $"已配置（模型 {model.Trim()} / {MaskKey(apiKey)}）：调用 /chat/completions，失败自动回退正则";
    }

    /// <summary>
    /// 脱敏：只保留前 4 位和后 4 位，避免设置页/日志泄露完整 Key。
    /// </summary>
    public static string MaskKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "(空)";
        }

        var key = apiKey.Trim();
        if (key.Length <= 10)
        {
            return "****";
        }

        return $"{key[..4]}…{key[^4..]}";
    }

    /// <summary>
    /// 规整 Base URL：去掉首尾空白与末尾斜杠。**空值保持为空**，不再回落默认地址。
    /// </summary>
    public static string NormalizeBaseUrl(string? baseUrl) =>
        string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : baseUrl.Trim().TrimEnd('/');

    /// <summary>规整模型名称：去掉首尾空白。**空值保持为空**。</summary>
    public static string NormalizeModel(string? model) =>
        string.IsNullOrWhiteSpace(model) ? string.Empty : model.Trim();
}
