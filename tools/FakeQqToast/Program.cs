// FakeQqToast —— 端到端测试用的「假通知」发送器。
//
// 桌面进程发 Toast 需要给本进程设置一个「已在开始菜单注册」的 AUMID。
// 本机实测 "Microsoft.Windows.PowerShell" 可用（Windows PowerShell 快捷方式自带该 AUMID），
// 所以联调时用它发一条带 #调课 的假消息，验证 QQ 调课同步插件的完整链路。
//
// 用法：
//   FakeQqToast.exe [aumid] [发送者] [消息文本]
//   FakeQqToast.exe Microsoft.Windows.PowerShell 班主任王老师 "#调课 第3节 语文改数学"

using System.Runtime.InteropServices;
using System.Security;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace QqSync.Tools;

internal static class Program
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    private static int Main(string[] args)
    {
        // --inherit：不设置显式 AUMID，直接继承「通过带 AUMID 的快捷方式启动」时得到的身份。
        var inherit = args.Length > 0 && args[0] == "--inherit";
        if (inherit)
        {
            args = args[1..];
        }

        var aumid = args.Length > 0 ? args[0] : "Microsoft.Windows.PowerShell";
        var sender = args.Length > 1 ? args[1] : "班主任王老师";
        var message = args.Length > 2 ? args[2] : "#调课 第3节 语文改数学";

        if (!inherit)
        {
            var hr = SetCurrentProcessExplicitAppUserModelID(aumid);
            if (hr != 0)
            {
                Console.WriteLine($"设置 AUMID 失败: 0x{hr:X8}");
                return 1;
            }
        }

        var xml = new XmlDocument();
        xml.LoadXml(
            "<toast><visual><binding template=\"ToastGeneric\">" +
            $"<text>{SecurityElement.Escape(sender)}</text>" +
            $"<text>{SecurityElement.Escape(message)}</text>" +
            "</binding></visual></toast>");

        var toast = new ToastNotification(xml);
        try
        {
            ToastNotificationManager.CreateToastNotifier().Show(toast);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"发送失败: {ex.GetType().Name}: {ex.Message} (0x{ex.HResult:X8})");
            return 1;
        }

        Console.WriteLine($"已发送 Toast: aumid={aumid} sender={sender} message={message}");
        return 0;
    }
}
