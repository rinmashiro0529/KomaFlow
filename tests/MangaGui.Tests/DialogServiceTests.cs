using MangaGui.Views;
using Xunit;

namespace MangaGui.Tests;

/// <summary>
/// Queue semantics of <see cref="DialogService"/> — no WPF involved: every show delegate here is a
/// plain task so the tests run on any thread without a dispatcher.
/// </summary>
public sealed class DialogServiceTests
{

    [Fact]
    public async Task UrgentDialog_InterruptsDisplayedNormal_AndShowsFirst()
    {
        var service = new DialogService();
        var normalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var urgentRan = false;
        CancellationToken normalToken = default;

        // A normal dialog is on screen, suspended on its (cancellable) token.
        var normalTask = service.ShowAsync(async ct =>
        {
            normalToken = ct;
            normalStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        }, safeDefault: -1);
        Assert.True(service.IsShowing);
        await normalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // An urgent request cancels the displayed normal dialog and shows first.
        var urgentTask = service.ShowAsync(_ =>
        {
            urgentRan = true;
            return Task.FromResult(2);
        }, safeDefault: -2, urgent: true);

        Assert.True(normalToken.IsCancellationRequested);
        Assert.Equal(-1, await normalTask);
        Assert.Equal(2, await urgentTask);
        Assert.True(urgentRan);
        Assert.False(service.IsShowing);
    }

    [Fact]
    public async Task UrgentDialogs_RunFifo_BeforeQueuedNormalDialogs()
    {
        var service = new DialogService();
        var order = new List<string>();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var t0 = service.ShowAsync(ct => gate.Task.WaitAsync(ct), -1);   // normal, showing
        var n1 = service.ShowAsync(_ => Task.FromResult(10), -10);       // queued normal
        var u1 = service.ShowAsync(_ =>
        {
            order.Add("u1");
            return Task.FromResult(1);
        }, -1, urgent: true);
        var u2 = service.ShowAsync(_ =>
        {
            order.Add("u2");
            return Task.FromResult(2);
        }, -2, urgent: true);
        var n2 = service.ShowAsync(_ =>
        {
            order.Add("n2");
            return Task.FromResult(20);
        }, -20);

        Assert.Equal(-1, await t0); // interrupted normal resolves to its safe default
        Assert.Equal(1, await u1);
        Assert.Equal(2, await u2);
        Assert.Equal(10, await n1);
        Assert.Equal(20, await n2);
        Assert.Equal(new[] { "u1", "u2", "n2" }, order); // queued normals resume after urgents
    }

    [Fact]
    public async Task ExternalCancel_RemovesQueuedRequest_WithSafeDefault()
    {
        var service = new DialogService();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t1 = service.ShowAsync(ct => gate.Task, -1);

        var ran = false;
        using var cts = new CancellationTokenSource();
        var t2 = service.ShowAsync(_ =>
        {
            ran = true;
            return Task.FromResult(2);
        }, safeDefault: -2, externalCancel: cts.Token);

        cts.Cancel();
        Assert.Equal(-2, await t2);   // dequeued with its safe default
        Assert.False(ran);            // ... and its show delegate never ran
        gate.SetResult(1);
        Assert.Equal(1, await t1);
    }

    [Fact]
    public async Task ExternalCancel_InterruptsDisplayedRequest()
    {
        var service = new DialogService();
        using var cts = new CancellationTokenSource();
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = service.ShowAsync(async ct =>
        {
            shown.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        }, safeDefault: -1, externalCancel: cts.Token);

        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        Assert.Equal(-1, await task);
    }

    [Fact]
    public async Task NonCancellationException_Propagates_AndQueueContinues()
    {
        var service = new DialogService();
        var expected = new InvalidOperationException("boom");

        var failing = service.ShowAsync<int>(_ => throw expected, -1);
        var next = service.ShowAsync(_ => Task.FromResult(2), -2);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => failing);
        Assert.Same(expected, ex);
        Assert.Equal(2, await next);
        Assert.False(service.IsShowing);
    }
}
