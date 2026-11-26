using System.Windows.Threading;
using MangaGui.ViewModels;

namespace MangaCli;

/// <summary>
/// <see cref="IUiTimer"/> backed by <see cref="DispatcherTimer"/> for the headless host — the same
/// behavior as the GUI's DispatcherUiTimer (which is internal to MangaGui). A real ticking timer is
/// required: the expiry auto-stop is driven by MainViewModel's 1-second billing timer calling
/// Billing.Evaluate(fireEvents: true).
/// </summary>
public sealed class DispatcherCliTimer : IUiTimer
{
    private readonly DispatcherTimer _inner;

    public DispatcherCliTimer(TimeSpan interval)
    {
        _inner = new DispatcherTimer { Interval = interval };
        _inner.Tick += (_, _) => Tick?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Tick;

    public void Start() => _inner.Start();

    public void Stop() => _inner.Stop();
}
