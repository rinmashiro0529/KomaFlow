using CommunityToolkit.Mvvm.ComponentModel;

namespace MangaGui.ViewModels;

/// <summary>
/// Drives the time-expired dialog. The unattended-action deadline is anchored at the moment the
/// expiry request was raised (<paramref name="requestedAtUtc"/>), not at construction or display
/// time, so a dialog that sat in the queue cannot postpone the automatic stop. Only
/// <see cref="TimeExpiredAction.DrainThenStop"/> and <see cref="TimeExpiredAction.StopImmediately"/>
/// may run unattended — extending never does. <see cref="Choose"/> is idempotent: the first call
/// wins and completes <see cref="Completion"/>; a dialog that ends with the safe result resolves it
/// to <see cref="TimeExpiredAction.None"/> (remind later — no extension, no stop).
/// </summary>
public partial class TimeExpiredDialogViewModel : ObservableObject
{
    private readonly IUiTimer _timer;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly DateTimeOffset _deadlineUtc;
    private readonly TaskCompletionSource<TimeExpiredAction> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _chosen;

    public TimeExpiredDialogViewModel(
        TimeExpiredRequest request,
        DateTimeOffset requestedAtUtc,
        Func<DateTimeOffset> utcNow,
        IUiTimer timer)
    {
        _utcNow = utcNow;
        _timer = timer;

        AutoAction = request.AutoAction is TimeExpiredAction.DrainThenStop or TimeExpiredAction.StopImmediately
                     && request.AutoActionDelay > TimeSpan.Zero
            ? request.AutoAction
            : TimeExpiredAction.None;
        _deadlineUtc = requestedAtUtc + request.AutoActionDelay;

        IsDrainVisible = request.Scenario == TimeExpiredScenario.BusyWithTasks;
        AccentAction = IsDrainVisible ? TimeExpiredAction.DrainThenStop : TimeExpiredAction.StopImmediately;
        StopNowButtonText = IsDrainVisible
            ? "立即停止（丢弃未完成任务并结束计费）"
            : "立即停止并结束计费";

        var acctPrefix = !string.IsNullOrWhiteSpace(request.AccountName)
            ? $"账号「{request.AccountName}」"
            : string.Empty;

        (BadgeText, HeadlineText, SummaryText) = request.Scenario switch
        {
            TimeExpiredScenario.DeployingTimeout => (
                "时长已到 · 仍在部署阶段",
                "已到达设定时长，但环境仍在部署中",
                $"{acctPrefix}已运行 {request.ElapsedText}（{request.EstimatedCuText}），部署尚未完成。您可以延长 30 分钟等待部署完成，或立即停止并结束计费："),
            TimeExpiredScenario.ErrorWhileBilling => (
                "时长已到 · 会话异常且仍在计费",
                "已到达设定时长，当前会话处于异常状态",
                $"{acctPrefix}已运行 {request.ElapsedText}（{request.EstimatedCuText}），云端 VM 可能仍在计费。建议立即停止计费，或延长 30 分钟后重试修复："),
            TimeExpiredScenario.IdleNotAutoStop => (
                "时长已到 · 仅提醒模式",
                "已到达设定时长（当前队列空闲）",
                $"{acctPrefix}已运行 {request.ElapsedText}（{request.EstimatedCuText}）。您启动时未勾选自动停止，请选择延长时长或立即停止计费："),
            _ => (
                "时长已到 · 队列仍有任务",
                "已到达设定时长，但队列尚未完成",
                $"{acctPrefix}已运行 {request.ElapsedText}（{request.EstimatedCuText}），队列中还有 {request.PendingCount} 个未完成任务。请选择下一步处理方式：")
        };

        _timer.Tick += OnTick;
        UpdateCountdownText();
        if (AutoAction != TimeExpiredAction.None)
        {
            _timer.Start();
        }
    }

    /// <summary>Completes with the chosen (or automatic) action, exactly once.</summary>
    public Task<TimeExpiredAction> Completion => _completion.Task;

    /// <summary>Scenario badge text shown above the headline.</summary>
    public string BadgeText { get; }

    /// <summary>Scenario headline, also used as the dialog title.</summary>
    public string HeadlineText { get; }

    /// <summary>Scenario body explaining the billing state.</summary>
    public string SummaryText { get; }

    /// <summary>True only for <see cref="TimeExpiredScenario.BusyWithTasks"/>.</summary>
    public bool IsDrainVisible { get; }

    /// <summary>Accent button in the busy scenario: drain the queue, then stop.</summary>
    public string DrainButtonText => "翻完当前队列再停";

    /// <summary>Second button: keep billing for 30 more minutes.</summary>
    public string ExtendButtonText => "延长 30 分钟";

    /// <summary>Stop-now button label — scenario-specific per spec 9.2.</summary>
    public string StopNowButtonText { get; }

    /// <summary>Last button, equivalent to Esc: closes with no billing change.</summary>
    public string RemindLaterButtonText => "稍后提醒";

    /// <summary>The billing-safe action bound to the accent button and the Enter key.</summary>
    public TimeExpiredAction AccentAction { get; }

    /// <summary>The action taken unattended at the deadline; <see cref="TimeExpiredAction.None"/> when the dialog only waits.</summary>
    public TimeExpiredAction AutoAction { get; }

    [ObservableProperty]
    private string _countdownText = string.Empty;

    /// <summary>Records the user's (or the timer's) choice. First call wins; later calls are ignored.</summary>
    public void Choose(TimeExpiredAction action)
    {
        if (_chosen)
        {
            return;
        }

        _chosen = true;
        _timer.Stop();
        _completion.TrySetResult(action);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_utcNow() >= _deadlineUtc)
        {
            Choose(AutoAction);
            return;
        }

        UpdateCountdownText();
    }

    private void UpdateCountdownText()
    {
        const string tail = "关闭此对话框不会延长时长，也不会停止计费，稍后会再次提醒。";
        if (AutoAction == TimeExpiredAction.None)
        {
            CountdownText = tail;
            return;
        }

        var remaining = _deadlineUtc - _utcNow();
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var actionText = AutoAction == TimeExpiredAction.DrainThenStop
            ? "翻完当前队列后停止计费"
            : "停止并结束计费";
        CountdownText = $"无人操作时将在 {(int)remaining.TotalMinutes}:{remaining.Seconds:D2} 后自动{actionText}。{tail}";
    }
}
