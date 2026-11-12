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
/// </summary>
[NotificationProviderInfo(
    "b7f3c1a2-5d4e-4f6a-9b8c-0d1e2f3a4b5c",
    "QQ 调课同步",
    "\uE02B",
    "监听 QQ 群聊中的「#调课」消息，自动修改临时层课表并提醒。")]
public sealed class TiaokeNotificationProvider(
    TiaokeSettings settings,
    QqToastScanner scanner,
    TiaokeParser parser,
    TempClassPlanPatcher patcher,
    ILogger<TiaokeNotificationProvider> logger) : NotificationProviderBase, IHostedService
{
    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _warnedAboutAccess;

    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
        {
            if (_loop is { IsCompleted: false })
            {
                return Task.CompletedTask;
            }

            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        }

        logger.LogInformation(
            "QQ 调课同步已启动（间隔 {Interval}s，关键词 {Keyword}，自动应用 {AutoApply}）",
            settings.ScanIntervalSeconds, settings.Keyword, settings.AutoApply);
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

        logger.LogInformation("QQ 调课同步正在停止…");
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
                if (settings.Enabled)
                {
                    var toasts = await scanner.ScanAsync(cancellationToken);

                    if (scanner.AccessStatus != UserNotificationListenerAccessStatus.Allowed
                        && !_warnedAboutAccess)
                    {
                        _warnedAboutAccess = true;
                        Show("QQ 调课同步", "没有通知监听权限，无法读取 QQ 消息。请检查 Windows 通知设置后重启 ClassIsland。");
                    }

                    foreach (var toast in toasts)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        HandleToast(toast);
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
                logger.LogWarning(ex, "调课同步轮询失败（连续 {Failures} 次）", failures);
            }

            // 连续失败时退避，避免刷屏；成功后恢复用户配置的间隔。
            var delay = failures > 0
                ? TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(failures, 5)))
                : TimeSpan.FromSeconds(settings.ScanIntervalSeconds);

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
        if (!text.Contains(settings.Keyword, StringComparison.Ordinal))
        {
            return;
        }

        var parse = parser.Parse(text, settings.Keyword);
        if (!parse.Success)
        {
            if (settings.NotifyOnParseFailure)
            {
                Show("QQ 调课", $"收到一条调课消息，但{parse.Reason}");
            }

            return;
        }

        var command = parse.Command!;
        if (!settings.AutoApply)
        {
            Show("QQ 调课（预览）", $"解析到 {command}，当前为干跑模式，未修改课表");
            return;
        }

        var outcome = patcher.Apply(command);
        Show(outcome.Applied ? "调课已同步" : "调课失败", $"{command}：{outcome.Message}");
    }

    private void Show(string title, string body)
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

        if (Dispatcher.UIThread.CheckAccess())
        {
            ShowNotification(request);
        }
        else
        {
            Dispatcher.UIThread.Post(() => ShowNotification(request));
        }
    }
}
