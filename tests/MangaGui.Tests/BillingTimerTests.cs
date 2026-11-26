using MangaGui.Models;
using MangaGui.Services;
using MangaGui.ViewModels;
using Xunit;

namespace MangaGui.Tests;

public sealed class BillingTimerTests
{
    [Fact]
    public void WallClockElapsed_HandlesSleepJumpWithoutDriftAndFiresExpiry()
    {
        var now = new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.FromHours(8));
        var timer = new BillingTimer(() => now);

        bool warned = false;
        bool expired = false;
        timer.FiveMinuteWarningTriggered += (_, _) => warned = true;
        timer.TimeLimitReached += (_, _) => expired = true;

        timer.Start(now, plannedMinutes: 60, autoStop: true);

        // Jump 56 minutes (simulating system sleep) -> remaining 4 min -> warning triggers
        now = now.AddMinutes(56);
        var s1 = timer.Evaluate();
        Assert.True(warned);
        Assert.False(expired);
        Assert.Equal(BillingSeverity.Warning, s1.Severity);
        Assert.InRange(s1.EstimatedCu, 2.17, 2.18); // 56/60 * 2.33 = 2.1746

        // Jump another 5 minutes -> 61 min elapsed -> expiry triggers
        now = now.AddMinutes(5);
        var s2 = timer.Evaluate();
        Assert.True(expired);
        Assert.Equal(BillingSeverity.Danger, s2.Severity);
    }

    [Fact]
    public void Extend_Adds30MinutesFromElapsedAndResetsWarningFlags()
    {
        var now = new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.FromHours(8));
        var timer = new BillingTimer(() => now);
        int warningCount = 0;
        timer.FiveMinuteWarningTriggered += (_, _) => warningCount++;

        timer.Start(now, plannedMinutes: 60, autoStop: true);
        now = now.AddMinutes(56);
        timer.Evaluate();
        Assert.Equal(1, warningCount);

        var updated = timer.Extend(30);
        Assert.NotNull(updated);
        Assert.Equal(90, updated!.PlannedMinutes);

        var snap = timer.Evaluate();
        Assert.Equal(BillingSeverity.Normal, snap.Severity);

        // Advance to 86 min -> warning should fire again for the extended deadline
        now = now.AddMinutes(30);
        timer.Evaluate();
        Assert.Equal(2, warningCount);
    }

    [Fact]
    public void UnlimitedMode_TurnsWarningAfter1HourAndDangerAfter2Hours()
    {
        var now = new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.FromHours(8));
        var timer = new BillingTimer(() => now);
        bool expired = false;
        timer.TimeLimitReached += (_, _) => expired = true;

        timer.Start(now, plannedMinutes: null, autoStop: false);

        now = now.AddMinutes(45);
        Assert.Equal(BillingSeverity.Normal, timer.Evaluate().Severity);

        now = now.AddMinutes(20); // 65 min
        Assert.Equal(BillingSeverity.Warning, timer.Evaluate().Severity);

        now = now.AddMinutes(60); // 125 min
        Assert.Equal(BillingSeverity.Danger, timer.Evaluate().Severity);
        Assert.False(expired);
    }

    [Fact]
    public void StartSessionDialogViewModel_RequiresExplicitAckForUnlimitedMode()
    {
        var vm = new StartSessionDialogViewModel();
        Assert.Equal(DurationChoice.Minutes60, vm.SelectedChoice);
        Assert.True(vm.CanConfirm);
        Assert.Equal(60, vm.PlannedMinutes);
        Assert.True(vm.EffectiveAutoStop);

        vm.SelectedChoice = DurationChoice.Unlimited;
        Assert.True(vm.IsUnlimited);
        Assert.False(vm.CanConfirm);
        Assert.Null(vm.PlannedMinutes);
        Assert.False(vm.EffectiveAutoStop);

        vm.AcknowledgeUnlimitedBilling = true;
        Assert.True(vm.CanConfirm);
    }
}
