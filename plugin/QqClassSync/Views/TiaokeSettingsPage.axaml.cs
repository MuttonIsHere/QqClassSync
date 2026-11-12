using System.ComponentModel;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
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

    public TiaokeSettingsPage()
    {
        _settings = TryResolveSettings();
        DataContext = _settings;
        InitializeComponent();

        // 页面可能被重复创建，先退订再订阅，保证只挂一次。
        _settings.PropertyChanged -= OnSettingsPropertyChanged;
        _settings.PropertyChanged += OnSettingsPropertyChanged;
        RefreshPlanStatus();
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
        _saveTimer ??= CreateSaveTimer();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

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
        RefreshPlanStatus();
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
        if (!int.TryParse(LessonInput.Text?.Trim(), out var lesson) || lesson <= 0)
        {
            UndoStatusText.Text = "请输入有效的节次（例如 3）";
            return;
        }

        try
        {
            var patcher = IAppHost.GetService<TempClassPlanPatcher>();
            var outcome = patcher.UndoLesson(lesson);
            UndoStatusText.Text = outcome.Message;
        }
        catch (Exception ex)
        {
            UndoStatusText.Text = "撤销失败：" + ex.Message;
        }

        RefreshPlanStatus();
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
