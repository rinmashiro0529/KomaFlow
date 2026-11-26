namespace MangaGui.Models;

/// <summary>
/// Single source of truth for Colab GPU billing constants (calibrated from live ccu-info probes).
/// </summary>
public static class BillingConstants
{
    public const double L4CuPerHour = 2.33;
    public const double A10040GCuPerHour = 8.16;
    public const double A10080GCuPerHour = 10.99;
    public const double G4CuPerHour = 10.00;
    public const string L4CuPerHourDisplay = "2.33 CU/h";

    public static string FormatRate(double cuPerHour) => $"{cuPerHour:F2} CU/h";
}

/// <summary>
/// Core lifecycle states of the Colab manga translation session.
/// </summary>
public enum SessionState
{
    Idle,
    Deploying,
    Ready,
    Busy,
    Draining,
    Stopping,
    AutoStopping,
    Error
}

/// <summary>
/// Three-state result for querying whether a remote Colab session exists (A-01).
/// Never conflates unknown/empty/unparseable output with ExplicitlyAbsent.
/// </summary>
public enum SessionPresence
{
    Present,
    ExplicitlyAbsent,
    Unknown
}

/// <param name="UnnamedAssignments">
/// Number of server-side assignments listed as <c>[?]</c> (no local session name). They bill like any
/// other runtime but cannot be stopped by name through the CLI.
/// </param>
public sealed record SessionQueryResult(
    SessionPresence Presence,
    string Reason,
    string RawOutput,
    int UnnamedAssignments = 0);

/// <summary>
/// Structured result for loading persisted session.json (A-02).
/// </summary>
public enum StoreLoadStatus
{
    Found,
    Missing,
    Corrupted,
    AccessDenied
}

public sealed record SessionLoadResult(
    StoreLoadStatus Status,
    SessionRecord? Record,
    string? ErrorMessage = null);

public sealed record StoreOperationResult(
    bool Success,
    string? ErrorMessage = null);

/// <summary>
/// Tracks how existing colab_manga scripts were resolved (A-10).
/// </summary>
public enum ScriptsSourceKind
{
    None,
    UserSelected,
    AutoDiscovered
}

/// <summary>
/// Stages reported during deployment for live progress tracking.
/// </summary>
public enum DeployStage
{
    None,
    CheckingSession,
    CreatingRuntime,
    UploadingScripts,
    Stage1TranslatorEnv,
    Stage2LlamaModel,
    Stage3EnvScript,
    Stage4LlamaServer,
    Stage5TranslatorWarmup,
    VerifyingHealth,
    Completed
}

/// <summary>
/// Visual severity for billing readout (especially under unlimited mode or nearing expiry).
/// </summary>
public enum BillingSeverity
{
    Normal,
    Warning,
    Danger
}

/// <summary>
/// Result of the startup probe against remote Colab sessions and local session.json.
/// </summary>
public enum StartupProbeOutcome
{
    Idle,
    RecoveredReady,
    RecoveredNeedsDeploy,
    OrphanSessionDetected,
    CleanedStaleRecord,
    ProbeFailed
}
