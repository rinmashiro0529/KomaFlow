using System.IO;
using MangaGui.Models;
using MangaGui.Services;
using Xunit;

namespace MangaGui.Tests;

public sealed class MultiAccountSessionTests
{
    private static (string Dir, SessionStore Store, VmScripts Scripts) CreateFixtureWithTwoAccounts()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"manga_multi_acct_{Guid.NewGuid():N}");
        var dataDir = Path.Combine(dir, "appdata");
        var acctDir = Path.Combine(dataDir, "accounts");
        var scriptsDir = Path.Combine(dir, "scripts");
        var assetsDir = Path.Combine(scriptsDir, "assets");
        Directory.CreateDirectory(acctDir);
        Directory.CreateDirectory(assetsDir);

        File.WriteAllText(Path.Combine(acctDir, "acct1.json"), """{"type":"authorized_user","refresh_token":"r1"}""");
        File.WriteAllText(Path.Combine(acctDir, "acct2.json"), """{"type":"authorized_user","refresh_token":"r2"}""");

        foreach (var f in VmScripts.RequiredExistingScripts)
            File.WriteAllText(Path.Combine(scriptsDir, f), "{}");
        foreach (var f in VmScripts.BundledGuiAssets)
            File.WriteAllText(Path.Combine(assetsDir, f), "#!/bin/bash\n");

        var store = new SessionStore(dataDir);
        var scripts = new VmScripts(store, scriptsDir);
        return (dir, store, scripts);
    }

    [Fact]
    public async Task StartOrDeploy_SafeFailover_AdvancesToNextAccountWhenConfirmedAbsent()
    {
        var (dir, store, scripts) = CreateFixtureWithTwoAccounts();
        try
        {
            var setup = new FakeVmSetup();
            var cli = new FakeColabCli((acct, args, _, _) =>
            {
                if (args[0] == "usage")
                {
                    var bal = acct.AccountId == "acct2" ? "200.00" : "185.00";
                    return Task.FromResult(new ColabResult(0,
                        $"Current balance: {bal} compute units\nUsage rate: 0.00/hr\nActive assignments: 0\n", ""));
                }
                if (args[0] == "sessions")
                    return Task.FromResult(new ColabResult(0, "[colab] No active sessions found on server.\n", ""));
                if (args[0] == "new")
                {
                    if (acct.AccountId == "acct2")
                        return Task.FromResult(new ColabResult(1, "", "HTTP 412 Allocation refused"));
                    return Task.FromResult(new ColabResult(0, "Session manga created.\n", ""));
                }
                if (args[0] == "ssh")
                {
                    if (setup.Handle(args) is { } setupRes) return Task.FromResult(setupRes);
                    var cmd = string.Join(" ", args);
                    if (cmd.Contains("vm_status.sh"))
                        return Task.FromResult(new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mtui ready\nGPU L4 14000/23034MiB\n", ""));
                    return Task.FromResult(new ColabResult(0, "OK\n", ""));
                }
                return Task.FromResult(new ColabResult(0, "OK\n", ""));
            });

            var accounts = new AccountManager(cli, store);
            await accounts.RefreshAllQuotasAsync(force: true);
            var mgr = new SessionManager(cli, scripts, store, new BillingTimer(),
                environmentIssuesProvider: () => [], accounts: accounts);

            var plan = accounts.ResolveCandidatesForStart();
            Assert.Equal("acct2", plan.Candidates[0].Id);
            Assert.Equal("acct1", plan.Candidates[1].Id);

            var ok = await mgr.StartOrDeployAsync(60, true, candidateAccounts: plan.Candidates, allowFailover: true);
            Assert.True(ok);
            Assert.Equal("acct1", mgr.ActiveSessionAccountId);
            Assert.Equal("acct1", cli.ActiveAccount.AccountId);

            // Persisted session record remembers owning account, and ExtendPlannedTime preserves it.
            var saved = store.LoadSession();
            Assert.NotNull(saved);
            Assert.Equal("acct1", saved!.AccountId);

            mgr.ExtendPlannedTime(30);
            var afterExtend = store.LoadSession();
            Assert.Equal("acct1", afterExtend!.AccountId);
            Assert.Equal(90, afterExtend.PlannedMinutes);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task StartOrDeploy_NeverFailsOverWhenReverifyIsUncertainOrCreatesUnnamedInstance()
    {
        var (dir, store, scripts) = CreateFixtureWithTwoAccounts();
        try
        {
            int acct2SessionCalls = 0;
            var cli = new FakeColabCli((acct, args, _, _) =>
            {
                if (args[0] == "usage")
                {
                    var bal = acct.AccountId == "acct2" ? "200.00" : "185.00";
                    return Task.FromResult(new ColabResult(0,
                        $"Current balance: {bal} compute units\nUsage rate: 0.00/hr\nActive assignments: 0\n", ""));
                }
                if (args[0] == "sessions")
                {
                    if (acct.AccountId == "acct2")
                    {
                        acct2SessionCalls++;
                        // 1st call before new: empty; 2nd call reverify after failed new: 1 new unnamed [?] instance!
                        if (acct2SessionCalls >= 2)
                            return Task.FromResult(new ColabResult(0, "[?] m-s-abc | Hardware: L4\n", ""));
                    }
                    return Task.FromResult(new ColabResult(0, "[colab] No active sessions found on server.\n", ""));
                }
                if (args[0] == "new")
                {
                    return Task.FromResult(new ColabResult(1, "", "request timed out", TimedOut: true));
                }
                return Task.FromResult(new ColabResult(0, "", ""));
            });

            var accounts = new AccountManager(cli, store);
            await accounts.RefreshAllQuotasAsync(force: true);
            var mgr = new SessionManager(cli, scripts, store, new BillingTimer(),
                environmentIssuesProvider: () => [], accounts: accounts);

            var plan = accounts.ResolveCandidatesForStart();
            var ok = await mgr.StartOrDeployAsync(60, true, candidateAccounts: plan.Candidates, allowFailover: true);
            Assert.False(ok);

            // Must NOT have called `new` on acct1!
            Assert.DoesNotContain(cli.AccountCalls, c => c.AccountId == "acct1" && c.Args[0] == "new");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task StartupProbe_DetectsCrossAccountOrphansAndStopsSecondaryAccountCleanly()
    {
        var (dir, store, scripts) = CreateFixtureWithTwoAccounts();
        try
        {
            bool acct2Alive = true;
            var cli = new FakeColabCli((acct, args, _, _) =>
            {
                if (args[0] == "sessions")
                {
                    if (acct.AccountId == "acct1")
                        return Task.FromResult(new ColabResult(0, "[manga] m-s-111 | Hardware: L4\n", ""));
                    if (acct.AccountId == "acct2" && acct2Alive)
                        return Task.FromResult(new ColabResult(0, "[manga] m-s-222 | Hardware: L4\n", ""));
                    return Task.FromResult(new ColabResult(0, "[colab] No active sessions found on server.\n", ""));
                }
                if (args[0] == "stop" && acct.AccountId == "acct2")
                {
                    acct2Alive = false;
                    return Task.FromResult(new ColabResult(0, "Stopped.\n", ""));
                }
                if (args[0] == "usage")
                {
                    return Task.FromResult(new ColabResult(0,
                        "Current balance: 198.25 compute units\nUsage rate: 0.00/hr\nActive assignments: 0\n", ""));
                }
                if (args[0] == "ssh")
                {
                    var cmd = string.Join(" ", args);
                    if (cmd.Contains("vm_status.sh"))
                        return Task.FromResult(new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mtui ready\nGPU L4 14000/23034MiB\n", ""));
                    return Task.FromResult(new ColabResult(0, "STALE_CLEAN_OK 0 0\n", ""));
                }
                return Task.FromResult(new ColabResult(0, "OK\n", ""));
            });

            // Local session.json belongs to acct1 while acct2 also has a stray cloud session!
            store.SaveSession(new SessionRecord
            {
                SessionName = "manga",
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                PlannedMinutes = 60,
                AutoStop = true,
                AccountId = "acct1",
                AccountName = "账号 1",
                BalanceBeforeStartCu = 200.00
            });

            var accounts = new AccountManager(cli, store);
            var mgr = new SessionManager(cli, scripts, store, new BillingTimer(),
                environmentIssuesProvider: () => [], accounts: accounts);

            var probe = await mgr.ProbeOnStartupAsync();
            Assert.Equal(StartupProbeOutcome.RecoveredReady, probe.Outcome);
            Assert.Equal("acct1", mgr.ActiveSessionAccountId);
            Assert.Single(mgr.OtherAccountWarnings);
            Assert.Equal("acct2", mgr.OtherAccountWarnings[0].AccountId);

            // Stop secondary orphan without touching primary acct1 session.
            var stopOther = await mgr.StopOtherAccountSessionAsync("acct2");
            Assert.True(stopOther.VerifiedStopped);
            Assert.Empty(mgr.OtherAccountWarnings);
            Assert.Equal("acct1", mgr.ActiveSessionAccountId);
            Assert.Equal(SessionState.Ready, mgr.State);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task StartupProbe_RefusesToDropBillingProtectionWhenSavedAccountCredentialFileIsMissing()
    {
        var (dir, store, scripts) = CreateFixtureWithTwoAccounts();
        try
        {
            var cli = new FakeColabCli((_, _, _) => Task.FromResult(new ColabResult(0, "", "")));
            var accounts = new AccountManager(cli, store);

            // Delete acct2's credential file while session.json still points to acct2.
            File.Delete(Path.Combine(store.DataDirectory, "accounts", "acct2.json"));
            store.SaveSession(new SessionRecord
            {
                SessionName = "manga",
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                PlannedMinutes = 60,
                AutoStop = true,
                AccountId = "acct2",
                AccountName = "账号 2"
            });

            var mgr = new SessionManager(cli, scripts, store, new BillingTimer(),
                environmentIssuesProvider: () => [], accounts: accounts);

            var probe = await mgr.ProbeOnStartupAsync();
            Assert.Equal(StartupProbeOutcome.ProbeFailed, probe.Outcome);
            Assert.True(mgr.HasActiveVm);
            Assert.Equal(SessionState.Error, mgr.State);
            Assert.Contains("账号 2", mgr.LastError);
            Assert.NotNull(store.LoadSession());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task StopSession_RefreshesPostStopBalanceAndReportsActualDeduction()
    {
        var (dir, store, scripts) = CreateFixtureWithTwoAccounts();
        try
        {
            bool alive = true;
            double currentBal = 200.00;
            var cli = new FakeColabCli((acct, args, _, _) =>
            {
                if (args[0] == "usage")
                    return Task.FromResult(new ColabResult(0,
                        $"Current balance: {currentBal:F2} compute units\nUsage rate: 0.00/hr\nActive assignments: 0\n", ""));
                if (args[0] == "sessions")
                    return Task.FromResult(new ColabResult(0,
                        alive && acct.AccountId == "acct2" ? "[manga] m-s-222 | Hardware: L4\n" : "[colab] No active sessions found on server.\n", ""));
                if (args[0] == "stop")
                {
                    alive = false;
                    currentBal = 198.25;
                    return Task.FromResult(new ColabResult(0, "Stopped.\n", ""));
                }
                if (args[0] == "ssh")
                    return Task.FromResult(new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mtui ready\nGPU L4 14000/23034MiB\n", ""));
                return Task.FromResult(new ColabResult(0, "OK\n", ""));
            });

            store.SaveSession(new SessionRecord
            {
                SessionName = "manga",
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-50),
                PlannedMinutes = 60,
                AutoStop = true,
                AccountId = "acct2",
                AccountName = "账号 2",
                BalanceBeforeStartCu = 200.00
            });

            var accounts = new AccountManager(cli, store);
            var mgr = new SessionManager(cli, scripts, store, new BillingTimer(),
                environmentIssuesProvider: () => [], accounts: accounts);

            await mgr.ProbeOnStartupAsync();
            var stopRes = await mgr.StopSessionAsync();
            Assert.True(stopRes.VerifiedStopped);
            Assert.Contains("198.25 CU", stopRes.Message);
            Assert.Contains("1.75 CU", stopRes.Message);
            Assert.Equal(198.25, accounts.GetQuota("acct2").CurrentBalanceCu);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
