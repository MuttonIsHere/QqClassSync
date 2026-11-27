using QqClassSync.Services;

namespace QqSync.Tests;

/// <summary>
/// 服务商预设（内置通讯录）的纯逻辑测试：列表完整性、按 URL 识别、覆盖确认、应用结果。
/// </summary>
internal static class AiProviderPresetTests
{
    public static int Run()
    {
        var failed = 0;

        var presets = AiProviderPresets.All;
        failed += Check(presets.Count >= 9, "预设列表至少包含 8 个服务商 + 自定义");
        failed += Check(presets[^1].IsCustom, "最后一项必须是「自定义 (Custom)」");
        failed += Check(presets.Take(presets.Count - 1).All(p => !p.IsCustom), "自定义项只出现一次且在末尾");
        failed += Check(presets.Take(presets.Count - 1).All(p => AiParsePolicy.IsHttpUrl(p.BaseUrl)), "所有预设地址都是有效 http(s) URL");

        var names = presets.Select(p => p.Name).ToList();
        foreach (var expected in new[] { "DeepSeek", "智谱", "Kimi", "通义", "豆包", "OpenAI", "硅基流动", "OpenRouter" })
        {
            failed += Check(names.Any(n => n.Contains(expected, StringComparison.Ordinal)), $"预设列表包含 {expected}");
        }

        var nonCustom = presets.Where(p => !p.IsCustom).ToList();
        failed += Check(
            nonCustom.Select(p => p.BaseUrl).Distinct(StringComparer.OrdinalIgnoreCase).Count() == nonCustom.Count,
            "预设 Base URL 不重复");

        // 按地址识别（忽略末尾斜杠）
        failed += Check(AiProviderPresets.Match("https://open.bigmodel.cn/api/paas/v4/")?.Name.Contains("智谱") == true, "带末尾斜杠也能识别智谱");
        failed += Check(AiProviderPresets.Match("https://api.deepseek.com/v1")?.Name == "DeepSeek", "识别 DeepSeek");
        failed += Check(AiProviderPresets.Match("") is null, "空地址不匹配任何预设");
        failed += Check(AiProviderPresets.Match("https://unknown.example/v1") is null, "未知地址返回 null（界面显示自定义）");

        // 覆盖确认
        var zhipu = presets.First(p => p.Name.Contains("智谱"));
        var doubao = presets.First(p => p.Name.Contains("豆包"));
        var custom = AiProviderPresets.Custom;
        failed += Check(AiProviderPresets.NeedsOverwriteConfirmation(zhipu, "https://api.deepseek.com/v1", "deepseek-chat"), "已有别的地址时需要确认覆盖");
        failed += Check(!AiProviderPresets.NeedsOverwriteConfirmation(zhipu, "", ""), "空配置直接填充、不确认");
        failed += Check(AiProviderPresets.NeedsOverwriteConfirmation(zhipu, zhipu.BaseUrl, "deepseek-chat"), "地址对但模型不同也需要确认");
        failed += Check(!AiProviderPresets.NeedsOverwriteConfirmation(zhipu, zhipu.BaseUrl, zhipu.Model), "完全一致时不需要确认");
        failed += Check(!AiProviderPresets.NeedsOverwriteConfirmation(doubao, doubao.BaseUrl, "ep-20261127-abc"), "豆包预设不覆盖用户接入点 ID");
        failed += Check(!AiProviderPresets.NeedsOverwriteConfirmation(custom, "https://x/v1", "m"), "自定义项从不弹覆盖确认");

        // 应用结果
        var (zBase, zModel) = AiProviderPresets.ResolveApply(zhipu, "old-model");
        failed += Check(zBase == zhipu.BaseUrl && zModel == "glm-4", "选择智谱后填入地址与推荐模型");
        var (cBase, cModel) = AiProviderPresets.ResolveApply(custom, "old-model");
        failed += Check(cBase == "" && cModel == "", "选择自定义后清空地址与模型");
        var (dBase, dModel) = AiProviderPresets.ResolveApply(doubao, "ep-20261127-abc");
        failed += Check(dBase == doubao.BaseUrl && dModel == "ep-20261127-abc", "选择豆包后保留用户接入点 ID");

        return failed;
    }

    private static int Check(bool ok, string message)
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {message}");
        return ok ? 0 : 1;
    }
}
