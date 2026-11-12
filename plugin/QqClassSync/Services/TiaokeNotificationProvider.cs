using System.Collections.Concurrent;
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
    private readonly AiTiaokeParser _aiParser;
    private readonly TempClassPlanPatcher _patcher;
    private readonly ILogger<TiaokeNotificationProvider> _logger;

    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _warnedAboutAccess;
    private IReadOnlyList<TiaokeCommand>? _pendingCommands;
    private DateTime _pendingExpiresAt;
    private string _pendingSource = "QQ";

    /// <summary>其它插件（如「小橙看班联动」）转发进来的消息队列。</summary>
    private readonly ConcurrentQueue<(string Text, string Source)> _externalInbox = new();

    public TiaokeNotificationProvider(
        TiaokeSettings settings,
        QqToastScanner scanner,
        TiaokeParser parser,
        AiTiaokeParser aiParser,
        TempClassPlanPatcher patcher,
        ILogger<TiaokeNotificationProvider> logger)
    {
        _settings = settings;
        _scanner = scanner;
        _parser = parser;
        _aiParser = aiParser;
        _patcher = patcher;
        _logger = logger;

        // 让其它插件能通过反射找到本插件并投递消息（见 Bridge/TiaokeBridge.cs）。
        Bridge.TiaokeBridge.Provider = this;

        lock (_lifecycleLock)
        {
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        }

        TiaokeLog.Info(_logger, 
            "QQ 调课同步已启动（间隔 {Interval}s，关键词 {Keyword}，自动应用 {AutoApply}）",
            settings.ScanIntervalSeconds, settings.Keyword, settings.AutoApply);
        TiaokeLog.Info(_logger, "今天生效的课表：{Plan}", patcher.DescribeTodayPlan());
        TiaokeLog.Info(_logger, 
            "AI 解析：{Status}",
            _aiParser.IsConfigured
                ? "已配置（调用 /chat/completions，8 秒超时，失败自动回退正则）"
                : "未启用或未填写 Key，使用正则解析（不会报错）");
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

        TiaokeLog.Info(_logger, "QQ 调课同步正在停止…");
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

                if (!_settings.Enabled)
                {
                    // 监听关闭时丢弃外部消息，避免重新开启后处理一堆过期指令。
                    while (_externalInbox.TryDequeue(out _))
                    {
                    }
                }
                else
                {
                    // 其它插件（如「小橙看班联动」）转发进来的消息：与 QQ 消息走同一套解析管线。
                    while (_externalInbox.TryDequeue(out var external))
                    {
                        try
                        {
                            await HandleTextAsync(external.Text, external.Source, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            TiaokeLog.Warn(_logger, ex, "处理外部消息失败（来源：{Source}）", external.Source);
                        }
                    }
                }

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
                            await HandleToastAsync(toast, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            // 单条消息处理失败不影响后续消息。
                            TiaokeLog.Warn(_logger, ex, "处理 QQ Toast #{Id} 失败", toast.Id);
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
                TiaokeLog.Warn(_logger, ex, "调课同步轮询失败（连续 {Failures} 次）", failures);
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

    private async Task HandleToastAsync(ToastSnapshot toast, CancellationToken cancellationToken)
    {
        await HandleTextAsync(toast.FullText, "QQ", cancellationToken);
    }

    /// <summary>
    /// 处理一条文本（QQ Toast，或其它插件经 <see cref="Bridge.TiaokeBridge"/> 转发过来的消息）。
    /// </summary>
    /// <param name="text">消息正文。</param>
    /// <param name="source">来源标签（QQ / 小橙看班 / …），用于提醒里标注来源。</param>
    private async Task HandleTextAsync(string text, string source, CancellationToken cancellationToken)
    {
        // 先处理「待确认 AI 调课」的操作词：#确认 应用、#放弃 取消。
        if (TiaokeParser.IsConfirmText(text))
        {
            await ApplyPendingAiCommandAsync(cancellationToken);
            return;
        }

        if (TiaokeParser.IsDropText(text))
        {
            if (_pendingCommands is not null)
            {
                _pendingCommands = null;
                Show("调课待确认", $"{Prefix(source)}已放弃待确认的 AI 调课");
            }

            return;
        }

        // 入口闸门：
        //   - 命中关键词/撤销词 → 走正则（快、免费）；
        //   - AI 已配置且消息像调课意图（如「今天第三节课不上了，让数学老师来上」）→ 允许进入 AI。
        var mentionsCommand = _parser.MentionsCommand(text, _settings.Keyword);
        var aiCandidate = _aiParser.IsConfigured && AiParsePolicy.LooksLikeIntent(text);
        if (!mentionsCommand && !aiCandidate)
        {
            return;
        }

        // 先用正则：命中就结束（快、免费、离线可靠）。
        // 没命中且 AI 候选时，才尝试 AI 模糊解析；AI 不可用/失败会返回 null，自动回退。
        var parse = _parser.Parse(text, _settings.Keyword);
        var fromAi = false;
        if (!parse.Success && aiCandidate)
        {
            var aiResult = await _aiParser.TryParseAsync(text, cancellationToken);
            if (aiResult is { Success: true })
            {
                parse = aiResult;
                fromAi = true;
            }
        }

        if (!parse.Success)
        {
            // 只有「明确发了指令词」的消息才提示格式错误；闲聊类消息保持安静。
            if (mentionsCommand && _settings.NotifyOnParseFailure)
            {
                Show("调课解析失败", $"{Prefix(source)}收到一条调课消息，但{parse.Reason}");
            }

            return;
        }

        var commands = parse.Commands;
        TiaokeLog.Info(_logger, 
            "解析到 {Count} 条指令（来源：{Parser} / {Source}）",
            commands.Count,
            fromAi ? "AI" : "正则",
            source);

        if (!_settings.AutoApply)
        {
            Show("调课（预览）", $"{Prefix(source)}解析到：{Describe(commands)}，当前为干跑模式，未执行");
            return;
        }

        // AI 解析的结果默认要先确认（防误判/防把闲聊当调课）：先挂起，等老师发「#确认」再由下面应用。
        if (fromAi && _settings.AiConfirmBeforeSend)
        {
            _pendingCommands = commands;
            _pendingSource = source;
            _pendingExpiresAt = DateTime.Now.AddMinutes(10);
            Show("AI 调课（待确认）",
                $"{Prefix(source)}检测到批量调课：{Describe(commands)}。回复 #确认 应用全部，或回复 #放弃 取消（10 分钟内有效）");
            return;
        }

        await ApplyCommandsAndNotifyAsync(commands, source, cancellationToken);
    }

    /// <summary>把指令列表渲染成「1. 第3节 语文→数学；2. 第5节 英语→物理」。</summary>
    private static string Describe(IReadOnlyList<TiaokeCommand> commands) =>
        commands.Count == 1
            ? commands[0].ToString()
            : string.Join("；", commands.Select((c, index) => $"{index + 1}. {c}"));

    /// <summary>
    /// 来源前缀：QQ 不显示，其它来源（如小橙看班）在提醒正文里标注，方便分辨消息从哪来。
    /// </summary>
    private static string Prefix(string source) =>
        string.Equals(source, "QQ", StringComparison.OrdinalIgnoreCase) ? string.Empty : $"【来源：{source}】";

    /// <summary>
    /// 供其它插件投递消息（经 <see cref="Bridge.TiaokeBridge"/> 调用）。线程安全。
    /// </summary>
    public bool SubmitExternal(string? text, string? source)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        _externalInbox.Enqueue((text, string.IsNullOrWhiteSpace(source) ? "外部" : source!));
        return true;
    }

    /// <summary>
    /// 执行一批调课/撤销并弹出结果提醒。返回是否有成功项（异常时返回 false，便于保留待确认状态）。
    ///
    /// 注意：必须在 UI 线程执行。CreateTempClassPlan() 会修改 Profile.OverlayClassPlanId，
    /// 触发 ClassIsland 课表网格（Avalonia 控件）的响应式刷新；在后台线程调用会抛
    /// "Call from invalid thread"。
    /// </summary>
    private async Task<bool> ApplyCommandsAndNotifyAsync(
        IReadOnlyList<TiaokeCommand> commands,
        string source,
        CancellationToken cancellationToken)
    {
        BatchPatchOutcome outcome;
        try
        {
            outcome = await Dispatcher.UIThread.InvokeAsync(() => ExecuteOnUiThread(commands));
        }
        catch (Exception ex)
        {
            // 不再静默：日志写清楚，提醒也告诉用户去看日志。
            TiaokeLog.Warn(_logger, ex, "执行调课失败（{Count} 条 / {Source}）", commands.Count, source);
            Show("调课失败", $"{Prefix(source)}{Describe(commands)}：执行时出错（{ex.GetType().Name}），详情见 ClassIsland 日志");
            return false;
        }

        var title = (outcome.AnyApplied, outcome.Failures.Count) switch
        {
            (true, 0) => "调课已执行",
            (true, _) => "调课部分成功",
            _ => "调课失败"
        };

        Show(title, $"{Prefix(source)}{outcome.Summary()}");
        return outcome.AnyApplied;
    }

    /// <summary>在 UI 线程内执行：撤销 + 调课/互换，汇总成「部分成功」结果。</summary>
    private BatchPatchOutcome ExecuteOnUiThread(IReadOnlyList<TiaokeCommand> commands)
    {
        var applied = new List<string>();
        var failures = new List<string>();

        var undoCommands = commands.Where(c => c.Kind == TiaokeCommandKind.Undo).ToList();
        if (undoCommands.Count > 0)
        {
            var lessons = undoCommands.Where(c => c.Lesson > 0).Select(c => c.Lesson).Distinct().ToList();
            var undoAll = undoCommands.Any(c => c.Lesson <= 0);
            var undoOutcome = _patcher.UndoLessons(undoAll ? Array.Empty<int>() : lessons);
            applied.AddRange(undoOutcome.Applied);
            failures.AddRange(undoOutcome.Failures);
        }

        var workCommands = commands.Where(c => c.Kind != TiaokeCommandKind.Undo).ToList();
        if (workCommands.Count > 0)
        {
            var applyOutcome = _patcher.ApplyBatch(workCommands);
            applied.AddRange(applyOutcome.Applied);
            failures.AddRange(applyOutcome.Failures);
        }

        return new BatchPatchOutcome(applied, failures);
    }

    /// <summary>
    /// 处理「#确认」：应用挂起的 AI 批量调课。执行失败时保留待确认状态，允许重试。
    /// </summary>
    private async Task ApplyPendingAiCommandAsync(CancellationToken cancellationToken)
    {
        if (_pendingCommands is not { } commands)
        {
            Show("调课待确认", "当前没有待确认的调课");
            return;
        }

        if (DateTime.Now > _pendingExpiresAt)
        {
            _pendingCommands = null;
            Show("调课待确认", "待确认的调课已过期（超过 10 分钟），请重新发送");
            return;
        }

        var applied = await ApplyCommandsAndNotifyAsync(commands, _pendingSource, cancellationToken);
        if (applied || _pendingCommands is null)
        {
            _pendingCommands = null;
        }
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
