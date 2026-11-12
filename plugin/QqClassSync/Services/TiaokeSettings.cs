using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QqClassSync.Services;

/// <summary>
/// 插件设置。存放在 ClassIsland 的插件配置目录（PluginConfigFolder/Settings.json），
/// 用户可以直接编辑文本文件。后续要做设置页时，把这里换成 PluginSettings 基类即可。
/// </summary>
public sealed class TiaokeSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>是否启用监听。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>触发关键词。</summary>
    public string Keyword { get; set; } = "#调课";

    /// <summary>轮询间隔（秒）。Windows 通知中心只提供轮询方式。</summary>
    public double ScanIntervalSeconds { get; set; } = 1.0;

    /// <summary>
    /// true：解析成功后直接改课表；false：只发提醒、不改课表（干跑模式，方便先验证解析）。
    /// </summary>
    public bool AutoApply { get; set; } = true;

    /// <summary>解析失败时是否也提醒（方便老师调整消息格式）。</summary>
    public bool NotifyOnParseFailure { get; set; } = true;

    /// <summary>
    /// 认可的通知来源显示名（默认 NT QQ / TIM）。留空表示不按名称判断。
    /// 联调时可以把测试用的应用名加进来。
    /// </summary>
    public string[] AllowedAppNames { get; set; } = ["QQ", "腾讯QQ", "TIM"];

    /// <summary>
    /// 认可的通知 AppUserModelId。联调时可以把「Windows PowerShell」的 AUMID 加进来做端到端测试。
    /// </summary>
    public string[] AllowedAppIds { get; set; } =
        ["Tencent.QQNT", "com.tencent.qq", "com.tencent.tim", "QQ"];

    [JsonIgnore]
    public string FilePath { get; private set; } = string.Empty;

    public static TiaokeSettings Load(string configFolder)
    {
        var path = Path.Combine(configFolder, "Settings.json");
        var settings = new TiaokeSettings();
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<TiaokeSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    settings = loaded;
                }
            }
        }
        catch
        {
            // 配置坏了就用默认值，不要让插件加载失败。
            settings = new TiaokeSettings();
        }

        settings.Normalize();
        settings.FilePath = path;
        settings.Save();
        return settings;
    }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Keyword))
        {
            Keyword = "#调课";
        }

        ScanIntervalSeconds = Math.Clamp(ScanIntervalSeconds, 0.3, 10);
    }

    public void Save()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
