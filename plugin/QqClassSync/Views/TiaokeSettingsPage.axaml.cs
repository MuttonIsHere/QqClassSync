using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using FluentAvalonia.UI.Controls;
using QqClassSync.Services;

namespace QqClassSync.Views;

/// <summary>
/// 设置页：监听开关、关键词、轮询间隔、行为开关，以及「撤销今天的调课」按钮。
/// 设置改动会防抖 500ms 后写回 Settings.json。
/// </summary>
[SettingsPageInfo("qq.classsync.settings", "QQ 调课同步", "\uE02B", "\uE02B")]
public partial class TiaokeSettingsPage : SettingsPageBase
{
    private readonly TiaokeSettings _settings;
    private DispatcherTimer? _saveTimer;
    private bool _suppressPresetChange;
    private AiProviderPreset? _lastPresetSelection;

    public TiaokeSettingsPage()
    {
        _settings = TryResolveSettings();
        DataContext = _settings;
        InitializeComponent();

        // 页面可能被重复创建，先退订再订阅，保证只挂一次。
        _settings.PropertyChanged -= OnSettingsPropertyChanged;
        _settings.PropertyChanged += OnSettingsPropertyChanged;

        ProviderPresetBox.ItemsSource = AiProviderPresets.All;
        SyncPresetSelection();

        RefreshPlanStatus();
        RefreshAiStatus();
    }

    private static TiaokeSettings TryResolveSettings()
    {
        // 设计器里没有宿主容器，退化成一份默认设置，避免设计时崩掉。
        try
        {
            return IAppHost.GetService<TiaokeSettings>();
        }
        catch
        {
            return new TiaokeSettings();
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 手动改地址/模型时，下拉菜单跟着切换到对应预设或「自定义」（只切显示，不改输入）。
        if (!_suppressPresetChange
            && e.PropertyName is nameof(TiaokeSettings.AiBaseUrl) or nameof(TiaokeSettings.AiModel))
        {
            SyncPresetSelection();
        }

        // AI 相关配置改动时实时刷新状态文案（不会显示 Key 明文）。
        if (e.PropertyName is nameof(TiaokeSettings.AiEnabled)
            or nameof(TiaokeSettings.AiApiKey)
            or nameof(TiaokeSettings.AiBaseUrl)
            or nameof(TiaokeSettings.AiModel)
            or nameof(TiaokeSettings.AiConfirmBeforeSend))
        {
            RefreshAiStatus();
        }

        _saveTimer ??= CreateSaveTimer();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>按当前 Base URL 识别预设；识别不到就显示「自定义」，但绝不清空已有输入。</summary>
    private void SyncPresetSelection()
    {
        var match = AiProviderPresets.Match(_settings.AiBaseUrl) ?? AiProviderPresets.Custom;
        _lastPresetSelection = match;

        _suppressPresetChange = true;
        ProviderPresetBox.SelectedItem = match;
        _suppressPresetChange = false;
    }

    /// <summary>
    /// 用户主动选择预设：需要覆盖已手改的配置时先确认；选「自定义」则按约定清空两项。
    /// </summary>
    private async void OnProviderPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressPresetChange)
        {
            return;
        }

        if (ProviderPresetBox.SelectedItem is not AiProviderPreset preset)
        {
            return;
        }

        try
        {
            if (preset.IsCustom)
            {
                ApplyPreset(preset);
                _lastPresetSelection = preset;
                return;
            }

            if (AiProviderPresets.NeedsOverwriteConfirmation(preset, _settings.AiBaseUrl, _settings.AiModel))
            {
                var overwrite = await ConfirmOverwriteAsync(preset);
                if (!overwrite)
                {
                    // 保留当前输入，下拉菜单退回上一次的选择。
                    _suppressPresetChange = true;
                    ProviderPresetBox.SelectedItem = _lastPresetSelection ?? AiProviderPresets.Custom;
                    _suppressPresetChange = false;
                    return;
                }
            }

            ApplyPreset(preset);
            _lastPresetSelection = preset;
        }
        catch (Exception ex)
        {
            // 对话框/绑定异常不能让设置页崩溃；保留原配置。
            AiStatusText.Text = "切换服务商预设失败：" + ex.GetType().Name;
            _suppressPresetChange = true;
            ProviderPresetBox.SelectedItem = _lastPresetSelection ?? AiProviderPresets.Custom;
            _suppressPresetChange = false;
        }
    }

