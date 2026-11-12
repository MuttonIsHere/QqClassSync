// QqNotifyProbe —— 任务1：极简 C# 控制台探针（.NET 8 + 标准 SDK）
//
// 验证目标：
//   1) UserNotificationListener 在当前进程能否拿到通知监听权限；
//   2) 能否枚举/实时拿到 QQ 的 Toast 通知；
//   3) "#调课" 消息长什么样、正则能不能解析。
//
// 运行：
//   dotnet run --project qq-sync/spike/QqNotifyProbe -- 40
//
// 说明：
//   - RequestAccessAsync 必须在 UI 线程（STA + 消息泵）上调用。QQListener 也是在
//     Avalonia UI 线程上调用的；在 MTA 线程上直接调，部分系统版本会直接返回 Denied。
//   - unpackaged 进程读 UserNotification.AppInfo 可能抛异常（拿不到应用身份），
//     所以 AppInfo 的读取全部包在 try/catch 里，失败时标记为「AppInfo 不可用」。
//   - 事件 NotificationChanged 在无包身份时可能静默不触发，因此轮询兜底。

using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Foundation;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace QqSync.Spike;

internal static class Program
{
    private const uint PM_REMOVE = 0x0001;

    // 老师群聊里约定的调课指令，例如：第3节 语文改数学 / #调课 第3节 语文改数学
    private static readonly Regex TiaokePattern = new(
        @"第\s*(?<lesson>[0-9一二三四五六七八九十]+)\s*节\s*(?<from>[^\s改]+)\s*改\s*(?<to>[^\s，,。；;]+)",
        RegexOptions.Compiled);

    private static int _eventCount;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var listenSeconds = 40;
        if (args.Length > 0 && int.TryParse(args[0], out var parsed) && parsed >= 0)
        {
            listenSeconds = parsed;
        }

        Console.WriteLine("=== QQ Toast 监听探针（.NET 8 + UserNotificationListener）===");
        Console.WriteLine($"exe    : {Environment.ProcessPath}");
        Console.WriteLine($"user   : {Environment.UserName}");
        Console.WriteLine($"OS     : {Environment.OSVersion}");
        Console.WriteLine($"listen : {listenSeconds}s");
        Console.WriteLine();

