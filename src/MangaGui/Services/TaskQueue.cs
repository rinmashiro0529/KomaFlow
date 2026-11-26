using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MangaGui.Models;

namespace MangaGui.Services;

/// <summary>
/// Serial manga translation worker with offline pre-check (E-01), isolated /content/jobs/<job_id>/ workspaces (A-06),
/// PROTO 2 exit-code completion contract + SHA256/ZIP verification (A-04), bounded poll/wall-clock timeouts (A-07),
/// batch-safe cross-volume atomic downloads (A-12), and single-task remove/retry recovery (A-13).
/// </summary>
public sealed class TaskQueue
{
    /// <summary>
    /// mtui is a Japanese-only pipeline (D8): archives whose name hints at English/Thai
    /// sources are rejected at enqueue time so they never consume GPU time.
    /// </summary>
    private static readonly Regex NonJapaneseHintRegex =
        new(@"(\benglish\b|\beng\b|\bthai\b|ไทย)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IColabCli _cli;
    private readonly SessionManager _session;
    private readonly Action<Action> _dispatchToUi;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan? _maxTaskDuration;
    private readonly TimeSpan _stallTimeout;
    private readonly long _transferChunkBytes;
    private readonly TimeSpan _transferRetryDelay;
    private readonly int _maxConsecutivePollFailures;
    private readonly PackageBudget _budget;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _workerSignal = new(0);
    private readonly object _queueLock = new();
    private readonly List<TranslationTaskItem> _pending = [];

    private int _jobSeq;
    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;
    private TranslationTaskItem? _activeItem;
    private bool _isQueuePaused;
    private string? _pauseReason;

    public ObservableCollection<TranslationTaskItem> Items { get; } = [];

    public event Action<LogLevel, string>? LogMessage;

    /// <summary>Raised when a task failure records raw CLI output (title, content) for the persistent log.</summary>
    public event Action<string, string>? RawOutputRecorded;
    public event Action<TranslationTaskItem>? TaskCompleted;
    public event Action? QueueChanged;

    public TaskQueue(
        IColabCli cli,
        SessionManager session,
        Action<Action>? dispatchToUi = null,
        TimeSpan? pollInterval = null,
        TimeSpan? maxTaskDuration = null,
        int maxConsecutivePollFailures = 5,
        PackageBudget? budget = null,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? stallTimeout = null,
        long? transferChunkBytes = null,
        TimeSpan? transferRetryDelay = null)
    {
        _cli = cli;
        _session = session;
        _dispatchToUi = dispatchToUi ?? (action => action());
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(12);
        _maxTaskDuration = maxTaskDuration;
        _stallTimeout = stallTimeout ?? TimeSpan.FromMinutes(20);
        _transferChunkBytes = Math.Max(1, transferChunkBytes ?? ChunkedTransfer.DefaultChunkBytes);
        _transferRetryDelay = transferRetryDelay ?? TimeSpan.FromSeconds(5);
        _maxConsecutivePollFailures = Math.Max(1, maxConsecutivePollFailures);
        _budget = budget ?? PackageBudget.Default;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public int UnfinishedCount
    {
        get
        {
            lock (_queueLock)
            {
                return _pending.Count + (_activeItem is not null ? 1 : 0);
            }
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_queueLock)
            {
                return _pending.Count;
            }
        }
    }

    public bool HasActiveOrPendingWork => UnfinishedCount > 0;

    public bool HasActiveTask
    {
        get
        {
            lock (_queueLock)
            {
                return _activeItem is not null;
            }
        }
    }

    public bool IsQueuePaused
    {
        get
        {
            lock (_queueLock)
            {
                return _isQueuePaused;
            }
        }
    }

    public string? PauseReason
    {
        get
        {
            lock (_queueLock)
            {
                return _pauseReason;
            }
        }
    }

    public void StartWorker()
    {
        if (_workerTask is not null && !_workerTask.IsCompleted)
        {
            return;
        }

        _workerCts = new CancellationTokenSource();
        _workerTask = Task.Run(() => WorkerLoopAsync(_workerCts.Token));
    }

    public void NotifySessionReady()
    {
        _workerSignal.Release();
    }

    public void ResumeQueue()
    {
        lock (_queueLock)
        {
            _isQueuePaused = false;
            _pauseReason = null;
        }

        Log(LogLevel.Info, "已手动恢复任务队列执行。");
        QueueChanged?.Invoke();
        StartWorker();
        _workerSignal.Release();
    }

    public void CancelPendingAndActive()
    {
        List<TranslationTaskItem> cancelledItems;
        lock (_queueLock)
        {
            cancelledItems = [.. _pending];
            _pending.Clear();
            _isQueuePaused = false;
            _pauseReason = null;
        }

        if (cancelledItems.Count > 0)
        {
            _dispatchToUi(() =>
            {
                foreach (var p in cancelledItems)
                {
                    p.Status = TranslationTaskStatus.Cancelled;
                }
            });
        }

        _workerCts?.Cancel();
        QueueChanged?.Invoke();
    }

    /// <summary>
    /// Enqueues local .zip / .cbz files with fast offline pre-check (E-01).
    /// Rejects new files while in Draining mode or when archive pre-check fails.
    /// </summary>
    public (int AddedCount, string? RejectReason) EnqueueFiles(IEnumerable<string> paths, string? outputDirectory = null)
    {
        return EnqueueFilesCore(paths, outputDirectory, onProgress: null, CancellationToken.None);
    }

    /// <summary>
    /// Async version of <see cref="EnqueueFiles"/> that runs folder expansion and ZIP archive pre-checks
    /// on a background thread so OLE drag-and-drop and the WPF UI thread never block.
    /// </summary>
    public Task<(int AddedCount, string? RejectReason)> EnqueueFilesAsync(
        IEnumerable<string> paths,
        string? outputDirectory = null,
        Action<int, int, string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = paths as IReadOnlyList<string> ?? paths.ToList();
        return Task.Run(
            () => EnqueueFilesCore(snapshot, outputDirectory, onProgress, cancellationToken),
            cancellationToken);
    }

    private (int AddedCount, string? RejectReason) EnqueueFilesCore(
        IEnumerable<string> paths,
        string? outputDirectory,
        Action<int, int, string>? onProgress,
        CancellationToken cancellationToken)
    {
        if (_session.State == SessionState.Draining)
        {
            return (0, "当前处于「翻完当前队列再停」收尾模式，无法添加新任务。如需继续翻译请先延长时长。");
        }

        var rejectedReasons = new List<string>();
        var candidates = ExpandDroppedPaths(paths, rejectedReasons, cancellationToken);
        if (candidates.Count == 0)
        {
            return (0, FormatRejectSummary(rejectedReasons));
        }

        var activeOrPendingPaths = GetActiveAndPendingNormalizedPaths();
        var seenInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toEnqueue = new List<(TranslationTaskItem Item, ArchivePrecheckResult Precheck)>();

        for (int i = 0; i < candidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = candidates[i];
            var fileName = SafeFileName(path);
            onProgress?.Invoke(i + 1, candidates.Count, fileName);

            var normalizedPath = NormalizePathForDedup(path);
            if (!seenInBatch.Add(normalizedPath) || activeOrPendingPaths.Contains(normalizedPath))
            {
                var dupMsg = $"{fileName}：已在当前队列中等待或处理中，已自动去重";
                Log(LogLevel.Warning, dupMsg);
                rejectedReasons.Add(dupMsg);
                continue;
            }

            if (!ZipPreprocessor.IsSupportedArchivePath(path))
            {
                var ext = SafeExtension(path);
                var hint = string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase)
                    ? "不支持快捷方式（.lnk），请拖入原始 .zip / .cbz 文件"
                    : "仅支持 .zip / .cbz";
                var msg = $"{fileName}：{hint}";
                Log(LogLevel.Warning, $"已忽略不支持的文件：{msg}");
                rejectedReasons.Add(msg);
                continue;
            }

            if (!string.Equals(_session.ConfiguredEngine, TranslationEngines.Mit, StringComparison.Ordinal))
            {
                var stem = Path.GetFileNameWithoutExtension(fileName);
                var hint = NonJapaneseHintRegex.Match(stem);
                if (hint.Success)
                {
                    var msg = $"{fileName}：新引擎只支持日文原文（文件名含『{hint.Value}』）；英文版可在设置里切换到旧引擎翻译";
                    Log(LogLevel.Warning, $"已忽略非日文来源文件：{msg}");
                    rejectedReasons.Add(msg);
                    continue;
                }
            }

            var precheck = ZipPreprocessor.PrecheckArchive(path, _budget, cancellationToken);
            if (!precheck.IsValid)
            {
                var msg = $"{fileName}：{precheck.ErrorMessage ?? precheck.Summary}";
                Log(LogLevel.Warning, $"离线预检未通过（未入队，不消耗 GPU）：{msg}");
                rejectedReasons.Add(msg);
                continue;
            }

            var jobId = NextJobId();
            var item = new TranslationTaskItem(jobId, path, outputDirectory)
            {
                TotalPages = precheck.ImageCount,
                PrecheckSummary = precheck.Summary,
                DetailHint = precheck.Summary
            };
            toEnqueue.Add((item, precheck));
        }

        if (toEnqueue.Count > 0)
        {
            // Re-check Draining state right before committing in case state changed during async precheck.
            if (_session.State == SessionState.Draining)
            {
                return (0, "预检期间已进入「翻完当前队列再停」收尾模式，未能添加新任务。");
            }

            lock (_queueLock)
            {
                foreach (var (item, _) in toEnqueue)
                {
                    _pending.Add(item);
                }
            }

            _dispatchToUi(() =>
            {
                foreach (var (item, _) in toEnqueue)
                {
                    Items.Add(item);
                }
            });

            foreach (var (item, precheck) in toEnqueue)
            {
                Log(LogLevel.Info, $"任务入队：{item.FileName}（{precheck.Summary}，{item.JobId}）");
            }

            QueueChanged?.Invoke();
            StartWorker();
            _workerSignal.Release();
        }

        return (toEnqueue.Count, FormatRejectSummary(rejectedReasons));
    }

