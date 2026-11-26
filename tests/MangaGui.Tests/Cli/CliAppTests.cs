using System.IO;
using System.Threading;
using MangaCli;
using MangaGui.Models;
using MangaGui.Services;
using Xunit;

namespace MangaGui.Tests.Cli;

/// <summary>
/// Integration tests for <see cref="CliApp"/> running the full headless GUI composition against
/// <see cref="FakeColabCli"/>. No real Colab calls, no real %APPDATA% — every test uses a temp
/// data dir, temp scripts/assets, a Local\ GUID mutex and short timeouts.
/// </summary>
public sealed class CliAppTests
{
    // xUnit creates a fresh test-class instance per test → each test gets its own temp dirs.
    private readonly CliFixture _fx = new();

    private string[] Args(params string[] args) => args;

    // --------------------------------------------------------- 1. usage errors

    [Fact]
    public async Task UnknownCommand_Exits2_WithZeroColabCalls()
    {
        var fake = new FakeColabCli((a, _, _) => Task.FromResult(new ColabResult(0, "OK", "")));
        var stdout = new StringWriter();
        var code = await CliApp.RunAsync(Args("frobnicate"), _fx.Env(fake, stdout));

        Assert.Equal(ExitCodes.Usage, code);
        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Run_WithoutMinutes_Exits2_WithZeroColabCalls()
    {
        var fake = new FakeColabCli((a, _, _) => Task.FromResult(new ColabResult(0, "OK", "")));
        var stdout = new StringWriter();
        var code = await CliApp.RunAsync(
            Args("run", "--data-dir", _fx.DataDir), _fx.Env(fake, stdout));

        Assert.Equal(ExitCodes.Usage, code);
        Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Run_MinutesZero_Exits2_WithZeroColabCalls()
    {
        var fake = new FakeColabCli((a, _, _) => Task.FromResult(new ColabResult(0, "OK", "")));
        var stdout = new StringWriter();
        var code = await CliApp.RunAsync(
            Args("run", "--minutes", "0", "--data-dir", _fx.DataDir), _fx.Env(fake, stdout));

        Assert.Equal(ExitCodes.Usage, code);
        Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Empty(fake.Calls);
    }

    // --------------------------------------------------------- 2. run success

    [Fact]
    public async Task Run_FullPipeline_DeploysTranslatesStopsVerified()
    {
        var book = _fx.CreateBookZip();
        var outDir = Path.Combine(_fx.TempRoot, "out");
        Directory.CreateDirectory(outDir);
        var fake = _fx.FullPipelineCli(out _);
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("run", book, "--minutes", "60", "--data-dir", _fx.DataDir,
                 "--output-dir", outDir, "--quiet-logs"),
            _fx.Env(fake, stdout));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(0, code);
        Assert.Equal(0, result.GetProperty("exit_code").GetInt32());

        var tasks = result.GetProperty("tasks").EnumerateArray().ToList();
        var task = Assert.Single(tasks);
        Assert.Equal("completed", task.GetProperty("status").GetString());
        var output = task.GetProperty("output").GetString();
        Assert.NotNull(output);
        Assert.True(File.Exists(output));

        var session = result.GetProperty("session");
        Assert.False(session.GetProperty("billing_active").GetBoolean());
        Assert.True(session.GetProperty("stop_verified").GetBoolean());
        Assert.True(Ndjson.HasCallStartingWith(fake, "new"));
        Assert.True(Ndjson.HasCallStartingWith(fake, "stop"));

        // billing{} is the pre-stop snapshot: elapsed/CU are still populated there even
        // though billing_active already reports false (current state).
        var billing = result.GetProperty("billing");
        Assert.Equal(60, billing.GetProperty("planned_minutes").GetInt32());
        Assert.True(billing.GetProperty("running").GetBoolean());
    }

    // --------------------------------------------------------- 3. deploy failure

    [Fact]
    public async Task Run_DeployFailure_Exits5_AndCallsStop()
    {
        var fake = _fx.FullPipelineCli(out _, failSetup: true);
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("run", "--minutes", "30", "--data-dir", _fx.DataDir, "--quiet-logs"),
            _fx.Env(fake, stdout));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(ExitCodes.DeployFailed, code);
        Assert.True(Ndjson.HasCallStartingWith(fake, "new"), "deploy should have created the session");
        Assert.True(Ndjson.HasCallStartingWith(fake, "stop"), "failed deploy must still call stop");
        Assert.False(result.GetProperty("session").GetProperty("billing_active").GetBoolean());
    }

    // --------------------------------------------------------- 4. stop unverified → 7

