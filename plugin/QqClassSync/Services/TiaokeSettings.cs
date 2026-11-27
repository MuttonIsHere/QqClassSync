using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QqClassSync.Services;

/// <summary>
/// 插件设置。存放在 ClassIsland 的插件配置目录（PluginConfigFolder/Settings.json）。
///
/// 继承 ObservableObject 是为了让设置页可以直接绑定；文件被外部修改时，
/// 轮询循环会调用 <see cref="ReloadIfChanged"/> 热重载。
/// </summary>
public partial class TiaokeSettings : ObservableObject
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>是否启用监听。</summary>
    [ObservableProperty]
    private bool _enabled = true;

    /// <summary>触发关键词。</summary>
    [ObservableProperty]
    private string _keyword = "#调课";

    /// <summary>轮询间隔（秒）。Windows 通知中心只提供轮询方式。</summary>
    [ObservableProperty]
    private double _scanIntervalSeconds = 1.0;

    /// <summary>
    /// true：解析成功后直接改课表；false：只发提醒、不改课表（干跑模式，方便先验证解析）。
    /// </summary>
    [ObservableProperty]
    private bool _autoApply = true;

    /// <summary>解析失败时是否也提醒（方便老师调整消息格式）。</summary>
    [ObservableProperty]
    private bool _notifyOnParseFailure = true;

    /// <summary>
    /// 认可的通知来源显示名（默认 NT QQ / TIM）。联调时可以把测试用的应用名加进来。
    /// </summary>
    [ObservableProperty]
    private string[] _allowedAppNames = ["QQ", "腾讯QQ", "TIM"];

    /// <summary>
    /// 认可的通知 AppUserModelId。联调时可以把测试应用的 AUMID 加进来。
    /// </summary>
    [ObservableProperty]
    private string[] _allowedAppIds =
        ["Tencent.QQNT", "com.tencent.qq", "com.tencent.tim", "QQ"];

    /// <summary>
    /// 是否启用 AI 模糊解析（实验性）。默认关闭；关闭或配置不全时自动回退正则解析。
    /// </summary>
    [ObservableProperty]
    private bool _aiEnabled;

    /// <summary>
    /// AI 接口的 API Key（DeepSeek / 智谱 / 豆包 / Kimi / 通义等）。
    /// 默认空；只保存在本机 Settings.json，不会进入仓库。
    /// </summary>
    [ObservableProperty]
    private string _aiApiKey = string.Empty;

    /// <summary>
    /// AI 接口地址（OpenAI 兼容）。默认空：留空就是留空，不再自动填 DeepSeek。
    /// </summary>
    [ObservableProperty]
    private string _aiBaseUrl = string.Empty;

    /// <summary>
    /// 模型名称（如 deepseek-chat / glm-4 / 豆包模型 ID）。默认空，由用户按服务商填写。
    /// </summary>
    [ObservableProperty]
    private string _aiModel = string.Empty;

    /// <summary>AI 解析出的结果是否要先确认再执行（默认开启，隐私/误判保护）。</summary>
    [ObservableProperty]
    private bool _aiConfirmBeforeSend = true;

    [JsonIgnore]
    public string FilePath { get; private set; } = string.Empty;

    [JsonIgnore]
    private DateTime _lastLoadedWriteTimeUtc = DateTime.MinValue;

    [JsonIgnore]
    public string ScanIntervalDisplay
    {
        get => ScanIntervalSeconds.ToString("0.##");
        set
        {
            if (double.TryParse(value, out var parsed))
            {
                ScanIntervalSeconds = parsed;
            }
        }
    }

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
                    ApplyLegacyModelMigration(json, loaded);
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

    /// <summary>
    /// 设置文件被外部修改后自动重载（轮询循环里调用），改 Settings.json 不用重启 ClassIsland。
    /// </summary>
    public void ReloadIfChanged()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            return;
        }

        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.LastWriteTimeUtc <= _lastLoadedWriteTimeUtc)
            {
                return;
            }

            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<TiaokeSettings>(json, JsonOptions);
            if (loaded is null)
            {
                return;
            }

            ApplyLegacyModelMigration(json, loaded);
            loaded.Normalize();
            CopyFrom(loaded);
            _lastLoadedWriteTimeUtc = info.LastWriteTimeUtc;
        }
        catch
        {
            // 文件正在被写入时可能读到半截内容，忽略这次，下一轮再试。
        }
    }

    private void CopyFrom(TiaokeSettings other)
    {
        // ObservableObject 生成属性的 setter 会做相等判断，值没变就不会触发 PropertyChanged，
        // 因此这里不会和设置页的「自动保存」形成回环。
        Enabled = other.Enabled;
        Keyword = other.Keyword;
        ScanIntervalSeconds = other.ScanIntervalSeconds;
        AutoApply = other.AutoApply;
        NotifyOnParseFailure = other.NotifyOnParseFailure;
        AllowedAppNames = other.AllowedAppNames;
        AllowedAppIds = other.AllowedAppIds;
        AiEnabled = other.AiEnabled;
        AiApiKey = other.AiApiKey;
        AiBaseUrl = other.AiBaseUrl;
        AiModel = other.AiModel;
        AiConfirmBeforeSend = other.AiConfirmBeforeSend;
    }

    /// <summary>
    /// 旧版本没有 AiModel 字段：只有 Base URL 仍指向 DeepSeek 时才补 deepseek-chat，
    /// 保证升级后原来的 DeepSeek 配置不失效；其它厂商或空地址一律保持空值，不猜。
    /// </summary>
    private static void ApplyLegacyModelMigration(string json, TiaokeSettings settings)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            // 手改配置可能用 camelCase，这里大小写不敏感地判断字段是否存在。
            var hasModelField = document.RootElement
                .EnumerateObject()
                .Any(property => string.Equals(property.Name, nameof(AiModel), StringComparison.OrdinalIgnoreCase));
            if (hasModelField)
            {
                return;
            }

            if ((settings.AiBaseUrl ?? string.Empty).Contains("deepseek.com", StringComparison.OrdinalIgnoreCase))
            {
                settings.AiModel = AiParsePolicy.LegacyDeepSeekModel;
            }
        }
        catch
        {
            // 迁移失败不影响加载；保持空值，由用户手动填写。
        }
    }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Keyword))
        {
            Keyword = "#调课";
        }

        ScanIntervalSeconds = Math.Clamp(ScanIntervalSeconds, 0.3, 10);

        // AI 配置：只去首尾空白/末尾斜杠。空值保持为空，不回填默认地址或默认模型。
        AiApiKey = (AiApiKey ?? string.Empty).Trim();
        AiBaseUrl = AiParsePolicy.NormalizeBaseUrl(AiBaseUrl);
        AiModel = AiParsePolicy.NormalizeModel(AiModel);
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
        _lastLoadedWriteTimeUtc = new FileInfo(FilePath).LastWriteTimeUtc;
    }
}
