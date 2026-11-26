using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using MangaGui.Models;
using MangaGui.Services;
using MangaGui.ViewModels;
using Xunit;

namespace MangaGui.Tests;

/// <summary>
/// Regression tests for the 2026-11-22 audit fixes: unattended billing safety (R-03, R-04),
/// chunked transfer and partial salvage (R-05, R-09), and log redaction (R-10).
/// </summary>
public sealed class HardeningTests : IDisposable
{
    private const string NoSessions = "[colab] No active sessions found on server.\n";
    private const string MangaLine = "[manga] gpu-l4-s-1a2b3c4d5e | Hardware: L4 | Shape: Standard | Variant: GPU\n";

    private readonly string _tempRoot;
    private readonly string _dataDir;
    private readonly string _scriptsDir;

    public HardeningTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"manga_gui_hard_test_{Guid.NewGuid():N}");
        _dataDir = Path.Combine(_tempRoot, "appdata");
        _scriptsDir = Path.Combine(_tempRoot, "scripts");
        var assetsDir = Path.Combine(_scriptsDir, "assets");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(assetsDir);
        foreach (var f in VmScripts.RequiredExistingScripts)
            File.WriteAllText(Path.Combine(_scriptsDir, f), "{}\n");
        foreach (var f in VmScripts.BundledGuiAssets)
            File.WriteAllText(Path.Combine(assetsDir, f), "#!/bin/bash\necho ok\n");
    }

    /// <summary>Simulated runtime: lifecycle commands are handled here, job commands by <paramref name="job"/>.</summary>
    private sealed class FakeRuntime
    {
        public bool Alive;
        public int StopCalls;
        private readonly FakeVmSetup _setup = new();

        public FakeColabCli Build(Func<IReadOnlyList<string>, ColabResult?> job) => new((args, onOut, _) =>
        {
            var custom = job(args);
            if (custom is not null) return Task.FromResult(custom);

            switch (args[0])
            {
                case "sessions":
                    return Task.FromResult(new ColabResult(0, Alive ? MangaLine : NoSessions, string.Empty));
                case "new":
                    Alive = true;
                    return Task.FromResult(new ColabResult(0, "[colab] Session READY.\n", string.Empty));
                case "stop":
                    Interlocked.Increment(ref StopCalls);
                    Alive = false;
                    return Task.FromResult(new ColabResult(0, "[colab] Session terminated.\n", string.Empty));
                case "ssh" when args.Contains("/content/vm_setup.sh"):
                    return Task.FromResult(_setup.Handle(args)!);
                case "ssh" when args.Contains("/content/vm_status.sh"):
                    return Task.FromResult(new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mit ready\nGPU NVIDIA L4\n", string.Empty));
                case "ssh" when args.Contains("clean"):
                    return Task.FromResult(new ColabResult(0, "CLEAN_OK\n", string.Empty));
                case "ssh" when args.Contains("stop_job"):
                    return Task.FromResult(new ColabResult(0, "STOP_JOB_OK\n", string.Empty));
                default:
                    return Task.FromResult(new ColabResult(0, "OK", string.Empty));
            }
        });
    }

    private static bool IsJobStatus(IReadOnlyList<string> args) =>
        args[0] == "ssh" && args.Contains("/content/vm_status.sh") && args[^1].StartsWith("/content/jobs/", StringComparison.Ordinal);

    private string CreateBook(string name, params string[] entryPaths)
    {
        var path = Path.Combine(_tempRoot, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        byte tag = 1;
        foreach (var entry in entryPaths)
        {
            TestImageFixtures.WriteBytesEntry(zip, entry, TestImageFixtures.CreateValidJpeg(tag++));
        }
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task LargeArchive_IsUploadedAndDownloadedInVerifiedParts_AndOriginalPageNamesAreRestored()
    {
        const long chunk = 64;
        var outFixture = Path.Combine(_tempRoot, "remote_out.zip");
        var (outBytes, outSha) = TestImageFixtures.CreateValidTranslatedZip(outFixture, pageCount: 2);
        var outData = File.ReadAllBytes(outFixture);
        Assert.True(outBytes > chunk);

        var uploadedParts = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        bool joinVerified = false;
        bool singleShotArchiveTransfer = false;

        var runtime = new FakeRuntime();
        var cli = runtime.Build(args =>
        {
            if (args[0] == "upload" && args[^1].Contains(".part_", StringComparison.Ordinal))
            {
                lock (uploadedParts) uploadedParts[args[^1]] = File.ReadAllBytes(args[^2]);
                return new ColabResult(0, "OK", string.Empty);
            }
            if (args[0] == "upload" && args[^1].EndsWith(".zip", StringComparison.Ordinal))
            {
                singleShotArchiveTransfer = true;
                return null;
            }
            if (args[0] == "ssh" && args.Contains("join_parts"))
            {
                byte[] joined;
                lock (uploadedParts) joined = uploadedParts.Values.SelectMany(b => b).ToArray();
                var sha = Convert.ToHexString(SHA256.HashData(joined)).ToLowerInvariant();
                joinVerified = args[^3] == uploadedParts.Count.ToString()
                               && args[^2] == joined.Length.ToString()
                               && args[^1] == sha
                               && uploadedParts.Values.All(p => p.Length <= chunk);
                return joinVerified
                    ? new ColabResult(0, $"JOIN_OK {joined.Length} {sha}\n", string.Empty)
                    : new ColabResult(1, string.Empty, "JOIN_ERROR checksum_mismatch");
            }
            if (args[0] == "ssh" && args.Contains("/content/vm_job.sh"))
                return new ColabResult(0, "PROTO 2\nIMAGES 2\nTRANSLATE_PID 7\n", string.Empty);
            if (IsJobStatus(args))
                return new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mit ready\nDONE_COUNT 2\nJOB_ALIVE 0\nJOB_EXIT_CODE 0\n", string.Empty);
            if (args[0] == "ssh" && args.Contains("pack"))
                return new ColabResult(0, $"PACK_OK {outBytes} {outSha} 2\n", string.Empty);
            if (args[0] == "ssh" && args.Contains("split_out"))
                return new ColabResult(0, $"SPLIT_OK {(outBytes + chunk - 1) / chunk}\n", string.Empty);
            if (args[0] == "download")
            {
                var remote = args[^2];
                int marker = remote.LastIndexOf(".part_", StringComparison.Ordinal);
                if (marker < 0)
                {
                    singleShotArchiveTransfer = true;
                    return new ColabResult(1, string.Empty, "unexpected single-shot download");
                }
                int index = int.Parse(remote[(marker + 6)..]);
                var slice = outData.Skip((int)(index * chunk)).Take((int)chunk).ToArray();
                File.WriteAllBytes(args[^1], slice);
                return new ColabResult(0, "OK", string.Empty);
            }
            return null;
        });

        var store = new SessionStore(_dataDir);
        var mgr = new SessionManager(cli, new VmScripts(store, _scriptsDir), store, new BillingTimer());
        Assert.True(await mgr.StartOrDeployAsync(60, true));

        var book = CreateBook("chapters.cbz", "ch1/1.jpg", "ch2/1.jpg");
        var queue = new TaskQueue(cli, mgr, a => a(), pollInterval: TimeSpan.FromMilliseconds(20),
            transferChunkBytes: chunk, transferRetryDelay: TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, queue.EnqueueFiles([book]).AddedCount);

        var item = Assert.Single(queue.Items);
        await WaitUntilAsync(() => item.Status is TranslationTaskStatus.Completed or TranslationTaskStatus.Failed);

        Assert.True(item.Status == TranslationTaskStatus.Completed, item.ErrorSummary);
        Assert.True(uploadedParts.Count > 1);
        Assert.True(joinVerified);
        Assert.False(singleShotArchiveTransfer);

        using var result = ZipFile.OpenRead(item.OutputZipPath!);
        Assert.Equal(["ch1/1.jpg", "ch2/1.jpg"], result.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        queue.CancelPendingAndActive();
    }

    [Fact]
    public async Task JobThatFailsMidway_StaysFailed_ButItsFinishedPagesAreSavedAsPartialArchive()
    {
        var partialFixture = Path.Combine(_tempRoot, "remote_partial.zip");
        var (partialBytes, partialSha) = TestImageFixtures.CreateValidTranslatedZip(partialFixture, pageCount: 1);
        bool packedAsPartial = false;

        var runtime = new FakeRuntime();
        var cli = runtime.Build(args =>
        {
            if (args[0] == "ssh" && args.Contains("/content/vm_job.sh"))
                return new ColabResult(0, "PROTO 2\nIMAGES 2\nTRANSLATE_PID 7\n", string.Empty);
            if (IsJobStatus(args))
                return new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mit ready\nDONE_COUNT 1\nJOB_ALIVE 0\nJOB_EXIT_CODE 137\n", string.Empty);
            if (args[0] == "ssh" && args.Contains("pack"))
            {
                packedAsPartial = args[^1] == "partial";
                return packedAsPartial
                    ? new ColabResult(0, $"PACK_OK {partialBytes} {partialSha} 1\n", string.Empty)
                    : new ColabResult(1, string.Empty, "PACK_ERROR nonzero_exit_code 137");
            }
            if (args[0] == "download")
            {
                File.Copy(partialFixture, args[^1], overwrite: true);
                return new ColabResult(0, "OK", string.Empty);
            }
            return null;
        });

        var store = new SessionStore(_dataDir);
        var mgr = new SessionManager(cli, new VmScripts(store, _scriptsDir), store, new BillingTimer());
        Assert.True(await mgr.StartOrDeployAsync(60, true));

        var book = CreateBook("oom_book.zip", "0001.jpg", "0002.jpg");
        var queue = new TaskQueue(cli, mgr, a => a(), pollInterval: TimeSpan.FromMilliseconds(20));
        Assert.Equal(1, queue.EnqueueFiles([book]).AddedCount);

        var item = Assert.Single(queue.Items);
        await WaitUntilAsync(() => item.Status is TranslationTaskStatus.Completed or TranslationTaskStatus.Failed);

        Assert.Equal(TranslationTaskStatus.Failed, item.Status);
        Assert.True(packedAsPartial);
        Assert.Contains("已保存部分成品 1/2", item.ErrorSummary);
        Assert.EndsWith("oom_book_translated_partial.zip", item.OutputZipPath);
        Assert.True(File.Exists(item.OutputZipPath!));
        Assert.False(File.Exists(Path.Combine(_tempRoot, "oom_book_translated.zip")));
        queue.CancelPendingAndActive();
    }

    [Fact]
    public async Task JobThatExitsWithCode3_ReportsMtuiPageFailures()
    {
        var partialFixture = Path.Combine(_tempRoot, "remote_partial_e3.zip");
        var (partialBytes, partialSha) = TestImageFixtures.CreateValidTranslatedZip(partialFixture, pageCount: 1);

        var runtime = new FakeRuntime();
        var cli = runtime.Build(args =>
        {
            if (args[0] == "ssh" && args.Contains("/content/vm_job.sh"))
                return new ColabResult(0, "PROTO 2\nIMAGES 2\nTRANSLATE_PID 7\n", string.Empty);
            if (IsJobStatus(args))
                return new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mtui ready\nDONE_COUNT 1\nJOB_ALIVE 0\nJOB_EXIT_CODE 3\n", string.Empty);
            if (args[0] == "ssh" && args.Contains("pack"))
                return args[^1] == "partial"
                    ? new ColabResult(0, $"PACK_OK {partialBytes} {partialSha} 1\n", string.Empty)
                    : new ColabResult(1, string.Empty, "PACK_ERROR nonzero_exit_code 3");
            if (args[0] == "download")
            {
                File.Copy(partialFixture, args[^1], overwrite: true);
                return new ColabResult(0, "OK", string.Empty);
            }
            return null;
        });

        var store = new SessionStore(_dataDir);
        var mgr = new SessionManager(cli, new VmScripts(store, _scriptsDir), store, new BillingTimer());
        Assert.True(await mgr.StartOrDeployAsync(60, true));

        var book = CreateBook("mtui_book.zip", "0001.jpg", "0002.jpg");
        var queue = new TaskQueue(cli, mgr, a => a(), pollInterval: TimeSpan.FromMilliseconds(20));
        Assert.Equal(1, queue.EnqueueFiles([book]).AddedCount);

        var item = Assert.Single(queue.Items);
        await WaitUntilAsync(() => item.Status is TranslationTaskStatus.Completed or TranslationTaskStatus.Failed);

        Assert.Equal(TranslationTaskStatus.Failed, item.Status);
        Assert.Contains("新引擎报告有页面翻译失败", item.ErrorSummary);
        Assert.Contains("已完成 1/2 页", item.ErrorSummary);
        Assert.DoesNotContain("异常退出", item.ErrorSummary);
        queue.CancelPendingAndActive();
    }

    [Fact]
    public async Task DrainingQueueThatGetsPaused_StillStopsBilling()
    {
        var runtime = new FakeRuntime();
        var cli = runtime.Build(args =>
        {
            if (args[0] == "ssh" && args.Contains("/content/vm_job.sh"))
                return new ColabResult(0, "PROTO 2\nIMAGES 1\nTRANSLATE_PID 7\n", string.Empty);
            if (IsJobStatus(args) || (args[0] == "ssh" && args.Contains("stop_job")))
                return new ColabResult(255, string.Empty, "ssh: connection lost");
            return null;
        });

        var store = new SessionStore(_dataDir);
        var mgr = new SessionManager(cli, new VmScripts(store, _scriptsDir), store, new BillingTimer());
        Assert.True(await mgr.StartOrDeployAsync(60, true));

        var queue = new TaskQueue(cli, mgr, a => a(), pollInterval: TimeSpan.FromMilliseconds(20), maxConsecutivePollFailures: 2);
        Assert.Equal(2, queue.EnqueueFiles([CreateBook("a.zip", "1.jpg"), CreateBook("b.zip", "1.jpg")]).AddedCount);
        await WaitUntilAsync(() => mgr.State == SessionState.Busy);
        mgr.TransitionToDraining();

        await WaitUntilAsync(() => mgr.State == SessionState.Idle);

        Assert.Equal(SessionState.Idle, mgr.State);
        Assert.False(mgr.HasActiveVm);
        Assert.True(runtime.StopCalls >= 1);
        Assert.False(mgr.Billing.Evaluate(fireEvents: false).IsRunning);
        Assert.False(queue.IsQueuePaused);
        Assert.Equal([TranslationTaskStatus.Failed, TranslationTaskStatus.Pending], queue.Items.Select(i => i.Status).ToArray());
        queue.CancelPendingAndActive();
    }

    [Fact]
    public async Task JobThatStopsProducingPages_FailsOnStallTimeoutInsteadOfWaitingForTheWallClockCap()
    {
        bool stopJobCalled = false;
        var runtime = new FakeRuntime();
        var cli = runtime.Build(args =>
        {
            if (args[0] == "ssh" && args.Contains("/content/vm_job.sh"))
                return new ColabResult(0, "PROTO 2\nIMAGES 2\nTRANSLATE_PID 7\n", string.Empty);
            if (IsJobStatus(args))
                return new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mit ready\nDONE_COUNT 1\nJOB_ALIVE 1\nJOB_EXIT_CODE RUNNING\n", string.Empty);
            if (args[0] == "ssh" && args.Contains("stop_job"))
            {
                stopJobCalled = true;
                return new ColabResult(0, "STOP_JOB_OK\n", string.Empty);
            }
            return null;
        });

        var store = new SessionStore(_dataDir);
        var mgr = new SessionManager(cli, new VmScripts(store, _scriptsDir), store, new BillingTimer());
        Assert.True(await mgr.StartOrDeployAsync(60, true));

        var queue = new TaskQueue(cli, mgr, a => a(), pollInterval: TimeSpan.FromMilliseconds(20),
            stallTimeout: TimeSpan.FromMilliseconds(250));
        Assert.Equal(1, queue.EnqueueFiles([CreateBook("stuck.zip", "1.jpg", "2.jpg")]).AddedCount);

        var item = Assert.Single(queue.Items);
        await WaitUntilAsync(() => item.Status == TranslationTaskStatus.Failed);

        Assert.Equal(TranslationTaskStatus.Failed, item.Status);
        Assert.Contains("停滞", item.ErrorSummary);
        Assert.True(stopJobCalled);
        Assert.False(queue.IsQueuePaused);
        queue.CancelPendingAndActive();
    }

    [Fact]
    public void BillingTimer_SnoozeExpiry_FiresAgainOnlyAfterTheSnoozeElapses()
    {
        var now = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
        int fired = 0;
        var timer = new BillingTimer(() => now);
        timer.TimeLimitReached += (_, _) => fired++;
        timer.Start(now, plannedMinutes: 60, autoStop: true);

        now = now.AddMinutes(61);
        timer.Evaluate();
        timer.Evaluate();
        Assert.Equal(1, fired);

        timer.SnoozeExpiry(TimeSpan.FromMinutes(10));
        now = now.AddMinutes(9);
        timer.Evaluate();
        Assert.Equal(1, fired);

        now = now.AddMinutes(2);
        timer.Evaluate();
        timer.Evaluate();
        Assert.Equal(2, fired);

        // Extending clears the snooze: nothing fires until the new limit is reached.
        timer.SnoozeExpiry(TimeSpan.FromMinutes(10));
        timer.Extend(30);
        now = now.AddMinutes(20);
        timer.Evaluate();
        Assert.Equal(2, fired);
        now = now.AddMinutes(15);
        timer.Evaluate();
        Assert.Equal(3, fired);
    }

    [Fact]
    public async Task Expiry_ReminderOnlySessionIsRemindedAgain_AndAutoStopSessionsGetASafeDefaultAction()
    {
        var store = new SessionStore(_dataDir);
        var now = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

        // (a) Reminder-only: the dialog never acts by itself, and dismissing it schedules another reminder.
        var runtimeA = new FakeRuntime();
        var cliA = runtimeA.Build(_ => null);
        var mgrA = new SessionManager(cliA, new VmScripts(store, _scriptsDir), store, new BillingTimer(() => now), () => now);
        var queueA = new TaskQueue(cliA, mgrA, a => a());
        using (var vmA = new MainViewModel(cliA, new VmScripts(store, _scriptsDir), mgrA, queueA, null, a => a(), store))
        {
            var requests = new List<TimeExpiredRequest>();
            vmA.RequestTimeExpiredDialog += request => { requests.Add(request); return Task.FromResult(TimeExpiredAction.None); };
            Assert.True(await mgrA.StartOrDeployAsync(60, autoStop: false));

            now = now.AddMinutes(61);
            mgrA.Billing.Evaluate();
            var first = Assert.Single(requests);
            Assert.Equal(TimeExpiredScenario.IdleNotAutoStop, first.Scenario);
            Assert.Equal(TimeExpiredAction.None, first.AutoAction);

            now = now.AddMinutes(1);
            mgrA.Billing.Evaluate();
            Assert.Single(requests);

            now += vmA.ExpiryReminderInterval;
            mgrA.Billing.Evaluate();
            Assert.Equal(2, requests.Count);
            Assert.Equal(0, runtimeA.StopCalls);
            Assert.True(mgrA.HasActiveVm);
        }

        // (b) Auto-stop session whose deploy failed: the dialog defaults to stopping after the idle grace.
        var runtimeB = new FakeRuntime();
        var cliB = runtimeB.Build(args =>
            args[0] == "ssh" && args.Contains("/content/vm_setup.sh")
                ? new ColabResult(1, "STAGE1_FAIL\n", string.Empty)
                : null);
        var mgrB = new SessionManager(cliB, new VmScripts(store, _scriptsDir), store, new BillingTimer(() => now), () => now);
        var queueB = new TaskQueue(cliB, mgrB, a => a());
        using (var vmB = new MainViewModel(cliB, new VmScripts(store, _scriptsDir), mgrB, queueB, null, a => a(), store))
        {
            TimeExpiredRequest? request = null;
            vmB.RequestTimeExpiredDialog += r => { request = r; return Task.FromResult(r.AutoAction); };
            Assert.False(await mgrB.StartOrDeployAsync(60, autoStop: true));
            Assert.Equal(SessionState.Error, mgrB.State);
            Assert.True(mgrB.HasActiveVm);

            now = now.AddMinutes(61);
            mgrB.Billing.Evaluate();
            await WaitUntilAsync(() => mgrB.State == SessionState.Idle);

            Assert.NotNull(request);
            Assert.Equal(TimeExpiredScenario.ErrorWhileBilling, request!.Scenario);
            Assert.Equal(TimeExpiredAction.StopImmediately, request.AutoAction);
            Assert.Equal(vmB.IdleExpiryGrace, request.AutoActionDelay);
            Assert.Equal(SessionState.Idle, mgrB.State);
            Assert.Equal(1, runtimeB.StopCalls);
        }
    }

    [Fact]
    public void LogSanitizer_CoversGoogleCredentialShapesEmailsAndForwardSlashPaths()
    {
        var raw = string.Join("\n",
            "{\"access_token\": \"ya29.a0AbCdEf-secret\", \"refresh_token\": \"1//0gRefreshSecret\"}",
            "client_secret: GOCSPX-ClientSecretValue",
            "standalone ya29.StandaloneTokenValue in a trace",
            "credentials for someone@example.com (quota project demo)",
            "C:/Users/Mashiro/.config/gcloud/application_default_credentials.json");

        var redacted = LogSanitizer.Redact(raw);

        Assert.DoesNotContain("a0AbCdEf-secret", redacted);
        Assert.DoesNotContain("0gRefreshSecret", redacted);
        Assert.DoesNotContain("ClientSecretValue", redacted);
        Assert.DoesNotContain("StandaloneTokenValue", redacted);
        Assert.DoesNotContain("someone@example.com", redacted);
        Assert.DoesNotContain("Mashiro", redacted);
        Assert.Contains("\"access_token\": \"<REDACTED>", redacted);
        Assert.Contains("C:/Users/<USER>/", redacted);
        Assert.Contains("quota project demo", redacted);
    }

    [Fact]
    public void BoundedOutputBuffer_OnlyMarksTruncationWhenLinesWereActuallyDropped()
    {
        var buffer = new ColabCli.BoundedOutputBuffer(maxChars: 256);
        var lines = Enumerable.Range(0, 5).Select(i => $"line_{i}_0123456789abcdefghij").ToArray();
        foreach (var line in lines) buffer.AppendLine(line);

        var text = buffer.Build();
        Assert.False(buffer.IsTruncated);
        Assert.DoesNotContain("TRUNCATED", text);
        Assert.Equal(lines, text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void RestoreOriginalNames_KeepsTranslatorExtensionAndLeavesUnknownEntriesAlone()
    {
        var translated = Path.Combine(_tempRoot, "translated.zip");
        using (var zip = ZipFile.Open(translated, ZipArchiveMode.Create))
        {
            TestImageFixtures.WriteBytesEntry(zip, "0001.png", TestImageFixtures.CreateValidPng(1));
            TestImageFixtures.WriteBytesEntry(zip, "0002.jpg", TestImageFixtures.CreateValidJpeg(2));
            TestImageFixtures.WriteBytesEntry(zip, "0009.jpg", TestImageFixtures.CreateValidJpeg(9));
            TestImageFixtures.WriteBytesEntry(zip, "notes.txt", [1, 2, 3]);
        }

        var restored = Path.Combine(_tempRoot, "restored.zip");
        ZipPreprocessor.RestoreOriginalNames(translated, ["第1話/1.jpg", "第1話/10.jpg"], restored);

        using var result = ZipFile.OpenRead(restored);
        Assert.Equal(
            ["0009.jpg", "notes.txt", "第1話/1.png", "第1話/10.jpg"],
            result.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        using var page = result.GetEntry("第1話/1.png")!.Open();
        var header = new byte[8];
        Assert.Equal(8, page.Read(header, 0, 8));
        Assert.True(ZipPreprocessor.HasValidImageMagicBytes(header, ".png"));
    }

    [Fact]
    public async Task StopConfirmation_SessionStoppedElsewhere_DoesNotStopTwice()
    {
        // While the "stop billing?" confirmation is still pending, another path stops the session;
        // answering "yes" afterwards must not issue a second stop.
        var store = new SessionStore(_dataDir);
        var runtime = new FakeRuntime();
        var cli = runtime.Build(_ => null);
        var mgr = new SessionManager(cli, new VmScripts(store, _scriptsDir), store, new BillingTimer(), () => DateTimeOffset.UtcNow);
        var queue = new TaskQueue(cli, mgr, a => a());
        using var vm = new MainViewModel(cli, new VmScripts(store, _scriptsDir), mgr, queue, null, a => a(), store);

        var dialogPending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.RequestConfirmStopDialog += _ =>
        {
            dialogPending.TrySetResult();
            return answer.Task;
        };

        Assert.True(await mgr.StartOrDeployAsync(60, autoStop: false));
        Assert.Equal(0, runtime.StopCalls);

        var stopTask = vm.StopServiceCommand.ExecuteAsync(null);
        await dialogPending.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The session is stopped via another path while the confirmation is still open.
        var res = await vm.ExecuteStopAsync(isAutoStop: false);
        Assert.True(res.VerifiedStopped);
        Assert.Equal(1, runtime.StopCalls);

        answer.SetResult(true); // user confirms, but there is nothing left to stop
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, runtime.StopCalls);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }
}
