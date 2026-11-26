using MangaGui.Models;
using MangaGui.Services;

namespace MangaCli;

/// <summary>
/// Wraps <see cref="IColabCli"/> for commands that must never touch the cloud (doctor, precheck,
/// settings, status without --probe, logs). Local probes (<see cref="IsColabAvailable"/>,
/// <see cref="UsesBatchShim"/>) pass through; every invocation of the colab executable is blocked,
/// counted, and returns a failure <see cref="ColabResult"/>.
/// </summary>
public sealed class OfflineGuardColabCli : IColabCli
{
    private readonly IColabCli _inner;
    private int _blockedCalls;

    public OfflineGuardColabCli(IColabCli inner)
    {
        _inner = inner;
    }

    public int BlockedCalls => _blockedCalls;

    public bool UsesBatchShim => _inner.UsesBatchShim;

    public ColabAccountContext ActiveAccount
    {
        get => _inner.ActiveAccount;
        set => _inner.ActiveAccount = value;
    }

    public bool IsColabAvailable(out string? resolvedPath) => _inner.IsColabAvailable(out resolvedPath);

    public Task<ColabResult> RunAsync(
        IReadOnlyList<string> args,
        TimeSpan timeout,
        Action<string>? onStdoutLine = null,
        Action<string>? onStderrLine = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _blockedCalls);
        return Task.FromResult(new ColabResult(1, string.Empty, "offline command: cloud call blocked"));
    }

    public Task<ColabResult> RunForAccountAsync(
        ColabAccountContext account,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        Action<string>? onStdoutLine = null,
        Action<string>? onStderrLine = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _blockedCalls);
        return Task.FromResult(new ColabResult(1, string.Empty, "offline command: cloud call blocked"));
    }

    public Task<ColabResult> RunSshAsync(
        string sessionName,
        IReadOnlyList<string> remoteArgs,
        TimeSpan timeout,
        Action<string>? onStdoutLine = null,
        Action<string>? onStderrLine = null,
        CancellationToken cancellationToken = default,
        bool allowRateLimitRetry = true)
    {
        Interlocked.Increment(ref _blockedCalls);
        return Task.FromResult(new ColabResult(1, string.Empty, "offline command: cloud call blocked"));
    }
}