    private List<string> ExpandDroppedPaths(
        IEnumerable<string> rawPaths,
        List<string> rejectedReasons,
        CancellationToken cancellationToken)
    {
        var result = new List<string>();
        foreach (var raw in rawPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var path = raw.Trim();
            if (Directory.Exists(path))
            {
                var folderName = SafeFileName(path);
                try
                {
                    var archives = Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
                        .Where(ZipPreprocessor.IsSupportedArchivePath)
                        .OrderBy(p => p, NaturalPathComparer.Instance)
                        .ToList();

                    if (archives.Count == 0)
                    {
                        var msg = $"文件夹「{folderName}」中未找到 .zip / .cbz 漫画压缩包";
                        Log(LogLevel.Warning, msg);
                        rejectedReasons.Add(msg);
                    }
                    else
                    {
                        Log(LogLevel.Info, $"已从文件夹「{folderName}」识别到 {archives.Count} 个漫画压缩包。");
                        result.AddRange(archives);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    var msg = $"无法读取文件夹「{folderName}」：{ex.Message}";
                    Log(LogLevel.Warning, msg);
                    rejectedReasons.Add(msg);
                }
                continue;
            }

            if (!File.Exists(path))
            {
                var msg = $"{SafeFileName(path)}：文件不存在或为无法直接读取的虚拟路径";
                Log(LogLevel.Warning, msg);
                rejectedReasons.Add(msg);
                continue;
            }

            result.Add(path);
        }

        return result;
    }

    private HashSet<string> GetActiveAndPendingNormalizedPaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_queueLock)
        {
            if (_activeItem is not null)
            {
                set.Add(NormalizePathForDedup(_activeItem.SourceZipPath));
            }
            foreach (var p in _pending)
            {
                set.Add(NormalizePathForDedup(p.SourceZipPath));
            }
        }
        return set;
    }

    private static string NormalizePathForDedup(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static string SafeFileName(string path)
    {
        try
        {
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
        catch
        {
            return path;
        }
    }

    private static string SafeExtension(string path)
    {
        try
        {
            return Path.GetExtension(path);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? FormatRejectSummary(List<string> rejectedReasons)
    {
        if (rejectedReasons.Count == 0)
        {
            return null;
        }

        return string.Join("；", rejectedReasons.Take(3))
               + (rejectedReasons.Count > 3 ? $" 等 {rejectedReasons.Count} 个文件未通过预检" : "");
    }

    public bool RemovePendingTask(TranslationTaskItem item)
    {
        bool removed;
        lock (_queueLock)
        {
            removed = _pending.Remove(item);
        }

        if (!removed)
        {
            return false;
        }

        _dispatchToUi(() => Items.Remove(item));
        Log(LogLevel.Info, $"已移出待处理任务：{item.FileName}");
        QueueChanged?.Invoke();
        return true;
    }

    public (bool Retried, string? RejectReason) RetryTask(TranslationTaskItem item)
    {
        if (_session.State == SessionState.Draining)
        {
            return (false, "当前处于收尾模式，无法重试任务。请先延长时长。");
        }

        if (item.Status is not (TranslationTaskStatus.Failed or TranslationTaskStatus.Cancelled))
        {
            return (false, "仅支持重试失败或已取消的任务。");
        }

        var precheck = ZipPreprocessor.PrecheckArchive(item.SourceZipPath, _budget);
        if (!precheck.IsValid)
        {
            return (false, $"重试前校验失败：{precheck.ErrorMessage ?? precheck.Summary}");
        }

        var replacement = new TranslationTaskItem(NextJobId(), item.SourceZipPath, item.TargetOutputDirectory)
        {
            TotalPages = precheck.ImageCount,
            PrecheckSummary = precheck.Summary,
            DetailHint = $"重试任务 · {precheck.Summary}"
        };

        lock (_queueLock)
        {
            _pending.Add(replacement);
        }

        _dispatchToUi(() =>
        {
            var idx = Items.IndexOf(item);
            if (idx >= 0)
            {
                Items[idx] = replacement;
            }
            else
            {
                Items.Add(replacement);
            }
        });

        Log(LogLevel.Info, $"任务已重新入队：{replacement.FileName}（{replacement.JobId}）");
        QueueChanged?.Invoke();
        StartWorker();
        _workerSignal.Release();
        return (true, null);
    }

    public void ClearCompleted()
    {
        _dispatchToUi(() =>
        {
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                if (Items[i].Status is TranslationTaskStatus.Completed or TranslationTaskStatus.Cancelled)
                {
                    Items.RemoveAt(i);
                }
            }
        });
        QueueChanged?.Invoke();
    }

    public void ClearFailedAndCancelled()
    {
        _dispatchToUi(() =>
        {
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                if (Items[i].Status is TranslationTaskStatus.Failed or TranslationTaskStatus.Cancelled)
                {
                    Items.RemoveAt(i);
                }
            }
        });
        QueueChanged?.Invoke();
    }

    private string NextJobId()
    {
        var seq = Interlocked.Increment(ref _jobSeq);
        return $"job_{DateTime.Now:yyyyMMddHHmmss}_{seq:D3}";
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _workerSignal.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            while (!ct.IsCancellationRequested)
            {
                if (_session.State is not (SessionState.Ready or SessionState.Busy or SessionState.Draining))
                {
                    break;
                }

                TranslationTaskItem? next;
                lock (_queueLock)
                {
                    if (_isQueuePaused || _pending.Count == 0)
                    {
                        break;
                    }
                    next = _pending[0];
                    _pending.RemoveAt(0);
                    _activeItem = next;
                }

                QueueChanged?.Invoke();
                _session.TransitionToBusy($"正在处理 {next.FileName}");

                try
                {
                    await ProcessSingleTaskAsync(next, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _dispatchToUi(() => next.Status = TranslationTaskStatus.Cancelled);
                    break;
                }
                catch (Exception ex)
                {
                    MarkFailed(next, ex.Message, ex.ToString());
                }
                finally
                {
                    lock (_queueLock)
                    {
                        _activeItem = null;
                    }
                    QueueChanged?.Invoke();
                }

                bool hasMore;
                bool paused;
                lock (_queueLock)
                {
                    hasMore = _pending.Count > 0;
                    paused = _isQueuePaused;
                }

                if (paused)
                {
                    if (_session.State == SessionState.Draining)
                    {
                        // "Finish the queue, then stop": a paused queue cannot finish, so stop now
                        // instead of leaving the runtime billing with nobody watching.
                        Log(LogLevel.Warning, "收尾模式下队列已暂停、剩余任务无法继续，按约定自动停止计费（未完成的任务保留在列表中）。");
                        await AutoStopAfterDrainAsync(tryKillTranslate: true).ConfigureAwait(false);
                    }
                    else
                    {
                        _session.TransitionToReadyFromBusy();
                    }
                    break;
                }

                if (!hasMore)
                {
                    if (_session.State == SessionState.Draining)
                    {
                        Log(LogLevel.Warning, "当前队列已全部处理完毕，按约定触发自动停止计费...");
                        await AutoStopAfterDrainAsync(tryKillTranslate: false).ConfigureAwait(false);
                    }
                    else
                    {
                        _session.TransitionToReadyFromBusy();
                    }
                    break;
                }
            }
        }
    }

    private async Task AutoStopAfterDrainAsync(bool tryKillTranslate)
    {
        var result = await _session.AutoStopUntilVerifiedAsync(tryKillTranslate, CancellationToken.None)
            .ConfigureAwait(false);
        if (!result.VerifiedStopped)
        {
            return;
        }

        // The runtime is gone, so a pause that protected it from a lingering job no longer applies.
        bool wasPaused;
        lock (_queueLock)
        {
            wasPaused = _isQueuePaused;
            _isQueuePaused = false;
            _pauseReason = null;
        }

        if (wasPaused)
        {
            QueueChanged?.Invoke();
        }
    }

    private async Task ProcessSingleTaskAsync(TranslationTaskItem item, CancellationToken ct)
    {
        var logBuffer = new StringBuilder();
        PreparedZipResult? prepared = null;
        bool remoteArtifactTouched = false;
        bool jobStarted = false;
        bool jobFinished = false;

        string remoteStagedZip = $"/content/{item.JobId}.zip";
        string remoteJobDir = $"/content/jobs/{item.JobId}";

        try
        {
            // 1. Local zip validation, budget enforcement, magic-byte check & natural-order flattening
            SetStatus(item, TranslationTaskStatus.Preparing, "校验压缩包与页序...");
            prepared = await ZipPreprocessor.PrepareForUploadAsync(
                item.SourceZipPath,
                item.JobId,
                budget: _budget,
                cancellationToken: ct).ConfigureAwait(false);

            _dispatchToUi(() => item.TotalPages = prepared.ImageCount);
            if (prepared.WasFlattenedOrCopied)
            {
                Log(LogLevel.Info, $"{item.FileName} 已自动按自然页序拍平重打包（共 {prepared.ImageCount} 页）。");
            }

            // 2. Upload zip to safe ASCII remote staged path (in parts when it exceeds the single-request size)
            SetStatus(item, TranslationTaskStatus.Uploading, $"上传 {item.FileName}...");
            Log(LogLevel.Info, $"开始上传 {item.FileName} → {remoteStagedZip}");
            remoteArtifactTouched = true;

            var uploadError = await UploadArchiveAsync(prepared.UploadZipPath, item, remoteStagedZip, remoteJobDir, logBuffer, ct)
                .ConfigureAwait(false);
            if (uploadError is not null)
            {
                MarkFailed(item, uploadError, logBuffer.ToString());
                return;
            }

            // 3. Start isolated job on VM via vm_job.sh (A-06, A-12: non-idempotent launch MUST NOT auto-retry on 429)
            SetStatus(item, TranslationTaskStatus.Translating, "启动云端独立翻译任务...");
            var runRes = await _cli.RunSshAsync(
                VmScripts.SessionName,
                ["bash", "/content/vm_job.sh", "start", remoteJobDir, prepared.ImageCount.ToString()],
                TimeSpan.FromMinutes(2),
                allowRateLimitRetry: false,
                cancellationToken: ct).ConfigureAwait(false);
            AppendLog(logBuffer, "VM_JOB_START", runRes.CombinedOutput);

            if (!runRes.IsSuccess)
            {
                MarkFailed(item, $"启动翻译失败：{FirstErrorLine(runRes.CombinedOutput)}", logBuffer.ToString());
                return;
            }

            jobStarted = true;
            var remoteImageCount = VmScripts.ParseExpectedImageCount(runRes.StdOut);
            var expectedPages = remoteImageCount > 0 ? remoteImageCount : prepared.ImageCount;
            _dispatchToUi(() =>
            {
                item.TotalPages = expectedPages;
                item.CompletedPages = 0;
            });
            Log(LogLevel.Info, $"{item.FileName} 开始翻译（隔离目录 {remoteJobDir}），共 {expectedPages} 页。");

            // 4. Merged polling loop via vm_status.sh <remoteJobDir> (A-04, A-07)
            // The wall-clock cap scales with the page count; a job that stops producing pages is
            // caught much earlier by the stall timeout.
            var maxDuration = _maxTaskDuration ?? TimeSpan.FromMinutes(Math.Max(90, expectedPages));
            var deadlineUtc = _utcNow() + maxDuration;
            var lastProgressUtc = _utcNow();
            int highestDone = 0;
            int consecutiveDeadWithoutExit = 0;
            int consecutivePollFailures = 0;
            int pollCount = 0;
            int lastLoggedPages = -1;
            bool waitingForProcessExitLogged = false;
            string? shortfall = null;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (_utcNow() >= deadlineUtc)
                {
                    await HandleTaskTimeoutAsync(item, prepared, remoteJobDir, logBuffer,
                        $"翻译超时（超过 {maxDuration.TotalMinutes:F0} 分钟）", highestDone, expectedPages, ct).ConfigureAwait(false);
                    return;
                }

                if (_utcNow() - lastProgressUtc >= _stallTimeout)
                {
                    await HandleTaskTimeoutAsync(item, prepared, remoteJobDir, logBuffer,
                        $"翻译停滞（{_stallTimeout.TotalMinutes:F0} 分钟内没有新页面产出，已完成 {highestDone}/{expectedPages} 页）",
                        highestDone, expectedPages, ct).ConfigureAwait(false);
                    return;
                }

                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
                pollCount++;

                var status = await _session.PollStatusAsync(remoteJobDir, ct).ConfigureAwait(false);
                if (status is null)
                {
                    consecutivePollFailures++;
                    AppendLog(logBuffer, $"POLL#{pollCount}", $"vm_status.sh failed ({consecutivePollFailures}/{_maxConsecutivePollFailures})");

                    if (consecutivePollFailures >= _maxConsecutivePollFailures)
                    {
                        _session.MarkHealthDisconnected($"翻译轮询连续 {consecutivePollFailures} 次失联，已中止当前任务。");
                        await TryStopRemoteJobAndPauseIfNeededAsync(remoteJobDir, logBuffer, "连续轮询失联").ConfigureAwait(false);
                        MarkFailed(item, $"云端连接连续 {consecutivePollFailures} 次失联，任务已中止", TailLines(logBuffer.ToString(), 100));
                        return;
                    }

                    continue;
                }

                consecutivePollFailures = 0;
                var done = status.DoneCount ?? 0;
                var alive = status.TranslateAliveCount ?? 0;
                var exitCode = status.JobExitCode;

                if (done > highestDone)
                {
                    highestDone = done;
                    lastProgressUtc = _utcNow();
                }

                _dispatchToUi(() =>
                {
                    item.CompletedPages = Math.Min(done, expectedPages);
                    item.DetailHint = string.IsNullOrWhiteSpace(status.LastSavingLine)
                        ? $"GPU: {status.GpuInfo}"
                        : status.LastSavingLine;
                });

                if (done != lastLoggedPages && done > 0)
                {
                    lastLoggedPages = done;
                    Log(LogLevel.Info, $"{item.FileName} 翻译进度：{done}/{expectedPages} 页");
                }

                // PROTO 2 strict completion contract (A-04):
                // Only proceed to pack after process has exited (alive == 0) AND exit_code == 0!
                if (exitCode.HasValue && alive == 0)
                {
                    jobFinished = true;
                    if (exitCode.Value == 3)
                    {
                        shortfall = $"新引擎报告有页面翻译失败（已完成 {done}/{expectedPages} 页）";
                    }
                    else if (exitCode.Value == 4)
                    {
                        shortfall = $"成品目录与预期不一致（已完成 {done}/{expectedPages} 页）";
                    }
                    else if (exitCode.Value != 0)
                    {
                        shortfall = $"翻译进程异常退出（退出码 {exitCode.Value}，已完成 {done}/{expectedPages} 页）";
                    }
                    else if (done < expectedPages)
                    {
                        shortfall = $"翻译进程退出但结果缺页（仅产出 {done}/{expectedPages} 页）";
                    }

                    break;
                }

                if (done >= expectedPages && alive > 0 && !waitingForProcessExitLogged)
                {
                    waitingForProcessExitLogged = true;
                    Log(LogLevel.Info, $"{item.FileName} 所有页面已写入，等待翻译进程安全落盘退出...");
                }

                if (alive == 0 && !exitCode.HasValue && pollCount >= 2)
                {
                    consecutiveDeadWithoutExit++;
                    if (consecutiveDeadWithoutExit >= 2)
                    {
                        jobFinished = true;
                        shortfall = $"翻译进程意外消失且无退出码（完成 {done}/{expectedPages} 页）";
                        break;
                    }
                }
                else
                {
                    consecutiveDeadWithoutExit = 0;
                }
            }

            if (shortfall is not null)
            {
                await AppendRemoteJobLogTailAsync(remoteJobDir, logBuffer, ct).ConfigureAwait(false);
                await SalvagePartialResultAsync(item, prepared, remoteJobDir, shortfall, highestDone, expectedPages, logBuffer, ct)
                    .ConfigureAwait(false);
                return;
            }

            // 5-6. Pack on the VM, download (in parts when large), verify size/SHA256/ZIP/pages, promote atomically
            var fetched = await FetchResultAsync(item, prepared, remoteJobDir, expectedPages, partial: false, logBuffer, ct)
                .ConfigureAwait(false);
            if (fetched.Error is not null)
            {
                if (fetched.MissingPages)
                {
                    await SalvagePartialResultAsync(item, prepared, remoteJobDir, fetched.Error, fetched.ImageCount, expectedPages, logBuffer, ct)
                        .ConfigureAwait(false);
                    return;
                }

                MarkFailed(item, fetched.Error, TailLines(logBuffer.ToString(), 100));
                return;
            }

            var finalOutputPath = fetched.FinalPath!;
            _dispatchToUi(() =>
            {
                item.CompletedPages = expectedPages;
                item.OutputZipPath = finalOutputPath;
                item.DetailHint = $"{finalOutputPath}（已校验 {fetched.ImageCount} 页）";
                item.Status = TranslationTaskStatus.Completed;
            });

            Log(LogLevel.Success, $"完成并校验通过：{item.FileName} → {finalOutputPath}（{fetched.ImageCount} 页）");
            TaskCompleted?.Invoke(item);
        }
        finally
        {
            // Local temp cleanup
            if (prepared is { IsTemporaryFile: true })
            {
                TryDeleteFile(prepared.UploadZipPath);
            }

            // Remote stop_job if cancelled mid-flight (A-06)
            if (jobStarted && !jobFinished && ct.IsCancellationRequested && ShouldRunRemoteCleanup())
            {
                await TryStopRemoteJobAndPauseIfNeededAsync(remoteJobDir, logBuffer, "任务被取消").ConfigureAwait(false);
            }

            // Always attempt remote cleanup in finally when VM is still alive (A-06)
            if (remoteArtifactTouched && ShouldRunRemoteCleanup())
            {
                await TryCleanRemoteJobAsync(remoteJobDir).ConfigureAwait(false);
            }
        }
    }

    private sealed record FetchOutcome(string? FinalPath, int ImageCount, string? Error, bool MissingPages = false);

    /// <summary>
    /// Uploads the prepared archive. Archives above the single-request size are split locally, uploaded
    /// part by part and re-assembled (with size + SHA256 check) on the VM, because the CLI sends each
    /// file as one Base64 request. Returns null on success or a user-facing failure summary.
    /// </summary>
    private async Task<string?> UploadArchiveAsync(
        string localZip,
        TranslationTaskItem item,
        string remoteStagedZip,
        string remoteJobDir,
        StringBuilder logBuffer,
        CancellationToken ct)
    {
        long size = new FileInfo(localZip).Length;
        if (size <= _transferChunkBytes)
        {
            var uploadRes = await _cli.RunAsync(
                ["upload", "-s", VmScripts.SessionName, localZip, remoteStagedZip],
                ChunkedTransfer.EstimateTimeout(size),
                cancellationToken: ct).ConfigureAwait(false);
            AppendLog(logBuffer, "UPLOAD", uploadRes.CombinedOutput);
            return uploadRes.IsSuccess ? null : "上传失败";
        }

        var partsDir = Path.Combine(Path.GetTempPath(), "manga_gui_xfer", item.JobId);
        try
        {
            var parts = await Task.Run(() => ChunkedTransfer.SplitFile(localZip, partsDir, _transferChunkBytes), ct)
                .ConfigureAwait(false);
            var sha256 = await Task.Run(() => ChunkedTransfer.Sha256Hex(localZip), ct).ConfigureAwait(false);
            Log(LogLevel.Info, $"{item.FileName} 较大，分 {parts.Count} 片上传并在云端校验合并。");

            for (int i = 0; i < parts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                int partNumber = i + 1;
                _dispatchToUi(() => item.DetailHint = $"分片上传 {partNumber}/{parts.Count}...");

                var remotePart = $"/content/{item.JobId}.part_{i:D4}";
                bool ok = await TransferWithRetryAsync(
                    ["upload", "-s", VmScripts.SessionName, parts[i], remotePart],
                    new FileInfo(parts[i]).Length,
                    $"UPLOAD_PART {partNumber}/{parts.Count}",
                    logBuffer,
                    succeeded: null,
                    ct).ConfigureAwait(false);
                if (!ok)
                {
                    return $"上传分片 {partNumber}/{parts.Count} 失败";
                }
            }

            var joinRes = await _cli.RunSshAsync(
                VmScripts.SessionName,
                ["bash", "/content/vm_pack.sh", "join_parts", remoteJobDir, parts.Count.ToString(), size.ToString(), sha256],
                TimeSpan.FromMinutes(10),
                allowRateLimitRetry: false,
                cancellationToken: ct).ConfigureAwait(false);
            AppendLog(logBuffer, "JOIN_PARTS", joinRes.CombinedOutput);

            return joinRes.IsSuccess && joinRes.StdOut.Contains("JOIN_OK", StringComparison.Ordinal)
                ? null
                : $"云端合并分片失败：{FirstErrorLine(joinRes.CombinedOutput)}";
        }
        finally
        {
            TryDeleteDirectory(partsDir);
        }
    }

    /// <summary>
    /// Packs the job output on the VM, downloads it (in parts when large), verifies size, SHA256, ZIP
    /// structure and page count, restores the original page names, and promotes the file into place.
    /// With <paramref name="partial"/> the pack accepts an unfinished job and the result is saved as
    /// "*_translated_partial".
    /// </summary>
    private async Task<FetchOutcome> FetchResultAsync(
        TranslationTaskItem item,
        PreparedZipResult prepared,
        string remoteJobDir,
        int minPages,
        bool partial,
        StringBuilder logBuffer,
        CancellationToken ct)
    {
        string remotePacked = $"{remoteJobDir}/out.zip";
        var workDir = Path.Combine(Path.GetTempPath(), "manga_gui_dl", item.JobId);
        string? stagingMovePath = null;

        try
        {
            SetStatus(item, TranslationTaskStatus.Packing, partial ? "打包已完成的页面..." : "打包翻译成品并计算校验和...");
            string[] packArgs = partial
                ? ["bash", "/content/vm_pack.sh", "pack", remoteJobDir, "partial"]
                : ["bash", "/content/vm_pack.sh", "pack", remoteJobDir];
            var packRes = await _cli.RunSshAsync(
                VmScripts.SessionName,
                packArgs,
                TimeSpan.FromMinutes(10),
                allowRateLimitRetry: false,
                cancellationToken: ct).ConfigureAwait(false);
            AppendLog(logBuffer, "PACK", packRes.CombinedOutput);

            var packInfo = VmScripts.ParsePackOutput(packRes.StdOut);
            if (!packRes.IsSuccess || packInfo is null || packInfo.SizeBytes <= 0)
            {
                return new FetchOutcome(null, 0, $"云端打包失败：{FirstErrorLine(packRes.CombinedOutput)}");
            }

            if (packInfo.ImageCount.HasValue && packInfo.ImageCount.Value < minPages)
            {
                return new FetchOutcome(
                    null,
                    packInfo.ImageCount.Value,
                    $"云端打包缺页（预期 {minPages} 页，打包仅 {packInfo.ImageCount.Value} 页）",
                    MissingPages: true);
            }

            SetStatus(item, TranslationTaskStatus.Downloading, "下载并校验翻译成品...");
            var finalOutputPath = ZipPreprocessor.ResolveUniqueOutputPath(
                item.SourceZipPath,
                item.TargetOutputDirectory,
                partial ? "_translated_partial" : "_translated");
            var finalDir = Path.GetDirectoryName(finalOutputPath);
            if (!string.IsNullOrWhiteSpace(finalDir))
            {
                Directory.CreateDirectory(finalDir);
            }

            // Always download into an ASCII temp folder: safe for a .cmd shim and for the part files.
            TryDeleteDirectory(workDir);
            Directory.CreateDirectory(workDir);
            var downloaded = Path.Combine(workDir, "out.downloading");

            var downloadError = await DownloadPackedAsync(item, remoteJobDir, remotePacked, packInfo.SizeBytes, workDir, downloaded, logBuffer, ct)
                .ConfigureAwait(false);
            if (downloadError is not null)
            {
                return new FetchOutcome(null, 0, downloadError);
            }

            var verify = ZipPreprocessor.VerifyDownloadedPackage(
                downloaded,
                expectedBytes: packInfo.SizeBytes,
                expectedSha256: packInfo.Sha256,
                expectedMinImageCount: minPages,
                budget: _budget);

            if (!verify.IsValid)
            {
                AppendLog(logBuffer, "VERIFY_FAILED", verify.ErrorMessage ?? "Downloaded archive verification failed");
                return new FetchOutcome(null, 0, $"成品校验失败：{verify.ErrorMessage}");
            }

            // Repacked archives were uploaded as 0001.jpg…; give the pages back their original paths.
            var promoteSource = downloaded;
            if (prepared.OriginalEntryPaths is { Count: > 0 } originalPaths)
            {
                try
                {
                    var restored = Path.Combine(workDir, "out.restored");
                    ZipPreprocessor.RestoreOriginalNames(downloaded, originalPaths, restored);
                    promoteSource = restored;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
                {
                    Log(LogLevel.Warning, $"{item.FileName} 还原原始页名失败，成品保留顺序编号文件名：{ex.Message}");
                }
            }

            stagingMovePath = finalOutputPath + ".staging";
            TryDeleteFile(stagingMovePath);
            File.Copy(promoteSource, stagingMovePath, overwrite: true);
            File.Move(stagingMovePath, finalOutputPath, overwrite: false);
            stagingMovePath = null;

            return new FetchOutcome(finalOutputPath, verify.ImageCount, null);
        }
        finally
        {
            if (stagingMovePath is not null)
            {
                TryDeleteFile(stagingMovePath);
            }

            TryDeleteDirectory(workDir);
        }
    }

    private async Task<string?> DownloadPackedAsync(
        TranslationTaskItem item,
        string remoteJobDir,
        string remotePacked,
        long sizeBytes,
        string workDir,
        string destination,
        StringBuilder logBuffer,
        CancellationToken ct)
    {
        if (sizeBytes <= _transferChunkBytes)
        {
            var dlRes = await _cli.RunAsync(
                ["download", "-s", VmScripts.SessionName, remotePacked, destination],
                ChunkedTransfer.EstimateTimeout(sizeBytes),
                cancellationToken: ct).ConfigureAwait(false);
            AppendLog(logBuffer, "DOWNLOAD", dlRes.CombinedOutput);
            return dlRes.IsSuccess && File.Exists(destination) ? null : "下载成品失败";
        }

        var splitRes = await _cli.RunSshAsync(
            VmScripts.SessionName,
            ["bash", "/content/vm_pack.sh", "split_out", remoteJobDir, _transferChunkBytes.ToString()],
            TimeSpan.FromMinutes(5),
            allowRateLimitRetry: false,
            cancellationToken: ct).ConfigureAwait(false);
        AppendLog(logBuffer, "SPLIT_OUT", splitRes.CombinedOutput);

        int partCount = VmScripts.ParseSplitCount(splitRes.StdOut);
        if (!splitRes.IsSuccess || partCount <= 0)
        {
            return $"云端分片失败：{FirstErrorLine(splitRes.CombinedOutput)}";
        }

        Log(LogLevel.Info, $"{item.FileName} 成品较大，分 {partCount} 片下载后本地合并校验。");
        var localParts = new List<string>(partCount);
        for (int i = 0; i < partCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            int partNumber = i + 1;
            _dispatchToUi(() => item.DetailHint = $"分片下载 {partNumber}/{partCount}...");

            var remotePart = $"{remotePacked}.part_{i:D4}";
            var localPart = Path.Combine(workDir, $"part_{i:D4}");
            bool ok = await TransferWithRetryAsync(
                ["download", "-s", VmScripts.SessionName, remotePart, localPart],
                _transferChunkBytes,
                $"DOWNLOAD_PART {partNumber}/{partCount}",
                logBuffer,
                succeeded: () => File.Exists(localPart),
                ct).ConfigureAwait(false);
            if (!ok)
            {
                return $"下载成品分片 {partNumber}/{partCount} 失败";
            }

            localParts.Add(localPart);
        }

        await Task.Run(() => ChunkedTransfer.ConcatFiles(localParts, destination), ct).ConfigureAwait(false);
        foreach (var part in localParts)
        {
            TryDeleteFile(part);
        }

        return null;
    }

    /// <summary>Runs one part transfer, retrying transient failures up to two more times.</summary>
    private async Task<bool> TransferWithRetryAsync(
        string[] args,
        long bytes,
        string tag,
        StringBuilder logBuffer,
        Func<bool>? succeeded,
        CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var res = await _cli.RunAsync(args, ChunkedTransfer.EstimateTimeout(bytes), cancellationToken: ct)
                .ConfigureAwait(false);
            if (res.IsSuccess && (succeeded is null || succeeded()))
            {
                return true;
            }

            AppendLog(logBuffer, $"{tag} attempt {attempt}/{maxAttempts} failed", res.CombinedOutput);
            if (attempt < maxAttempts)
            {
                await Task.Delay(_transferRetryDelay, ct).ConfigureAwait(false);
            }
        }

        return false;
    }

    /// <summary>
    /// A job that ended without all pages still fails, but the pages it did produce are packed and
    /// saved as "*_translated_partial" so the GPU time already spent is not thrown away.
    /// </summary>
    private async Task SalvagePartialResultAsync(
        TranslationTaskItem item,
        PreparedZipResult prepared,
        string remoteJobDir,
        string shortfall,
        int producedPages,
        int expectedPages,
        StringBuilder logBuffer,
        CancellationToken ct)
    {
        if (producedPages <= 0 || !ShouldRunRemoteCleanup())
        {
            MarkFailed(item, shortfall, TailLines(logBuffer.ToString(), 100));
            return;
        }

        Log(LogLevel.Warning, $"{item.FileName}：{shortfall}，尝试取回已完成的页面...");
        var fetched = await FetchResultAsync(item, prepared, remoteJobDir, minPages: 1, partial: true, logBuffer, ct)
            .ConfigureAwait(false);
        if (fetched.Error is not null)
        {
            AppendLog(logBuffer, "SALVAGE_FAILED", fetched.Error);
            MarkFailed(item, $"{shortfall}；部分成品未能取回", TailLines(logBuffer.ToString(), 100));
            return;
        }

        var partialPath = fetched.FinalPath!;
        _dispatchToUi(() => item.OutputZipPath = partialPath);
        MarkFailed(
            item,
            $"{shortfall}；已保存部分成品 {fetched.ImageCount}/{expectedPages} 页：{partialPath}",
            TailLines(logBuffer.ToString(), 100));
    }

    private bool ShouldRunRemoteCleanup() =>
        _session.HasActiveVm
        && _session.State is not (SessionState.Stopping or SessionState.AutoStopping or SessionState.Idle);

    private async Task HandleTaskTimeoutAsync(
        TranslationTaskItem item,
        PreparedZipResult prepared,
        string remoteJobDir,
        StringBuilder logBuffer,
        string summary,
        int producedPages,
        int expectedPages,
        CancellationToken ct)
    {
        AppendLog(logBuffer, "TIMEOUT", summary);
        bool jobStopped = await TryStopRemoteJobAndPauseIfNeededAsync(remoteJobDir, logBuffer, "翻译超时").ConfigureAwait(false);
        if (jobStopped)
        {
            // The job is confirmed dead, so the pages it finished can be packed safely.
            await SalvagePartialResultAsync(item, prepared, remoteJobDir, summary, producedPages, expectedPages, logBuffer, ct)
                .ConfigureAwait(false);
            return;
        }

        MarkFailed(item, summary, TailLines(logBuffer.ToString(), 100));
    }

    /// <summary>Returns true when the remote job is confirmed stopped; otherwise pauses the queue.</summary>
    private async Task<bool> TryStopRemoteJobAndPauseIfNeededAsync(string remoteJobDir, StringBuilder logBuffer, string trigger)
    {
        try
        {
            var stopRes = await _cli.RunSshAsync(
                VmScripts.SessionName,
                ["bash", "/content/vm_pack.sh", "stop_job", remoteJobDir],
                TimeSpan.FromSeconds(15),
                allowRateLimitRetry: false,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            AppendLog(logBuffer, "STOP_JOB", stopRes.CombinedOutput);

            if (!stopRes.IsSuccess || !stopRes.StdOut.Contains("STOP_JOB_OK", StringComparison.Ordinal))
            {
                PauseQueue($"由于{trigger}且无法确认云端旧翻译进程已退出，已暂停后续任务以防 GPU 冲突。请核查或重启会话后再继续。");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            AppendLog(logBuffer, "STOP_JOB_ERR", ex.Message);
            PauseQueue($"由于{trigger}且终止云端旧进程失败（{ex.Message}），已暂停后续任务以防 GPU 冲突。");
            return false;
        }
    }

    private void PauseQueue(string reason)
    {
        lock (_queueLock)
        {
            _isQueuePaused = true;
            _pauseReason = reason;
        }
        Log(LogLevel.Warning, reason);
        QueueChanged?.Invoke();
    }

    private async Task TryCleanRemoteJobAsync(string remoteJobDir)
    {
        try
        {
            var cleanRes = await _cli.RunSshAsync(
                VmScripts.SessionName,
                ["bash", "/content/vm_pack.sh", "clean", remoteJobDir],
                TimeSpan.FromSeconds(30),
                allowRateLimitRetry: false,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            if (!cleanRes.IsSuccess || !cleanRes.StdOut.Contains("CLEAN_OK", StringComparison.Ordinal))
            {
                Log(LogLevel.Warning, $"云端临时目录清理未确认成功（{remoteJobDir}）：{FirstErrorLine(cleanRes.CombinedOutput)}");
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, $"云端临时目录清理异常（{remoteJobDir}）：{ex.Message}");
        }
    }

    private async Task AppendRemoteJobLogTailAsync(string remoteJobDir, StringBuilder logBuffer, CancellationToken ct)
    {
        try
        {
            var tailRes = await _cli.RunSshAsync(
                VmScripts.SessionName,
                ["bash", "/content/vm_pack.sh", "tail_log", remoteJobDir, "100"],
                TimeSpan.FromSeconds(30),
                allowRateLimitRetry: false,
                cancellationToken: ct).ConfigureAwait(false);
            AppendLog(logBuffer, "TRANSLATE_LOG_TAIL", tailRes.CombinedOutput);
        }
        catch (Exception ex)
        {
            AppendLog(logBuffer, "TRANSLATE_LOG_TAIL_ERR", ex.Message);
        }
    }

    private void SetStatus(TranslationTaskItem item, TranslationTaskStatus status, string hint)
    {
        _dispatchToUi(() =>
        {
            item.Status = status;
            item.DetailHint = hint;
        });
    }

    private void MarkFailed(TranslationTaskItem item, string summary, string rawLog)
    {
        var tailed = TailLines(rawLog, 100);
        _dispatchToUi(() =>
        {
            item.ErrorSummary = summary;
            item.RawLog = tailed;
            item.DetailHint = summary;
            item.Status = TranslationTaskStatus.Failed;
        });
        Log(LogLevel.Error, $"任务失败：{item.FileName} — {summary}");
        RawOutputRecorded?.Invoke($"任务失败原始输出 · {item.FileName}", tailed);
    }

    private void Log(LogLevel level, string message) => LogMessage?.Invoke(level, message);

    private static void AppendLog(StringBuilder sb, string tag, string content)
    {
        sb.AppendLine($"=== {tag} ({DateTime.Now:HH:mm:ss}) ===");
        if (!string.IsNullOrWhiteSpace(content))
        {
            sb.AppendLine(content);
        }
    }

    private static string FirstErrorLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "未知错误";
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var line = lines.Length > 0 ? lines[^1].Trim() : text.Trim();
        return line.Length > 120 ? line[..120] + "..." : line;
    }

    public static string TailLines(string text, int maxLines)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length <= maxLines)
        {
            return string.Join(Environment.NewLine, lines);
        }

        return string.Join(Environment.NewLine, lines[^maxLines..]);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignore temp cleanup errors.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore temp cleanup errors.
        }
    }
}
