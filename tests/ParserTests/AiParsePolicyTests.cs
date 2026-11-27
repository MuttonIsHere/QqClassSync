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
        failed += Check(!AiParsePolicy.IsConfigured(false, "", "https://api.deepseek.com/v1", "deepseek-chat"), "关闭 + 空 Key → 不走 AI");
        failed += Check(!AiParsePolicy.IsConfigured(false, "sk-xxx", "https://api.deepseek.com/v1", "deepseek-chat"), "关闭 + 有 Key → 不走 AI");
        failed += Check(!AiParsePolicy.IsConfigured(true, "", "https://api.deepseek.com/v1", "deepseek-chat"), "开启 + 空 Key → 不走 AI（回退正则）");
        failed += Check(!AiParsePolicy.IsConfigured(true, "   ", "https://api.deepseek.com/v1", "deepseek-chat"), "开启 + 空白 Key → 不走 AI");
        failed += Check(!AiParsePolicy.IsConfigured(true, "sk-xxx", "", "glm-4"), "开启 + 空 BaseUrl → 不走 AI");
        failed += Check(!AiParsePolicy.IsConfigured(true, "sk-xxx", "https://open.bigmodel.cn/api/paas/v4", ""), "开启 + 空模型名 → 不走 AI");
        failed += Check(!AiParsePolicy.IsConfigured(true, "sk-xxx", "https://open.bigmodel.cn/api/paas/v4", "   "), "开启 + 空白模型名 → 不走 AI");
        failed += Check(!AiParsePolicy.IsConfigured(true, "sk-xxx", "not-a-url", "glm-4"), "开启 + 非法地址 → 不走 AI");
        failed += Check(!AiParsePolicy.IsConfigured(true, "sk-xxx", "/v1", "glm-4"), "开启 + 相对路径 → 不走 AI");
        failed += Check(!AiParsePolicy.IsConfigured(true, null, null, null), "开启 + 全 null → 不走 AI");
        failed += Check(AiParsePolicy.IsConfigured(true, "sk-abc", "https://api.deepseek.com/v1", "deepseek-chat"), "DeepSeek 配置齐全 → 走 AI");
        failed += Check(AiParsePolicy.IsConfigured(true, "sk-abc", "https://open.bigmodel.cn/api/paas/v4", "glm-4"), "智谱配置齐全 → 走 AI");

        // 状态文案
        failed += Check(AiParsePolicy.DescribeStatus(false, "", "", "").Contains("正则"), "未启用文案应提到正则解析");
        failed += Check(AiParsePolicy.DescribeStatus(true, "", "https://x/v1", "glm-4").Contains("回退"), "空 Key 文案应说明自动回退");
        failed += Check(AiParsePolicy.DescribeStatus(true, "sk-abc", "", "glm-4").Contains("Base URL"), "空地址文案应点名 Base URL");
        failed += Check(AiParsePolicy.DescribeStatus(true, "sk-abc", "https://x/v1", "").Contains("模型"), "空模型文案应点名模型");
        failed += Check(AiParsePolicy.DescribeStatus(true, "sk-abc", "not-a-url", "glm-4").Contains("格式"), "非法地址文案应提示格式");
        failed += Check(AiParsePolicy.DescribeStatus(true, "sk-abc", "https://x/v1", "glm-4").Contains("glm-4"), "已配置文案应显示模型名");
        failed += Check(AiParsePolicy.IsHttpUrl("https://open.bigmodel.cn/api/paas/v4"), "完整 https 地址有效");
        failed += Check(!AiParsePolicy.IsHttpUrl("ftp://example.com"), "非 http(s) 协议无效");

        // Key 脱敏：不能出现完整 Key
        var masked = AiParsePolicy.MaskKey("sk-1234567890abcdef");
        failed += Check(!masked.Contains("1234567890abcdef"), "脱敏后不能包含完整 Key");
        failed += Check(AiParsePolicy.MaskKey("").Length > 0, "空 Key 脱敏应有占位文案");

        // BaseUrl / 模型规整：空值保持为空，绝不回填默认值
        failed += Check(AiParsePolicy.NormalizeBaseUrl("https://api.deepseek.com/v1/") == "https://api.deepseek.com/v1", "去除末尾斜杠");
        failed += Check(AiParsePolicy.NormalizeBaseUrl("") == "", "空 BaseUrl 保持为空（不再回落默认地址）");
        failed += Check(AiParsePolicy.NormalizeBaseUrl("   ") == "", "空白 BaseUrl 保持为空");
        failed += Check(AiParsePolicy.NormalizeBaseUrl(null) == "", "null BaseUrl 保持为空");
        failed += Check(AiParsePolicy.NormalizeModel(" glm-4 ") == "glm-4", "模型名去首尾空白");
        failed += Check(AiParsePolicy.NormalizeModel("") == "", "空模型名保持为空");
        failed += Check(AiParsePolicy.NormalizeModel(null) == "", "null 模型名保持为空");

        // 意图预筛：老师的自然语言要能进 AI，闲聊不能
        failed += Check(AiParsePolicy.LooksLikeIntent("今天第三节课不上了，让数学老师来上"), "自然语言调课应进入 AI 预筛");
        failed += Check(AiParsePolicy.LooksLikeIntent("第三节换一下，语文老师代课"), "换课/代课应进入预筛");
        failed += Check(!AiParsePolicy.LooksLikeIntent("今天作业好多啊"), "普通闲聊不应进入预筛");
        failed += Check(!AiParsePolicy.LooksLikeIntent(""), "空文本不应进入预筛");

        return failed;
    }

    private static int Check(bool ok, string message)
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {message}");
        return ok ? 0 : 1;
    }
}
