using System.Windows.Threading;
using MangaGui;
using MangaGui.Services;
using MangaGui.ViewModels;

namespace MangaCli;

/// <summary>
/// Hosts the exact same object graph as the GUI (SessionStore → VmScripts → IColabCli →
/// BillingTimer → SessionManager → TaskQueue → MainViewModel) on a dedicated STA thread running a
/// WPF <see cref="Dispatcher"/> — with no window. Timers, collection changes and await
/// continuations all run on that thread, identical to the GUI threading model.
/// </summary>
public sealed class HeadlessHost : IAsyncDisposable
{
    /// <summary>The composition created on the dispatcher thread.</summary>
    public sealed record Components(AppServices Services, TaskQueue Queue, MainViewModel ViewModel);

    private readonly TaskCompletionSource<Components> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private Dispatcher? _dispatcher;
    private Components? _components;

    public HeadlessHost(
        string? dataDirectory,
        string? appBaseDirectory,
        IColabCli? cli,
        Func<DateTimeOffset>? utcNow,
        TimeSpan[]? autoStopRetryDelays,
        TimeSpan? taskPollInterval,
        TimeSpan? busyExpiryGrace,
        TimeSpan? idleExpiryGrace)
    {
        _thread = new Thread(() => ThreadMain(
                dataDirectory, appBaseDirectory, cli, utcNow, autoStopRetryDelays,
                taskPollInterval, busyExpiryGrace, idleExpiryGrace))
        {
            IsBackground = true,
            Name = "MangaCli.Dispatcher"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Completes once the composition exists and the dispatcher loop is about to start.</summary>
    public Task<Components> Ready => _ready.Task;

    private void ThreadMain(
        string? dataDirectory,
        string? appBaseDirectory,
        IColabCli? cli,
        Func<DateTimeOffset>? utcNow,
        TimeSpan[]? autoStopRetryDelays,
        TimeSpan? taskPollInterval,
        TimeSpan? busyExpiryGrace,
        TimeSpan? idleExpiryGrace)
    {
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _dispatcher = dispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

            var services = AppServices.Create(
                dataDirectory, appBaseDirectory, cli, utcNow, autoStopRetryDelays);
            Action<Action> safeDispatch = action => App.SafeDispatchToUi(dispatcher, action);
            var queue = new TaskQueue(
                services.Cli, services.Session, safeDispatch,
                pollInterval: taskPollInterval, utcNow: utcNow);
            var vm = new MainViewModel(
                services.Cli, services.Scripts, services.Session, queue,
                themeService: null, dispatchToUi: safeDispatch, store: services.Store,
                timerFactory: interval => new DispatcherCliTimer(interval))
            {
                // The CLI reports probe failures through exit codes instead of background retries.
                StartupProbeRetryDelays = []
            };
            if (busyExpiryGrace is { } busy)
            {
                vm.BusyExpiryGrace = busy;
            }
            if (idleExpiryGrace is { } idle)
            {
                vm.IdleExpiryGrace = idle;
            }

            _components = new Components(services, queue, vm);
            _ready.TrySetResult(_components);
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the dispatcher thread; await continuations inside it stay on
    /// the dispatcher, exactly like a GUI event handler.
    /// </summary>
    public Task<T> InvokeAsync<T>(Func<Task<T>> work)
    {
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("Headless host is not started.");
        return dispatcher.InvokeAsync(work).Task.Unwrap();
    }

    /// <summary>Non-generic overload: runs <paramref name="work"/> on the dispatcher thread.</summary>
    public async Task InvokeAsync(Func<Task> work) => await InvokeAsync(async () => { await work(); return 0; });

    public async ValueTask DisposeAsync()
    {
        var dispatcher = _dispatcher;
        var components = _components;
        if (dispatcher is not null && components is not null)
        {
            try
            {
                if (!dispatcher.HasShutdownFinished)
                {
                    await dispatcher.InvokeAsync(() =>
                    {
                        components.Queue.CancelPendingAndActive();
                        components.ViewModel.Dispose();
                    }).Task;
                }
            }
            catch
            {
                // The dispatcher may already be shutting down; proceed to shutdown regardless.
            }

            try
            {
                dispatcher.InvokeShutdown();
            }
            catch
            {
                // Best-effort shutdown.
            }
        }

        if (!_thread.Join(TimeSpan.FromSeconds(15)))
        {
            throw new InvalidOperationException("Headless dispatcher thread did not exit within 15s.");
        }
    }
}
