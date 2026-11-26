using MangaGui.ViewModels;
using Xunit;

namespace MangaGui.Tests;

/// <summary>
/// The unattended-action deadline is anchored at <c>requestedAtUtc</c> — the moment the expiry
/// request was raised — so a queued dialog can never postpone the automatic stop, and only the two
/// billing-safe actions may ever run by themselves.
/// </summary>
public sealed class TimeExpiredDialogViewModelTests
{
    private static TimeExpiredRequest MakeRequest(
        TimeExpiredAction autoAction,
        TimeSpan delay,
        TimeExpiredScenario scenario = TimeExpiredScenario.BusyWithTasks)
        => new("60 min", "约 1.94 CU", 3, scenario, autoAction, delay);

    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AtDeadline_WithNoUserResponse_AutoActionCompletes()
    {
        var timer = new ManualUiTimer(TimeSpan.FromSeconds(1));
        var now = T0;
        var vm = new TimeExpiredDialogViewModel(
            MakeRequest(TimeExpiredAction.StopImmediately, TimeSpan.FromMinutes(2)),
            T0, () => now, timer);

        now = T0.AddMinutes(2);
        timer.RaiseTick();

        Assert.True(vm.Completion.IsCompleted);
        Assert.Equal(TimeExpiredAction.StopImmediately, await vm.Completion);
    }

    [Fact]
    public void Extend30Minutes_NeverAutoCompletes()
    {
        var timer = new ManualUiTimer(TimeSpan.FromSeconds(1));
        var now = T0;
        var vm = new TimeExpiredDialogViewModel(
            MakeRequest(TimeExpiredAction.Extend30Minutes, TimeSpan.FromMinutes(2)),
            T0, () => now, timer);

        now = T0.AddMinutes(10);
        timer.RaiseTick(); // also covers the timer never having been started

        Assert.False(vm.Completion.IsCompleted);
    }

    [Fact]
    public void NoAutoAction_NeverCompletes_NoMatterHowMuchTimePasses()
    {
        var timer = new ManualUiTimer(TimeSpan.FromSeconds(1));
        var now = T0;
        var vm = new TimeExpiredDialogViewModel(
            MakeRequest(TimeExpiredAction.None, TimeSpan.Zero),
            T0, () => now, timer);

        now = T0.AddDays(1);
        timer.RaiseTick();

        Assert.False(vm.Completion.IsCompleted);
        Assert.Equal("关闭此对话框不会延长时长，也不会停止计费，稍后会再次提醒。", vm.CountdownText);
    }

    [Fact]
    public async Task Deadline_IsAnchoredAtRequestedAtUtc_NotConstructionOrDisplay()
    {
        var timer = new ManualUiTimer(TimeSpan.FromSeconds(1));
        var now = T0;
        var delay = TimeSpan.FromMinutes(2);
        var vm = new TimeExpiredDialogViewModel(
            MakeRequest(TimeExpiredAction.StopImmediately, delay),
            T0, () => now, timer);

        // The clock has already advanced to delay - 1s by the time a tick lands (e.g. the dialog
        // sat in the queue): still not past the deadline.
        now = T0 + delay - TimeSpan.FromSeconds(1);
        timer.RaiseTick();
        Assert.False(vm.Completion.IsCompleted);

        // Two seconds later the deadline is crossed.
        now = now.AddSeconds(2);
        timer.RaiseTick();
        Assert.True(vm.Completion.IsCompleted);
        Assert.Equal(TimeExpiredAction.StopImmediately, await vm.Completion);
    }

    [Fact]
    public async Task Choose_IsIdempotent_FirstCallWins()
    {
        var timer = new ManualUiTimer(TimeSpan.FromSeconds(1));
        var vm = new TimeExpiredDialogViewModel(
            MakeRequest(TimeExpiredAction.StopImmediately, TimeSpan.FromMinutes(2)),
            T0, () => T0, timer);

        vm.Choose(TimeExpiredAction.Extend30Minutes);
        vm.Choose(TimeExpiredAction.StopImmediately);

        Assert.Equal(TimeExpiredAction.Extend30Minutes, await vm.Completion);
        Assert.False(timer.IsEnabled); // timer stopped by the first choice
    }

    [Fact]
    public void CountdownText_ShowsAutoActionVerb_AndSafeCloseNote()
    {
        var timer = new ManualUiTimer(TimeSpan.FromSeconds(1));
        var now = T0;
        var vm = new TimeExpiredDialogViewModel(
            MakeRequest(TimeExpiredAction.DrainThenStop, TimeSpan.FromMinutes(3)),
            T0, () => now, timer);

        Assert.Contains("自动翻完当前队列后停止计费", vm.CountdownText);
        Assert.Contains("稍后会再次提醒", vm.CountdownText);

        now = T0.AddMinutes(2).AddSeconds(30);
        timer.RaiseTick();
        Assert.Contains("0:30", vm.CountdownText);
    }
}
