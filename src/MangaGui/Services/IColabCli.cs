using MangaGui.Models;

namespace MangaGui.Services;

/// <summary>
/// Result of executing a colab CLI subprocess.
/// </summary>
public sealed record ColabResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    bool TimedOut = false,
    int RetryCount = 0)
{
    public bool IsSuccess => ExitCode == 0 && !TimedOut;

    public string CombinedOutput => string.IsNullOrWhiteSpace(StdErr)
        ? StdOut
        : string.IsNullOrWhiteSpace(StdOut)
            ? StdErr
            : $"{StdOut}\n[STDERR]\n{StdErr}";
}

/// <summary>
/// Abstraction over google-colab-cli for production subprocess execution and deterministic unit testing.
/// </summary>
public interface IColabCli
{
    bool UsesBatchShim => false;

    /// <summary>
    /// Active account context used by <see cref="RunAsync"/> and <see cref="RunSshAsync"/>.
    /// </summary>
    ColabAccountContext ActiveAccount
    {
        get => ColabAccountContext.Default;
        set { }
    }

    bool IsColabAvailable(out string? resolvedPath);

    Task<ColabResult> RunAsync(
        IReadOnlyList<string> args,
        TimeSpan timeout,
        Action<string>? onStdoutLine = null,
        Action<string>? onStderrLine = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a colab CLI command explicitly scoped to <paramref name="account"/> without mutating
    /// <see cref="ActiveAccount"/> or contending with SSH serialization. Used for concurrent quota
    /// polling, whoami checks, and cross-account orphan session scans.
    /// </summary>
    Task<ColabResult> RunForAccountAsync(
        ColabAccountContext account,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        Action<string>? onStdoutLine = null,
        Action<string>? onStderrLine = null,
        CancellationToken cancellationToken = default)
        => RunAsync(args, timeout, onStdoutLine, onStderrLine, cancellationToken);

    Task<ColabResult> RunSshAsync(
        string sessionName,
        IReadOnlyList<string> remoteArgs,
        TimeSpan timeout,
        Action<string>? onStdoutLine = null,
        Action<string>? onStderrLine = null,
        CancellationToken cancellationToken = default,
        bool allowRateLimitRetry = true);
}
