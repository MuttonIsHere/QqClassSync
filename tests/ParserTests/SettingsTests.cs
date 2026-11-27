using QqClassSync.Services;

namespace QqSync.Tests;

/// <summary>
/// 设置持久化测试：重点验证「清空 Base URL / 模型名称后不会被自动补回默认值」。
/// </summary>
internal static class SettingsTests
{
    public static int Run()
    {
        var failed = 0;
        // 用测试程序目录，避免 %TEMP% 在某些沙箱环境里不可写。
        var dir = Path.Combine(AppContext.BaseDirectory, "settings-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var path = Path.Combine(dir, "Settings.json");

            // 1) 首次加载：默认地址/模型都为空（不再强制 DeepSeek）
            var settings = TiaokeSettings.Load(dir);
            failed += Check(settings.AiBaseUrl == "", "新配置的 Base URL 默认为空");
            failed += Check(settings.AiModel == "", "新配置的模型名称默认为空");

            // 2) 清空后保存再加载：必须保持为空（复现用户报告的自动回填问题）
            settings.AiEnabled = true;
            settings.AiApiKey = "sk-test-key";
            settings.AiBaseUrl = "https://api.deepseek.com/v1";
            settings.AiModel = "deepseek-chat";
            settings.Save();

            var reloaded = TiaokeSettings.Load(dir);
            reloaded.AiBaseUrl = "";
            reloaded.AiModel = "";

            // 设置页保存前会调用 Normalize()；这里复现同样的顺序，确认不会被回填。
            reloaded.Normalize();
            failed += Check(reloaded.AiBaseUrl == "", "Normalize 不会把空 Base URL 补回默认地址");
            failed += Check(reloaded.AiModel == "", "Normalize 不会给空模型名补默认值");
            reloaded.Save();

            var cleared = TiaokeSettings.Load(dir);
            failed += Check(cleared.AiBaseUrl == "", "清空 Base URL 后重新加载仍为空");
            failed += Check(cleared.AiModel == "", "清空模型名称后重新加载仍为空");
            failed += Check(!AiParsePolicy.IsConfigured(cleared.AiEnabled, cleared.AiApiKey, cleared.AiBaseUrl, cleared.AiModel),
                "清空后 AI 视为未配置（回退正则）");

            // 3) 自定义厂商配置能原样保存
            cleared.AiBaseUrl = "https://open.bigmodel.cn/api/paas/v4/";
            cleared.AiModel = "glm-4";
            cleared.Save();
            var custom = TiaokeSettings.Load(dir);
            failed += Check(custom.AiBaseUrl == "https://open.bigmodel.cn/api/paas/v4", "自定义 Base URL 去尾斜杠后保存");
            failed += Check(custom.AiModel == "glm-4", "自定义模型名称能保存");
            failed += Check(AiParsePolicy.IsConfigured(custom.AiEnabled, custom.AiApiKey, custom.AiBaseUrl, custom.AiModel),
                "自定义厂商配置被视为可用");

            // 4) 旧配置迁移：没有 AiModel 字段 + DeepSeek 地址 → 自动补 deepseek-chat
            var legacyDir = Path.Combine(dir, "legacy-deepseek");
            Directory.CreateDirectory(legacyDir);
            File.WriteAllText(Path.Combine(legacyDir, "Settings.json"),
                "{\"AiEnabled\":true,\"AiApiKey\":\"sk-test\",\"AiBaseUrl\":\"https://api.deepseek.com/v1\"}");
            var legacy = TiaokeSettings.Load(legacyDir);
            failed += Check(legacy.AiModel == "deepseek-chat", "旧 DeepSeek 配置自动迁移模型名");

            // 5) 旧配置迁移：没有 AiModel 字段但地址是别家 → 保持为空，不猜
            var otherDir = Path.Combine(dir, "legacy-other");
            Directory.CreateDirectory(otherDir);
            File.WriteAllText(Path.Combine(otherDir, "Settings.json"),
                "{\"AiEnabled\":true,\"AiApiKey\":\"sk-test\",\"AiBaseUrl\":\"https://open.bigmodel.cn/api/paas/v4\"}");
            var other = TiaokeSettings.Load(otherDir);
            failed += Check(other.AiModel == "", "旧的非 DeepSeek 配置不自动填模型名");

            // 6) 已显式写空 AiModel 的配置不会被迁移覆盖
            var explicitEmptyDir = Path.Combine(dir, "explicit-empty");
            Directory.CreateDirectory(explicitEmptyDir);
            File.WriteAllText(Path.Combine(explicitEmptyDir, "Settings.json"),
                "{\"AiEnabled\":true,\"AiApiKey\":\"sk-test\",\"AiBaseUrl\":\"https://api.deepseek.com/v1\",\"AiModel\":\"\"}");
            var explicitEmpty = TiaokeSettings.Load(explicitEmptyDir);
            failed += Check(explicitEmpty.AiModel == "", "显式清空的模型名不会被迁移补回");

            // 7) 文件里确实写入了空值，而不是被改写回默认地址
            var raw = File.ReadAllText(Path.Combine(dir, "Settings.json"));
            failed += Check(raw.Contains("\"AiModel\": \"glm-4\""), "落盘文件包含自定义模型名");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        return failed;
    }

    private static int Check(bool ok, string message)
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {message}");
        return ok ? 0 : 1;
    }
}
