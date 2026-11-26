using System.IO;
using MangaGui.Services;

namespace MangaCli;

/// <summary>
/// Environment knobs for <see cref="CliApp.RunAsync"/> — the seams the tests need (injected writers,
/// fake colab CLI, clock, mutex name, timeouts) without touching the process console or real paths.
/// </summary>
public sealed class CliEnvironment
{
    public TextWriter Stdout { get; init; } = Console.Out;
    public TextWriter Stderr { get; init; } = Console.Error;

    /// <summary>Factory for the colab CLI; null builds the real <see cref="ColabCli"/>.</summary>
    public Func<IColabCli>? ColabFactory { get; init; }

    public string MutexName { get; init; } = MangaGui.Services.AppServices.SingleInstanceMutexName;
    public Func<DateTimeOffset>? UtcNow { get; init; }
    public string? AppBaseDirectory { get; init; }
    public TimeSpan? TaskPollInterval { get; init; }
    public TimeSpan? BusyExpiryGrace { get; init; }
    public TimeSpan? IdleExpiryGrace { get; init; }
    public TimeSpan[]? AutoStopRetryDelays { get; init; }
    public TimeSpan BillingEventInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan StopVerifyTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Bound on the post-cancel wait that lets the active task's remote cleanup finish.</summary>
    public TimeSpan InterruptCleanupBound { get; init; } = TimeSpan.FromSeconds(90);
}
