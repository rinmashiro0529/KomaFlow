namespace MangaGui.ViewModels;

public sealed record ConfirmStopSummary(
    string ElapsedText,
    string EstimatedCuText,
    int UnfinishedTaskCount,
    bool IsDeploying,
    string? AccountName = null);

public enum TimeExpiredAction
{
    None,
    Extend30Minutes,
    DrainThenStop,
    StopImmediately
}

public enum TimeExpiredScenario
{
    BusyWithTasks,
    DeployingTimeout,
    ErrorWhileBilling,
    IdleNotAutoStop
}

/// <summary>
/// What the time-expired dialog shows, plus the action it takes by itself when nobody answers within
/// <see cref="AutoActionDelay"/>. <see cref="TimeExpiredAction.None"/> means it only waits.
/// </summary>
public sealed record TimeExpiredRequest(
    string ElapsedText,
    string EstimatedCuText,
    int PendingCount,
    TimeExpiredScenario Scenario,
    TimeExpiredAction AutoAction,
    TimeSpan AutoActionDelay,
    string? AccountName = null);

public enum CloseGuardAction
{
    StopAndExit,
    MinimizeToTray,
    Cancel
}

public enum OrphanSessionAction
{
    Adopt,
    StopNow,
    Dismiss
}
