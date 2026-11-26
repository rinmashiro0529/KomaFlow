namespace MangaCli;

/// <summary>
/// Parsed command line. <see cref="Positionals"/> carries file paths for run/precheck and the
/// sub-command tokens for settings.
/// </summary>
internal sealed class CliOptions
{
    public string Command { get; set; } = string.Empty;
    public List<string> Positionals { get; } = [];

    // Shared options
    public string? DataDir { get; set; }
    public string? EventsFile { get; set; }
    public bool QuietLogs { get; set; }

    // status
    public bool Probe { get; set; }

    // accounts
    public bool Refresh { get; set; }

    // logs
    public int Tail { get; set; } = 200;
    public bool Crash { get; set; }

    // run / precheck / stop
    public int? Minutes { get; set; }
    public bool NoAutoStop { get; set; }
    public bool KeepSession { get; set; }
    public string? Engine { get; set; }
    public string? Gpu { get; set; }
    public string? Inpaint { get; set; }
    public string? G4Mode { get; set; }
    public string? Account { get; set; }
    public string? OutputDir { get; set; }
    public string OnOrphan { get; set; } = "fail";
    public string OnExpiry { get; set; } = "auto";
    public string? ScriptsDir { get; set; }
    public int DeployRetries { get; set; }
}

internal sealed record ParseResult(CliOptions Options, string? Error)
{
    public bool Success => Error is null;
}
