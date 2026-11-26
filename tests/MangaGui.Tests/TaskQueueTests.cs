using System.IO;
using System.IO.Compression;
using MangaGui.Models;
using MangaGui.Services;
using Xunit;

namespace MangaGui.Tests;

public sealed class TaskQueueTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _dataDir;
    private readonly string _scriptsDir;

    public TaskQueueTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"manga_gui_tq_test_{Guid.NewGuid():N}");
        _dataDir = Path.Combine(_tempRoot, "appdata");
        _scriptsDir = Path.Combine(_tempRoot, "scripts");
        var assetsDir = Path.Combine(_scriptsDir, "assets");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(assetsDir);

        foreach (var f in VmScripts.RequiredExistingScripts)
            File.WriteAllText(Path.Combine(_scriptsDir, f), "#!/bin/bash\n");
        foreach (var f in VmScripts.BundledGuiAssets)
            File.WriteAllText(Path.Combine(assetsDir, f), "#!/bin/bash\n");
    }

    [Fact]
    public async Task TaskQueue_Proto2FullPipelineWaitsForExitCodeVerifiesSha256CleansAndDrains()
    {
        bool remoteAlive = true;
        int pollStep = 0;
        bool cleanCalled = false;
        bool packCalledWhileAlive = false;
        string? uploadedRemotePath = null;
        string? jobDirUsed = null;

        var prebuiltOutZip = Path.Combine(_tempRoot, "remote_pack_fixture.zip");
        var (outBytes, outSha) = TestImageFixtures.CreateValidTranslatedZip(prebuiltOutZip, pageCount: 2);
        var setup = new FakeVmSetup();

        var cli = new FakeColabCli((args, onOut, _) =>
        {
            var cmd = args[0];
            if (cmd == "sessions")
                return Task.FromResult(new ColabResult(0, remoteAlive ? "manga L4" : "No active sessions found.", string.Empty));
            if (cmd == "upload")
            {
                uploadedRemotePath = args[^1];
                return Task.FromResult(new ColabResult(0, "OK", string.Empty));
            }
            if (cmd == "ssh" && args.Contains("/content/vm_setup.sh"))
            {
                return Task.FromResult(setup.Handle(args)!);
            }
            if (cmd == "ssh" && args.Contains("/content/vm_job.sh"))
            {
                jobDirUsed = args[^2];
                return Task.FromResult(new ColabResult(0, "PROTO 2\nIMAGES 2\nTRANSLATE_PID 999", string.Empty));
            }
            if (cmd == "ssh" && args.Contains("/content/vm_status.sh"))
            {
                if (args[^1] == "/content/vm_status.sh")
                    return Task.FromResult(new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mit ready\nMIT_OK\nGPU NVIDIA L4", string.Empty));

                pollStep++;
                // Poll 1: 1/2 alive; Poll 2: 2/2 STILL ALIVE (A-04 must NOT pack yet!); Poll 3: 2/2 alive=0 exit_code=0!
                if (pollStep == 1)
                    return Task.FromResult(new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mit ready\nDONE_COUNT 1\nJOB_ALIVE 1\nJOB_EXIT_CODE RUNNING\nLAST_SAVING Saving 0001.jpg", string.Empty));
                if (pollStep == 2)
                    return Task.FromResult(new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mit ready\nDONE_COUNT 2\nJOB_ALIVE 1\nJOB_EXIT_CODE RUNNING\nLAST_SAVING Saving 0002.jpg", string.Empty));
                return Task.FromResult(new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mit ready\nDONE_COUNT 2\nJOB_ALIVE 0\nJOB_EXIT_CODE 0\nLAST_SAVING Saving 0002.jpg", string.Empty));
            }
            if (cmd == "ssh" && args.Contains("pack"))
            {
                if (pollStep < 3) packCalledWhileAlive = true;
                return Task.FromResult(new ColabResult(0, $"PACK_OK {outBytes} {outSha} 2", string.Empty));
            }
            if (cmd == "download")
            {
                File.Copy(prebuiltOutZip, args[^1], overwrite: true);
                return Task.FromResult(new ColabResult(0, "DOWNLOAD_OK", string.Empty));
            }
            if (cmd == "ssh" && args.Contains("clean"))
            {
                cleanCalled = true;
                return Task.FromResult(new ColabResult(0, "CLEAN_OK", string.Empty));
            }
            if (cmd == "stop")
            {
                remoteAlive = false;
                return Task.FromResult(new ColabResult(0, "Session terminated", string.Empty));
            }

            return Task.FromResult(new ColabResult(0, "OK", string.Empty));
        });

        var store = new SessionStore(_dataDir);
        var mgr = new SessionManager(cli, new VmScripts(store, _scriptsDir), store, new BillingTimer());
        Assert.True(await mgr.StartOrDeployAsync(60, true));

        var bookPath = Path.Combine(_tempRoot, "[测试] 漫画 第01卷 (Raw).cbz");
        var customOutDir = Path.Combine(_tempRoot, "out_books");
        using (var z = ZipFile.Open(bookPath, ZipArchiveMode.Create))
        {
            TestImageFixtures.WriteBytesEntry(z, "sub/1.jpg", TestImageFixtures.CreateValidJpeg(1));
            TestImageFixtures.WriteBytesEntry(z, "sub/2.jpg", TestImageFixtures.CreateValidJpeg(2));
        }

        var queue = new TaskQueue(cli, mgr, action => action(), pollInterval: TimeSpan.FromMilliseconds(20));
        var (added, reject) = queue.EnqueueFiles([bookPath], customOutDir);
        Assert.Equal(1, added);
        Assert.Null(reject);

        // Draining rejects new files
        mgr.TransitionToDraining();
        var (addedWhileDraining, drainingReject) = queue.EnqueueFiles([bookPath]);
        Assert.Equal(0, addedWhileDraining);
        Assert.NotNull(drainingReject);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (mgr.State != SessionState.Idle && DateTime.UtcNow < deadline)
        {
            await Task.Delay(30);
        }

        var task = Assert.Single(queue.Items);
        Assert.Equal(TranslationTaskStatus.Completed, task.Status);
        Assert.False(packCalledWhileAlive); // A-04!
        Assert.Equal(3, pollStep);
        Assert.NotNull(task.OutputZipPath);
        Assert.True(File.Exists(task.OutputZipPath!));
        Assert.StartsWith(customOutDir, task.OutputZipPath);
        Assert.EndsWith("[测试] 漫画 第01卷 (Raw)_translated.cbz", task.OutputZipPath);
        Assert.StartsWith("/content/job_", uploadedRemotePath);
        Assert.StartsWith("/content/jobs/job_", jobDirUsed);
        Assert.True(cleanCalled);
        Assert.Equal(SessionState.Idle, mgr.State);

        // A-12: vm_job.sh start & vm_pack.sh pack MUST pass allowRateLimitRetry: false!
        Assert.Contains(cli.SshCalls, c => c.RemoteArgs.Contains("/content/vm_job.sh") && !c.AllowRateLimitRetry);
        Assert.Contains(cli.SshCalls, c => c.RemoteArgs.Contains("pack") && !c.AllowRateLimitRetry);
    }

    [Fact]
    public async Task TaskQueue_RejectsCorruptDownloadAndStillCleansRemoteInFinally_AndSupportsRetry()
    {
        // A-04 + A-06 + A-13: Corrupt 22-byte download fails task, cleans remote in finally,
        // and RetryTask creates fresh JobId and succeeds on second attempt!
        int downloadAttempt = 0;
        int cleanCount = 0;
        var prebuiltOutZip = Path.Combine(_tempRoot, "good_retry.zip");
        var (outBytes, outSha) = TestImageFixtures.CreateValidTranslatedZip(prebuiltOutZip, pageCount: 1);
        var setup = new FakeVmSetup();

        var cli = new FakeColabCli((args, onOut, _) =>
        {
            var cmd = args[0];
            if (cmd == "sessions") return Task.FromResult(new ColabResult(0, "No active sessions found.", string.Empty));
            if (cmd == "new" || cmd == "upload") return Task.FromResult(new ColabResult(0, "OK", string.Empty));
            if (cmd == "ssh" && args.Contains("/content/vm_setup.sh"))
            {
                return Task.FromResult(setup.Handle(args)!);
            }
            if (cmd == "ssh" && args.Contains("/content/vm_job.sh"))
                return Task.FromResult(new ColabResult(0, "PROTO 2\nIMAGES 1\nTRANSLATE_PID 111", string.Empty));
            if (cmd == "ssh" && args.Contains("/content/vm_status.sh"))
            {
                if (args[^1] == "/content/vm_status.sh")
                    return Task.FromResult(new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mit ready", string.Empty));
                return Task.FromResult(new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mit ready\nDONE_COUNT 1\nJOB_ALIVE 0\nJOB_EXIT_CODE 0", string.Empty));
            }
            if (cmd == "ssh" && args.Contains("pack"))
                return Task.FromResult(new ColabResult(0, $"PACK_OK {outBytes} {outSha} 1", string.Empty));
            if (cmd == "download")
            {
                downloadAttempt++;
                if (downloadAttempt == 1)
                {
                    // Empty 22-byte EOCD zip -> MUST be rejected!
                    File.WriteAllBytes(args[^1], [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
                }
                else
                {
                    File.Copy(prebuiltOutZip, args[^1], overwrite: true);
                }
                return Task.FromResult(new ColabResult(0, "OK", string.Empty));
            }
            if (cmd == "ssh" && args.Contains("clean"))
            {
                cleanCount++;
                return Task.FromResult(new ColabResult(0, "CLEAN_OK", string.Empty));
            }
            return Task.FromResult(new ColabResult(0, "OK", string.Empty));
        })
        {
            // Also verify batch-shim safe ASCII download staging path (A-12)!
            UsesBatchShim = true
        };

        var store = new SessionStore(_dataDir);
        var mgr = new SessionManager(cli, new VmScripts(store, _scriptsDir), store, new BillingTimer());
        Assert.True(await mgr.StartOrDeployAsync(60, true));

        var bookPath = Path.Combine(_tempRoot, "A&B_book.zip");
        using (var z = ZipFile.Open(bookPath, ZipArchiveMode.Create))
        {
            TestImageFixtures.WriteBytesEntry(z, "001.jpg", TestImageFixtures.CreateValidJpeg(1));
        }

        var queue = new TaskQueue(cli, mgr, action => action(), pollInterval: TimeSpan.FromMilliseconds(15));
        Assert.Equal(1, queue.EnqueueFiles([bookPath]).AddedCount);

        var deadline1 = DateTime.UtcNow.AddSeconds(5);
        while (queue.Items[0].Status != TranslationTaskStatus.Failed && DateTime.UtcNow < deadline1)
            await Task.Delay(20);

        var failedItem = queue.Items[0];
        var failedJobId = failedItem.JobId;
        Assert.Equal(TranslationTaskStatus.Failed, failedItem.Status);
        Assert.Contains("校验失败", failedItem.ErrorSummary);
        Assert.Equal(1, cleanCount); // finally cleaned remote even on failure!
        Assert.False(File.Exists(Path.Combine(_tempRoot, "A&B_book_translated.zip")));

        // Retry failed task (A-13)
        var (retried, retryErr) = queue.RetryTask(failedItem);
        Assert.True(retried);
        Assert.Null(retryErr);
        Assert.NotEqual(failedJobId, queue.Items[0].JobId);

        var deadline2 = DateTime.UtcNow.AddSeconds(5);
        while (queue.Items[0].Status != TranslationTaskStatus.Completed && DateTime.UtcNow < deadline2)
            await Task.Delay(20);

        Assert.Equal(TranslationTaskStatus.Completed, queue.Items[0].Status);
        Assert.True(File.Exists(queue.Items[0].OutputZipPath!));
        Assert.Equal(2, cleanCount);
    }

    [Fact]
    public async Task TaskQueue_WhenPollFailsConsecutivelyAndStopJobFails_PausesQueueBeforeNextTask()
    {
        // A-06 + A-07: Consecutive poll failures abort job, call stop_job; if stop_job fails, pause queue so book2 never runs!
        int jobStarts = 0;
        var setup = new FakeVmSetup();
        var cli = new FakeColabCli((args, onOut, _) =>
        {
            var cmd = args[0];
            if (cmd == "sessions") return Task.FromResult(new ColabResult(0, "No active sessions found.", string.Empty));
            if (cmd == "new" || cmd == "upload") return Task.FromResult(new ColabResult(0, "OK", string.Empty));
            if (cmd == "ssh" && args.Contains("/content/vm_setup.sh"))
            {
                return Task.FromResult(setup.Handle(args)!);
            }
            if (cmd == "ssh" && args.Contains("/content/vm_job.sh"))
            {
                jobStarts++;
                return Task.FromResult(new ColabResult(0, "PROTO 2\nIMAGES 1\nTRANSLATE_PID 555", string.Empty));
            }
            if (cmd == "ssh" && args.Contains("/content/vm_status.sh"))
            {
                if (args[^1] == "/content/vm_status.sh")
                    return Task.FromResult(new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mit ready", string.Empty));
                return Task.FromResult(new ColabResult(1, "", "SSH broken pipe"));
            }
            if (cmd == "ssh" && args.Contains("stop_job"))
                return Task.FromResult(new ColabResult(1, "", "STOP_JOB_STILL_ALIVE 555"));
            if (cmd == "ssh" && args.Contains("clean"))
                return Task.FromResult(new ColabResult(0, "CLEAN_OK", string.Empty));
            return Task.FromResult(new ColabResult(0, "OK", string.Empty));
        });

        var store = new SessionStore(_dataDir);
        var mgr = new SessionManager(cli, new VmScripts(store, _scriptsDir), store, new BillingTimer());
        Assert.True(await mgr.StartOrDeployAsync(60, true));

        var b1 = Path.Combine(_tempRoot, "b1.zip");
        var b2 = Path.Combine(_tempRoot, "b2.zip");
        foreach (var p in new[] { b1, b2 })
        {
            using var z = ZipFile.Open(p, ZipArchiveMode.Create);
            TestImageFixtures.WriteBytesEntry(z, "001.jpg", TestImageFixtures.CreateValidJpeg(1));
        }

        var queue = new TaskQueue(cli, mgr, action => action(),
            pollInterval: TimeSpan.FromMilliseconds(10),
            maxConsecutivePollFailures: 2);

        Assert.Equal(2, queue.EnqueueFiles([b1, b2]).AddedCount);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!queue.IsQueuePaused && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.True(queue.IsQueuePaused);
        Assert.Equal(TranslationTaskStatus.Failed, queue.Items[0].Status);
        Assert.Equal(TranslationTaskStatus.Pending, queue.Items[1].Status);
        Assert.Equal(1, jobStarts); // Book 2 MUST NOT launch while book 1 couldn't be stopped!
        Assert.True(mgr.LatestStatus.IsDisconnected);

        // Also verify RemovePendingTask works on pending book 2 (A-13)
        Assert.True(queue.RemovePendingTask(queue.Items[1]));
        Assert.Single(queue.Items);
    }

    [Fact]
    public void EnqueueFiles_OfflinePrecheckRejectsInvalidArchiveWithoutAdding()
    {
        // E-01: Invalid zip rejected at enqueue time before GPU billing
        var store = new SessionStore(_dataDir);
        var cli = new FakeColabCli((_, _, _) => Task.FromResult(new ColabResult(0, "OK", string.Empty)));
        var mgr = new SessionManager(cli, new VmScripts(store, _scriptsDir), store, new BillingTimer());
        var queue = new TaskQueue(cli, mgr, action => action());

        var badZip = Path.Combine(_tempRoot, "broken.zip");
        File.WriteAllText(badZip, "not a zip");

        var (added, reject) = queue.EnqueueFiles([badZip]);
        Assert.Equal(0, added);
        Assert.NotNull(reject);
        Assert.Empty(queue.Items);
    }

    [Fact]
    public async Task EnqueueFilesAsync_ExpandsFoldersDedupesAndReportsUnsupportedPaths()
    {
        var store = new SessionStore(_dataDir);
        var cli = new FakeColabCli((_, _, _) => Task.FromResult(new ColabResult(0, "OK", string.Empty)));
        var mgr = new SessionManager(cli, new VmScripts(store, _scriptsDir), store, new BillingTimer());
        var queue = new TaskQueue(cli, mgr, action => action());

        var bookFolder = Path.Combine(_tempRoot, "manga_dir");
        var emptyFolder = Path.Combine(_tempRoot, "empty_dir");
        Directory.CreateDirectory(bookFolder);
        Directory.CreateDirectory(emptyFolder);

        var b1 = Path.Combine(bookFolder, "vol1.zip");
        var b2 = Path.Combine(bookFolder, "vol2.cbz");
        foreach (var p in new[] { b1, b2 })
        {
            using var z = ZipFile.Open(p, ZipArchiveMode.Create);
            TestImageFixtures.WriteBytesEntry(z, "001.jpg", TestImageFixtures.CreateValidJpeg(1));
        }

        var lnkPath = Path.Combine(_tempRoot, "shortcut.lnk");
        File.WriteAllText(lnkPath, "fake shortcut");

        var progressCalls = new List<(int Index, int Total, string Name)>();
        var (added, reject) = await queue.EnqueueFilesAsync(
            [bookFolder, emptyFolder, lnkPath, b1, Path.Combine(_tempRoot, "ghost.zip")],
            outputDirectory: null,
            onProgress: (idx, total, name) => progressCalls.Add((idx, total, name)));

        Assert.Equal(2, added);
        Assert.Equal(2, queue.Items.Count);
        Assert.NotEmpty(progressCalls);
        Assert.NotNull(reject);
        Assert.Contains("empty_dir", reject);
        Assert.Contains("快捷方式", reject);
        Assert.Contains("等 4 个文件未通过预检", reject);

        // Dropping b2 again while already queued must dedupe cleanly
        var (added2, reject2) = await queue.EnqueueFilesAsync([b2]);
        Assert.Equal(0, added2);
        Assert.Contains("去重", reject2);
    }

    [Fact]
    public void CancelPendingAndActive_ReleasesQueueLockBeforeUiDispatch_AndLogEntryUsesReferenceEquality()
    {
        // S-05: Two identical LogEntry values at the same tick must NOT be equal (prevents WPF ItemsControl collisions).
        var now = DateTimeOffset.UtcNow;
        var logA = new LogEntry(now, LogLevel.Info, "same message");
        var logB = new LogEntry(now, LogLevel.Info, "same message");
        Assert.NotEqual(logA, logB);

        // S-06: CancelPendingAndActive must NOT hold _queueLock while calling _dispatchToUi.
        var store = new SessionStore(_dataDir);
        var cli = new FakeColabCli((_, _, _) => Task.FromResult(new ColabResult(0, "OK", string.Empty)));
        var mgr = new SessionManager(cli, new VmScripts(store, _scriptsDir), store, new BillingTimer());

        TaskQueue? queueRef = null;
        bool crossThreadLockAcquiredInsideDispatch = false;
        var queue = new TaskQueue(cli, mgr, dispatchToUi: action =>
        {
            // Run an independent thread that acquires _queueLock (via PendingCount) while inside _dispatchToUi!
            // If _queueLock were still held here, Task.Run(...).Wait(2000) would deadlock and return false.
            if (queueRef is not null)
            {
                var probe = Task.Run(() => queueRef.PendingCount);
                crossThreadLockAcquiredInsideDispatch = probe.Wait(TimeSpan.FromSeconds(2));
            }
            action();
        });
        queueRef = queue;

        var zPath = Path.Combine(_tempRoot, "lock_test.zip");
        using (var z = ZipFile.Open(zPath, ZipArchiveMode.Create))
        {
            TestImageFixtures.WriteBytesEntry(z, "001.jpg", TestImageFixtures.CreateValidJpeg(1));
        }

        Assert.Equal(1, queue.EnqueueFiles([zPath]).AddedCount);
        crossThreadLockAcquiredInsideDispatch = false;
        queue.CancelPendingAndActive();

        Assert.True(crossThreadLockAcquiredInsideDispatch, "CancelPendingAndActive held _queueLock across _dispatchToUi (AB-BA deadlock risk)!");
        Assert.Equal(TranslationTaskStatus.Cancelled, queue.Items[0].Status);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }
}