        UserNotificationListener listener;
        try
        {
            listener = UserNotificationListener.Current;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"!! UserNotificationListener.Current 失败: {Describe(ex)}");
            return 1;
        }

        // ---- 1. 当前权限（不触发授权流程）----
        Console.WriteLine("--- 1. GetAccessStatus ---");
        try
        {
            Console.WriteLine($"AccessStatus = {listener.GetAccessStatus()}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"!! GetAccessStatus 失败: {Describe(ex)}");
        }

        // ---- 2. 在 UI 线程上请求权限 ----
        Console.WriteLine();
        Console.WriteLine("--- 2. RequestAccessAsync（STA + 消息泵）---");
        UserNotificationListenerAccessStatus status;
        try
        {
            var op = listener.RequestAccessAsync();
            status = PumpUntil(op, TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"!! RequestAccessAsync 异常: {Describe(ex)}");
            return 1;
        }

        Console.WriteLine($"AccessStatus = {status}");
        if (status != UserNotificationListenerAccessStatus.Allowed)
        {
            Console.WriteLine();
            Console.WriteLine("结论：当前进程没有通知监听权限，无法读取 Toast。");
            Console.WriteLine("排查：1) 设置 → 系统 → 通知，确认允许应用访问通知；");
            Console.WriteLine("      2) 首次请求失败后，权限会被按「可执行文件所在目录」记住（实测），");
            Console.WriteLine("         换个目录或注册包身份后再试；");
            Console.WriteLine("      3) 官方要求：打包应用需声明 userNotificationListener 能力。");
            return 1;
        }

        // ---- 3. 事件订阅（验证无包身份时是否有效）----
        Console.WriteLine();
        Console.WriteLine("--- 3. 订阅 NotificationChanged ---");
        try
        {
            listener.NotificationChanged += OnNotificationChanged;
            Console.WriteLine("已订阅");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"!! 订阅失败: {Describe(ex)}");
        }

        // ---- 4. 枚举已有通知 ----
        Console.WriteLine();
        Console.WriteLine("--- 4. 已有 Toast ---");
        try
        {
            var existing = listener.GetNotificationsAsync(NotificationKinds.Toast).GetAwaiter().GetResult();
            Console.WriteLine($"已有通知条数 = {existing.Count}");
            foreach (var notification in existing)
            {
                Dump(notification, "存在");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"!! GetNotificationsAsync 异常: {Describe(ex)}");
        }

        // ---- 5. 监听（事件 + 轮询兜底）----
        Console.WriteLine();
        Console.WriteLine("--- 5. 监听中（目标：在 QQ 里发一条含 #调课 的消息）---");
        var deadline = DateTime.Now.AddSeconds(listenSeconds);
        string? lastSeen = null;
        while (DateTime.Now < deadline)
        {
            Pump(50);

            try
            {
                var list = listener.GetNotificationsAsync(NotificationKinds.Toast).GetAwaiter().GetResult();
                foreach (var notification in list)
                {
                    var key = $"{notification.Id}|{notification.CreationTime:O}";
                    if (key == lastSeen)
                    {
                        continue;
                    }

                    lastSeen = key;
                    Dump(notification, "轮询");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"!! 轮询异常: {Describe(ex)}");
            }

            Thread.Sleep(1000);
        }

        Console.WriteLine();
        Console.WriteLine($"监听结束。NotificationChanged 事件触发次数 = {_eventCount}");
        return 0;
    }

    private static void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        Interlocked.Increment(ref _eventCount);
        try
        {
            Console.WriteLine();
            Console.WriteLine($">>> NotificationChanged: {args.ChangeKind} id={args.UserNotificationId}");

            // 事件参数只带 ID，不带通知对象，必须再回查一次。
            var notification = sender.GetNotification(args.UserNotificationId);
            if (notification != null)
            {
                Dump(notification, "事件");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"!! 事件处理异常: {Describe(ex)}");
        }
    }

    private static void Dump(UserNotification notification, string tag)
    {
        var appKnown = TryReadAppIdentity(notification, out var app, out var aumid);

        var texts = Array.Empty<string>();
        try
        {
            var binding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (binding != null)
            {
                texts = binding.GetTextElements()
                    .Select(t => t.Text)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToArray();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    (读取文本失败: {ex.GetType().Name})");
        }

        var appLabel = appKnown ? app : "<AppInfo 不可用>";
        Console.WriteLine($"[{tag}] {notification.CreationTime:HH:mm:ss} app='{appLabel}' aumid='{aumid}'");
        for (var i = 0; i < texts.Length; i++)
        {
            Console.WriteLine($"    text[{i}] = {texts[i]}");
        }

        var joined = string.Join(" ", texts);
        if (!joined.Contains("#调课"))
        {
            return;
        }

        Console.WriteLine("    >>> 命中 #调课");
        var match = TiaokePattern.Match(joined);
        if (match.Success)
        {
            Console.WriteLine($"    解析: 第{match.Groups["lesson"].Value}节 "
                              + $"{match.Groups["from"].Value} -> {match.Groups["to"].Value}");
        }
        else
        {
            Console.WriteLine("    解析: 规则未命中（记录原文以便调整正则）");
        }
    }

    private static bool TryReadAppIdentity(UserNotification notification, out string app, out string aumid)
    {
        app = string.Empty;
        aumid = string.Empty;
        try
        {
            app = notification.AppInfo?.DisplayInfo?.DisplayName ?? string.Empty;
            aumid = notification.AppInfo?.AppUserModelId ?? string.Empty;
            return true;
        }
        catch
        {
            // unpackaged 进程读 AppInfo 常见异常，视为身份不可用。
            return false;
        }
    }

    private static void Pump(int milliseconds)
    {
        var until = DateTime.Now.AddMilliseconds(milliseconds);
        while (DateTime.Now < until)
        {
            while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            Thread.Sleep(20);
        }
    }

    private static T PumpUntil<T>(IAsyncOperation<T> operation, TimeSpan timeout)
    {
        var until = DateTime.Now.Add(timeout);
        while (operation.Status == AsyncStatus.Started && DateTime.Now < until)
        {
            Pump(20);
        }

        if (operation.Status != AsyncStatus.Completed)
        {
            throw new TimeoutException($"等待异步操作完成超时，状态 = {operation.Status}");
        }

        return operation.GetResults();
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message} (0x{ex.HResult:X8})";

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PtX;
        public int PtY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PeekMessage(out Msg msg, IntPtr hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(ref Msg msg);
}
