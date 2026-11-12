using QqClassSync.Services;

namespace QqSync.Tests;

/// <summary>
/// AI 解析「是否可用」判定的单元测试。
/// 重点：留空 / 关闭时必须是 false（= 回退正则），不能抛异常。
/// </summary>
internal static class AiParsePolicyTests
{
    public static int Run()
    {
        var failed = 0;

        // 关键红线：默认配置（关闭 + 空 Key）必须不可用
        failed += Check(!AiParsePolicy.IsConfigured(false, "", "https://api.deepseek.com/v1"), "关闭 + 空 Key → 不走 AI");
        failed += Check(!AiParsePolicy.IsConfigured(false, "sk-xxx", "https://api.deepseek.com/v1"), "关闭 + 有 Key → 不走 AI");
        failed += Check(!AiParsePolicy.IsConfigured(true, "", "https://api.deepseek.com/v1"), "开启 + 空 Key → 不走 AI（回退正则）");
        failed += Check(!AiParsePolicy.IsConfigured(true, "   ", "https://api.deepseek.com/v1"), "开启 + 空白 Key → 不走 AI");
        failed += Check(!AiParsePolicy.IsConfigured(true, "sk-xxx", ""), "开启 + 空 BaseUrl → 不走 AI");
        failed += Check(!AiParsePolicy.IsConfigured(true, null, null), "开启 + 全 null → 不走 AI");
        failed += Check(AiParsePolicy.IsConfigured(true, "sk-abc", "https://api.deepseek.com/v1"), "配置齐全 → 走 AI");

        // 状态文案
        failed += Check(AiParsePolicy.DescribeStatus(false, "", "").Contains("正则"), "未启用文案应提到正则解析");
        failed += Check(AiParsePolicy.DescribeStatus(true, "", "").Contains("回退"), "空 Key 文案应说明自动回退");

        // Key 脱敏：不能出现完整 Key
        var masked = AiParsePolicy.MaskKey("sk-1234567890abcdef");
        failed += Check(!masked.Contains("1234567890abcdef"), "脱敏后不能包含完整 Key");
        failed += Check(AiParsePolicy.MaskKey("").Length > 0, "空 Key 脱敏应有占位文案");

        // BaseUrl 规整
        failed += Check(AiParsePolicy.NormalizeBaseUrl("https://api.deepseek.com/v1/") == "https://api.deepseek.com/v1", "去除末尾斜杠");
        failed += Check(AiParsePolicy.NormalizeBaseUrl("") == AiParsePolicy.DefaultBaseUrl, "空值回落默认地址");
        failed += Check(AiParsePolicy.NormalizeBaseUrl(null) == AiParsePolicy.DefaultBaseUrl, "null 回落默认地址");

        return failed;
    }

    private static int Check(bool ok, string message)
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {message}");
        return ok ? 0 : 1;
    }
}
