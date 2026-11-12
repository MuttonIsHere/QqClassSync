using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using QqClassSync.Models;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace QqClassSync.Services;

/// <summary>
/// 轮询 Windows 通知中心，筛出 QQ 的 Toast。
///
/// 为什么用轮询而不是 NotificationChanged：
///   实测（以及 QQListener 的经验）表明，无包身份的进程订阅 NotificationChanged 会失败
///   （COMException 0x80070490），但 GetNotificationsAsync 可以正常读取。
///
/// 权限注意：RequestAccessAsync 必须在 UI 线程调用，否则部分系统版本直接返回 Denied。
/// 因此权限请求统一通过 Avalonia Dispatcher 投递到 UI 线程。
/// </summary>
public sealed class QqToastScanner(TiaokeSettings settings, ILogger<QqToastScanner> logger)
{
    private const int SeenCapacity = 512;

    private readonly SemaphoreSlim _accessGate = new(1, 1);
    private readonly HashSet<uint> _seenIds = [];
    private readonly Queue<uint> _seenOrder = new();
    private UserNotificationListenerAccessStatus _accessStatus = UserNotificationListenerAccessStatus.Unspecified;
    private bool _baselineCaptured;

    /// <summary>
    /// 确保已拿到通知监听权限。拒绝后不会反复请求，避免每次轮询都打系统接口。
    /// </summary>
    public async Task<bool> EnsureAccessAsync(CancellationToken cancellationToken)
    {
        await _accessGate.WaitAsync(cancellationToken);
        try
        {
            if (_accessStatus == UserNotificationListenerAccessStatus.Allowed)
            {
                return true;
            }

            if (_accessStatus == UserNotificationListenerAccessStatus.Denied)
            {
                return false;
            }

            var operation = await Dispatcher.UIThread.InvokeAsync(
                () => UserNotificationListener.Current.RequestAccessAsync());

            _accessStatus = await operation;

            TiaokeLog.Info(logger, "通知监听权限：{Status}", _accessStatus);
            if (_accessStatus != UserNotificationListenerAccessStatus.Allowed)
            {
                TiaokeLog.Warn(logger, 
                    "没有通知监听权限（{Status}）。请确认 Windows 通知设置允许应用访问通知，" +
                    "并注意权限是绑定到宿主进程的。",
                    _accessStatus);
                return false;
            }

            return true;        }
        finally
        {
            _accessGate.Release();
        }
    }

    /// <summary>
    /// 扫描一次，返回本次新出现的 QQ Toast。首次扫描只记录基线，不返回旧通知。
    /// </summary>
    public async Task<IReadOnlyList<ToastSnapshot>> ScanAsync(CancellationToken cancellationToken)
    {
        if (!await EnsureAccessAsync(cancellationToken))
        {
            return [];
        }

        IReadOnlyList<UserNotification> notifications;
        try
        {
            notifications = await UserNotificationListener.Current.GetNotificationsAsync(NotificationKinds.Toast);
        }
        catch (Exception ex)
        {
            TiaokeLog.Warn(logger, ex, "读取通知列表失败。");
            return [];
        }

        var fresh = new List<ToastSnapshot>();
        foreach (var notification in notifications)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_seenIds.Add(notification.Id))
            {
                continue;
            }

            RememberSeen(notification.Id);

            if (!_baselineCaptured)
            {
                continue;
            }

            if (!IsQqNotification(notification, out var appName, out var aumid))
            {
                continue;
            }

            fresh.Add(new ToastSnapshot(
                notification.Id,
                appName,
                aumid,
                ReadTexts(notification),
                notification.CreationTime));

            TiaokeLog.Info(logger, "收到 QQ Toast #{Id}: {Text}", notification.Id, fresh[^1].FullText);
        }

        _baselineCaptured = true;
        return fresh;
    }

    /// <summary>
    /// 最近一次权限请求的结果，供上层决定是否提示用户。
    /// </summary>
    public UserNotificationListenerAccessStatus AccessStatus => _accessStatus;

    /// <summary>
    /// 清空去重记录，下次扫描会重新建立基线。
    /// </summary>
    public void Reset()
    {
        _seenIds.Clear();
        _seenOrder.Clear();
        _baselineCaptured = false;
    }

    private void RememberSeen(uint id)
    {
        _seenOrder.Enqueue(id);
        while (_seenOrder.Count > SeenCapacity)
        {
            _seenIds.Remove(_seenOrder.Dequeue());
        }
    }

    private bool IsQqNotification(UserNotification notification, out string appName, out string aumid)
    {
        appName = string.Empty;
        aumid = string.Empty;
        try
        {
            appName = notification.AppInfo?.DisplayInfo?.DisplayName ?? string.Empty;
            aumid = notification.AppInfo?.AppUserModelId ?? string.Empty;
        }
        catch (Exception ex)
        {
            // 无包身份时读 AppInfo 可能抛异常：此时无法确认来源，按隐私优先直接忽略。
            TiaokeLog.Debug(logger, ex, "读取通知的应用身份失败，忽略该通知。");
            return false;
        }

        // 先落到局部变量：out 参数不能被 lambda 捕获。
        var trimmedName = appName.Trim();
        var trimmedId = aumid.Trim();

        if (settings.AllowedAppNames.Any(n =>
                string.Equals(n.Trim(), trimmedName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return settings.AllowedAppIds.Any(id =>
            string.Equals(id.Trim(), trimmedId, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] ReadTexts(UserNotification notification)
    {
        try
        {
            var binding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (binding is null)
            {
                return [];
            }

            return binding.GetTextElements()
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }
}
