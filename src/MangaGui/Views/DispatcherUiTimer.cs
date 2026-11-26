using System.Windows.Threading;
using MangaGui.ViewModels;

namespace MangaGui.Views;

/// <summary>
/// <see cref="IUiTimer"/> backed by <see cref="DispatcherTimer"/> so timers keep ticking on the UI
/// thread while the ViewModel layer itself stays free of WPF references.
/// </summary>
internal sealed class DispatcherUiTimer : IUiTimer
{
    private readonly DispatcherTimer _inner;

    public DispatcherUiTimer(TimeSpan interval)
    {
        _inner = new DispatcherTimer { Interval = interval };
        _inner.Tick += (_, _) => Tick?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Tick;

    public void Start() => _inner.Start();

    public void Stop() => _inner.Stop();
}
