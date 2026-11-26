using MangaGui.Models;
using Wpf.Ui.TaskBar;

namespace MangaGui.Views;

/// <summary>
/// Pure mapping from session/queue state to the Windows taskbar progress indicator
/// (design spec 10.2). Rows are evaluated top-down; the first match wins, so a paused
/// session still shows <see cref="TaskBarProgressState.Paused"/> even when the last
/// session state was an error.
/// </summary>
public static class TaskbarProgressMapper
{
    /// <param name="state">Current session state.</param>
    /// <param name="hasActiveVm">Whether a billed runtime exists.</param>
    /// <param name="isQueuePaused">Whether the task queue is paused.</param>
    /// <param name="deployPercent">Deploy progress 0–100, used only while deploying.</param>
    /// <param name="activeTaskPercent">Progress of the running task, or null when none is active.</param>
    public static (TaskBarProgressState State, int Value) Map(
        SessionState state,
        bool hasActiveVm,
        bool isQueuePaused,
        int deployPercent,
        int? activeTaskPercent)
    {
        if (state == SessionState.Deploying)
        {
            return (TaskBarProgressState.Normal, Math.Clamp(deployPercent, 0, 100));
        }

        if ((state == SessionState.Busy || state == SessionState.Draining) && activeTaskPercent is { } taskPercent)
        {
            return (TaskBarProgressState.Normal, Math.Clamp(taskPercent, 0, 100));
        }

        if (isQueuePaused && hasActiveVm)
        {
            return (TaskBarProgressState.Paused, 0);
        }

        if (state == SessionState.Error && hasActiveVm)
        {
            return (TaskBarProgressState.Error, 0);
        }

        return (TaskBarProgressState.None, 0);
    }
}
