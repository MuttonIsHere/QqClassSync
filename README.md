# QQ 调课同步（QqClassSync）

监听 QQ 群聊里的调课消息，自动给 ClassIsland 打「临时层课表补丁」，并在大屏弹出提醒。

> 与「小橙看班」项目完全独立。本目录是独立新项目。

## 目录结构

```
qq-sync/
├── ref/                     参考仓库（浅克隆）
│   ├── ClassIsland/         ClassIsland 主仓库（IPublicProfileService 源码依据）
│   ├── QQListener/          BSOD-MEMZ/QQListener（Python 版参考）
│   └── QQListener-alright/  alright-qwq/QQListener（本机已装插件的 C# 源码）
├── spike/
│   ├── QqNotifyProbe/       任务1：控制台通知探针（.NET 8）
│   └── legacy-csc/          早期无 SDK 时的 csc 探针（已归档，仅留作记录）
├── plugin/QqClassSync/      任务3：ClassIsland 插件
└── tools/FakeQqToast/       测试用假通知发送器
```

## 任务1：UserNotificationListener 探针

`spike/QqNotifyProbe` 是极简控制台程序：

```powershell
dotnet run --project qq-sync/spike/QqNotifyProbe -- 40
```

**结论（本机实测）**

| 问题 | 结论 |
|---|---|
| 能否拿到权限 | ✅ `AccessStatus = Allowed`（ClassIsland 宿主进程里也是 Allowed） |
| 能否读到 QQ Toast | ✅ 能，AppInfo 也可用（App 名 = `QQ`，AUMID = `QQ`） |
| 事件 `NotificationChanged` | ❌ 订阅抛 `COMException 0x80070490`，无包身份时事件不可用 |
| 可行方案 | **必须轮询** `GetNotificationsAsync(NotificationKinds.Toast)` |

**坑与规律**

1. `RequestAccessAsync` 必须在 UI 线程调用（STA + 消息泵；插件里走 Avalonia `Dispatcher.UIThread`）。
2. 权限按「可执行文件所在目录」记忆：在 `Documents\deepseek-harness\default-workspace` 下运行曾被判 `Denied`，
   同一 exe 复制到 `%TEMP%`、桌面、ClassIsland 应用目录均 `Allowed`。插件部署在 ClassIsland 应用目录，不受影响。
3. QQ Toast 的文本结构：`text[0]` = 群名，`text[1]` = 「发送者：内容」。

## 任务2：IPublicProfileService 临时层课表

接口定义：`ClassIsland.Shared.IPC/Abstractions/Services/IPublicProfileService.cs`
插件里注入的是 `ClassIsland.Core.Abstractions.Services.IProfileService : IPublicProfileService`。

实现在 `ClassIsland/Services/ProfileService.cs`，关键语义：

- `CreateTempClassPlan(id, ...)`：**复制**源课表 → 标记 `IsOverlay = true`、`OverlaySourceId = id`、
  名字加「（临时层）」，挂到 `Profile.OrderedSchedules[今天]`，并把 `Profile.OverlayClassPlanId` 指向它。
  今天已有临时层时返回 `null`（不会重复创建）。
- `ClassPlan.Classes` 与时间表里 `TimeType == 0`（上课）的时间点一一对应 ⇒ **第 X 节 = `Classes[X-1]`**。
- 改课就是把 `Classes[i].SubjectId` 换成目标科目 GUID，并 `IsChangedClass = true`。
- `ClearTempClassPlan()` 清掉当天临时层；`SetupTempClassPlanGroup(key)` / `ClearTempClassPlanGroup()` 用于整组临时课表。
- 改完必须 `SaveProfile()`。

## 任务3：插件

```
plugin/QqClassSync/
├── Plugin.cs                          [PluginEntrance] 注册服务
├── Services/TiaokeParser.cs           解析「#调课 第X节 A改B」（支持中文数字）
├── Services/QqToastScanner.cs         轮询通知中心 → 筛 QQ → 去重
├── Services/TempClassPlanPatcher.cs   打临时层课表补丁
├── Services/TiaokeNotificationProvider.cs  提醒提供方 + 后台循环
├── Services/TiaokeSettings.cs         设置（Settings.json）
└── manifest.yml
```

构建（含 `.cipx` 打包）：

```powershell
cd qq-sync/plugin/QqClassSync
dotnet build -c Release -p:CreateCipx=true -p:PowershellBinaryName=powershell
# 产物：bin/Release/net8.0-windows10.0.19041.0/QqClassSync.dll
#       cipx/QqClassSync.cipx（= 输出目录 zip + checksums.md MD5 清单）
```

> `.cipx` 由 `ClassIsland.PluginSdk` 的 `CreateCipx` target 生成，默认用 `pwsh`；本机只有 Windows PowerShell 5.1，
> 所以要传 `-p:PowershellBinaryName=powershell`。

部署（文件夹版）：

```powershell
Copy-Item .\bin\Release\net8.0-windows10.0.19041.0\* `
  "<ClassIsland 安装目录>\data\Plugins\QqClassSync\" -Force
```

设置文件：`<ClassIsland>\data\Config\Plugins\qq.classsync\Settings.json`

```json
{
  "Enabled": true,
  "Keyword": "#调课",
  "ScanIntervalSeconds": 1,
  "AutoApply": true,
  "NotifyOnParseFailure": true,
  "AllowedAppNames": ["QQ", "腾讯QQ", "TIM"],
  "AllowedAppIds": ["Tencent.QQNT", "com.tencent.qq", "com.tencent.tim", "QQ"]
}
```

- `AutoApply = false`：干跑模式，只提醒不改课表（方便先验证解析）。
- `AllowedAppNames` / `AllowedAppIds`：通知来源白名单，联调时可加入测试应用。

## 已验证 / 待验证

已验证（含日志证据）：

- 插件被 ClassIsland 2.1.0.1 加载：`QQ 调课同步(qq.classsync,0.1.0.0)`
- 提醒提供方注册：`注册提醒提供方：b7f3c1a2-...（QQ 调课同步）`
- 后台启动 + 权限：`QQ 调课同步已启动（间隔 1s…）`、`通知监听权限：Allowed`
- 探针在真实 QQ 群消息上抓到 Toast（群名 + 发送者：内容）

待验证：一条真实包含 `#调课` 的 QQ 消息 → 临时层课表被改写 + 大屏提醒。
（测试用的假 Toast 需要给进程注册 AUMID；本机 shell 的 AUMID 缓存需重新登录才刷新，未能自动发出。）

## 已知限制

- `NotificationChanged` 事件需要包身份，插件采用 1 秒轮询；轮询有 512 条去重窗口。
- 权限绑定宿主进程：若 ClassIsland 侧被拒，`Allowed` 会失败并弹一次提示。
- 当前没有设置页（改 `Settings.json` 即可）；后续可换 `PluginSettings` 基类做 UI。
