namespace MangaGui.ViewModels;

/// <summary>
/// Periodic UI timer abstraction so <see cref="MainViewModel"/> does not depend on WPF's
/// DispatcherTimer. The app injects a DispatcherTimer-backed factory; tests inject
/// <see cref="ManualUiTimer"/> instances they can fire by hand.
/// </summary>
public interface IUiTimer
{
    /// <summary>Raised each time the interval elapses. Thread affinity belongs to the implementation.</summary>
    event EventHandler? Tick;

    void Start();

    void Stop();
}

/// <summary>
/// Manually driven <see cref="IUiTimer"/> for tests: fires <see cref="IUiTimer.Tick"/> only from
/// <see cref="RaiseTick"/> while enabled, so nothing runs on a background clock unless asked.
/// </summary>
public sealed class ManualUiTimer : IUiTimer
{
    public ManualUiTimer(TimeSpan interval)
    {
        Interval = interval;
    }

    public TimeSpan Interval { get; }

    public bool IsEnabled { get; private set; }

    public event EventHandler? Tick;

    public void Start() => IsEnabled = true;

    public void Stop() => IsEnabled = false;

    public void RaiseTick()
    {
        if (IsEnabled)
        {
            Tick?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>
/// Fallback used when no timer factory is injected: never fires, matching the previous behavior of
/// a DispatcherTimer created on a thread without a dispatcher loop.
/// </summary>
public sealed class NullUiTimer : IUiTimer
{
    public event EventHandler? Tick
    {
        add { }
        remove { }
    }

    public void Start()
    {
    }

    public void Stop()
    {
    }
}
