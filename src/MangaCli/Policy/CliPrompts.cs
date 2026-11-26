using MangaGui.ViewModels;

namespace MangaCli;

public enum OrphanPolicy
{
    Fail,
    Adopt,
    Stop,
    Dismiss
}

public enum ExpiryPolicy
{
    Auto,
    Drain,
    Stop,
    None
}

/// <summary>
/// Answers the MainViewModel <c>Request*</c> dialog events from command-line policy instead of a
/// human. Every automatic answer is emitted as a <c>prompt</c> event so the decision is auditable.
/// </summary>
public sealed class CliPrompts
{
    private readonly EventWriter _writer;
    private readonly CancellationToken _interrupt;

    public CliPrompts(
        EventWriter writer,
        bool allowStart,
        int? minutes,
        bool autoStop,
        OrphanPolicy orphanPolicy,
        ExpiryPolicy expiryPolicy,
        string? scriptsDir,
        CancellationToken interrupt)
    {
        _writer = writer;
        _interrupt = interrupt;
        AllowStart = allowStart;
        Minutes = minutes;
        AutoStop = autoStop;
        OrphanPolicy = orphanPolicy;
        ExpiryPolicy = expiryPolicy;
        ScriptsDir = scriptsDir;
    }

    public bool AllowStart { get; }
    public int? Minutes { get; }
    public bool AutoStop { get; }
    public OrphanPolicy OrphanPolicy { get; }
    public ExpiryPolicy ExpiryPolicy { get; }
    public string? ScriptsDir { get; }

    /// <summary>Set whenever an orphan session was detected (regardless of the chosen policy).</summary>
    public bool OrphanDetected { get; private set; }

    /// <summary>Latest diagnostics snapshot captured via RequestShowDiagnosticsDialog.</summary>
    public DiagnosticsSnapshot? Diagnostics { get; private set; }

    public void Attach(MainViewModel vm)
    {
        vm.RequestStartDialog += OnStartDialog;
        vm.RequestOrphanSessionDialog += OnOrphanDialog;
        vm.RequestTimeExpiredDialog += OnTimeExpiredDialog;
        vm.RequestConfirmStopDialog += OnConfirmStop;
        vm.RequestBrowseScriptsFolder += OnBrowseScriptsFolder;
        vm.RequestBrowseOutputFolder += OnBrowseOutputFolder;
        vm.RequestBrowseZipFiles += OnBrowseZipFiles;
        vm.RequestShowDiagnosticsDialog += OnShowDiagnostics;
    }

    /// <summary>Resolves the start-dialog answer; also used by the CLI when adopting an orphan.</summary>
    public (bool Confirmed, int? PlannedMinutes, bool AutoStop) AnswerStart()
    {
        var answer = AllowStart ? (true, Minutes, AutoStop) : (false, (int?)null, true);
        _writer.Prompt("start", answer.Item1
            ? $"confirmed_minutes={Minutes?.ToString() ?? "null"},auto_stop={AutoStop}"
            : "declined");
        return answer;
    }

    /// <summary>Resolves the orphan-session answer; also used directly by commands probing manually.</summary>
    public OrphanSessionAction AnswerOrphan()
    {
        OrphanDetected = true;
        var action = OrphanPolicy switch
        {
            OrphanPolicy.Adopt => MangaGui.ViewModels.OrphanSessionAction.Adopt,
            OrphanPolicy.Stop => MangaGui.ViewModels.OrphanSessionAction.StopNow,
            _ => MangaGui.ViewModels.OrphanSessionAction.Dismiss
        };
        _writer.Prompt("orphan_session", CliJson.ToSnakeCase(action.ToString()));
        return action;
    }

    private Task<(bool Confirmed, int? PlannedMinutes, bool AutoStop)> OnStartDialog() =>
        Task.FromResult(AnswerStart());

    private Task<MangaGui.ViewModels.OrphanSessionAction> OnOrphanDialog() =>
        Task.FromResult(AnswerOrphan());

    private Task<bool> OnConfirmStop(ConfirmStopSummary _)
    {
        _writer.Prompt("confirm_stop", "declined");
        return Task.FromResult(false);
    }

    private async Task<TimeExpiredAction> OnTimeExpiredDialog(TimeExpiredRequest request)
    {
        TimeExpiredAction answer;
        if (_interrupt.IsCancellationRequested)
        {
            // A run being interrupted must finish billing as quickly as possible.
            answer = TimeExpiredAction.StopImmediately;
        }
        else
        {
            switch (ExpiryPolicy)
            {
                case ExpiryPolicy.Auto:
                    if (request.AutoAction is TimeExpiredAction.DrainThenStop or TimeExpiredAction.StopImmediately)
                    {
                        try
                        {
                            await Task.Delay(request.AutoActionDelay, _interrupt);
                            answer = request.AutoAction;
                        }
                        catch (OperationCanceledException)
                        {
                            answer = TimeExpiredAction.StopImmediately;
                        }
                    }
                    else
                    {
                        answer = TimeExpiredAction.None;
                    }
                    break;
                case ExpiryPolicy.Drain:
                    answer = TimeExpiredAction.DrainThenStop;
                    break;
                case ExpiryPolicy.Stop:
                    answer = TimeExpiredAction.StopImmediately;
                    break;
                default:
                    answer = TimeExpiredAction.None;
                    break;
            }
        }

        _writer.Prompt("time_expired", CliJson.ToSnakeCase(answer.ToString()));
        return answer;
    }

    private string? OnBrowseScriptsFolder()
    {
        _writer.Prompt("browse_scripts_folder", ScriptsDir ?? "null");
        return ScriptsDir;
    }

    private string? OnBrowseOutputFolder()
    {
        _writer.Prompt("browse_output_folder", "null");
        return null;
    }

    private IReadOnlyList<string>? OnBrowseZipFiles()
    {
        _writer.Prompt("browse_zip_files", "null");
        return null;
    }

    private void OnShowDiagnostics(DiagnosticsSnapshot snapshot) => Diagnostics = snapshot;
}
