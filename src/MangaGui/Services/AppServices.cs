namespace MangaGui.Services;

/// <summary>
/// Shared composition root for the session stack (store → scripts → cli → billing → session manager),
/// used by both the WPF app (<see cref="App.OnStartup"/>) and the MangaCli headless host so the two
/// front-ends always run the exact same orchestration objects.
/// </summary>
public sealed record AppServices(
    SessionStore Store,
    VmScripts Scripts,
    IColabCli Cli,
    BillingTimer Billing,
    AccountManager Accounts,
    SessionManager Session)
{
    /// <summary>
    /// Mutex name shared with MangaCli so only one front-end (GUI window/tray or CLI command)
    /// can own a cloud session at a time.
    /// </summary>
    public const string SingleInstanceMutexName = @"Global\MangaGui_ColabL4_SingleInstance";

    public static AppServices Create(
        string? dataDirectory = null,
        string? appBaseDirectory = null,
        IColabCli? cli = null,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan[]? autoStopRetryDelays = null)
    {
        var store = new SessionStore(dataDirectory);
        _ = store.LoadSettings();

        var scripts = new VmScripts(store, appBaseDirectory);
        cli ??= new ColabCli(configuredExePathProvider: () => store.LoadSettings().ColabExePath);
        var billing = new BillingTimer(utcNow);
        var accounts = new AccountManager(cli, store, utcNow);
        var session = new SessionManager(
            cli,
            scripts,
            store,
            billing,
            utcNow: utcNow,
            autoStopRetryDelays: autoStopRetryDelays,
            environmentIssuesProvider: EnvironmentChecks.GetSshIssues,
            accounts: accounts);

        return new AppServices(store, scripts, cli, billing, accounts, session);
    }
}
