using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Models.Notification;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QqClassSync.Models;
using Windows.UI.Notifications.Management;

namespace QqClassSync.Services;

/// <summary>
/// 提醒提供方 + 后台轮询宿主。
/// 之所以继承 NotificationProviderBase：这样可以用 ShowNotification 把提醒推给 ClassIsland
/// 的提醒渠道（大屏/遮罩/语音都走宿主既有配置），而不是自己造一套弹窗。
///
/// 生命周期说明：宿主按 IHostedService 顺序启动，实测有其它插件在 StartAsync 里抛异常，
/// 会让后续服务直接不启动。所以轮询循环在构造函数里就起，不依赖宿主调用 StartAsync。
/// </summary>
[NotificationProviderInfo(
    "b7f3c1a2-5d4e-4f6a-9b8c-0d1e2f3a4b5c",
    "QQ 调课同步",
    "\uE02B",
    "监听 QQ 群聊中的「#调课」消息，自动修改临时层课表并提醒。")]
public sealed class TiaokeNotificationProvider : NotificationProviderBase, IHostedService
{
    private readonly TiaokeSettings _settings;
    private readonly QqToastScanner _scanner;
    private readonly TiaokeParser _parser;
    private readonly TempClassPlanPatcher _patcher;
    private readonly ILogger<TiaokeNotificationProvider> _logger;

    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _warnedAboutAccess;

    public TiaokeNotificationProvider(
        TiaokeSettings settings,
        QqToastScanner scanner,
        TiaokeParser parser,
        TempClassPlanPatcher patcher,
        ILogger<TiaokeNotificationProvider> logger)
    {
        _settings = settings;
        _scanner = scanner;
        _parser = parser;
        _patcher = patcher;
        _logger = logger;

        lock (_lifecycleLock)
        {
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        }

        _logger.LogInformation(
            "QQ 调课同步已启动（间隔 {Interval}s，关键词 {Keyword}，自动应用 {AutoApply}）",
            settings.ScanIntervalSeconds, settings.Keyword, settings.AutoApply);
        _logger.LogInformation("今天生效的课表：{Plan}", patcher.DescribeTodayPlan());
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        // 构造函数里已经启动；这里只做幂等确认。
        return Task.CompletedTask;
    }

    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        Task? loop;
        CancellationTokenSource? cts;
        lock (_lifecycleLock)
        {
            loop = _loop;
            cts = _cts;
        }

        if (loop is null || cts is null)
        {
            return;
        }

        _logger.LogInformation("QQ 调课同步正在停止…");
        await cts.CancelAsync();
        try
        {
            await loop.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }

        lock (_lifecycleLock)
        {
            if (ReferenceEquals(_loop, loop))
            {
                _loop = null;
                _cts = null;
                cts.Dispose();
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // 支持热更新配置：改了 Settings.json 无需重启 ClassIsland。
                _settings.ReloadIfChanged();

                if (_settings.Enabled)
                {
                    var toasts = await _scanner.ScanAsync(cancellationToken);

                    if (_scanner.AccessStatus != UserNotificationListenerAccessStatus.Allowed
                        && !_warnedAboutAccess)
                    {
                        _warnedAboutAccess = true;
                        Show("QQ 调课同步", "没有通知监听权限，无法读取 QQ 消息。请检查 Windows 通知设置后重启 ClassIsland。");
                    }

                    foreach (var toast in toasts)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            HandleToast(toast);
                        }
                        catch (Exception ex)
                        {
                            // 单条消息处理失败不影响后续消息。
                            _logger.LogWarning(ex, "处理 QQ Toast #{Id} 失败", toast.Id);
                        }
                    }

                    failures = 0;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                _logger.LogWarning(ex, "调课同步轮询失败（连续 {Failures} 次）", failures);
            }

            // 连续失败时退避，避免刷屏；成功后恢复用户配置的间隔。
            var delay = failures > 0
                ? TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(failures, 5)))
                : TimeSpan.FromSeconds(Math.Clamp(_settings.ScanIntervalSeconds, 0.3, 10));

            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void HandleToast(ToastSnapshot toast)
    {
        var text = toast.FullText;
        if (!_parser.MentionsCommand(text, _settings.Keyword))
        {
            return;
        }

        var parse = _parser.Parse(text, _settings.Keyword);
        if (!parse.Success)
        {
            if (_settings.NotifyOnParseFailure)
            {
                Show("QQ 调课", $"收到一条调课消息，但{parse.Reason}");
            }

            return;
        }

        var command = parse.Command!;
        _logger.LogInformation("解析到指令：{Command}", command);

        if (!_settings.AutoApply)
        {
            Show("QQ 调课（预览）", $"解析到 {command}，当前为干跑模式，未执行");
            return;
        }

        var isUndo = command.Kind == TiaokeCommandKind.Undo;
        var outcome = isUndo ? _patcher.Undo() : _patcher.Apply(command);

        var title = (isUndo, outcome.Applied) switch
        {
            (true, true) => "调课已撤销",
            (true, false) => "撤销失败",
            (false, true) => "调课已同步",
            _ => "调课失败"
        };

        Show(title, $"{command}：{outcome.Message}");
    }

    private void Show(string title, string body)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ShowOnUiThread(title, body);
            return;
        }

        // 整个通知请求（包括 NotificationContent）都必须在 UI 线程构建：
        // NotificationContent.CreateTwoIconsMask 内部会 new LucideIconSource（Avalonia 对象），
        // 在后台线程构造会抛 "Call from invalid thread"，导致通知发不出去。
        Dispatcher.UIThread.Post(() => ShowOnUiThread(title, body));
    }

    private void ShowOnUiThread(string title, string body)
    {
        var request = new NotificationRequest
        {
            MaskContent = NotificationContent.CreateTwoIconsMask(title, rightIcon: "\uE02B", factory: content =>
            {
                content.Duration = TimeSpan.FromSeconds(2);
                content.SpeechContent = title;
            }),
            OverlayContent = NotificationContent.CreateSimpleTextContent(body, content =>
            {
                content.Duration = TimeSpan.FromSeconds(8);
                content.SpeechContent = body;
            }),
            RequestNotificationSettings =
            {
                IsSettingsEnabled = true,
                IsSpeechEnabled = true,
                IsNotificationEffectEnabled = true,
                IsNotificationSoundEnabled = true
            }
        };

        ShowNotification(request);
    }
}