    [Fact]
    public async Task Stop_SessionsStillListsManga_Exits7_BillingActive()
    {
        // Cloud keeps listing manga forever → stop can never be verified.
        var fake = _fx.FullPipelineCli(out _, sessionsAlwaysAlive: true, startAlive: true);
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("stop", "--data-dir", _fx.DataDir, "--quiet-logs"),
            _fx.Env(fake, stdout,
                stopVerifyTimeout: TimeSpan.FromSeconds(2),
                autoStopRetryDelays: [TimeSpan.FromMilliseconds(50)]));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(ExitCodes.StopUnverified, code);
        Assert.True(Ndjson.HasCallStartingWith(fake, "stop"));
        var session = result.GetProperty("session");
        Assert.True(session.GetProperty("billing_active").GetBoolean());
        Assert.False(session.GetProperty("stop_verified").GetBoolean());
    }

    // --------------------------------------------------------- 5. orphan handling

    [Fact]
    public async Task Run_OrphanSession_OnOrphanFail_Exits8_NoNewCall()
    {
        // sessions lists manga but no local session.json → orphan.
        var fake = _fx.FullPipelineCli(out _, startAlive: true);
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("run", "--minutes", "30", "--data-dir", _fx.DataDir, "--on-orphan", "fail", "--quiet-logs"),
            _fx.Env(fake, stdout));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(ExitCodes.OrphanRefused, code);
        Assert.False(Ndjson.HasCallStartingWith(fake, "new"), "orphan+fail must not deploy");
        Assert.False(Ndjson.HasCallStartingWith(fake, "stop"), "orphan+fail must not touch the session");
    }

    [Fact]
    public async Task Run_OrphanSession_OnOrphanStop_CallsStop()
    {
        var fake = _fx.FullPipelineCli(out _, startAlive: true);
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("run", "--minutes", "30", "--data-dir", _fx.DataDir, "--on-orphan", "stop", "--quiet-logs"),
            _fx.Env(fake, stdout));

        Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.True(Ndjson.HasCallStartingWith(fake, "stop"), "orphan+stop must call colab stop");
        // After the orphan is gone the normal deploy runs; whatever the final code,
        // the orphan policy itself was exercised.
        Assert.True(Ndjson.HasCallStartingWith(fake, "new") || code == 0 || code == ExitCodes.DeployFailed || code == ExitCodes.TaskFailed);
    }

    // --------------------------------------------------------- 6. lock contention

    [Fact]
    public async Task Run_WhenLockHeld_Exits4_WithZeroColabCalls()
    {
        var fake = new FakeColabCli((a, _, _) => Task.FromResult(new ColabResult(0, "OK", "")));
        var stdout = new StringWriter();
        var env = _fx.Env(fake, stdout);
        using var held = new Mutex(initiallyOwned: false, env.MutexName);

        var code = await CliApp.RunAsync(
            Args("run", "--minutes", "30", "--data-dir", _fx.DataDir), env);

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(ExitCodes.Locked, code);
        Assert.True(result.GetProperty("lock_held").GetBoolean());
        Assert.Empty(fake.Calls);
    }

    // --------------------------------------------------------- 7. interrupt

    [Fact]
    public async Task Run_InterruptedDuringTask_Exits130_AndVerifiesStop()
    {
        var book = _fx.CreateBookZip();
        var outDir = Path.Combine(_fx.TempRoot, "out_int");
        Directory.CreateDirectory(outDir);
        using var interrupt = new CancellationTokenSource();
        var fake = _fx.FullPipelineCli(out _, onJobStart: () => interrupt.Cancel());
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("run", book, "--minutes", "30", "--data-dir", _fx.DataDir,
                 "--output-dir", outDir, "--quiet-logs"),
            _fx.Env(fake, stdout), interrupt.Token);

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(ExitCodes.Interrupted, code);
        Assert.True(Ndjson.HasCallStartingWith(fake, "stop"), "interrupt must still stop the session");
        Assert.True(result.GetProperty("session").GetProperty("stop_verified").GetBoolean());
        Assert.False(result.GetProperty("session").GetProperty("billing_active").GetBoolean());
    }

    // --------------------------------------------------------- 8. time expiry → drain then stop

    [Fact]
    public async Task Run_TimeExpired_AutoPolicy_DrainsThenStops()
    {
        var book = _fx.CreateBookZip();
        var outDir = Path.Combine(_fx.TempRoot, "out_exp");
        Directory.CreateDirectory(outDir);
        var now = DateTimeOffset.UtcNow;
        // When the translation job starts, jump the injected clock well past the 1-minute plan.
        var fake = _fx.FullPipelineCli(out _, onJobStart: () => now = now.AddMinutes(5));
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("run", book, "--minutes", "1", "--data-dir", _fx.DataDir,
                 "--output-dir", outDir, "--on-expiry", "auto", "--quiet-logs"),
            _fx.Env(fake, stdout,
                utcNow: () => now,
                busyExpiryGrace: TimeSpan.FromMilliseconds(100),
                stopVerifyTimeout: TimeSpan.FromSeconds(10)));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        var prompts = Ndjson.Events(stdout.ToString(), "prompt");
        Assert.Contains(prompts, p =>
            p.GetProperty("kind").GetString() == "time_expired"
            && p.GetProperty("answer").GetString() == "drain_then_stop");
        // Queue drained then the session was stopped & verified: 0 ok, 6 if the task
        // was cancelled by the jumped clock — but never 7 (billing must be settled).
        Assert.Contains(code, new[] { 0, 6 });
        Assert.False(result.GetProperty("session").GetProperty("billing_active").GetBoolean());
    }

    // --------------------------------------------------------- 9. precheck

    [Fact]
    public async Task Precheck_ValidZip_Exits0_WithPageCount()
    {
        var book = _fx.CreateBookZip();
        var fake = new FakeColabCli((a, _, _) => Task.FromResult(new ColabResult(0, "OK", "")));
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("precheck", book, "--data-dir", _fx.DataDir, "--quiet-logs"),
            _fx.Env(fake, stdout));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(0, code);
        var accepted = result.GetProperty("accepted").EnumerateArray().ToList();
        Assert.Single(accepted);
        Assert.True(accepted[0].GetProperty("total_pages").GetInt32() >= 1);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Precheck_EnglishFilenameUnderMtui_Rejected_Exits2()
    {
        // mtui rejects source hints containing "english" — a filename rejection, offline.
        var book = _fx.CreateBookZip("english book.zip");
        var fake = new FakeColabCli((a, _, _) => Task.FromResult(new ColabResult(0, "OK", "")));
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("precheck", book, "--data-dir", _fx.DataDir, "--quiet-logs"),
            _fx.Env(fake, stdout));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(ExitCodes.Usage, code);
        Assert.NotEmpty(result.GetProperty("rejected").EnumerateArray());
        Assert.Empty(fake.Calls);
    }

    // --------------------------------------------------------- 10. offline commands make zero cloud calls

    [Theory]
    [InlineData("doctor")]
    [InlineData("settings get")]
    [InlineData("status")]
    [InlineData("logs")]
    public async Task OfflineCommands_ZeroCloudCalls(string command)
    {
        var fake = new FakeColabCli((a, _, _) => Task.FromResult(new ColabResult(0, "OK", "")));
        var stdout = new StringWriter();
        var args = command.Split(' ').Concat(new[] { "--data-dir", _fx.DataDir, "--quiet-logs" }).ToArray();

        var code = await CliApp.RunAsync(args, _fx.Env(fake, stdout));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.True(code is 0 or 3, $"{command} exited {code}");
        Assert.Equal(0, result.GetProperty("cloud_calls_blocked").GetInt32());
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task PrecheckOffline_ZeroCloudCalls()
    {
        var book = _fx.CreateBookZip();
        var fake = new FakeColabCli((a, _, _) => Task.FromResult(new ColabResult(0, "OK", "")));
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("precheck", book, "--data-dir", _fx.DataDir, "--quiet-logs"),
            _fx.Env(fake, stdout));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(0, code);
        Assert.Equal(0, result.GetProperty("cloud_calls_blocked").GetInt32());
        Assert.Empty(fake.Calls);
    }

    // --------------------------------------------------------- 11. help / version

    [Fact]
    public async Task Help_AndVersion_EmitResult_AndExit0()
    {
        var stdout = new StringWriter();
        var code = await CliApp.RunAsync(Args("--help"), _fx.Env(null, stdout));
        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(0, code);
        Assert.True(result.TryGetProperty("usage", out _));

        stdout = new StringWriter();
        code = await CliApp.RunAsync(Args("--version"), _fx.Env(null, stdout));
        result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(0, code);
        Assert.True(result.TryGetProperty("version", out _));
    }

    // --------------------------------------------------------- 12. session recovery (adopt local record)

    [Fact]
    public async Task Run_WithExistingSessionRecord_RecoversWithoutNew()
    {
        // Pre-populate session.json: an in-flight session from a crashed front-end.
        File.WriteAllText(Path.Combine(_fx.DataDir, "session.json"),
            """
            {"session_name":"manga","started_at":"2026-11-23T03:00:00+08:00","planned_minutes":60,"auto_stop":true,"start_time_unknown":false}
            """);

        var book = _fx.CreateBookZip();
        var outDir = Path.Combine(_fx.TempRoot, "out_rec");
        Directory.CreateDirectory(outDir);
        var fake = _fx.FullPipelineCli(out _, startAlive: true);
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("run", book, "--minutes", "30", "--data-dir", _fx.DataDir,
                 "--output-dir", outDir, "--quiet-logs"),
            _fx.Env(fake, stdout));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(0, code);
        Assert.False(Ndjson.HasCallStartingWith(fake, "new"), "recovery must not redeploy");
        var task = Assert.Single(result.GetProperty("tasks").EnumerateArray());
        Assert.Equal("completed", task.GetProperty("status").GetString());
        Assert.True(Ndjson.HasCallStartingWith(fake, "stop"));
    }

    // --------------------------------------------------------- C. rework additions

    [Fact]
    public async Task Run_RecoveredNeedsDeploy_RetriesDeploy_ThenProcesses()
    {
        // session.json + cloud lists manga + first vm_status not ready → RecoveredNeedsDeploy
        // → GUI-equivalent "重试部署" (vm_setup again, NO colab new) → task runs → 0.
        // started_at 用当前时间：固定过去时间会让记录在测试期间「已到期」，到期自动停机
        // 恰好在 Error/Deploying 窗口选择 stop_immediately 抢占任务，形成竞态。
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O");
        File.WriteAllText(Path.Combine(_fx.DataDir, "session.json"),
            $$"""{"session_name":"manga","started_at":"{{startedAt}}","planned_minutes":60,"auto_stop":true,"start_time_unknown":false}""");

        var book = _fx.CreateBookZip();
        var outDir = Path.Combine(_fx.TempRoot, "out_rnd");
        Directory.CreateDirectory(outDir);
        var fake = _fx.FullPipelineCli(out _, startAlive: true, firstStatusNotReady: true);
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("run", book, "--minutes", "30", "--data-dir", _fx.DataDir,
                 "--output-dir", outDir, "--quiet-logs"),
            _fx.Env(fake, stdout));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(0, code);
        Assert.False(Ndjson.HasCallStartingWith(fake, "new"), "recovery must not create a new session");
        Assert.True(fake.SshCalls.Any(c => c.RemoteArgs.Contains("/content/vm_setup.sh")),
            "RecoveredNeedsDeploy must re-run deployment via RetryDeployCommand");
        var task = Assert.Single(result.GetProperty("tasks").EnumerateArray());
        Assert.Equal("completed", task.GetProperty("status").GetString());
        Assert.True(result.GetProperty("session").GetProperty("stop_verified").GetBoolean());
    }

    [Fact]
    public async Task Run_InterruptDuringDeploy_PreemptsAndStops()
    {
        // vm_setup --poll hangs; interrupt must preempt the deploy via the dispatcher-posted
        // stop (same as pressing「停止」in the GUI mid-deploy) and finish <10s with 130.
        var remoteAlive = false;
        var deployPollStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeColabCli(async (args, onOut, ct) =>
        {
            var cmd = args[0];
            if (cmd == "sessions")
            {
                return new ColabResult(0, remoteAlive ? "manga L4" : "No active sessions found.", string.Empty);
            }

            if (cmd == "new")
            {
                remoteAlive = true;
                return new ColabResult(0, "OK", string.Empty);
            }

            if (cmd == "upload")
            {
                return new ColabResult(0, "OK", string.Empty);
            }

            if (cmd == "ssh" && args.Contains("/content/vm_setup.sh") && args.Contains("--poll"))
            {
                deployPollStarted.TrySetResult();
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), ct);
                }
                catch (OperationCanceledException)
                {
                    return new ColabResult(1, string.Empty, "cancelled");
                }
            }

            if (cmd == "ssh" && args.Contains("/content/vm_setup.sh"))
            {
                return new ColabResult(0, "SETUP_DETACHED\n", string.Empty);
            }

            if (cmd == "stop")
            {
                remoteAlive = false;
                return new ColabResult(0, "Session terminated", string.Empty);
            }

            return new ColabResult(0, "OK", string.Empty);
        });

        var stdout = new StringWriter();
        using var interrupt = new CancellationTokenSource();
        var runTask = CliApp.RunAsync(
            Args("run", "--minutes", "30", "--data-dir", _fx.DataDir, "--quiet-logs"),
            _fx.Env(fake, stdout), interrupt.Token);

        await deployPollStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        interrupt.Cancel();

        var code = await runTask.WaitAsync(TimeSpan.FromSeconds(10));
        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(ExitCodes.Interrupted, code);
        Assert.True(Ndjson.HasCallStartingWith(fake, "stop"), "interrupt during deploy must stop the VM");
        Assert.True(result.GetProperty("session").GetProperty("stop_verified").GetBoolean());
    }

    [Fact]
    public async Task Run_SessionStoppedWithPendingTasks_Exits6()
    {
        // Job status polls keep failing → task fails while a second book is still
        // Pending/also fails → not all tasks completed → 6 (not 0).
        var b1 = _fx.CreateBookZip("vol1.zip");
        var b2 = _fx.CreateBookZip("vol2.zip");
        var outDir = Path.Combine(_fx.TempRoot, "out_pend");
        Directory.CreateDirectory(outDir);
        var fake = _fx.FullPipelineCli(out _, pollFails: true);
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("run", b1, b2, "--minutes", "30", "--data-dir", _fx.DataDir,
                 "--output-dir", outDir, "--quiet-logs"),
            _fx.Env(fake, stdout));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(ExitCodes.TaskFailed, code);
        var tasks = result.GetProperty("tasks").EnumerateArray().ToList();
        Assert.Equal(2, tasks.Count);
        Assert.DoesNotContain(tasks, t => t.GetProperty("status").GetString() == "completed");
        Assert.True(result.GetProperty("session").GetProperty("stop_verified").GetBoolean());
    }

    [Fact]
    public async Task Run_OrphanAdopt_StartsWorker()
    {
        var book = _fx.CreateBookZip();
        var outDir = Path.Combine(_fx.TempRoot, "out_adopt");
        Directory.CreateDirectory(outDir);
        var fake = _fx.FullPipelineCli(out _, startAlive: true);
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("run", book, "--minutes", "30", "--data-dir", _fx.DataDir,
                 "--output-dir", outDir, "--on-orphan", "adopt", "--quiet-logs"),
            _fx.Env(fake, stdout));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(0, code);
        Assert.False(Ndjson.HasCallStartingWith(fake, "new"), "adopted orphan must not redeploy");
        var task = Assert.Single(result.GetProperty("tasks").EnumerateArray());
        Assert.Equal("completed", task.GetProperty("status").GetString());
        var prompts = Ndjson.Events(stdout.ToString(), "prompt");
        Assert.Contains(prompts, p =>
            p.GetProperty("kind").GetString() == "orphan_session"
            && p.GetProperty("answer").GetString() == "adopt");
        Assert.True(result.GetProperty("session").GetProperty("stop_verified").GetBoolean());
    }

    // --------------------------------------------------------- G1: manga.json only for mit

    private void SetEngine(CliFixture fx, string engine)
    {
        var store = new SessionStore(fx.DataDir);
        var s = store.LoadSettings();
        s.TranslationEngine = engine;
        Assert.True(store.TrySaveSettings(s).Success);
    }

    [Fact]
    public async Task Doctor_UnderMtui_WithoutMangaJson_Exits0()
    {
        var fx = new CliFixture(includeMangaJson: false);
        var stdout = new StringWriter();
        var code = await CliApp.RunAsync(Args("doctor", "--data-dir", fx.DataDir), fx.Env(null, stdout));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(0, code);
        Assert.Equal("mtui", result.GetProperty("engine").GetString());
        var issues = result.GetProperty("issues").EnumerateArray().Select(i => i.GetString()).ToList();
        Assert.DoesNotContain(issues, i => i is not null && i.Contains("manga.json"));
    }

    [Fact]
    public async Task Doctor_UnderMit_WithoutMangaJson_Exits3()
    {
        var fx = new CliFixture(includeMangaJson: false);
        SetEngine(fx, "mit");
        var stdout = new StringWriter();
        var code = await CliApp.RunAsync(Args("doctor", "--data-dir", fx.DataDir), fx.Env(null, stdout));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(ExitCodes.Environment, code);
        var issues = result.GetProperty("issues").EnumerateArray().Select(i => i.GetString()).ToList();
        Assert.Contains(issues, i => i is not null && i.Contains("manga.json"));
    }

    [Fact]
    public async Task Run_UnderMtui_WithoutMangaJson_DeploysDetachMtui()
    {
        var fx = new CliFixture(includeMangaJson: false);
        var book = fx.CreateBookZip();
        var fake = fx.FullPipelineCli(out _);
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("run", book, "--minutes", "60", "--data-dir", fx.DataDir, "--quiet-logs"),
            fx.Env(fake, stdout));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(0, code);
        var detach = fake.Calls.FirstOrDefault(c =>
            c.Contains("--detach") && c.Contains("mtui"));
        Assert.NotNull(detach);
    }

    // --------------------------------------------------------- G2/G3: engine switching

    /// <summary>
    /// Alive cloud session + local record. Session status reports <paramref name="initialEngine"/>
    /// until a --detach deploys a different engine, then reports that engine (engine_ready written).
    /// </summary>
    private FakeColabCli SwitchableEngineCli(CliFixture fx, string initialEngine, int failDeployTimes,
        List<string> callLog, out Func<string?> downloadedTo)
    {
        var deployedEngine = initialEngine;
        var remoteAlive = true;
        var detachAttempts = 0;
        var failSetup = new FakeVmSetup(chunks: ["ERROR: redeploy failed\n"], exitCode: 1);
        var okSetup = new FakeVmSetup();
        FakeVmSetup? activeSetup = null;
        var prebuilt = Path.Combine(fx.TempRoot, $"pack_{Guid.NewGuid():N}.zip");
        var (outBytes, outSha) = TestImageFixtures.CreateValidTranslatedZip(prebuilt, pageCount: 2);
        string? dl = null;
        downloadedTo = () => dl;

        return new FakeColabCli((args, _, _) =>
        {
            callLog.Add(string.Join(" ", args));
            if (args[0] == "sessions")
                return Task.FromResult(new ColabResult(0,
                    remoteAlive ? "manga L4" : "No active sessions found.", string.Empty));
            if (args[0] == "ssh" && args.Contains("/content/vm_setup.sh"))
            {
                var di = args.ToList().IndexOf("--detach");
                if (di >= 0)
                {
                    detachAttempts++;
                    activeSetup = detachAttempts <= failDeployTimes ? failSetup : okSetup;
                    if (activeSetup == okSetup)
                    {
                        deployedEngine = args[di + 1];
                    }
                }

                return Task.FromResult((activeSetup ?? okSetup).Handle(args)!);
            }
            if (args[0] == "ssh" && args.Contains("/content/vm_job.sh"))
                return Task.FromResult(new ColabResult(0, "PROTO 2\nIMAGES 2\nTRANSLATE_PID 9", string.Empty));
            if (args[0] == "ssh" && args.Contains("/content/vm_status.sh"))
            {
                return Task.FromResult(args[^1] == "/content/vm_status.sh"
                    ? new ColabResult(0, $"PROTO 2\nLLAMA_OK\nENGINE {deployedEngine} ready\nGPU L4", string.Empty)
                    : new ColabResult(0,
                        $"PROTO 2\nLLAMA_OK\nENGINE {deployedEngine} ready\nDONE_COUNT 2\nJOB_ALIVE 0\nJOB_EXIT_CODE 0", string.Empty));
            }
            if (args[0] == "ssh" && args.Contains("pack"))
                return Task.FromResult(new ColabResult(0, $"PACK_OK {outBytes} {outSha} 2", string.Empty));
            if (args[0] == "download")
            {
                dl = args[^1];
                File.Copy(prebuilt, args[^1], overwrite: true);
                return Task.FromResult(new ColabResult(0, "OK", string.Empty));
            }
            if (args[0] == "stop")
            {
                remoteAlive = false;
                return Task.FromResult(new ColabResult(0, "Session terminated", string.Empty));
            }

            return Task.FromResult(new ColabResult(0, "OK", string.Empty));
        });
    }

    /// <summary>Cloud session exists but ENGINE reports none missing → recovered-but-not-ready.</summary>
    private FakeColabCli EngineNoneCli(CliFixture fx, List<string> callLog)
    {
        var detached = false;
        var remoteAlive = true;
        var setup = new FakeVmSetup();
        var prebuilt = Path.Combine(fx.TempRoot, $"pack_{Guid.NewGuid():N}.zip");
        var (outBytes, outSha) = TestImageFixtures.CreateValidTranslatedZip(prebuilt, pageCount: 2);

        return new FakeColabCli((args, _, _) =>
        {
            callLog.Add(string.Join(" ", args));
            if (args[0] == "sessions")
                return Task.FromResult(new ColabResult(0,
                    remoteAlive ? "manga L4" : "No active sessions found.", string.Empty));
            if (args[0] == "ssh" && args.Contains("/content/vm_setup.sh"))
            {
                if (args.Contains("--detach"))
                {
                    detached = true;
                }

                return Task.FromResult(setup.Handle(args)!);
            }
            if (args[0] == "ssh" && args.Contains("/content/vm_job.sh"))
                return Task.FromResult(new ColabResult(0, "PROTO 2\nIMAGES 2\nTRANSLATE_PID 9", string.Empty));
            if (args[0] == "ssh" && args.Contains("/content/vm_status.sh"))
            {
                var engineLine = detached ? "ENGINE mit ready" : "ENGINE none missing";
                return Task.FromResult(args[^1] == "/content/vm_status.sh"
                    ? new ColabResult(0, $"PROTO 2\nLLAMA_OK\n{engineLine}\nGPU L4", string.Empty)
                    : new ColabResult(0,
                        $"PROTO 2\nLLAMA_OK\n{engineLine}\nDONE_COUNT 2\nJOB_ALIVE 0\nJOB_EXIT_CODE 0", string.Empty));
            }
            if (args[0] == "ssh" && args.Contains("pack"))
                return Task.FromResult(new ColabResult(0, $"PACK_OK {outBytes} {outSha} 2", string.Empty));
            if (args[0] == "download")
            {
                File.Copy(prebuilt, args[^1], overwrite: true);
                return Task.FromResult(new ColabResult(0, "OK", string.Empty));
            }
            if (args[0] == "stop")
            {
                remoteAlive = false;
                return Task.FromResult(new ColabResult(0, "Session terminated", string.Empty));
            }

            return Task.FromResult(new ColabResult(0, "OK", string.Empty));
        });
    }

    private void SeedSessionRecord(CliFixture fx)
    {
        var store = new SessionStore(fx.DataDir);
        store.SaveSession(new SessionRecord
        {
            SessionName = "manga",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            PlannedMinutes = 60,
            AutoStop = false
        });
    }

    [Fact]
    public async Task Run_EngineNoneMissingSession_RunEngineMit_RetriesDeploy()
    {
        // G2: cloud reports ENGINE none missing → DeployedEngine null → --engine must NOT be
        // refused as a mismatch; the A3 retry deploys the configured engine on the same VM.
        var fx = new CliFixture();
        SeedSessionRecord(fx);
        var calls = new List<string>();
        var fake = EngineNoneCli(fx, calls);
        var book = fx.CreateBookZip();
        var outDir = Path.Combine(fx.TempRoot, "out");
        Directory.CreateDirectory(outDir);
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("run", book, "--minutes", "60", "--engine", "mit", "--data-dir", fx.DataDir,
                 "--output-dir", outDir, "--quiet-logs"),
            fx.Env(fake, stdout));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(0, code);
        Assert.Contains(calls, c => c.Contains("--detach") && c.Contains("mit"));
        Assert.False(calls.Any(c => c.StartsWith("new", StringComparison.Ordinal)),
            "recovered session must not create a new runtime");
        var states = Ndjson.Events(stdout.ToString(), "state");
        Assert.DoesNotContain(states, e => e.GetProperty("engine_mismatch").GetBoolean());
    }

    [Fact]
    public async Task Run_RecoveredReadyMtui_EngineMit_RedeploysInSession()
    {
        // G3: recovered Ready mtui session + --engine mit → in-session redeploy before tasks.
        var fx = new CliFixture();
        SeedSessionRecord(fx);
        var calls = new List<string>();
        var fake = SwitchableEngineCli(fx, "mtui", failDeployTimes: 0, calls, out _);
        var book = fx.CreateBookZip();
        var outDir = Path.Combine(fx.TempRoot, "out");
        Directory.CreateDirectory(outDir);
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("run", book, "--minutes", "60", "--engine", "mit", "--data-dir", fx.DataDir,
                 "--output-dir", outDir, "--quiet-logs"),
            fx.Env(fake, stdout));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(0, code);

        var detachIdx = calls.FindIndex(c => c.Contains("--detach") && c.Contains("mit"));
        var jobIdx = calls.FindIndex(c => c.Contains("vm_job.sh start"));
        Assert.True(detachIdx >= 0, "no --detach mit call observed");
        Assert.True(jobIdx > detachIdx,
            $"vm_job.sh start ran before the redeploy (detach@{detachIdx}, job@{jobIdx})");
        Assert.False(calls.Any(c => c.StartsWith("new", StringComparison.Ordinal)),
            "in-session switch must not create a new runtime");
        var warnings = result.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).ToList();
        Assert.Contains(warnings, w => w is not null && w.Contains("重新部署"));
    }

    [Fact]
    public async Task Run_EngineSwitch_RedeployFails_Exits5_StopVerified()
    {
        // G3 failure: in-session redeploy fails → deploy-failure path stops + verifies (exit 5).
        // No --deploy-retries: exactly ONE --detach mit launch (the A3 block must not add another).
        var fx = new CliFixture();
        SeedSessionRecord(fx);
        var calls = new List<string>();
        var fake = SwitchableEngineCli(fx, "mtui", failDeployTimes: int.MaxValue, calls, out _);
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("run", "--minutes", "60", "--engine", "mit", "--data-dir", fx.DataDir, "--quiet-logs"),
            fx.Env(fake, stdout));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(ExitCodes.DeployFailed, code);
        Assert.Equal(1, calls.Count(c => c.Contains("--detach") && c.Contains("mit")));
        Assert.True(result.GetProperty("session").GetProperty("stop_verified").GetBoolean());
        Assert.False(result.GetProperty("session").GetProperty("billing_active").GetBoolean());
    }

    [Fact]
    public async Task Run_EngineSwitch_RedeployFailsThenRetrySucceeds_RunsTasks()
    {
        // G3: switch deploy fails once, --deploy-retries 1 retries → second --detach mit
        // succeeds, tasks run, exit 0 (stale failure flag must not stop a healthy session).
        var fx = new CliFixture();
        SeedSessionRecord(fx);
        var calls = new List<string>();
        var fake = SwitchableEngineCli(fx, "mtui", failDeployTimes: 1, calls, out _);
        var book = fx.CreateBookZip();
        var outDir = Path.Combine(fx.TempRoot, "out");
        Directory.CreateDirectory(outDir);
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("run", book, "--minutes", "60", "--engine", "mit", "--deploy-retries", "1",
                 "--data-dir", fx.DataDir, "--output-dir", outDir, "--quiet-logs"),
            fx.Env(fake, stdout));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(0, code);
        var detachIdxs = calls.Select((c, i) => (c, i))
            .Where(t => t.c.Contains("--detach") && t.c.Contains("mit"))
            .Select(t => t.i).ToList();
        Assert.Equal(2, detachIdxs.Count);
        var jobIdx = calls.FindIndex(c => c.Contains("vm_job.sh start"));
        Assert.True(jobIdx > detachIdxs[1],
            $"vm_job.sh start must run after the successful redeploy (detach@{detachIdxs[1]}, job@{jobIdx})");
        Assert.False(calls.Any(c => c.StartsWith("new", StringComparison.Ordinal)),
            "in-session switch must not create a new runtime");
    }

    [Fact]
    public async Task Run_RecoveredMitSession_NoEngineFlag_RunsOnCloudEngineWithWarning()
    {
        // G3: no --engine + cloud engine differs from configured → run on cloud engine, warn.
        var fx = new CliFixture();
        SeedSessionRecord(fx);
        SetEngine(fx, "mit"); // settings say mit, cloud runs mtui
        var calls = new List<string>();
        var fake = SwitchableEngineCli(fx, "mtui", failDeployTimes: 0, calls, out _);
        var book = fx.CreateBookZip();
        var outDir = Path.Combine(fx.TempRoot, "out");
        Directory.CreateDirectory(outDir);
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("run", book, "--minutes", "60", "--data-dir", fx.DataDir,
                 "--output-dir", outDir, "--quiet-logs"),
            fx.Env(fake, stdout));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(0, code);
        Assert.DoesNotContain(calls, c => c.Contains("--detach")); // no --engine flag must not trigger a redeploy
        var warnings = result.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).ToList();
        Assert.Contains(warnings, w => w is not null && w.Contains("按云端引擎翻译"));
    }

    // --------------------------------------------------------- 10. F1 keep-session result message

    [Fact]
    public async Task Run_KeepSession_TaskSuccess_MessageSaysKeptNotStopped()
    {
        // F1: with --keep-session the success message must not claim 停机核实.
        var fx = new CliFixture();
        var fake = fx.FullPipelineCli(out _);
        var book = fx.CreateBookZip();
        var outDir = Path.Combine(fx.TempRoot, "out");
        Directory.CreateDirectory(outDir);
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("run", book, "--minutes", "30", "--keep-session", "--data-dir", fx.DataDir,
                 "--output-dir", outDir, "--quiet-logs"),
            fx.Env(fake, stdout));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(0, code);
        Assert.Equal("全部 1 个任务完成，会话按 --keep-session 保留（仍在计费）。",
            result.GetProperty("message").GetString());
        Assert.True(result.GetProperty("session").GetProperty("billing_active").GetBoolean());
    }

    [Fact]
    public async Task Run_KeepSession_SmokeSuccess_MessageSaysKeptNotStopped()
    {
        var fx = new CliFixture();
        var fake = fx.FullPipelineCli(out _);
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("run", "--minutes", "30", "--keep-session", "--data-dir", fx.DataDir, "--quiet-logs"),
            fx.Env(fake, stdout));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(0, code);
        Assert.Equal("部署冒烟成功，会话按 --keep-session 保留（仍在计费）。",
            result.GetProperty("message").GetString());
        Assert.False(result.GetProperty("session").GetProperty("stop_verified").ValueKind == System.Text.Json.JsonValueKind.True);
    }

    // --------------------------------------------------------- 11. F4 --scripts-dir preflight

    [Fact]
    public async Task Run_ScriptsDir_Valid_ReachesMitDeploy_AndPersists()
    {
        // F4: --scripts-dir configures the scripts directory before the asset preflight
        // (same as the GUI/CLI browse path — persisted to settings).
        var fx = new CliFixture();
        var fake = fx.FullPipelineCli(out _);
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("run", "--minutes", "30", "--engine", "mit", "--scripts-dir", fx.ScriptsDir,
                 "--data-dir", fx.DataDir, "--quiet-logs"),
            fx.Env(fake, stdout));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(0, code);
        var calls = fake.Calls.Select(c => string.Join(" ", c)).ToList();
        Assert.Contains(calls, c => c.Contains("--detach") && c.Contains("mit"));
        Assert.Equal(fx.ScriptsDir, new SessionStore(fx.DataDir).LoadSettings().ScriptsDir);
    }

    [Fact]
    public async Task Run_ScriptsDir_Invalid_Exits2_NoCloudCalls()
    {
        // F4: a --scripts-dir without manga.json fails fast — no colab call at all.
        var fx = new CliFixture();
        var badDir = Path.Combine(fx.TempRoot, "no_manga_json");
        Directory.CreateDirectory(badDir);
        var fake = fx.FullPipelineCli(out _);
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("run", "--minutes", "30", "--engine", "mit", "--scripts-dir", badDir,
                 "--data-dir", fx.DataDir, "--quiet-logs"),
            fx.Env(fake, stdout));

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("--scripts-dir 目录缺少 manga.json", result.GetProperty("message").GetString());
        Assert.Empty(fake.Calls);
    }

    // --------------------------------------------- 11. F7 interrupt waits for remote cleanup

    /// <summary>
    /// Recovered-ready session whose job polls RUNNING forever; <paramref name="cleanupDelay"/>
    /// slows vm_pack.sh stop_job/clean (the SSH calls in the cancelled task's finally) so the
    /// test can observe whether `run` waits for them before writing the result.
    /// </summary>
    private FakeColabCli InterruptibleCli(CliFixture fx, List<string> callLog,
        TimeSpan cleanupDelay, Action? onJobStart)
    {
        var remoteAlive = true;
        var setup = new FakeVmSetup();
        var prebuilt = Path.Combine(fx.TempRoot, $"pack_{Guid.NewGuid():N}.zip");
        var (outBytes, outSha) = TestImageFixtures.CreateValidTranslatedZip(prebuilt, pageCount: 2);

        return new FakeColabCli(async (args, _, ct) =>
        {
            var joined = string.Join(" ", args);
            callLog.Add(joined);
            if (args[0] == "sessions")
                return new ColabResult(0,
                    remoteAlive ? "manga L4" : "No active sessions found.", string.Empty);
            if (args[0] == "ssh" && args.Contains("/content/vm_setup.sh"))
                return setup.Handle(args)!;
            if (args[0] == "ssh" && args.Contains("/content/vm_job.sh"))
            {
                onJobStart?.Invoke();
                return new ColabResult(0, "PROTO 2\nIMAGES 2\nTRANSLATE_PID 9", string.Empty);
            }
            if (args[0] == "ssh" && args.Contains("/content/vm_status.sh"))
            {
                return args[^1] == "/content/vm_status.sh"
                    ? new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mtui ready\nGPU L4", string.Empty)
                    : new ColabResult(0,
                        "PROTO 2\nLLAMA_OK\nENGINE mtui ready\nDONE_COUNT 1\nJOB_ALIVE 1\nJOB_EXIT_CODE RUNNING", string.Empty);
            }
            if (args[0] == "ssh" && (args.Contains("stop_job") || args.Contains("clean") || args.Contains("clean_stale")))
            {
                await Task.Delay(cleanupDelay, CancellationToken.None);
                return new ColabResult(0, "OK", string.Empty);
            }
            if (args[0] == "ssh" && args.Contains("pack"))
                return new ColabResult(0, $"PACK_OK {outBytes} {outSha} 2", string.Empty);
            if (args[0] == "download")
            {
                File.Copy(prebuilt, args[^1], overwrite: true);
                return new ColabResult(0, "OK", string.Empty);
            }
            if (args[0] == "stop")
            {
                remoteAlive = false;
                return new ColabResult(0, "Session terminated", string.Empty);
            }
            return new ColabResult(0, "OK", string.Empty);
        });
    }

    private void SeedAliveRecord(CliFixture fx)
    {
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");
        File.WriteAllText(Path.Combine(fx.DataDir, "session.json"),
            $$"""{"session_name":"manga","started_at":"{{startedAt}}","planned_minutes":60,"auto_stop":true,"start_time_unknown":false}""");
    }

    [Fact]
    public async Task Run_InterruptKeepSession_WaitsForRemoteCleanup_Cancelled()
    {
        // F7：中断 --keep-session 时必须等活动任务的 stop_job/clean 跑完再写 result；
        // 否则进程退出会让远端翻译任务残留。fake 把这两条 SSH 各延迟 1.2s。
        var fx = new CliFixture();
        var zip = fx.CreateBookZip();
        SeedAliveRecord(fx);

        var calls = new List<string>();
        using var interrupt = new CancellationTokenSource();
        var fake = InterruptibleCli(fx, calls, TimeSpan.FromMilliseconds(1200),
            onJobStart: () => _ = Task.Run(async () =>
            {
                await Task.Delay(400);
                await interrupt.CancelAsync();
            }));
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("run", zip, "--minutes", "30", "--keep-session",
                 "--data-dir", fx.DataDir, "--quiet-logs"),
            fx.Env(fake, stdout), interrupt.Token);

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(ExitCodes.Interrupted, code);
        var task = Assert.Single(result.GetProperty("tasks").EnumerateArray());
        Assert.Equal("cancelled", task.GetProperty("status").GetString());
        // RunAsync 返回时这两条调用必然已完成 → result 写在远端清理之后。
        Assert.Contains(calls, c => c.Split(' ').Contains("stop_job"));
        Assert.Contains(calls, c => c.Split(' ').Contains("clean"));
        Assert.DoesNotContain(calls, c => c.StartsWith("stop "));
    }

    [Fact]
    public async Task Run_InterruptKeepSession_CleanupBoundHit_Warns()
    {
        // F7：远端清理超过 InterruptCleanupBound → 只告警不阻塞 result。
        var fx = new CliFixture();
        var zip = fx.CreateBookZip();
        SeedAliveRecord(fx);

        var calls = new List<string>();
        using var interrupt = new CancellationTokenSource();
        var fake = InterruptibleCli(fx, calls, TimeSpan.FromSeconds(10),
            onJobStart: () => _ = Task.Run(async () =>
            {
                await Task.Delay(400);
                await interrupt.CancelAsync();
            }));
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(
            Args("run", zip, "--minutes", "30", "--keep-session",
                 "--data-dir", fx.DataDir, "--quiet-logs"),
            fx.Env(fake, stdout, interruptCleanupBound: TimeSpan.FromMilliseconds(400)),
            interrupt.Token);

        var result = Ndjson.AssertNdjsonResult(stdout.ToString());
        Assert.Equal(ExitCodes.Interrupted, code);
        Assert.Contains(result.GetProperty("warnings").EnumerateArray(),
            w => w.GetString()!.Contains("云端任务清理未在"));
    }
}
