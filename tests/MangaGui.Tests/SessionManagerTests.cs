using System.IO;
using MangaGui.Models;
using MangaGui.Services;
using Xunit;

namespace MangaGui.Tests;

public sealed class SessionManagerTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _dataDir;
    private readonly string _scriptsDir;
    private readonly string _assetsDir;

    public SessionManagerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"manga_gui_sm_test_{Guid.NewGuid():N}");
        _dataDir = Path.Combine(_tempRoot, "appdata");
        _scriptsDir = Path.Combine(_tempRoot, "scripts");
        _assetsDir = Path.Combine(_scriptsDir, "assets");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_assetsDir);

        foreach (var f in VmScripts.RequiredExistingScripts)
        {
            File.WriteAllText(Path.Combine(_scriptsDir, f), "#!/bin/bash\necho ok\n");
        }
        foreach (var f in VmScripts.BundledGuiAssets)
        {
            File.WriteAllText(Path.Combine(_assetsDir, f), "#!/bin/bash\necho ok\n");
        }
    }

    [Fact]
    public void ParseSessionPresence_StrictlyDistinguishesPresentExplicitlyAbsentAndUnknown()
    {
        // A-01: Empty stdout, garbage, or non-zero exit MUST return Unknown (never ExplicitlyAbsent!)
        Assert.Equal(SessionPresence.Unknown, VmScripts.ParseSessionPresence(new ColabResult(0, "", "")).Presence);
        Assert.Equal(SessionPresence.Unknown, VmScripts.ParseSessionPresence(new ColabResult(0, "   \n  ", "")).Presence);
        Assert.Equal(SessionPresence.Unknown, VmScripts.ParseSessionPresence(new ColabResult(0, "Authentication warning: retry later", "")).Presence);
        Assert.Equal(SessionPresence.Unknown, VmScripts.ParseSessionPresence(new ColabResult(1, "No active sessions found.", "error")).Presence);

        // Explicit absence
        Assert.Equal(SessionPresence.ExplicitlyAbsent, VmScripts.ParseSessionPresence(new ColabResult(0, "No active sessions found.", "")).Presence);

        // An unrecognized table without the session name proves nothing: Unknown, never ExplicitlyAbsent.
        Assert.Equal(SessionPresence.Unknown, VmScripts.ParseSessionPresence(new ColabResult(0, "NAME   GPU   STATUS\nother  L4    RUNNING\n", "")).Presence);

        // Present
        Assert.Equal(SessionPresence.Present, VmScripts.ParseSessionPresence(new ColabResult(0, "NAME   GPU\nmanga  L4\n", "")).Presence);
    }

    // Output formats of google-colab-cli 0.8.0 (commands/session.py): one line per server-side assignment.
    private const string CliNoSessions = "[colab] No active sessions found on server.\n";
    private const string CliMangaLine = "[manga] gpu-l4-s-1a2b3c4d5e | Hardware: L4 | Shape: Standard | Variant: GPU\n";
    private const string CliUnnamedLine = "[?] gpu-l4-s-9z8y7x6w5v | Hardware: L4 | Shape: Standard | Variant: GPU\n";
    private const string CliOtherLine = "[a1b2c3] gpu-t4-s-0f0e0d0c0b | Hardware: T4 | Shape: Standard | Variant: GPU\n";
    private const string CliUpgradeBanner =
        "\n[colab] A new version of Colab CLI is available: 0.9.0 (current: 0.8.0)\n[colab] Run 'pip install -U google-colab-cli' to update.\n\n";

    [Fact]
    public void ParseSessionPresence_UnderstandsRealCliLineFormatAndUnnamedAssignments()
    {
        static SessionQueryResult Parse(string stdout) => VmScripts.ParseSessionPresence(new ColabResult(0, stdout, ""));

        Assert.Equal(SessionPresence.ExplicitlyAbsent, Parse(CliNoSessions).Presence);
        Assert.Equal(SessionPresence.ExplicitlyAbsent, Parse(CliUpgradeBanner + CliNoSessions).Presence);

        Assert.Equal(SessionPresence.Present, Parse(CliMangaLine).Presence);
        Assert.Equal(SessionPresence.Present, Parse("[colab] Pruned 1 stale local session(s).\n" + CliMangaLine).Presence);
        Assert.Equal(SessionPresence.Present, Parse(CliUpgradeBanner + CliOtherLine + CliMangaLine).Presence);

        var other = Parse(CliOtherLine);
        Assert.Equal(SessionPresence.ExplicitlyAbsent, other.Presence);
        Assert.Equal(0, other.UnnamedAssignments);

        var unnamed = Parse(CliUnnamedLine + CliUnnamedLine);
        Assert.Equal(SessionPresence.ExplicitlyAbsent, unnamed.Presence);
        Assert.Equal(2, unnamed.UnnamedAssignments);

        var both = Parse(CliMangaLine + CliUnnamedLine);
        Assert.Equal(SessionPresence.Present, both.Presence);
        Assert.Equal(1, both.UnnamedAssignments);

        // A session whose name merely starts with the target name is a different session.
        Assert.Equal(SessionPresence.ExplicitlyAbsent, Parse(CliMangaLine.Replace("[manga]", "[manga2]")).Presence);
    }

    [Fact]
    public async Task RealCliFormat_OrphanIsDetected_RecordedSessionRecovers_AndUnnamedInstanceIsReported()
    {
        // Orphan: runtime exists in the cloud, no local record -> must be surfaced with billing protection.
        var store = new SessionStore(_dataDir);
        var orphanCli = new FakeColabCli((_, _, _) => Task.FromResult(new ColabResult(0, CliMangaLine, string.Empty)));
        var orphanMgr = new SessionManager(orphanCli, new VmScripts(store, _scriptsDir), store, new BillingTimer());
        var orphanProbe = await orphanMgr.ProbeOnStartupAsync();
        Assert.Equal(StartupProbeOutcome.OrphanSessionDetected, orphanProbe.Outcome);
        Assert.True(orphanMgr.HasActiveVm);

        // Recorded session still alive and healthy -> recovers to Ready; retry-deploy path also works.
        store.SaveSession(new SessionRecord { SessionName = "manga", StartedAt = DateTimeOffset.UtcNow.AddMinutes(-20), PlannedMinutes = 60 });
        var liveSetup = new FakeVmSetup();
        var liveCli = new FakeColabCli((args, _, _) => Task.FromResult(args[0] switch
        {
            "sessions" => new ColabResult(0, CliUpgradeBanner + CliMangaLine, string.Empty),
            "ssh" when args.Contains("/content/vm_setup.sh") => liveSetup.Handle(args)!,
            "ssh" => new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mit ready\nGPU L4, 15000 MiB, 23034 MiB\n", string.Empty),
            _ => new ColabResult(0, "ok", string.Empty)
        }));
        var liveMgr = new SessionManager(liveCli, new VmScripts(store, _scriptsDir), store, new BillingTimer());
        var liveProbe = await liveMgr.ProbeOnStartupAsync();
        Assert.Equal(StartupProbeOutcome.RecoveredReady, liveProbe.Outcome);
        Assert.Equal(SessionState.Ready, liveMgr.State);
        Assert.True(await liveMgr.StartOrDeployAsync(60, true, preserveExistingStartTime: true));
        Assert.DoesNotContain(liveCli.Calls, c => c[0] == "new");

        // After stop the named session is gone but an unnamed assignment remains: stop is verified for
        // "manga", and the leftover instance is reported instead of being silently ignored.
        bool stopped = false;
        var unnamedCli = new FakeColabCli((args, _, _) =>
        {
            if (args[0] == "stop") { stopped = true; return Task.FromResult(new ColabResult(0, "[colab] Session terminated.\n", string.Empty)); }
            if (args[0] == "sessions") return Task.FromResult(new ColabResult(0, stopped ? CliUnnamedLine : CliMangaLine + CliUnnamedLine, string.Empty));
            return Task.FromResult(new ColabResult(0, "ok", string.Empty));
        });
        var unnamedMgr = new SessionManager(unnamedCli, new VmScripts(store, _scriptsDir), store, new BillingTimer());
        var stopResult = await unnamedMgr.StopSessionAsync();
        Assert.True(stopResult.VerifiedStopped);
        Assert.Contains("未命名实例", stopResult.Message);
        Assert.Equal(SessionState.Idle, unnamedMgr.State);
        Assert.NotNull(unnamedMgr.UnnamedAssignmentWarning);
    }

    [Fact]
    public async Task StartupProbe_PreArmsProtectionForRecordedSession_AndFlagsUnknownStateWithoutRecord()
    {
        var store = new SessionStore(_dataDir);

        // No record + CLI cannot answer -> visible error state (not a silent Idle), still startable.
        var failingCli = new FakeColabCli((_, _, _) => Task.FromResult(new ColabResult(1, string.Empty, "network unreachable")));
        var noRecordMgr = new SessionManager(failingCli, new VmScripts(store, _scriptsDir), store, new BillingTimer());
        var noRecordProbe = await noRecordMgr.ProbeOnStartupAsync();
        Assert.Equal(StartupProbeOutcome.ProbeFailed, noRecordProbe.Outcome);
        Assert.Equal(SessionState.Error, noRecordMgr.State);
        Assert.False(noRecordMgr.HasActiveVm);
        Assert.False(string.IsNullOrWhiteSpace(noRecordMgr.LastError));

        // With a record, the close guard must already be armed while the first CLI call is in flight.
        store.SaveSession(new SessionRecord { SessionName = "manga", StartedAt = DateTimeOffset.UtcNow, PlannedMinutes = 60 });
        var gate = new TaskCompletionSource<ColabResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowCli = new FakeColabCli((_, _, _) => gate.Task);
        var recordedMgr = new SessionManager(slowCli, new VmScripts(store, _scriptsDir), store, new BillingTimer());
        var probeTask = recordedMgr.ProbeOnStartupAsync();
        Assert.True(recordedMgr.HasActiveVm);
        Assert.True(recordedMgr.Billing.Evaluate(fireEvents: false).IsRunning);

        gate.SetResult(new ColabResult(0, CliNoSessions, string.Empty));
        var recordedProbe = await probeTask;
        Assert.Equal(StartupProbeOutcome.CleanedStaleRecord, recordedProbe.Outcome);
        Assert.False(recordedMgr.HasActiveVm);
    }

    [Fact]
    public async Task Stop_WaitsForInFlightColabNewInsteadOfKillingIt()
    {
        var store = new SessionStore(_dataDir);
        bool remoteAlive = false;
        var createGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var createStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool createTokenWasCancellable = true;
        var order = new List<string>();

        var cli = new FakeColabCli(async (args, _, ct) =>
        {
            switch (args[0])
            {
                case "sessions":
                    return new ColabResult(0, remoteAlive ? CliMangaLine : CliNoSessions, string.Empty);
                case "new":
                    createTokenWasCancellable = ct.CanBeCanceled;
                    createStarted.TrySetResult();
                    await createGate.Task;
                    remoteAlive = true;
                    lock (order) order.Add("new-returned");
                    return new ColabResult(0, "[colab] Session READY.\n", string.Empty);
                case "stop":
                    lock (order) order.Add("stop");
                    remoteAlive = false;
                    return new ColabResult(0, "[colab] Session terminated.\n", string.Empty);
                default:
                    return new ColabResult(0, "ok", string.Empty);
            }
        });

        var mgr = new SessionManager(cli, new VmScripts(store, _scriptsDir), store, new BillingTimer());
        var deployTask = mgr.StartOrDeployAsync(60, true);
        await createStarted.Task;

        var stopTask = mgr.StopSessionAsync();
        await Task.Delay(150);
        Assert.False(stopTask.IsCompleted);
        lock (order) Assert.Empty(order);

        createGate.SetResult();
        var stopResult = await stopTask;
        Assert.False(await deployTask);

        Assert.False(createTokenWasCancellable);
        Assert.True(stopResult.VerifiedStopped);
        lock (order) Assert.Equal(["new-returned", "stop"], order);
        Assert.Equal(SessionState.Idle, mgr.State);
        Assert.False(remoteAlive);
    }

    [Fact]
    public async Task AutoStopUntilVerified_RetriesAfterUnverifiedStop_AndGivesUpWhenUserTakesOver()
    {
        var store = new SessionStore(_dataDir);
        int stopCalls = 0;
        bool remoteAlive = true;
        var cli = new FakeColabCli((args, _, _) =>
        {
            if (args[0] == "stop")
            {
                // First automatic attempt fails (both internal tries); the retry succeeds.
                if (Interlocked.Increment(ref stopCalls) > 2) remoteAlive = false;
                return Task.FromResult(new ColabResult(remoteAlive ? 1 : 0, string.Empty, remoteAlive ? "network error" : string.Empty));
            }
            if (args[0] == "sessions") return Task.FromResult(new ColabResult(0, remoteAlive ? CliMangaLine : CliNoSessions, string.Empty));
            return Task.FromResult(new ColabResult(0, "ok", string.Empty));
        });

        var scheduled = new List<int>();
        var mgr = new SessionManager(cli, new VmScripts(store, _scriptsDir), store, new BillingTimer(),
            autoStopRetryDelays: [TimeSpan.FromMilliseconds(50)]);
        mgr.AutoStopRetryScheduled += (attempt, _) => scheduled.Add(attempt);

        var result = await mgr.AutoStopUntilVerifiedAsync();
        Assert.True(result.VerifiedStopped);
        Assert.Equal([1], scheduled);
        Assert.Equal(3, stopCalls);
        Assert.Equal(SessionState.Idle, mgr.State);

        // Stop can never be verified; once another operation changes the state the loop must end.
        var stuckSetup = new FakeVmSetup();
        var stuckCli = new FakeColabCli((args, _, _) => Task.FromResult(args[0] switch
        {
            "sessions" => new ColabResult(0, CliMangaLine, string.Empty),
            "stop" => new ColabResult(1, string.Empty, "network error"),
            "ssh" when args.Contains("/content/vm_setup.sh") => stuckSetup.Handle(args)!,
            "ssh" => new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mit ready\nGPU L4\n", string.Empty),
            _ => new ColabResult(0, "ok", string.Empty)
        }));
        var stuckMgr = new SessionManager(stuckCli, new VmScripts(store, _scriptsDir), store, new BillingTimer(),
            autoStopRetryDelays: [TimeSpan.FromSeconds(30)]);
        var loop = stuckMgr.AutoStopUntilVerifiedAsync();
        for (int i = 0; i < 400 && stuckMgr.State != SessionState.Error; i++) await Task.Delay(20);
        Assert.Equal(SessionState.Error, stuckMgr.State);
        Assert.True(stuckMgr.HasActiveVm);

        Assert.True(await stuckMgr.StartOrDeployAsync(60, true, preserveExistingStartTime: true));
        var loopResult = await loop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(loopResult.VerifiedStopped);
        Assert.Equal(SessionState.Ready, stuckMgr.State);
    }

    [Fact]
    public async Task Preflight_BlocksColabNewWhenLocalSshPrerequisitesAreMissing()
    {
        var store = new SessionStore(_dataDir);
        var cli = new FakeColabCli((_, _, _) => Task.FromResult(new ColabResult(0, CliNoSessions, string.Empty)));
        var mgr = new SessionManager(cli, new VmScripts(store, _scriptsDir), store, new BillingTimer(),
            environmentIssuesProvider: () => ["~/.ssh 下没有 id_ed25519.pub"]);

        Assert.False(await mgr.StartOrDeployAsync(60, true));
        Assert.Empty(cli.Calls);
        Assert.False(mgr.HasActiveVm);
        Assert.Contains("id_ed25519.pub", mgr.LastError);

        var profile = Path.Combine(_tempRoot, "profile");
        var bin = Path.Combine(_tempRoot, "bin");
        Directory.CreateDirectory(Path.Combine(profile, ".ssh"));
        Directory.CreateDirectory(bin);
        Assert.Equal(2, EnvironmentChecks.GetSshIssues(bin, profile).Count);
        File.WriteAllText(Path.Combine(bin, "ssh.exe"), string.Empty);
        File.WriteAllText(Path.Combine(profile, ".ssh", "id_rsa.pub"), "ssh-rsa AAAA");
        Assert.Single(EnvironmentChecks.GetSshIssues(bin, profile));
        File.WriteAllText(Path.Combine(profile, ".ssh", "id_ed25519.pub"), "ssh-ed25519 AAAA");
        Assert.Empty(EnvironmentChecks.GetSshIssues(bin, profile));
    }

    [Fact]
    public void GetSshIssues_WindowsOpenSshPresent_IgnoresMsysSshOnPath()
    {
        var profile = Path.Combine(_tempRoot, "profile");
        var msysBin = Path.Combine(_tempRoot, "git", "usr", "bin");
        Directory.CreateDirectory(Path.Combine(profile, ".ssh"));
        Directory.CreateDirectory(msysBin);
        File.WriteAllText(Path.Combine(msysBin, "ssh.exe"), string.Empty);
        File.WriteAllText(Path.Combine(profile, ".ssh", "id_ed25519.pub"), "ssh-ed25519 AAAA");

        // Windows OpenSSH installed → the child PATH is rewritten to prefer it, so the
        // MSYS ssh ahead of it on our own PATH is not an issue.
        var openSshDir = Path.Combine(_tempRoot, "win-openssh");
        Assert.Empty(EnvironmentChecks.GetSshIssues(msysBin, profile, openSshDir));
    }

    [Fact]
    public void GetSshIssues_NoWindowsOpenSsh_MsysSshFirstIsFlagged()
    {
        var profile = Path.Combine(_tempRoot, "profile");
        var msysBin = Path.Combine(_tempRoot, "git", "usr", "bin");
        Directory.CreateDirectory(Path.Combine(profile, ".ssh"));
        Directory.CreateDirectory(msysBin);
        File.WriteAllText(Path.Combine(msysBin, "ssh.exe"), string.Empty);
        File.WriteAllText(Path.Combine(profile, ".ssh", "id_ed25519.pub"), "ssh-ed25519 AAAA");

        var issue = Assert.Single(EnvironmentChecks.GetSshIssues(msysBin, profile, windowsOpenSshDir: null));
        Assert.Contains("Git/MSYS", issue);
        Assert.Contains("OpenSSH", issue);
    }

    [Fact]
    public void GetSshIssues_NoWindowsOpenSsh_NoSshOnPath_KeepsMissingMessage()
    {
        var profile = Path.Combine(_tempRoot, "profile");
        var bin = Path.Combine(_tempRoot, "bin");
        Directory.CreateDirectory(Path.Combine(profile, ".ssh"));
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(profile, ".ssh", "id_ed25519.pub"), "ssh-ed25519 AAAA");

        var issue = Assert.Single(EnvironmentChecks.GetSshIssues(bin, profile, windowsOpenSshDir: null));
        Assert.Contains("未在 PATH 中找到 ssh.exe", issue);
    }

    [Fact]
    public async Task DeployAndStop_FullLifecyclePersistsAndVerifiesSession()
    {
        bool remoteAlive = false;
        var observedStages = new List<DeployStage>();
        var setup = new FakeVmSetup(
        [
            "===== STAGE1 mit environment =====\nSTAGE1_OK\n===== STAGE2 llama.cpp + model =====\n",
            "STAGE2_OK\n===== STAGE3 env.sh =====\n===== STAGE4 llama-server =====\nLLAMA_UP\n",
            "===== STAGE5 mit api server =====\nMIT_UP\n===== SUMMARY =====\nVM_SETUP_COMPLETE\n"
        ]);

        var cli = new FakeColabCli((args, onOut, _) =>
        {
            var cmd = args[0];
            if (cmd == "sessions")
            {
                return Task.FromResult(new ColabResult(0,
                    remoteAlive ? "NAME   GPU\nmanga  L4\n" : "No active sessions found.",
                    string.Empty));
            }
            if (cmd == "new")
            {
                remoteAlive = true;
                return Task.FromResult(new ColabResult(0, "Session READY", string.Empty));
            }
            if (cmd == "upload")
            {
                return Task.FromResult(new ColabResult(0, "Uploaded", string.Empty));
            }
            if (cmd == "ssh" && args.Contains("/content/vm_setup.sh"))
            {
                return Task.FromResult(setup.Handle(args)!);
            }
            if (cmd == "ssh" && args.Contains("/content/vm_status.sh"))
            {
                return Task.FromResult(new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mit ready\nMIT_OK\nGPU NVIDIA L4, 14000 MiB, 23034 MiB", string.Empty));
            }
            if (cmd == "stop")
            {
                remoteAlive = false;
                return Task.FromResult(new ColabResult(0, "Session terminated", string.Empty));
            }

            return Task.FromResult(new ColabResult(0, "OK", string.Empty));
        });

        var store = new SessionStore(_dataDir);
        var scripts = new VmScripts(store, _scriptsDir);
        var billing = new BillingTimer();
        var mgr = new SessionManager(cli, scripts, store, billing);
        mgr.StateChanged += () =>
        {
            if (mgr.DeployStage != DeployStage.None)
            {
                observedStages.Add(mgr.DeployStage);
            }
        };

        var deployed = await mgr.StartOrDeployAsync(plannedMinutes: 60, autoStop: true);

        Assert.True(deployed);
        Assert.Equal(SessionState.Ready, mgr.State);
        Assert.True(mgr.HasActiveVm);
        Assert.True(File.Exists(store.SessionFilePath));
        Assert.Contains(DeployStage.CreatingRuntime, observedStages);
        Assert.Contains(DeployStage.UploadingScripts, observedStages);
        Assert.Contains(DeployStage.Stage2LlamaModel, observedStages);
        Assert.Contains(DeployStage.Stage5TranslatorWarmup, observedStages);
        Assert.Contains(DeployStage.Completed, observedStages);

        var stopRes = await mgr.StopSessionAsync(isAutoStop: true, tryKillTranslate: false);
        Assert.True(stopRes.VerifiedStopped);
        Assert.Equal(SessionState.Idle, mgr.State);
        Assert.False(mgr.HasActiveVm);
        Assert.False(File.Exists(store.SessionFilePath));
    }

    [Fact]
    public async Task StopSession_RefusesToClaimStoppedWhenSessionsOutputIsEmptyOrUnparseable()
    {
        // A-01: Empty stdout on `colab sessions` MUST NOT clear HasActiveVm or session.json!
        var store = new SessionStore(_dataDir);
        store.SaveSession(new SessionRecord { SessionName = "manga", StartedAt = DateTimeOffset.UtcNow, PlannedMinutes = 60 });

        var cli = new FakeColabCli((args, _, _) =>
        {
            if (args[0] == "stop")
                return Task.FromResult(new ColabResult(0, "Stop sent", string.Empty));
            if (args[0] == "sessions")
                return Task.FromResult(new ColabResult(0, string.Empty, string.Empty));
            return Task.FromResult(new ColabResult(0, "OK", string.Empty));
        });

        var mgr = new SessionManager(cli, new VmScripts(store, _scriptsDir), store, new BillingTimer());
        var stopRes = await mgr.StopSessionAsync();

        Assert.False(stopRes.VerifiedStopped);
        Assert.Equal(SessionState.Error, mgr.State);
        Assert.True(mgr.HasActiveVm);
        Assert.True(File.Exists(store.SessionFilePath));
    }

    [Fact]
    public async Task Deploy_WhenColabNewTimesOutButRemoteSessionExists_KeepsBillingProtection()
    {
        // A-02: `colab new` times out, re-verify `colab sessions` shows manga present -> MUST keep HasActiveVm & session.json
        int sessionsCalls = 0;
        var cli = new FakeColabCli((args, _, _) =>
        {
            if (args[0] == "sessions")
            {
                sessionsCalls++;
                return Task.FromResult(new ColabResult(0,
                    sessionsCalls == 1 ? "No active sessions found." : "NAME  GPU\nmanga L4\n",
                    string.Empty));
            }
            if (args[0] == "new")
            {
                return Task.FromResult(new ColabResult(-1, string.Empty, "Timed out", TimedOut: true));
            }
            return Task.FromResult(new ColabResult(0, "OK", string.Empty));
        });

        var store = new SessionStore(_dataDir);
        var mgr = new SessionManager(cli, new VmScripts(store, _scriptsDir), store, new BillingTimer());

        var ok = await mgr.StartOrDeployAsync(60, true);

        Assert.False(ok);
        Assert.Equal(SessionState.Error, mgr.State);
        Assert.True(mgr.HasActiveVm);
        Assert.True(File.Exists(store.SessionFilePath));
    }

    [Fact]
    public async Task Deploy_WhenColabNewFailsAndReverifyConfirmsAbsent_CleansProvisionalSession()
    {
        // A-02: `colab new` fails with quota error AND re-verify confirms ExplicitlyAbsent -> safe to clean provisional file
        var cli = new FakeColabCli((args, _, _) =>
        {
            if (args[0] == "sessions")
                return Task.FromResult(new ColabResult(0, "No active sessions found.", string.Empty));
            if (args[0] == "new")
                return Task.FromResult(new ColabResult(1, string.Empty, "L4 GPU quota unavailable"));
            return Task.FromResult(new ColabResult(0, "OK", string.Empty));
        });

        var store = new SessionStore(_dataDir);
        var mgr = new SessionManager(cli, new VmScripts(store, _scriptsDir), store, new BillingTimer());

        var ok = await mgr.StartOrDeployAsync(60, true);

        Assert.False(ok);
        Assert.Equal(SessionState.Error, mgr.State);
        Assert.False(mgr.HasActiveVm);
        Assert.False(File.Exists(store.SessionFilePath));
    }

    [Fact]
    public async Task StopSession_PreemptsInFlightDeployAndBlocksStaleWriteBackAndIsSingleFlight()
    {
        // A-03: Stop cancels in-flight deploy immediately, prevents stale deploy from overwriting Idle,
        // and concurrent Stop calls share one single-flight stop execution.
        var deployEnteredSetup = new TaskCompletionSource();
        int stopCallCount = 0;
        bool remoteAlive = false;
        var setup = new FakeVmSetup(neverFinish: true); // deploy stays stuck in the poll loop

        var cli = new FakeColabCli(async (args, _, ct) =>
        {
            if (args[0] == "sessions")
            {
                return new ColabResult(0, remoteAlive ? "NAME GPU\nmanga L4" : "No active sessions found.", string.Empty);
            }
            if (args[0] == "new")
            {
                remoteAlive = true;
                return new ColabResult(0, "READY", string.Empty);
            }
            if (args[0] == "upload")
            {
                return new ColabResult(0, "OK", string.Empty);
            }
            if (args[0] == "ssh" && args.Contains("/content/vm_setup.sh"))
            {
                deployEnteredSetup.TrySetResult();
                return setup.Handle(args)!;
            }
            if (args[0] == "stop")
            {
                Interlocked.Increment(ref stopCallCount);
                await Task.Delay(80);
                remoteAlive = false;
                return new ColabResult(0, "Stopped", string.Empty);
            }
            return new ColabResult(0, "OK", string.Empty);
        });

        var store = new SessionStore(_dataDir);
        var mgr = new SessionManager(cli, new VmScripts(store, _scriptsDir), store, new BillingTimer());

        var deployTask = mgr.StartOrDeployAsync(60, true);
        await deployEnteredSetup.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // Fire two concurrent stops while deploy is stuck in vm_setup.sh
        var stop1 = mgr.StopSessionAsync();
        var stop2 = mgr.StopSessionAsync();

        var stopResults = await Task.WhenAll(stop1, stop2);
        var deployOk = await deployTask;

        Assert.False(deployOk);
        Assert.All(stopResults, r => Assert.True(r.VerifiedStopped));
        Assert.Equal(1, stopCallCount); // Single-flight!
        Assert.Equal(SessionState.Idle, mgr.State);
        Assert.False(mgr.HasActiveVm);
    }

    [Fact]
    public async Task Deploy_SucceedsWhenLlamaIsHealthyEvenIfMit8001IsDown()
    {
        // A-11: GUI translation depends on llama :6006 only; mit :8001 down must NOT block Ready!
        var setup = new FakeVmSetup();
        var cli = new FakeColabCli((args, onOut, _) =>
        {
            if (args[0] == "sessions")
                return Task.FromResult(new ColabResult(0, "No active sessions found.", string.Empty));
            if (args[0] == "new")
                return Task.FromResult(new ColabResult(0, "READY", string.Empty));
            if (args[0] == "upload")
                return Task.FromResult(new ColabResult(0, "OK", string.Empty));
            if (args[0] == "ssh" && args.Contains("/content/vm_setup.sh"))
            {
                return Task.FromResult(setup.Handle(args)!);
            }
            if (args[0] == "ssh" && args.Contains("/content/vm_status.sh"))
            {
                return Task.FromResult(new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mit ready\nMIT_DOWN\nGPU NVIDIA L4", string.Empty));
            }
            return Task.FromResult(new ColabResult(0, "OK", string.Empty));
        });

        var store = new SessionStore(_dataDir);
        var mgr = new SessionManager(cli, new VmScripts(store, _scriptsDir), store, new BillingTimer());

        Assert.True(await mgr.StartOrDeployAsync(60, true));
        Assert.Equal(SessionState.Ready, mgr.State);
        Assert.True(mgr.LatestStatus.LlamaOk);
        Assert.False(mgr.LatestStatus.MitOk);
        Assert.True(mgr.LatestStatus.IsReadyForTranslation);
    }

    [Fact]
    public async Task PreflightScriptsGuard_BlocksColabNewWhenScriptsMissing_AndUserDirOverridesAutoDiscovery()
    {
        // A-10: Missing bundled assets MUST fail BEFORE calling `colab new`!
        var emptyBase = Path.Combine(_tempRoot, "empty_base");
        Directory.CreateDirectory(emptyBase);

        var cli = new FakeColabCli((_, _, _) => Task.FromResult(new ColabResult(0, "OK", string.Empty)));
        var store = new SessionStore(_dataDir);
        var missingScripts = new VmScripts(store, emptyBase);
        var mgr = new SessionManager(cli, missingScripts, store, new BillingTimer());

        var ok = await mgr.StartOrDeployAsync(60, true);
        Assert.False(ok);
        Assert.Empty(cli.Calls); // Zero CLI calls, zero billing!
        Assert.False(mgr.HasActiveVm);

        // A-10: User-selected ScriptsDir overrides auto-discovered parent directory
        var customDir = Path.Combine(_tempRoot, "user_custom_scripts");
        Directory.CreateDirectory(customDir);
        foreach (var f in VmScripts.RequiredExistingScripts)
            File.WriteAllText(Path.Combine(customDir, f), "# custom\n");

        var scripts = new VmScripts(store, _scriptsDir);
        Assert.Equal(_scriptsDir, scripts.ExistingScriptsDirectory);
        Assert.Equal(ScriptsSourceKind.AutoDiscovered, scripts.ExistingScriptsSource);

        Assert.True(scripts.TrySetExistingScriptsDirectory(customDir));
        var reloaded = new VmScripts(store, _scriptsDir);
        Assert.Equal(customDir, reloaded.ExistingScriptsDirectory);
        Assert.Equal(ScriptsSourceKind.UserSelected, reloaded.ExistingScriptsSource);
    }

    [Fact]
    public async Task PersistenceFailure_OnDeleteStillTransitionsToIdleWithWarning_AndCorruptedJsonProtected()
    {
        // A-02: Corrupted session.json + failed sessions probe -> protects billing & surfaces PersistenceWarning
        var store = new SessionStore(_dataDir);
        File.WriteAllText(store.SessionFilePath, "{ broken json");

        var failCli = new FakeColabCli((_, _, _) => Task.FromResult(new ColabResult(1, "", "network offline")));
        var mgr1 = new SessionManager(failCli, new VmScripts(store, _scriptsDir), store, new BillingTimer());
        var probe = await mgr1.ProbeOnStartupAsync();
        Assert.Equal(StartupProbeOutcome.ProbeFailed, probe.Outcome);
        Assert.True(mgr1.HasActiveVm);
        Assert.False(string.IsNullOrWhiteSpace(mgr1.PersistenceWarning));

        // Disk delete failure (file locked open) during Stop MUST NOT block Idle state transition
        File.WriteAllText(store.SessionFilePath, """{"session_name":"manga","started_at":"2026-11-20T01:00:00Z"}""");
        using var fileLock = new FileStream(store.SessionFilePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var okCli = new FakeColabCli((args, _, _) =>
        {
            if (args[0] == "sessions")
                return Task.FromResult(new ColabResult(0, "No active sessions found.", string.Empty));
            return Task.FromResult(new ColabResult(0, "Stopped", string.Empty));
        });

        var mgr2 = new SessionManager(okCli, new VmScripts(store, _scriptsDir), store, new BillingTimer());
        var stopRes = await mgr2.StopSessionAsync();
        Assert.True(stopRes.VerifiedStopped);
        Assert.Equal(SessionState.Idle, mgr2.State);
        Assert.False(mgr2.HasActiveVm);
        Assert.False(string.IsNullOrWhiteSpace(mgr2.PersistenceWarning));
    }

    [Fact]
    public async Task DeployFailureAfterNew_KeepsHasActiveVmAndSessionJsonForBillingProtection()
    {
        bool remoteAlive = false;
        var cli = new FakeColabCli((args, _, _) =>
        {
            var cmd = args[0];
            if (cmd == "sessions")
            {
                return Task.FromResult(new ColabResult(0, remoteAlive ? "manga  L4" : "No active sessions found.", string.Empty));
            }
            if (cmd == "new")
            {
                remoteAlive = true;
                return Task.FromResult(new ColabResult(0, "Session READY", string.Empty));
            }
            if (cmd == "upload")
            {
                return Task.FromResult(new ColabResult(0, "Uploaded", string.Empty));
            }
            if (cmd == "ssh")
            {
                return Task.FromResult(new ColabResult(1, "STAGE2_FAIL", "curl download failed"));
            }
            return Task.FromResult(new ColabResult(0, string.Empty, string.Empty));
        });

        var store = new SessionStore(_dataDir);
        var scripts = new VmScripts(store, _scriptsDir);
        var mgr = new SessionManager(cli, scripts, store, new BillingTimer());

        var deployed = await mgr.StartOrDeployAsync(60, true);

        Assert.False(deployed);
        Assert.Equal(SessionState.Error, mgr.State);
        Assert.True(mgr.HasActiveVm);
        Assert.True(File.Exists(store.SessionFilePath));
    }

    [Fact]
    public async Task StartupProbe_RecoversReadySessionWithOriginalStartedAt()
    {
        var originalStart = new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.FromHours(8));
        var now = originalStart.AddMinutes(35);

        var store = new SessionStore(_dataDir);
        store.SaveSession(new SessionRecord
        {
            SessionName = "manga",
            StartedAt = originalStart,
            PlannedMinutes = 60,
            AutoStop = true
        });

        var cli = new FakeColabCli((args, _, _) =>
        {
            if (args[0] == "sessions")
            {
                return Task.FromResult(new ColabResult(0, "NAME  GPU\nmanga L4", string.Empty));
            }
            return Task.FromResult(new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mit ready\nMIT_DOWN\nGPU NVIDIA L4", string.Empty));
        });

        var billing = new BillingTimer(() => now);
        var mgr = new SessionManager(cli, new VmScripts(store, _scriptsDir), store, billing, () => now);

        var probe = await mgr.ProbeOnStartupAsync();

        Assert.Equal(StartupProbeOutcome.RecoveredReady, probe.Outcome);
        Assert.Equal(SessionState.Ready, mgr.State);
        Assert.True(mgr.HasActiveVm);

        var snap = billing.Evaluate(fireEvents: false);
        Assert.Equal(35, (int)snap.Elapsed.TotalMinutes);
    }

    [Fact]
    public async Task StartupProbe_DetectsOrphanSessionAndDeletesStaleWhenExplicitlyAbsent()
    {
        var store = new SessionStore(_dataDir);
        var orphanCli = new FakeColabCli((_, _, _) =>
            Task.FromResult(new ColabResult(0, "manga   L4   RUNNING", string.Empty)));
        var mgr1 = new SessionManager(orphanCli, new VmScripts(store, _scriptsDir), store, new BillingTimer());

        var probe1 = await mgr1.ProbeOnStartupAsync();
        Assert.Equal(StartupProbeOutcome.OrphanSessionDetected, probe1.Outcome);
        Assert.True(mgr1.HasActiveVm);

        store.SaveSession(new SessionRecord { SessionName = "manga", StartedAt = DateTimeOffset.UtcNow, PlannedMinutes = 60 });
        var absentCli = new FakeColabCli((_, _, _) =>
            Task.FromResult(new ColabResult(0, "No active sessions found.", string.Empty)));
        var mgr2 = new SessionManager(absentCli, new VmScripts(store, _scriptsDir), store, new BillingTimer());

        var probe2 = await mgr2.ProbeOnStartupAsync();
        Assert.Equal(StartupProbeOutcome.CleanedStaleRecord, probe2.Outcome);
        Assert.Equal(SessionState.Idle, mgr2.State);
        Assert.False(mgr2.HasActiveVm);
        Assert.False(File.Exists(store.SessionFilePath));
    }

    [Fact]
    public async Task StartupProbe_CleansStaleRemoteJobsBetweenUploadAndStatusCheck()
    {
        // F6: a killed front-end leaves remote job_* dirs; recovery must run
        // vm_pack.sh clean_stale after the asset upload and before the status check.
        var store = new SessionStore(_dataDir);
        store.SaveSession(new SessionRecord
        {
            SessionName = "manga",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            PlannedMinutes = 60,
            AutoStop = true
        });

        var order = new List<string>();
        var cli = new FakeColabCli((args, _, _) =>
        {
            if (args[0] == "sessions")
            {
                return Task.FromResult(new ColabResult(0, "manga L4", string.Empty));
            }

            if (args[0] == "upload")
            {
                order.Add("upload");
                return Task.FromResult(new ColabResult(0, "OK", string.Empty));
            }

            if (args[0] == "ssh" && args.Contains("clean_stale"))
            {
                order.Add("clean_stale");
                return Task.FromResult(new ColabResult(0, "STALE_CLEAN_OK 2 1", string.Empty));
            }

            if (args[0] == "ssh" && args.Contains("/content/vm_status.sh"))
            {
                order.Add("vm_status");
                return Task.FromResult(new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mtui ready\nGPU L4", string.Empty));
            }

            return Task.FromResult(new ColabResult(0, "ok", string.Empty));
        });

        var logs = new List<(LogLevel Level, string Message)>();
        var mgr = new SessionManager(cli, new VmScripts(store, _scriptsDir), store, new BillingTimer());
        mgr.LogMessage += (level, message) => logs.Add((level, message));

        var probe = await mgr.ProbeOnStartupAsync();

        Assert.Equal(StartupProbeOutcome.RecoveredReady, probe.Outcome);
        var lastUpload = order.FindLastIndex(x => x == "upload");
        var iClean = order.IndexOf("clean_stale");
        var iStatus = order.IndexOf("vm_status");
        Assert.True(lastUpload >= 0 && iClean > lastUpload && iStatus > iClean,
            $"expected upload < clean_stale < vm_status, got [{string.Join(",", order)}]");
        Assert.Contains(logs, l => l.Message.Contains("已清理上次遗留的 3 个云端任务"));
    }

    [Fact]
    public async Task StartupProbe_CleanStaleFailure_StillRecoversWithWarning()
    {
        // F6: clean_stale is best-effort — a failure logs a Warning but must not block recovery.
        var store = new SessionStore(_dataDir);
        store.SaveSession(new SessionRecord
        {
            SessionName = "manga",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            PlannedMinutes = 60,
            AutoStop = true
        });

        var cli = new FakeColabCli((args, _, _) =>
        {
            if (args[0] == "sessions")
            {
                return Task.FromResult(new ColabResult(0, "manga L4", string.Empty));
            }

            if (args[0] == "ssh" && args.Contains("clean_stale"))
            {
                return Task.FromResult(new ColabResult(1, string.Empty, "SSH broken pipe"));
            }

            if (args[0] == "ssh")
            {
                return Task.FromResult(new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mtui ready\nGPU L4", string.Empty));
            }

            return Task.FromResult(new ColabResult(0, "ok", string.Empty));
        });

        var logs = new List<(LogLevel Level, string Message)>();
        var mgr = new SessionManager(cli, new VmScripts(store, _scriptsDir), store, new BillingTimer());
        mgr.LogMessage += (level, message) => logs.Add((level, message));

        var probe = await mgr.ProbeOnStartupAsync();

        Assert.Equal(StartupProbeOutcome.RecoveredReady, probe.Outcome);
        Assert.Equal(SessionState.Ready, mgr.State);
        Assert.Contains(logs, l => l.Level == LogLevel.Warning && l.Message.Contains("清理云端遗留任务失败"));
    }

    [Fact]
    public async Task StartupProbe_MitNote_OnlyAppendedWhenDeployedEngineIsMit()
    {
        // F2: the ":8001 not responding" note is irrelevant under mtui — it must only
        // appear when the deployed engine is actually mit and :8001 is down.
        FakeColabCli MkCli(string engineLine) => new((args, _, _) => Task.FromResult(args[0] switch
        {
            "sessions" => new ColabResult(0, "manga L4", string.Empty),
            "ssh" => new ColabResult(0, $"PROTO 2\nLLAMA_OK\n{engineLine}\nMIT_DOWN\nGPU L4", string.Empty),
            _ => new ColabResult(0, "ok", string.Empty)
        }));

        var store1 = new SessionStore(_dataDir);
        store1.SaveSession(new SessionRecord { SessionName = "manga", StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10), PlannedMinutes = 60 });
        var mgrMtui = new SessionManager(MkCli("ENGINE mtui ready"), new VmScripts(store1, _scriptsDir), store1, new BillingTimer());
        var probeMtui = await mgrMtui.ProbeOnStartupAsync();
        Assert.Equal(StartupProbeOutcome.RecoveredReady, probeMtui.Outcome);
        Assert.DoesNotContain("8001", mgrMtui.StatusDetail);

        var dataDir2 = Path.Combine(_tempRoot, "appdata_mit");
        Directory.CreateDirectory(dataDir2);
        var store2 = new SessionStore(dataDir2);
        store2.SaveSession(new SessionRecord { SessionName = "manga", StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10), PlannedMinutes = 60 });
        var mgrMit = new SessionManager(MkCli("ENGINE mit ready"), new VmScripts(store2, _scriptsDir), store2, new BillingTimer());
        var probeMit = await mgrMit.ProbeOnStartupAsync();
        Assert.Equal(StartupProbeOutcome.RecoveredReady, probeMit.Outcome);
        Assert.Contains("8001", mgrMit.StatusDetail);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }
}
