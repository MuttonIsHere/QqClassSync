namespace QqClassSync.Services;

/// <summary>
/// AI 服务商预设：只描述「Base URL + 推荐模型」，不包含任何 Key。
/// 下拉菜单用它做一键填充；最后一项固定是「自定义」，不填任何默认值。
/// </summary>
public sealed record AiProviderPreset(string Name, string BaseUrl, string Model, string Note = "")
{
    /// <summary>「自定义」项：不预设地址和模型。</summary>
    public bool IsCustom => string.IsNullOrEmpty(BaseUrl) && string.IsNullOrEmpty(Model);

    public override string ToString() => Name;
}

/// <summary>
/// 内置通讯录：常见 OpenAI 兼容服务商 + 聚合平台。
/// 纯数据 + 纯函数，方便单测；不依赖 Avalonia / 宿主。
/// </summary>
public static class AiProviderPresets
{
    public static AiProviderPreset Custom { get; } = new(
        "自定义 (Custom)",
        string.Empty,
        string.Empty,
        "手动填写 Base URL 与模型名称；不会自动补默认地址");

    /// <summary>下拉菜单的全部选项，最后一项固定为「自定义」。</summary>
    public static IReadOnlyList<AiProviderPreset> All { get; } =
    [
        new("DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat", "Key 需为 DeepSeek 的 Key"),
        new("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4", "Key 需为智谱开放平台的 Key"),
        new("Kimi (Moonshot)", "https://api.moonshot.cn/v1", "moonshot-v1-8k", "Key 需为 Moonshot 的 Key"),
        new("通义千问", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus", "Key 需为阿里云百炼的 Key"),
        new("豆包（火山方舟）", "https://ark.cn-beijing.volces.com/api/v3", string.Empty, "模型填控制台的「接入点 ID」（ep-…）"),
        new("OpenAI", "https://api.openai.com/v1", "gpt-4o-mini", "Key 需为 OpenAI 的 Key"),
        new("硅基流动 SiliconFlow", "https://api.siliconflow.cn/v1", "Qwen/Qwen2.5-7B-Instruct", "Key 需为 SiliconFlow 的 Key"),
        new("OpenRouter", "https://openrouter.ai/api/v1", "openai/gpt-4o-mini", "Key 需为 OpenRouter 的 Key"),
        Custom
    ];

    /// <summary>
    /// 按 Base URL 识别当前属于哪个预设（忽略大小写、末尾斜杠）。
    /// 找不到就返回 null（界面显示为「自定义」，但不会清空已有输入）。
    /// </summary>
    public static AiProviderPreset? Match(string? baseUrl)
    {
        var normalized = AiParsePolicy.NormalizeBaseUrl(baseUrl);
        if (normalized.Length == 0)
        {
            return null;
        }

        return All.FirstOrDefault(preset =>
            !preset.IsCustom
            && string.Equals(
                AiParsePolicy.NormalizeBaseUrl(preset.BaseUrl),
                normalized,
                StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>选中预设后实际要写入的值；豆包没有通用模型，保留用户当前模型。</summary>
    public static (string BaseUrl, string Model) ResolveApply(AiProviderPreset preset, string? currentModel)
    {
        if (preset.IsCustom)
        {
            return (string.Empty, string.Empty);
        }

        var model = string.IsNullOrWhiteSpace(preset.Model)
            ? AiParsePolicy.NormalizeModel(currentModel)
            : preset.Model;
        return (preset.BaseUrl, model);
    }

    /// <summary>
    /// 当前输入与目标预设不一致、且用户已经填过内容时，需要先确认再覆盖。
    /// 空输入视为「还没配」，直接填充，不打扰用户。
    /// </summary>
    public static bool NeedsOverwriteConfirmation(AiProviderPreset preset, string? currentBaseUrl, string? currentModel)
    {
        if (preset.IsCustom)
        {
            return false;
        }

        var baseUrl = AiParsePolicy.NormalizeBaseUrl(currentBaseUrl);
        var model = AiParsePolicy.NormalizeModel(currentModel);
        var presetBase = AiParsePolicy.NormalizeBaseUrl(preset.BaseUrl);

        if (baseUrl.Length > 0
            && !string.Equals(baseUrl, presetBase, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 预设本身没有推荐模型（豆包）时，不比较模型，也不覆盖用户的接入点 ID。
        if (!string.IsNullOrWhiteSpace(preset.Model)
            && model.Length > 0
            && !string.Equals(model, preset.Model.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
