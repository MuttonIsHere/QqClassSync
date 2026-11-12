namespace QqClassSync.Services;

/// <summary>
/// AI 解析的「是否可用」判定。抽成纯函数是为了能脱离插件宿主做单元测试，
/// 也避免各处重复写「开关 + Key + BaseUrl」的三重判断。
///
/// 红线：配置不完整时只返回 false，调用方必须静默回退到正则解析，不允许抛异常。
/// </summary>
public static class AiParsePolicy
{
    public const string DefaultBaseUrl = "https://api.deepseek.com/v1";

    /// <summary>
    /// 只有「开关打开 + API Key 非空 + BaseUrl 非空」三者同时满足，才认为 AI 解析可用。
    /// </summary>
    public static bool IsConfigured(bool enabled, string? apiKey, string? baseUrl) =>
        enabled
        && !string.IsNullOrWhiteSpace(apiKey)
        && !string.IsNullOrWhiteSpace(baseUrl);

    /// <summary>
    /// 给设置页显示的状态文案（不会暴露 Key 内容）。
    /// </summary>
    public static string DescribeStatus(bool enabled, string? apiKey, string? baseUrl)
    {
        if (!enabled)
        {
            return "未启用：仍使用正则解析（#调课 第X节 A改B）";
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "已开启但未填写 API Key：自动回退到正则解析，不会报错";
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return "Base URL 为空：自动回退到正则解析，不会报错";
        }

        return $"已配置（{MaskKey(apiKey)}）：本版本仍走正则解析，模型调用将在后续版本接入";
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
    /// 规整 Base URL：去掉末尾斜杠；为空时回落到默认值。
    /// </summary>
    public static string NormalizeBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return DefaultBaseUrl;
        }

        return baseUrl.Trim().TrimEnd('/');
    }
}
