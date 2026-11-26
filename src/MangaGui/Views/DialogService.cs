using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace MangaGui.Views;

/// <summary>
/// Serializes in-window dialogs so that only one <c>show</c> delegate runs at a time. Urgent
/// requests interrupt the currently displayed normal dialog (its token is cancelled, so it resolves
/// to <c>safeDefault</c>) and jump ahead of every queued normal request; urgent requests keep FIFO
/// order among themselves and queued normal requests are never cancelled by them — they resume
/// once all urgent dialogs have finished. <paramref name="externalCancel"/> lets a caller retract
/// its own request: a queued one completes with <c>safeDefault</c> without running, a displayed one
/// gets its token cancelled. Non-cancellation exceptions from <c>show</c> propagate to the caller
/// and never stall the queue.
///
/// The class holds no WPF-UI types itself, so unit tests can drive it without a window;
/// <see cref="DialogServiceContentDialogExtensions.ShowContentDialogAsync"/> adapts it to
/// <see cref="ContentDialog"/>.
/// </summary>
public sealed class DialogService
{
    private readonly Action<bool>? _onShowingChanged;
    private readonly object _gate = new();
    private readonly List<Request> _urgentQueue = new();
    private readonly List<Request> _normalQueue = new();
    private Request? _current;
    private bool _processing;

    /// <param name="onShowingChanged">
    /// Invoked on the caller's (UI) thread: <c>true</c> when a dialog starts displaying,
    /// <c>false</c> once it has finished. The window uses it to disable page content and the
    /// title-bar menu while remembering <see cref="System.Windows.Input.Keyboard.FocusedElement"/>,
    /// then restores both. Tests pass <c>null</c>.
    /// </param>
    public DialogService(Action<bool>? onShowingChanged = null)
    {
        _onShowingChanged = onShowingChanged;
    }

    /// <summary>True while a dialog is displayed. Always reset when the show delegate returns.</summary>
    public bool IsShowing { get; private set; }

    /// <summary>
    /// Enqueues a dialog. <paramref name="show"/> must observe the supplied token (for WPF-UI this
    /// is <c>dialog.ShowAsync(ct)</c>); when it ends in <see cref="OperationCanceledException"/> —
    /// interrupted by an urgent dialog or cancelled via <paramref name="externalCancel"/> — the
    /// returned task resolves to <paramref name="safeDefault"/>.
    /// </summary>
    public Task<T> ShowAsync<T>(
        Func<CancellationToken, Task<T>> show,
        T safeDefault,
        bool urgent = false,
        CancellationToken externalCancel = default)
    {
        var request = new Request<T>(show, safeDefault)
        {
            Urgent = urgent,
            Cts = CancellationTokenSource.CreateLinkedTokenSource(externalCancel)
        };
        if (externalCancel.CanBeCanceled)
        {
            request.Registration = externalCancel.Register(() => OnExternalCancel(request));
        }

        lock (_gate)
        {
            (urgent ? _urgentQueue : _normalQueue).Add(request);

            if (request.Cts.IsCancellationRequested && RemoveQueued(request))
            {
                // Already cancelled before it could run — resolves without ever executing show.
                request.CompleteQueuedRemoval();
            }
            else if (urgent && _current is { Urgent: false })
            {
                // Interrupt the normal dialog that is on screen: it resolves to its safeDefault.
                _current.Cts.Cancel();
            }
        }

        Pump();
        return request.Tcs.Task;
    }

    private void OnExternalCancel(Request request)
    {
        lock (_gate)
        {
            if (ReferenceEquals(request, _current))
            {
                request.Cts.Cancel();
                return;
            }

            if (RemoveQueued(request))
            {
                request.CompleteQueuedRemoval();
            }
        }
    }

    private bool RemoveQueued(Request request)
        => _normalQueue.Remove(request) | _urgentQueue.Remove(request);

    private async void Pump()
    {
        lock (_gate)
        {
            if (_processing)
            {
                return;
            }

            _processing = true;
        }

        try
        {
            while (true)
            {
                Request next;
                lock (_gate)
                {
                    if (_urgentQueue.Count > 0)
                    {
                        next = _urgentQueue[0];
                        _urgentQueue.RemoveAt(0);
                    }
                    else if (_normalQueue.Count > 0)
                    {
                        next = _normalQueue[0];
                        _normalQueue.RemoveAt(0);
                    }
                    else
                    {
                        _processing = false;
                        return;
                    }

                    _current = next;
                    IsShowing = true;
                }

                _onShowingChanged?.Invoke(true);
                try
                {
                    // No ConfigureAwait(false): continuations must stay on the UI thread.
                    await next.RunAsync(next.Cts.Token);
                }
                finally
                {
                    lock (_gate)
                    {
                        _current = null;
                        IsShowing = false;
                    }

                    _onShowingChanged?.Invoke(false);
                    next.Dispose();
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _processing = false;
            }
        }
    }

    private abstract class Request : IDisposable
    {
        public required CancellationTokenSource Cts { get; init; }
        public required bool Urgent { get; init; }
        public CancellationTokenRegistration Registration { get; set; }

        /// <summary>Runs the show delegate and completes the caller-facing task.</summary>
        public abstract Task RunAsync(CancellationToken ct);

        /// <summary>Resolves the caller-facing task when the request is dropped while queued.</summary>
        public abstract void CompleteQueuedRemoval();

        public virtual void Dispose()
        {
            Registration.Dispose();
            Cts.Dispose();
        }
    }

    private sealed class Request<T> : Request
    {
        private readonly Func<CancellationToken, Task<T>> _show;
        private readonly T _safeDefault;

        public Request(Func<CancellationToken, Task<T>> show, T safeDefault)
        {
            _show = show;
            _safeDefault = safeDefault;
        }

        public TaskCompletionSource<T> Tcs { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task RunAsync(CancellationToken ct)
        {
            try
            {
                Tcs.TrySetResult(await _show(ct));
            }
            catch (OperationCanceledException)
            {
                // Interrupted or externally cancelled: resolve to the billing-safe default.
                Tcs.TrySetResult(_safeDefault);
            }
            catch (Exception ex)
            {
                Tcs.TrySetException(ex);
            }
        }

        public override void CompleteQueuedRemoval()
        {
            Tcs.TrySetResult(_safeDefault);
            Dispose();
        }
    }
}

public static class DialogServiceContentDialogExtensions
{
    /// <summary>
    /// Displays <paramref name="dialog"/> inside the main window's dialog host through the queue.
    /// The task resolves to the <see cref="ContentDialogResult"/> produced by the dialog's buttons,
    /// or <see cref="ContentDialogResult.None"/> when the dialog is interrupted or cancelled —
    /// the billing-safe result, per design spec 9.1-5.
    /// </summary>
    public static Task<ContentDialogResult> ShowContentDialogAsync(
        this DialogService service,
        ContentDialog dialog,
        ContentPresenter host,
        bool urgent = false,
        CancellationToken externalCancel = default)
        => service.ShowAsync(
            async ct =>
            {
                dialog.DialogHost = host;
                return await dialog.ShowAsync(ct);
            },
            ContentDialogResult.None,
            urgent,
            externalCancel);
}