    private void ApplyPreset(AiProviderPreset preset)
    {
        var (baseUrl, model) = AiProviderPresets.ResolveApply(preset, _settings.AiModel);
        _settings.AiBaseUrl = baseUrl;
        _settings.AiModel = model;
        RefreshAiStatus();
    }

    private async Task<bool> ConfirmOverwriteAsync(AiProviderPreset preset)
    {
        var current = $"当前：{Display(_settings.AiBaseUrl)} / {Display(_settings.AiModel)}";
        var presetModel = string.IsNullOrWhiteSpace(preset.Model) ? "(保留当前模型)" : Display(preset.Model);
        var target = $"预设：{Display(preset.BaseUrl)} / {presetModel}";
        var dialog = new ContentDialog
        {
            Title = $"用「{preset.Name}」覆盖当前配置？",
            Content = new TextBlock
            {
                Text = $"{current}\n{target}\n\n覆盖后仍可手动修改模型名称。",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "覆盖",
            CloseButtonText = "保留当前",
            DefaultButton = ContentDialogButton.Primary
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static string Display(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(空)" : value.Trim();

    private DispatcherTimer CreateSaveTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                _settings.Normalize();
                _settings.Save();
            }
            catch
            {
                // 写盘失败不应影响用户继续操作，轮询循环会用内存里的值继续工作。
            }
        };
        return timer;
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SyncPresetSelection();
        RefreshPlanStatus();
        RefreshAiStatus();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (_saveTimer is not null)
        {
            _saveTimer.Stop();
            _saveTimer = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnUndoClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var patcher = IAppHost.GetService<TempClassPlanPatcher>();
            var outcome = patcher.Undo();
            UndoStatusText.Text = outcome.Message;
        }
        catch (Exception ex)
        {
            UndoStatusText.Text = "撤销失败：" + ex.Message;
        }

        RefreshPlanStatus();
    }

    private void OnUndoLessonClick(object? sender, RoutedEventArgs e)
    {
        // 支持一次撤销多节，例如输入 3 或 3,5
        var lessons = (LessonInput.Text ?? string.Empty)
            .Split([',', '，', '、', ' ', ';', '；'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part.Trim(), out var n) ? n : 0)
            .Where(n => n > 0)
            .Distinct()
            .ToList();

        if (lessons.Count == 0)
        {
            UndoStatusText.Text = "请输入有效的节次（例如 3 或 3,5）";
            return;
        }

        try
        {
            var patcher = IAppHost.GetService<TempClassPlanPatcher>();
            var outcome = patcher.UndoLessons(lessons);
            UndoStatusText.Text = outcome.Summary();
        }
        catch (Exception ex)
        {
            UndoStatusText.Text = "撤销失败：" + ex.Message;
        }

        RefreshPlanStatus();
    }

    private void RefreshAiStatus()
    {
        try
        {
            AiStatusText.Text = AiParsePolicy.DescribeStatus(
                _settings.AiEnabled,
                _settings.AiApiKey,
                _settings.AiBaseUrl,
                _settings.AiModel);
        }
        catch
        {
            AiStatusText.Text = "AI 状态未知（不影响正则解析）";
        }
    }

    private void RefreshPlanStatus()
    {
        try
        {
            var patcher = IAppHost.GetService<TempClassPlanPatcher>();
            PlanStatusText.Text = patcher.DescribeTodayPlan();
        }
        catch
        {
            PlanStatusText.Text = "无法读取课表信息";
        }
    }
}
