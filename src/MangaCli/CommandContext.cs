using System.Collections.Specialized;
using MangaGui.Models;
using MangaGui.Services;
using MangaGui.ViewModels;

namespace MangaCli;

/// <summary>
/// Result carried back to <see cref="CliApp"/> for serialization as the final <c>result</c> line.
/// </summary>
internal sealed class CommandResult
{
    public required string Command { get; init; }
    public int ExitCode { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> Warnings { get; } = [];
    public Dictionary<string, object?> Fields { get; } = [];

    public CommandResult Fail(int exitCode, string message)
    {
        ExitCode = exitCode;
        Message = message;
        return this;
    }
}

/// <summary>
/// Per-command state: parsed options, writers, the optional process lock, and the lazily created
/// headless host with events and prompts wired in.
/// </summary>
internal sealed class CommandContext : IAsyncDisposable
{
    public required CliEnvironment Env { get; init; }
    public required CliOptions Options { get; init; }
    public required EventWriter Writer { get; init; }
    public required CancellationToken Interrupt { get; init; }

    public ProcessLock? Lock { get; private set; }
    public HeadlessHost? Host { get; private set; }
    public CliPrompts? Prompts { get; set; }
    public OfflineGuardColabCli? Guard { get; private set; }

    private CancellationTokenSource? _billingLoopCts;
    private Task? _billingLoop;

    public AppServices Services => HostComponents!.Services;
    public SessionManager Session => Services.Session;
    public AccountManager Accounts => Services.Accounts;
    public SessionStore Store => Services.Store;
    public VmScripts Scripts => Services.Scripts;
    public IColabCli Cli => Services.Cli;
    public TaskQueue Queue => HostComponents!.Queue;
    public MainViewModel Vm => HostComponents!.ViewModel;
    private HeadlessHost.Components? HostComponents { get; set; }

    /// <summary>Acquires the shared GUI/CLI mutex; false means another front-end holds it.</summary>
    public bool TryAcquireLock()
    {
        if (Lock is not null)
        {
            return true;
        }

        return ProcessLock.TryAcquire(Env.MutexName, out var handle) && (Lock = handle) is not null;
    }

    /// <summary>
    /// Creates the headless host (STA dispatcher + full GUI composition), wires NDJSON events,
    /// attaches <see cref="Prompts"/> if set, and starts the billing event loop.
    /// <paramref name="offline"/> wraps the CLI in <see cref="OfflineGuardColabCli"/>.
    /// </summary>
    public async Task EnsureHostAsync(bool offline)
    {
        if (Host is not null)
        {
            return;
        }

        IColabCli? cli = Env.ColabFactory?.Invoke();
        if (offline)
        {
            cli ??= CreateDefaultCliForOffline();
            Guard = new OfflineGuardColabCli(cli);
            cli = Guard;
        }

        Host = new HeadlessHost(
            Options.DataDir,
            Env.AppBaseDirectory,
            cli,
            Env.UtcNow,
            Env.AutoStopRetryDelays,
            Env.TaskPollInterval,
            Env.BusyExpiryGrace,
            Env.IdleExpiryGrace);
        HostComponents = await Host.Ready;

        WireEvents();
        Prompts?.Attach(Vm);
        EmitState();
        EmitBilling();
        StartBillingLoop();
    }

    /// <summary>Runs <paramref name="work"/> on the headless dispatcher thread.</summary>
    public Task<T> InvokeAsync<T>(Func<Task<T>> work) => Host!.InvokeAsync(work);

    /// <summary>Runs <paramref name="work"/> on the headless dispatcher thread.</summary>
    public async Task InvokeAsync(Func<Task> work) => await Host!.InvokeAsync(async () => { await work(); return 0; });

    private IColabCli CreateDefaultCliForOffline()
    {
        var store = new SessionStore(Options.DataDir);
        return new ColabCli(configuredExePathProvider: () => store.LoadSettings().ColabExePath);
    }

    private void WireEvents()
    {
        var vm = Vm;

        ((INotifyCollectionChanged)vm.Logs).CollectionChanged += (_, e) =>
        {
            if (e.NewItems is null)
            {
                return;
            }

            foreach (LogEntry entry in e.NewItems)
            {
                Writer.Log(entry.Level, entry.Message);
            }
        };

        vm.PropertyChanged += (_, _) => EmitState();

        foreach (var item in vm.Tasks)
        {
            AttachTask(item);
        }

        ((INotifyCollectionChanged)vm.Tasks).CollectionChanged += (_, e) =>
        {
            if (e.NewItems is null)
            {
                return;
            }

            foreach (TranslationTaskItem item in e.NewItems)
            {
                AttachTask(item);
            }
        };

        vm.RequestSystemNotice += (title, message) => Writer.Notice(title, message);
        vm.RequestFiveMinuteWarningToast += message => Writer.Notice("five_minute_warning", message);
    }

    private void AttachTask(TranslationTaskItem item)
    {
        item.PropertyChanged += (_, _) => EmitTask(item);
        EmitTask(item);
    }

    private void EmitTask(TranslationTaskItem item) => Writer.TaskEvent(item);

    private void EmitState()
    {
        var session = Session;
        var vm = Vm;
        Writer.State(new Dictionary<string, object?>
        {
            ["session_state"] = session.State,
            ["has_active_vm"] = session.HasActiveVm,
            ["badge"] = vm.RuntimeBadgeText,
            ["detail"] = vm.StatusDetailText,
            ["deploy_stage"] = vm.DeployStageText,
            ["deploy_percent"] = vm.DeployProgressPercent,
            ["health"] = vm.HealthStatusDisplay,
            ["gpu"] = vm.GpuInfoDisplay,
            ["queue_paused"] = Queue.IsQueuePaused,
            ["pause_reason"] = Queue.PauseReason,
            ["error"] = vm.HasErrorBanner ? vm.ErrorBannerText : null,
            ["engine_mismatch"] = vm.HasEngineMismatch,
            ["account_id"] = session.ActiveSessionAccountId,
            ["account_name"] = session.ActiveSessionAccountName
        });
    }

    private void EmitBilling() => Writer.Billing(Session.Billing.Evaluate(fireEvents: false));

    private void StartBillingLoop()
    {
        _billingLoopCts = new CancellationTokenSource();
        var ct = _billingLoopCts.Token;
        var interval = Env.BillingEventInterval;
        var session = Session;
        var writer = Writer;

        _billingLoop = Task.Run(async () =>
        {
            var lastEmit = DateTimeOffset.Now;
            MangaGui.Models.BillingSeverity? lastSeverity = null;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var snap = session.Billing.Evaluate(fireEvents: false);
                var now = DateTimeOffset.Now;
                if (snap.Severity != lastSeverity || now - lastEmit >= interval)
                {
                    writer.Billing(snap);
                    lastSeverity = snap.Severity;
                    lastEmit = now;
                }
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_billingLoopCts is not null)
        {
            await _billingLoopCts.CancelAsync();
            try
            {
                if (_billingLoop is not null)
                {
                    await _billingLoop;
                }
            }
            catch
            {
                // Loop shutdown is best-effort.
            }
        }

        if (Host is not null)
        {
            await Host.DisposeAsync();
        }

        Lock?.Dispose();
    }
}
