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
                var loaded = JsonSerializer.Deserialize<TiaokeSettings>(File.ReadAllText(path), JsonOptions);
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

            var loaded = JsonSerializer.Deserialize<TiaokeSettings>(File.ReadAllText(FilePath), JsonOptions);
            if (loaded is null)
            {
                return;
            }

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
        _lastLoadedWriteTimeUtc = new FileInfo(FilePath).LastWriteTimeUtc;
    }
}
