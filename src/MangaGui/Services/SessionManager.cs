using MangaGui.Models;

namespace MangaGui.Services;

public sealed record StopResult(bool VerifiedStopped, string Message, string? RawOutput = null, int RemainingUnnamedAssignments = 0);

public sealed record StartupProbeResult(
    StartupProbeOutcome Outcome,
    string Message,
    SessionRecord? Record = null,
    VmStatusSnapshot? Status = null);

/// <summary>
/// Orchestrates the Colab L4 runtime lifecycle state machine, atomic epoch-guarded transitions (A-03),
/// three-state session verification (A-01), pessimistic billing protection & memory-first persistence (A-02),
/// script preflight validation (A-10), and llama-only readiness decoupling (A-11).
/// </summary>
public sealed class SessionManager
{
    private readonly IColabCli _cli;
    private readonly VmScripts _scripts;
    private readonly SessionStore _store;
    private readonly BillingTimer _billing;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _stateLock = new();
    private readonly object _stopLock = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private static readonly TimeSpan[] DefaultAutoStopRetryDelays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5)
    ];

    private readonly TimeSpan[] _autoStopRetryDelays;
    private readonly Func<IReadOnlyList<string>>? _environmentIssuesProvider;
    private readonly AccountManager? _accounts;

    private long _operationEpoch;
    private CancellationTokenSource? _activeLifecycleCts;
    private Task<StopResult>? _activeStopTask;
    private Task<StopResult>? _activeAutoStopLoop;
    private Task<ColabResult>? _pendingCreateTask;
    private bool _isProbingStartup;
    private string? _unnamedAssignmentWarning;

    private SessionState _state = SessionState.Idle;
    private DeployStage _deployStage = DeployStage.None;
    private bool _hasActiveVm;
    private string _statusDetail = "未运行";
    private string? _lastError;
    private string? _lastRawOutput;
    private string? _persistenceWarning;
    private VmStatusSnapshot _latestStatus = new();
    private string? _activeSessionAccountId;
    private string _activeSessionAccountName = ColabAccountContext.Default.DisplayName;
    private double? _balanceBeforeStartCu;
    private string _activeRequestedGpuTier = GpuTiers.L4;
    private string? _activeDetectedGpuProfile;
    private string? _activeDetectedGpuName;
    private string _activeInpaintMode = InpaintModes.LamaLarge;
    private bool _hasLiveCalibratedRate;
    private List<OtherAccountSessionWarning> _otherAccountWarnings = [];

    public event Action? StateChanged;
    public event Action<LogLevel, string>? LogMessage;

    /// <summary>Raised when a failure records raw CLI output (title, content) for the persistent log.</summary>
    public event Action<string, string>? RawOutputRecorded;

    /// <summary>
    /// Raised after an automatic stop could not be verified and another attempt has been scheduled
    /// (attempt number, delay before it runs).
    /// </summary>
    public event Action<int, TimeSpan>? AutoStopRetryScheduled;

    public SessionManager(
        IColabCli cli,
        VmScripts scripts,
        SessionStore store,
        BillingTimer billing,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan[]? autoStopRetryDelays = null,
        Func<IReadOnlyList<string>>? environmentIssuesProvider = null,
        AccountManager? accounts = null)
    {
        _cli = cli;
        _scripts = scripts;
        _store = store;
        _billing = billing;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _autoStopRetryDelays = autoStopRetryDelays is { Length: > 0 } ? autoStopRetryDelays : DefaultAutoStopRetryDelays;
        _environmentIssuesProvider = environmentIssuesProvider;
        _accounts = accounts;
    }

    public SessionState State { get { lock (_stateLock) return _state; } }
    public DeployStage DeployStage { get { lock (_stateLock) return _deployStage; } }
    public bool HasActiveVm { get { lock (_stateLock) return _hasActiveVm; } }
    public bool IsProbingStartup { get { lock (_stateLock) return _isProbingStartup; } }
    public string StatusDetail { get { lock (_stateLock) return _statusDetail; } }
    public string? LastError { get { lock (_stateLock) return _lastError; } }
    public string? LastRawOutput { get { lock (_stateLock) return _lastRawOutput; } }
    public string? PersistenceWarning { get { lock (_stateLock) return _persistenceWarning; } }
    public string? UnnamedAssignmentWarning { get { lock (_stateLock) return _unnamedAssignmentWarning; } }
    public VmStatusSnapshot LatestStatus { get { lock (_stateLock) return _latestStatus; } }
    public string? ActiveSessionAccountId { get { lock (_stateLock) return _activeSessionAccountId; } }
    public string ActiveSessionAccountName { get { lock (_stateLock) return _activeSessionAccountName; } }
    public string ActiveRequestedGpuTier { get { lock (_stateLock) return _activeRequestedGpuTier; } }
    public string? ActiveDetectedGpuProfile { get { lock (_stateLock) return _activeDetectedGpuProfile; } }
    public string? ActiveDetectedGpuName { get { lock (_stateLock) return _activeDetectedGpuName; } }
    public string ActiveInpaintMode { get { lock (_stateLock) return _activeInpaintMode; } }
    public IReadOnlyList<OtherAccountSessionWarning> OtherAccountWarnings { get { lock (_stateLock) return _otherAccountWarnings.ToList(); } }
    public AccountManager? Accounts => _accounts;
    public BillingTimer Billing => _billing;

    /// <summary>Local prerequisites that would make a paid runtime unusable (empty when all are met).</summary>
    public IReadOnlyList<string> GetEnvironmentIssues() => _environmentIssuesProvider?.Invoke() ?? [];

    /// <summary>
    /// Always called on app launch regardless of whether session.json exists,
    /// ensuring orphaned billing sessions are detected immediately and never silently assumed stopped.
    /// </summary>
    public async Task<StartupProbeResult> ProbeOnStartupAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CancellationTokenSource? linkedCts = null;
        long myEpoch;
        try
        {
            myEpoch = BeginLifecycleOperation(cancellationToken, out linkedCts, isStartupProbe: true);
            var ct = linkedCts.Token;

            var loadResult = _store.LoadSessionWithStatus();
            var savedRecord = loadResult.Record;
            if (loadResult.Status is StoreLoadStatus.Corrupted or StoreLoadStatus.AccessDenied)
            {
                SetPersistenceWarning(loadResult.ErrorMessage);
                Log(LogLevel.Warning, loadResult.ErrorMessage ?? "本地 session.json 异常。");
            }
            else
            {
                SetPersistenceWarning(null);
            }

            // A local record means a runtime may still be billing: arm the close guard and timer
            // before the first CLI round-trip instead of after it.
            if (savedRecord is not null)
            {
                var savedTier = GpuTiers.Normalize(savedRecord.RequestedGpu);
                var savedRate = savedRecord.BillingCuPerHour is > 0
                    ? savedRecord.BillingCuPerHour.Value
                    : !string.IsNullOrWhiteSpace(savedRecord.DetectedGpuProfile)
                        ? GpuProfiles.DefaultCuPerHourForProfile(savedRecord.DetectedGpuProfile)
                        : GpuTiers.GetSpec(savedTier).DefaultCuPerHour;
                SetActiveGpuTracking(savedTier, savedRecord.DetectedGpuProfile, savedRecord.DetectedGpuName, savedRecord.InpaintMode, liveRate: savedRecord.BillingCuPerHour is > 0);
                _billing.Start(savedRecord.StartedAt, savedRecord.PlannedMinutes, savedRecord.AutoStop, savedRecord.StartTimeUnknown, savedRate);
                var bound = BindSavedRecordAccount(savedRecord, out var missingAccountError);
                if (!bound)
                {
                    UpdateStateIfEpoch(myEpoch, SessionState.Error, DeployStage.None, hasActiveVm: true,
                        detail: "上次会话归属账号凭据缺失（拒绝自动放行）",
                        error: missingAccountError);
                    Log(LogLevel.Error, missingAccountError!);
                    return new StartupProbeResult(StartupProbeOutcome.ProbeFailed, missingAccountError!, savedRecord);
                }

                UpdateStateIfEpoch(myEpoch, SessionState.Idle, DeployStage.None, hasActiveVm: true,
                    detail: $"正在核实上次会话（{ActiveSessionAccountName}）是否仍在计费...");
            }
            else if (_accounts is not null && _accounts.GetEnabledAccounts().Count > 1)
            {
                // Multi-account idle startup: scan ALL enabled accounts concurrently so no orphan
                // session on a secondary account can silently keep billing.
                return await ProbeAllAccountsWhenNoLocalRecordAsync(myEpoch, loadResult, ct).ConfigureAwait(false);
            }
            else if (_accounts is not null)
            {
                var singleCtx = _accounts.ResolveContextOrDefault(_accounts.SelectedAccountId);
                _cli.ActiveAccount = singleCtx;
                SetActiveAccountTracking(singleCtx.AccountId, singleCtx.DisplayName, balanceBeforeStartCu: null);
            }

            Log(LogLevel.Info, "启动探测：正在检查云端 Colab 会话状态...");
            var sessionsRes = await _cli.RunAsync(
                ["sessions"],
                TimeSpan.FromSeconds(30),
                cancellationToken: ct).ConfigureAwait(false);

            if (!IsEpochCurrent(myEpoch))
            {
                return new StartupProbeResult(StartupProbeOutcome.ProbeFailed, "启动探测已被更新的状态操作中断。", savedRecord);
            }

            var query = VmScripts.ParseSessionPresence(sessionsRes);

            if (query.Presence == SessionPresence.Unknown)
            {
                var err = $"无法确认 Colab 会话状态：{query.Reason}";
                Log(LogLevel.Warning, err);

                if (savedRecord is not null)
                {
                    var savedRate = savedRecord.BillingCuPerHour is > 0
                        ? savedRecord.BillingCuPerHour.Value
                        : GpuTiers.GetSpec(savedRecord.RequestedGpu).DefaultCuPerHour;
                    _billing.Start(savedRecord.StartedAt, savedRecord.PlannedMinutes, savedRecord.AutoStop, savedRecord.StartTimeUnknown, savedRate);
                    UpdateStateIfEpoch(myEpoch, SessionState.Error, DeployStage.None, hasActiveVm: true,
                        detail: "会话状态未知（拒绝自动放行）", error: err, rawOutput: query.RawOutput);
                }
                else if (loadResult.Status is StoreLoadStatus.Corrupted or StoreLoadStatus.AccessDenied)
                {
                    _billing.Start(_utcNow(), plannedMinutes: null, autoStop: false, startTimeUnknown: true);
                    UpdateStateIfEpoch(myEpoch, SessionState.Error, DeployStage.None, hasActiveVm: true,
                        detail: "本地记录损坏且云端状态未知", error: $"{loadResult.ErrorMessage}\n{err}", rawOutput: query.RawOutput);
                }
                else
                {
                    UpdateStateIfEpoch(myEpoch, SessionState.Error, DeployStage.None, hasActiveVm: false,
                        detail: "云端会话状态未知",
                        error: $"{err}。云端可能有正在计费的会话，请点「复查会话」重试；确认没有会话后可直接启动。",
                        rawOutput: query.RawOutput);
                }

                return new StartupProbeResult(StartupProbeOutcome.ProbeFailed, err, savedRecord);
            }

            NoteUnnamedAssignments(query);

            if (query.Presence == SessionPresence.ExplicitlyAbsent)
            {
                var clearedAccountId = savedRecord?.AccountId ?? ActiveSessionAccountId;
                if (savedRecord is not null || loadResult.Status == StoreLoadStatus.Corrupted)
                {
                    _billing.Stop();
                    ClearActiveAccountTracking();
                    UpdateStateIfEpoch(myEpoch, SessionState.Idle, DeployStage.None, hasActiveVm: false, detail: "未运行");
                    TryDeleteSessionWithWarning();
                    Log(LogLevel.Info, "云端确认无活动会话，已清理本地过期记录。");

                    if (_accounts is not null && _accounts.GetEnabledAccounts().Count > 1)
                    {
                        await ScanOtherAccountsForOrphansAsync(clearedAccountId, ct).ConfigureAwait(false);
                    }

                    return new StartupProbeResult(StartupProbeOutcome.CleanedStaleRecord, "本地会话记录已过期，已清理。");
                }

                ClearActiveAccountTracking();
                UpdateStateIfEpoch(myEpoch, SessionState.Idle, DeployStage.None, hasActiveVm: false, detail: "未运行");
                Log(LogLevel.Info, "当前无运行中的 Colab 会话。");
                return new StartupProbeResult(StartupProbeOutcome.Idle, "当前无活动会话。");
            }

            // SessionPresence.Present
            if (savedRecord is null)
            {
                var orphanAcctName = ActiveSessionAccountName;
                _billing.Start(_utcNow(), plannedMinutes: null, autoStop: false, startTimeUnknown: true);
                UpdateStateIfEpoch(myEpoch, SessionState.Error, DeployStage.None, hasActiveVm: true,
                    detail: $"检测到孤儿会话（{orphanAcctName} · 正在计费）",
                    error: $"云端账号「{orphanAcctName}」存在未由本应用记录的 manga 会话，请选择接管或立即停止计费。");
                Log(LogLevel.Warning, $"检测到云端孤儿会话 manga（账号：{orphanAcctName}，正在计费，起始时间未知）！");
                return new StartupProbeResult(StartupProbeOutcome.OrphanSessionDetected, $"检测到云端正在运行的孤儿会话 manga（{orphanAcctName}）。");
            }

            // savedRecord != null && Present
            var recoveredRate = savedRecord.BillingCuPerHour is > 0
                ? savedRecord.BillingCuPerHour.Value
                : !string.IsNullOrWhiteSpace(savedRecord.DetectedGpuProfile)
                    ? GpuProfiles.DefaultCuPerHourForProfile(savedRecord.DetectedGpuProfile)
                    : GpuTiers.GetSpec(savedRecord.RequestedGpu).DefaultCuPerHour;
            _billing.Start(savedRecord.StartedAt, savedRecord.PlannedMinutes, savedRecord.AutoStop, savedRecord.StartTimeUnknown, recoveredRate);
            Log(LogLevel.Info, $"检测到上次会话（{ActiveSessionAccountName}）仍在运行，正在核实翻译服务健康状态...");

            if (_accounts is not null && _accounts.GetEnabledAccounts().Count > 1)
            {
                await ScanOtherAccountsForOrphansAsync(ActiveSessionAccountId, ct).ConfigureAwait(false);
                if (!IsEpochCurrent(myEpoch))
                {
                    return new StartupProbeResult(StartupProbeOutcome.ProbeFailed, "启动恢复已被新操作中断。", savedRecord);
                }
            }

            // Re-upload the bundled helper scripts BEFORE the health check so an older VM
            // image always answers with the current ENGINE-aware vm_status.sh (D9).
            var refreshRes = await _scripts.UploadBundledAssetsOnlyAsync(_cli, ct).ConfigureAwait(false);
            if (!IsEpochCurrent(myEpoch))
            {
                return new StartupProbeResult(StartupProbeOutcome.ProbeFailed, "启动恢复已被新操作中断。", savedRecord);
            }

            if (!refreshRes.IsSuccess)
            {
                Log(LogLevel.Warning, $"接管前刷新管理脚本失败（不影响健康检查，将沿用 VM 现有脚本）：{TrimOneLine(refreshRes.CombinedOutput)}");
            }

            // F6：进程启动时本地队列必然为空，云端残留 job_* 都是被杀掉的前端遗留——
            // 不清掉会与本次新任务并发占用 GPU。不要从 RetryDeploy 或可能有活动任务的路径调用。
            await CleanStaleRemoteJobsAsync(ct).ConfigureAwait(false);

            var status = await CheckHealthWithFallbackAsync(ct).ConfigureAwait(false);
            if (!IsEpochCurrent(myEpoch))
            {
                return new StartupProbeResult(StartupProbeOutcome.ProbeFailed, "启动恢复已被新操作中断。", savedRecord);
            }

            if (status.IsReadyForTranslation)
            {
                lock (_stateLock)
                {
                    _latestStatus = status;
                }
                var mitNote = status.DeployedEngine == TranslationEngines.Mit && !status.MitOk
                    ? "（mit :8001 未响应，不影响本工具翻译）" : "";
                UpdateStateIfEpoch(myEpoch, SessionState.Ready, DeployStage.Completed, hasActiveVm: true,
                    detail: $"已恢复连接，服务就绪{mitNote}");
                Log(LogLevel.Success, $"已从上次会话恢复就绪状态，沿用原始计费时间。{mitNote}");
                return new StartupProbeResult(StartupProbeOutcome.RecoveredReady, "已恢复运行中的服务。", savedRecord, status);
            }

            lock (_stateLock)
            {
                _latestStatus = status;
            }
            UpdateStateIfEpoch(myEpoch, SessionState.Error, DeployStage.None, hasActiveVm: true,
                detail: "Runtime 运行中，服务未就绪",
                error: "检测到 VM 仍在计费但核心模型服务 (llama :6006) 未就绪，可点击「重试部署」幂等恢复或立即停止计费。");
            Log(LogLevel.Warning, "VM 仍在运行，但 llama-server (:6006) 尚未就绪，等待用户选择恢复部署或停止。");
            return new StartupProbeResult(StartupProbeOutcome.RecoveredNeedsDeploy, "Runtime 存活但服务未就绪。", savedRecord, status);
        }
        catch (OperationCanceledException)
        {
            return new StartupProbeResult(StartupProbeOutcome.ProbeFailed, "启动探测已取消。");
        }
        finally
        {
            EndLifecycleOperation(linkedCts, isStartupProbe: true);
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Re-queries `colab sessions` on demand (or after repeated SSH health failures) to detect externally terminated VMs (A-07).
    /// </summary>
    public async Task<SessionQueryResult> ReprobeSessionStatusAsync(CancellationToken cancellationToken = default)
    {
        var epochAtStart = Interlocked.Read(ref _operationEpoch);
        var sessionsRes = await _cli.RunAsync(["sessions"], TimeSpan.FromSeconds(30), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var query = VmScripts.ParseSessionPresence(sessionsRes);

        if (query.Presence != SessionPresence.Unknown)
        {
            NoteUnnamedAssignments(query);
        }

        if (query.Presence == SessionPresence.ExplicitlyAbsent)
        {
            // Reset only if no lifecycle operation started while the query was in flight; a deploy that
            // began meanwhile has already armed billing protection that this result must not clear.
            bool didReset = false;
            StoreOperationResult? deleteResult = null;
            lock (_stateLock)
            {
                if (Interlocked.Read(ref _operationEpoch) == epochAtStart
                    && _state is not (SessionState.Deploying or SessionState.Stopping or SessionState.AutoStopping))
                {
                    _billing.Stop();
                    _latestStatus = new VmStatusSnapshot();
                    _state = SessionState.Idle;
                    _deployStage = DeployStage.None;
                    _hasActiveVm = false;
                    _statusDetail = "云端会话已结束";
                    _lastError = null;
                    deleteResult = _store.TryDeleteSession();
                    didReset = true;
                }
            }

            if (didReset)
            {
                SetPersistenceWarning(deleteResult is { Success: false } ? deleteResult.ErrorMessage : null);
                Log(LogLevel.Warning, "复查确认云端已无活动 manga 会话（可能已被 Colab 自动回收），已结束计费计时。");
                StateChanged?.Invoke();
            }
        }
        else if (query.Presence == SessionPresence.Present)
        {
            Log(LogLevel.Info, "复查结果：云端 manga 会话仍在运行（正在计费）。");
        }
        else
        {
            Log(LogLevel.Warning, $"复查会话状态未明：{query.Reason}");
        }

        return query;
    }

    /// <summary>
    /// Adopts an existing orphan session by persisting a fresh session record with user-chosen duration,
    /// then checks health (or triggers idempotent deploy if llama-server is down).
    /// </summary>
    public async Task<bool> AdoptOrphanSessionAsync(int? plannedMinutes, bool autoStop, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CancellationTokenSource? linkedCts = null;
        long myEpoch;
        bool needFallbackDeploy = false;

        try
        {
            myEpoch = BeginLifecycleOperation(cancellationToken, out linkedCts, isStartupProbe: false);
            var ct = linkedCts.Token;

            var activeCtx = _cli.ActiveAccount;
            var balanceBefore = _accounts?.GetQuota(activeCtx.AccountId).CurrentBalanceCu;
            SetActiveAccountTracking(activeCtx.AccountId, activeCtx.DisplayName, balanceBefore);

            var record = new SessionRecord
            {
                SessionName = VmScripts.SessionName,
                StartedAt = _utcNow(),
                PlannedMinutes = plannedMinutes,
                AutoStop = autoStop,
                StartTimeUnknown = true,
                AccountId = activeCtx.AccountId,
                AccountName = activeCtx.DisplayName,
                BalanceBeforeStartCu = balanceBefore
            };

            // Memory-first protection before disk persistence (A-02)
            _billing.Start(record.StartedAt, record.PlannedMinutes, record.AutoStop, startTimeUnknown: true);
            UpdateStateIfEpoch(myEpoch, SessionState.Deploying, DeployStage.UploadingScripts, hasActiveVm: true,
                detail: "正在接管会话并上传管理脚本...");
            TrySaveSessionWithWarning(record);

            var uploadRes = await _scripts.UploadBundledAssetsOnlyAsync(_cli, ct).ConfigureAwait(false);
            if (!IsEpochCurrent(myEpoch)) return false;

            if (!uploadRes.IsSuccess)
            {
                UpdateStateIfEpoch(myEpoch, SessionState.Error, DeployStage.None, hasActiveVm: true,
                    detail: "接管上传管理脚本失败", error: TrimOneLine(uploadRes.CombinedOutput), rawOutput: uploadRes.CombinedOutput);
                return false;
            }

            // F6：同恢复路径——进程启动时本地队列为空，云端 job_* 均为遗留，先清理再健康检查。
            await CleanStaleRemoteJobsAsync(ct).ConfigureAwait(false);

            var status = await PollStatusAsync(null, ct).ConfigureAwait(false);
            if (!IsEpochCurrent(myEpoch)) return false;

            if (status is not null && status.IsReadyForTranslation)
            {
                ApplyDetectedGpuFromStatus(status, persistIfChanged: true);
                var mitNote = status.DeployedEngine == TranslationEngines.Mit && !status.MitOk
                    ? "（mit :8001 未响应，不影响翻译）" : "";
                UpdateStateIfEpoch(myEpoch, SessionState.Ready, DeployStage.Completed, hasActiveVm: true,
                    detail: $"已接管现有会话{mitNote}");
                Log(LogLevel.Success, $"已成功接管云端 manga 会话，核心服务健康。{mitNote}");
                return true;
            }

            Log(LogLevel.Info, "现有会话核心模型服务未就绪，将执行幂等部署补齐...");
            needFallbackDeploy = true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            EndLifecycleOperation(linkedCts, isStartupProbe: false);
            _lifecycleGate.Release();
        }

        if (needFallbackDeploy)
        {
            return await StartOrDeployAsync(plannedMinutes, autoStop, preserveExistingStartTime: true, cancellationToken)
                .ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Preflights required scripts (A-10), checks/creates requested GPU runtime with pessimistic billing protection (A-02),
    /// uploads scripts, streams adaptive vm_setup.sh, and enters Ready once llama :6006 is healthy (A-11).
    /// Epoch-guarded so StopSessionAsync preempts cleanly without stale state write-back (A-03).
    /// </summary>
    public async Task<bool> StartOrDeployAsync(
        int? plannedMinutes,
        bool autoStop,
        bool preserveExistingStartTime = false,
        CancellationToken cancellationToken = default,
        IReadOnlyList<ColabAccountProfile>? candidateAccounts = null,
        bool allowFailover = false,
        string? requestedGpuTier = null,
        string? inpaintMode = null,
        string? g4SubMode = null)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CancellationTokenSource? linkedCts = null;
        long myEpoch;

        try
        {
            myEpoch = BeginLifecycleOperation(cancellationToken, out linkedCts, isStartupProbe: false);
            var ct = linkedCts.Token;

            var engine = ConfiguredEngine;
            var existingOnDisk = preserveExistingStartTime ? _store.LoadSession() : null;
            var effectiveGpuTier = GpuTiers.Normalize(
                !string.IsNullOrWhiteSpace(requestedGpuTier)
                    ? requestedGpuTier
                    : !string.IsNullOrWhiteSpace(existingOnDisk?.RequestedGpu)
                        ? existingOnDisk.RequestedGpu
                        : ConfiguredGpuTier);
            var tierSpec = GpuTiers.GetSpec(effectiveGpuTier);
            var effectiveInpaint = GpuTiers.ResolveEffectiveInpaintMode(
                effectiveGpuTier,
                !string.IsNullOrWhiteSpace(inpaintMode)
                    ? inpaintMode
                    : !string.IsNullOrWhiteSpace(existingOnDisk?.InpaintMode)
                        ? existingOnDisk.InpaintMode
                        : ConfiguredInpaintMode);
            var effectiveG4Sub = G4SubModes.Normalize(g4SubMode ?? ConfiguredG4SubMode);
            var setupProfileHint = GpuTiers.ResolveSetupProfileHint(effectiveGpuTier, effectiveG4Sub);

            // 0. Preflight scripts BEFORE creating any billing GPU runtime (A-10)
            if (!_scripts.HasRequiredFilesForEngine(engine))
            {
                _scripts.RefreshLocations();
            }

            if (!_scripts.HasRequiredFilesForEngine(engine))
            {
                var missing = string.Join("；", _scripts.GetMissingScriptsReport(engine));
                FailDeployIfEpoch(myEpoch, $"启动前校验失败（未申请 GPU，未产生计费）：{missing}", missing, vmAlive: HasActiveVm);
                return false;
            }

            // `colab ssh` needs a local ssh client and an Ed25519/ECDSA key; check before paying for a runtime.
            var environmentIssues = _environmentIssuesProvider?.Invoke();
            if (environmentIssues is { Count: > 0 })
            {
                var issues = string.Join("；", environmentIssues);
                var prefix = HasActiveVm ? "部署前校验失败" : "启动前校验失败（未申请 GPU，未产生计费）";
                FailDeployIfEpoch(myEpoch, $"{prefix}：{issues}", issues, vmAlive: HasActiveVm);
                return false;
            }

            // Resolve candidate account(s): redeploy on an active VM is strictly pinned to the
            // session's owning account; fresh starts can iterate ranked candidates with safe failover.
            var resolvedCandidates = ResolveStartCandidates(candidateAccounts, allowFailover, out bool canFailover);
            if (resolvedCandidates.Count == 0)
            {
                FailDeployIfEpoch(myEpoch, "启动前校验失败（未申请 GPU，未产生计费）：没有可用的 Colab 账号（请检查账号配置或重新登录）。", null, vmAlive: HasActiveVm);
                return false;
            }

            SessionRecord? provisionalRecord = null;
            for (int idx = 0; idx < resolvedCandidates.Count; idx++)
            {
                var candidateCtx = resolvedCandidates[idx];
                bool hasNextCandidate = canFailover && idx + 1 < resolvedCandidates.Count;

                _cli.ActiveAccount = candidateCtx;
                var balBefore = _accounts?.GetQuota(candidateCtx.AccountId).CurrentBalanceCu;
                SetActiveAccountTracking(candidateCtx.AccountId, candidateCtx.DisplayName, balBefore);

                var acctTag = _accounts is { HasMultipleAccounts: true } ? $"[{candidateCtx.DisplayName}] " : string.Empty;
                UpdateStateIfEpoch(myEpoch, SessionState.Deploying, DeployStage.CheckingSession, hasActiveVm: HasActiveVm,
                    detail: $"{acctTag}正在检查云端会话...");
                Log(LogLevel.Info, $"{acctTag}开始启动流程：检查现有 Colab 会话...");

                // 1. Check whether session 'manga' already exists
                var sessionsRes = await _cli.RunAsync(["sessions"], TimeSpan.FromSeconds(30), cancellationToken: ct)
                    .ConfigureAwait(false);
                if (!IsEpochCurrent(myEpoch)) return false;

                var sessionQuery = VmScripts.ParseSessionPresence(sessionsRes);
                if (sessionQuery.Presence == SessionPresence.Unknown)
                {
                    if (hasNextCandidate && !HasActiveVm)
                    {
                        var nextName = resolvedCandidates[idx + 1].DisplayName;
                        Log(LogLevel.Warning, $"{acctTag}检查会话失败（{sessionQuery.Reason}，未申请 GPU），自动顺延尝试下一个账号「{nextName}」...");
                        continue;
                    }

                    FailDeployIfEpoch(myEpoch, $"{acctTag}检查会话失败：{sessionQuery.Reason}", sessionQuery.RawOutput, vmAlive: HasActiveVm);
                    return false;
                }

                NoteUnnamedAssignments(sessionQuery);
                int unnamedBeforeCreate = sessionQuery.UnnamedAssignments;

                bool alreadyExists = sessionQuery.Presence == SessionPresence.Present;
                var existingRecord = preserveExistingStartTime ? _store.LoadSession() : null;
                var startedAt = existingRecord?.StartedAt ?? _utcNow();
                var initialRate = existingRecord?.BillingCuPerHour is > 0
                    ? existingRecord.BillingCuPerHour.Value
                    : tierSpec.DefaultCuPerHour;
                var record = new SessionRecord
                {
                    SessionName = VmScripts.SessionName,
                    StartedAt = startedAt,
                    PlannedMinutes = plannedMinutes,
                    AutoStop = autoStop,
                    StartTimeUnknown = existingRecord?.StartTimeUnknown ?? (alreadyExists && existingRecord is null),
                    AccountId = candidateCtx.AccountId,
                    AccountName = candidateCtx.DisplayName,
                    BalanceBeforeStartCu = existingRecord?.BalanceBeforeStartCu ?? balBefore,
                    RequestedGpu = effectiveGpuTier,
                    DetectedGpuProfile = existingRecord?.DetectedGpuProfile,
                    DetectedGpuName = existingRecord?.DetectedGpuName,
                    BillingCuPerHour = initialRate,
                    InpaintMode = effectiveInpaint
                };

                if (!alreadyExists)
                {
                    // A-02: Pessimistic billing protection BEFORE calling `colab new`
                    SetActiveGpuTracking(effectiveGpuTier, null, null, effectiveInpaint, liveRate: false);
                    UpdateStateIfEpoch(myEpoch, SessionState.Deploying, DeployStage.CreatingRuntime, hasActiveVm: true,
                        detail: $"{acctTag}正在申请 {tierSpec.ShortName} GPU Runtime...");
                    TrySaveSessionWithWarning(record);
                    Log(LogLevel.Info, $"{acctTag}未发现活动会话，正在创建 {tierSpec.ShortName} GPU Runtime（参考 {tierSpec.RateDisplay}）...");

                    // The CLI registers the session name locally only after the server has assigned the runtime.
                    // Killing it in between leaves a billing runtime that cannot be stopped by name, so the
                    // subprocess is never cancelled here; StopSessionAsync waits for it instead.
                    var createTask = _cli.RunAsync(
                        GpuTiers.BuildCreateArgs(effectiveGpuTier, VmScripts.SessionName),
                        TimeSpan.FromMinutes(5),
                        cancellationToken: CancellationToken.None);
                    lock (_stateLock)
                    {
                        _pendingCreateTask = createTask;
                    }

                    ColabResult newRes;
                    try
                    {
                        newRes = await createTask.WaitAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Preempted by StopSessionAsync or external cancellation — do not clobber Stop's state.
                        return false;
                    }

                    if (!IsEpochCurrent(myEpoch)) return false;

                    if (!newRes.IsSuccess)
                    {
                        // A-02: Re-verify via `colab sessions` before assuming no VM was created!
                        var reverifyRes = await _cli.RunAsync(
                            ["sessions"],
                            TimeSpan.FromSeconds(15),
                            cancellationToken: CancellationToken.None).ConfigureAwait(false);
                        if (!IsEpochCurrent(myEpoch)) return false;

                        var reverify = VmScripts.ParseSessionPresence(reverifyRes);
                        if (reverify.Presence == SessionPresence.ExplicitlyAbsent)
                        {
                            _billing.Stop();
                            TryDeleteSessionWithWarning();
                            NoteUnnamedAssignments(reverify);
                            int newUnnamed = reverify.UnnamedAssignments - unnamedBeforeCreate;

                            // Safe failover: ONLY advance to the next candidate when we have proved
                            // zero named and zero new unnamed instances were created on this account.
                            if (newUnnamed <= 0 && hasNextCandidate)
                            {
                                var nextName = resolvedCandidates[idx + 1].DisplayName;
                                Log(LogLevel.Warning, $"{acctTag}申请 {tierSpec.ShortName} 失败（{TrimOneLine(newRes.CombinedOutput)}），已核实未产生计费，自动顺延尝试「{nextName}」...");
                                ClearActiveAccountTracking();
                                UpdateStateIfEpoch(myEpoch, SessionState.Deploying, DeployStage.CheckingSession, hasActiveVm: false,
                                    detail: $"账号「{candidateCtx.DisplayName}」不可用，切换至「{nextName}」...");
                                continue;
                            }

                            var hint = newUnnamed > 0
                                ? $"{acctTag}创建 {tierSpec.ShortName} 的请求没有正常返回，云端多出 {newUnnamed} 个未命名实例（很可能就是本次申请的实例，正在计费）。{VmScripts.UnnamedAssignmentHint}"
                                : newRes.CombinedOutput.Contains("quota", StringComparison.OrdinalIgnoreCase)
                                       || newRes.CombinedOutput.Contains("503", StringComparison.Ordinal)
                                       || newRes.CombinedOutput.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                                       || newRes.CombinedOutput.Contains("Allocation refused", StringComparison.OrdinalIgnoreCase)
                                ? $"{acctTag}{tierSpec.ShortName} GPU 当前库存不足或配额受限，已核实未创建新实例，可改选 A100 标准 / L4 或换号重试（不会自动降级到其他 GPU）。"
                                : $"{acctTag}创建 {tierSpec.ShortName} 会话失败：{TrimOneLine(newRes.CombinedOutput)}";
                            ClearActiveAccountTracking();
                            FailDeployIfEpoch(myEpoch, hint, newRes.CombinedOutput, vmAlive: false);
                            return false;
                        }

                        // VM exists or could not be proven absent — KEEP billing protection armed and NEVER failover!
                        _billing.Start(record.StartedAt, record.PlannedMinutes, record.AutoStop, startTimeUnknown: false, initialRate);
                        var uncertainMsg = reverify.Presence == SessionPresence.Present
                            ? $"{acctTag}创建 {tierSpec.ShortName} 命令报错/超时，但复查发现云端会话已创建并正在计费！可点击「重试部署」或「立即停止计费」。"
                            : $"{acctTag}创建 {tierSpec.ShortName} 命令失败/超时且无法确认云端是否已分配 GPU，已保留计费保护！请立即复查或点击「立即停止计费」。";
                        FailDeployIfEpoch(myEpoch, uncertainMsg,
                            $"{newRes.CombinedOutput}\n[REVERIFY]\n{reverify.RawOutput}",
                            vmAlive: true);
                        return false;
                    }

                    // Update StartAt to actual creation completion time
                    record.StartedAt = _utcNow();
                    Log(LogLevel.Success, $"{acctTag}{tierSpec.ShortName} GPU Runtime 创建就绪，开始计费计时。");

                    // Best-effort live CU/h calibration from `colab usage` (never blocks or fails deploy).
                    if (_accounts is not null)
                    {
                        try
                        {
                            using var rateCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            rateCts.CancelAfter(TimeSpan.FromSeconds(8));
                            var liveQuota = await _accounts.RefreshAccountQuotaAsync(candidateCtx.AccountId, rateCts.Token)
                                .ConfigureAwait(false);
                            if (liveQuota.Status == AccountQuotaStatus.Ok && liveQuota.UsageRateHourly is > 0 and var liveRate)
                            {
                                record.BillingCuPerHour = liveRate;
                                SetActiveGpuTracking(effectiveGpuTier, record.DetectedGpuProfile, record.DetectedGpuName, effectiveInpaint, liveRate: true);
                                Log(LogLevel.Info, $"{acctTag}已从 Colab 校准实时费率：{BillingConstants.FormatRate(liveRate)}");
                            }
                        }
                        catch
                        {
                            // Keep tier reference rate if live probe times out.
                        }
                    }
                }
                else
                {
                    SetActiveGpuTracking(effectiveGpuTier, record.DetectedGpuProfile, record.DetectedGpuName, effectiveInpaint, liveRate: record.BillingCuPerHour is > 0);
                    Log(LogLevel.Info, $"{acctTag}检测到云端已有 manga 会话，跳过创建并执行幂等接管。");
                }

                provisionalRecord = record;
                break;
            }

            if (provisionalRecord is null)
            {
                ClearActiveAccountTracking();
                FailDeployIfEpoch(myEpoch, $"所有候选账号均未能创建 {tierSpec.ShortName} 会话，未产生计费。", null, vmAlive: false);
                return false;
            }

            // Arm billing in memory first, then persist (A-02)
            _billing.Start(
                provisionalRecord.StartedAt,
                provisionalRecord.PlannedMinutes,
                provisionalRecord.AutoStop,
                provisionalRecord.StartTimeUnknown,
                provisionalRecord.BillingCuPerHour ?? tierSpec.DefaultCuPerHour);
            TrySaveSessionWithWarning(provisionalRecord);

            // 2. Upload manga.json plus the bundled vm_setup/vm_job/vm_status/vm_pack scripts
            UpdateStateIfEpoch(myEpoch, SessionState.Deploying, DeployStage.UploadingScripts, hasActiveVm: true,
                detail: "正在上传部署与管理脚本...");
            var uploadRes = await _scripts.UploadAllScriptsAsync(
                _cli,
                engine,
                msg =>
                {
                    UpdateStateIfEpoch(myEpoch, SessionState.Deploying, DeployStage.UploadingScripts, hasActiveVm: true, detail: msg);
                    Log(LogLevel.Info, msg);
                },
                ct).ConfigureAwait(false);

            if (!IsEpochCurrent(myEpoch)) return false;

            if (!uploadRes.IsSuccess)
            {
                FailDeployIfEpoch(myEpoch, $"上传脚本失败：{TrimOneLine(uploadRes.CombinedOutput)}", uploadRes.CombinedOutput, vmAlive: true);
                return false;
            }

            // 3. Stream vm_setup.sh execution
            UpdateStateIfEpoch(myEpoch, SessionState.Deploying, DeployStage.Stage1TranslatorEnv, hasActiveVm: true,
                detail: "正在安装环境与模型（首次下载耗时取决于模型大小和网络速度）...");
            Log(LogLevel.Info, $"正在 VM 上执行幂等部署脚本 vm_setup.sh（引擎：{engine}）...");

            // Run detached + poll with short ssh calls: a single 30-minute ssh dies silently
            // mid-deploy ("client_loop: send disconnect") while the VM keeps billing.
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var currentStage = DeployStage.Stage1TranslatorEnv;
            var passProfileHint = setupProfileHint != GpuProfiles.Auto || effectiveInpaint != InpaintModes.LamaLarge
                ? setupProfileHint
                : null;
            var passInpaint = effectiveInpaint != InpaintModes.LamaLarge ? effectiveInpaint : null;
            var setupRes = await VmScripts.RunDetachedSetupAsync(
                _cli,
                engine,
                line =>
                {
                    if (!IsEpochCurrent(myEpoch)) return;
                    var nextStage = VmScripts.ParseDeployStageFromLine(line, currentStage);
                    if (nextStage != currentStage)
                    {
                        currentStage = nextStage;
                        var stageLabel = FormatDeployStage(currentStage);
                        UpdateStateIfEpoch(myEpoch, SessionState.Deploying, currentStage, hasActiveVm: true, detail: stageLabel);
                        Log(LogLevel.Info, $"部署进度：{stageLabel}");
                    }
                    if (currentStage is DeployStage.Stage2LlamaModel or DeployStage.Stage5TranslatorWarmup)
                        UpdateStateIfEpoch(myEpoch, SessionState.Deploying, currentStage, true, detail: $"{FormatDeployStage(currentStage)} · 已等待 {elapsed.Elapsed:mm\\:ss} · 最近日志 {DateTime.Now:HH:mm:ss}：{TrimOneLine(line)}");
                },
                ct,
                profileHint: passProfileHint,
                inpaintMode: passInpaint, onPoll: () =>
                {
                    if (currentStage is DeployStage.Stage2LlamaModel or DeployStage.Stage5TranslatorWarmup)
                        UpdateStateIfEpoch(myEpoch, SessionState.Deploying, currentStage, true, detail: $"{FormatDeployStage(currentStage)} · 已等待 {elapsed.Elapsed:mm\\:ss} · 最近轮询 {DateTime.Now:HH:mm:ss}");
                }).ConfigureAwait(false);

            if (!IsEpochCurrent(myEpoch)) return false;

            if (!setupRes.IsSuccess || !setupRes.StdOut.Contains("VM_SETUP_COMPLETE", StringComparison.Ordinal))
            {
                FailDeployIfEpoch(
                    myEpoch,
                    $"VM 部署未完成（VM 仍在计费，可重试部署或立即停止）：{TrimOneLine(setupRes.CombinedOutput)}",
                    setupRes.CombinedOutput,
                    vmAlive: true);
                return false;
            }

            // 4. Final health check (A-11: llama :6006 is required; mit :8001 is optional)
            UpdateStateIfEpoch(myEpoch, SessionState.Deploying, DeployStage.VerifyingHealth, hasActiveVm: true,
                detail: "正在自检翻译引擎健康状态...");
            var status = await PollStatusAsync(null, ct).ConfigureAwait(false);
            if (!IsEpochCurrent(myEpoch)) return false;

            if (status is null || !status.IsReadyForTranslation)
            {
                FailDeployIfEpoch(myEpoch, "部署脚本已结束，但核心模型服务 (llama :6006) 未响应。", setupRes.CombinedOutput, vmAlive: true);
                return false;
            }

            ApplyDetectedGpuFromStatus(status, persistIfChanged: true);
            var engineDisplay = FormatEngineDisplay(status.DeployedEngine);
            var profileNote = !string.IsNullOrWhiteSpace(status.GpuProfile)
                ? $" | 自适应方案：{GpuProfiles.DisplayName(status.GpuProfile)}"
                : string.Empty;
            var fallbackNote = status.FallbackLlamaOk.HasValue
                ? $" | 兜底 Sakura :6007 {(status.FallbackLlamaOk.Value ? "✓" : "⚠")}"
                : string.Empty;
            UpdateStateIfEpoch(myEpoch, SessionState.Ready, DeployStage.Completed, hasActiveVm: true,
                detail: "服务就绪，可拖入漫画 .zip / .cbz");
            Log(LogLevel.Success, $"部署完成！llama :6006 ✓{fallbackNote} | 引擎：{engineDisplay} ✓{profileNote} | GPU: {status.GpuInfo}");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            EndLifecycleOperation(linkedCts, isStartupProbe: false);
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Single-flight, preemptive atomic stop (A-01, A-02, A-03):
    /// immediately bumps epoch & cancels any active deploy/probe, runs optional best-effort kill,
    /// invokes `colab stop -s manga`, and verifies via three-state `ParseSessionPresence == ExplicitlyAbsent`.
    /// </summary>
    public Task<StopResult> StopSessionAsync(
        bool isAutoStop = false,
        bool tryKillTranslate = false,
        CancellationToken cancellationToken = default)
    {
        lock (_stopLock)
        {
            if (_activeStopTask is not null && !_activeStopTask.IsCompleted)
            {
                return _activeStopTask;
            }

            // Preempt any in-flight deploy/probe immediately!
            var stopEpoch = Interlocked.Increment(ref _operationEpoch);
            CancelActiveLifecycleCts();

            var task = ExecuteStopAsync(stopEpoch, isAutoStop, tryKillTranslate, cancellationToken);
            _activeStopTask = task;
            return task;
        }
    }

    private async Task<StopResult> ExecuteStopAsync(
        long stopEpoch,
        bool isAutoStop,
        bool tryKillTranslate,
        CancellationToken cancellationToken)
    {
        try
        {
            var targetStoppingState = isAutoStop ? SessionState.AutoStopping : SessionState.Stopping;
            if (_accounts is not null && !string.IsNullOrWhiteSpace(ActiveSessionAccountId))
            {
                _cli.ActiveAccount = _accounts.ResolveContextOrDefault(ActiveSessionAccountId);
            }

            var stopAcctTag = _accounts is { HasMultipleAccounts: true } ? $"[{ActiveSessionAccountName}] " : string.Empty;
            UpdateStateIfEpoch(stopEpoch, targetStoppingState, DeployStage.None, hasActiveVm: true,
                detail: isAutoStop ? $"{stopAcctTag}已到设定时长，正在自动停止计费..." : $"{stopAcctTag}正在停止 Colab 会话并核实...");
            Log(LogLevel.Warning, isAutoStop ? $"{stopAcctTag}触发到时自动停止流程..." : $"{stopAcctTag}开始停止 Colab 会话并结束计费...");

            Task<ColabResult>? pendingCreate;
            lock (_stateLock)
            {
                pendingCreate = _pendingCreateTask;
            }

            if (pendingCreate is { IsCompleted: false })
            {
                Log(LogLevel.Info, "GPU 创建请求仍在进行，等待它返回后再停止（中途打断可能留下无法按名称停止的实例）...");
                UpdateStateIfEpoch(stopEpoch, targetStoppingState, DeployStage.None, hasActiveVm: true,
                    detail: "等待 GPU 创建请求返回后停止...");
                try
                {
                    await pendingCreate.ConfigureAwait(false);
                }
                catch
                {
                    // The create outcome is re-checked through `colab sessions` below.
                }
            }

            if (tryKillTranslate)
            {
                try
                {
                    Log(LogLevel.Info, "尝试终止云端正在进行的翻译进程（超时 10s，不阻塞停机）...");
                    await _cli.RunSshAsync(
                        VmScripts.SessionName,
                        ["bash", "/content/vm_pack.sh", "kill_translate"],
                        TimeSpan.FromSeconds(10),
                        allowRateLimitRetry: false,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort only; never block stopping billing.
                }
            }

            string lastCombined = string.Empty;
            string lastVerifyReason = string.Empty;

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                Log(LogLevel.Info, attempt == 1 ? "正在执行 colab stop -s manga..." : "第 2 次重试 colab stop -s manga...");
                var stopRes = await _cli.RunAsync(
                    ["stop", "-s", VmScripts.SessionName],
                    TimeSpan.FromSeconds(60),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                lastCombined = stopRes.CombinedOutput;

                Log(LogLevel.Info, "正在通过 colab sessions 严格复查会话是否彻底终止...");
                var verifyRes = await _cli.RunAsync(
                    ["sessions"],
                    TimeSpan.FromSeconds(30),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var query = VmScripts.ParseSessionPresence(verifyRes);
                lastVerifyReason = query.Reason;

                if (query.Presence == SessionPresence.ExplicitlyAbsent)
                {
                    var stoppedAccountId = ActiveSessionAccountId;
                    var stoppedAccountName = ActiveSessionAccountName;
                    double? balanceBefore;
                    lock (_stateLock)
                    {
                        balanceBefore = _balanceBeforeStartCu;
                        _latestStatus = new VmStatusSnapshot();
                    }

                    // Memory-first reset before disk deletion (A-02)
                    _billing.Stop();
                    ClearActiveAccountTracking();
                    UpdateStateIfEpoch(stopEpoch, SessionState.Idle, DeployStage.None, hasActiveVm: false, detail: "已停止计费");
                    TryDeleteSessionWithWarning();
                    NoteUnnamedAssignments(query);

                    // Best-effort balance refresh after verified stop so UI reflects real deduction.
                    string balanceNote = string.Empty;
                    if (_accounts is not null && !string.IsNullOrWhiteSpace(stoppedAccountId))
                    {
                        try
                        {
                            using var postStopCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                            var updatedQuota = await _accounts.RefreshAccountQuotaAsync(stoppedAccountId, postStopCts.Token)
                                .ConfigureAwait(false);
                            if (updatedQuota.Status == AccountQuotaStatus.Ok && updatedQuota.CurrentBalanceCu is { } afterCu)
                            {
                                balanceNote = balanceBefore.HasValue
                                    ? $"账号「{stoppedAccountName}」最新余额：{afterCu:F2} CU（本次实扣 ≈ {Math.Max(0, balanceBefore.Value - afterCu):F2} CU）。"
                                    : $"账号「{stoppedAccountName}」最新余额：{afterCu:F2} CU。";
                            }
                        }
                        catch
                        {
                            // Never fail or delay a verified stop because of a post-stop usage refresh.
                        }
                    }

                    if (query.UnnamedAssignments > 0)
                    {
                        Log(LogLevel.Success, $"已核实 manga 会话停止。{balanceNote}");
                        return new StopResult(
                            true,
                            $"账号「{stoppedAccountName ?? "系统默认 ADC"}」的 manga 会话已停止，但仍有 {query.UnnamedAssignments} 个未命名实例。{VmScripts.UnnamedAssignmentHint}",
                            RemainingUnnamedAssignments: query.UnnamedAssignments);
                    }

                    Log(LogLevel.Success, $"已严格核实云端无活动会话，计费已安全结束。{balanceNote}");
                    return new StopResult(true, string.IsNullOrEmpty(balanceNote) ? "已核实停止计费。" : $"已核实停止计费。{balanceNote}");
                }

                lastCombined = $"{stopRes.CombinedOutput}\n[SESSIONS VERIFY: {query.Presence} - {query.Reason}]\n{verifyRes.CombinedOutput}";
                Log(LogLevel.Warning, $"第 {attempt} 次停机核实未通过（{query.Reason}）。");

                if (attempt == 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
            }

            var manualStopCmd = _accounts?.BuildManualStopCommandHint(ActiveSessionAccountId) ?? "colab stop -s manga";
            var acctLabel = _accounts is { HasMultipleAccounts: true } ? $"（账号：{ActiveSessionAccountName}）" : string.Empty;
            var manualMsg = $"停止核实失败{acctLabel}（{lastVerifyReason}）！云端会话可能仍在计费，请立即在终端手动执行：{manualStopCmd}；若 colab sessions 只列出 [?] 开头的行，说明实例没有名称，需到 Colab 网页「管理会话」终止。";
            UpdateStateIfEpoch(stopEpoch, SessionState.Error, DeployStage.None, hasActiveVm: true,
                detail: $"停止核实失败{acctLabel}，请手动停止计费",
                error: manualMsg,
                rawOutput: lastCombined);
            Log(LogLevel.Error, manualMsg);
            return new StopResult(false, manualMsg, lastCombined);
        }
        finally
        {
            lock (_stopLock)
            {
                _activeStopTask = null;
            }
        }
    }

    /// <summary>
    /// Automatic stop that keeps retrying until the stop is verified. Between attempts it waits with
    /// growing delays and gives up as soon as the session leaves the "error while billing" state,
    /// which means the user (or another operation) has taken over.
    /// </summary>
    public Task<StopResult> AutoStopUntilVerifiedAsync(
        bool tryKillTranslate = false,
        CancellationToken cancellationToken = default)
    {
        lock (_stopLock)
        {
            if (_activeAutoStopLoop is { IsCompleted: false })
            {
                return _activeAutoStopLoop;
            }

            var task = RunAutoStopLoopAsync(tryKillTranslate, cancellationToken);
            _activeAutoStopLoop = task;
            return task;
        }
    }

    private async Task<StopResult> RunAutoStopLoopAsync(bool tryKillTranslate, CancellationToken cancellationToken)
    {
        int attempt = 0;
        while (true)
        {
            var result = await StopSessionAsync(isAutoStop: true, tryKillTranslate, cancellationToken).ConfigureAwait(false);
            if (result.VerifiedStopped)
            {
                return result;
            }

            var delay = _autoStopRetryDelays[Math.Min(attempt, _autoStopRetryDelays.Length - 1)];
            attempt++;
            Log(LogLevel.Warning, $"自动停止未能核实，云端可能仍在计费；将在 {FormatDelay(delay)} 后进行第 {attempt} 次重试。");
            AutoStopRetryScheduled?.Invoke(attempt, delay);

            if (!await WaitWhileStuckBillingAsync(delay, cancellationToken).ConfigureAwait(false))
            {
                Log(LogLevel.Info, "会话状态已变化，自动停止重试结束。");
                return result;
            }
        }
    }

    private async Task<bool> WaitWhileStuckBillingAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var slice = TimeSpan.FromSeconds(1);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (waited.Elapsed < delay)
        {
            var remaining = delay - waited.Elapsed;
            await Task.Delay(remaining < slice ? remaining : slice, cancellationToken).ConfigureAwait(false);
            if (!IsStuckBilling())
            {
                return false;
            }
        }

        return IsStuckBilling();
    }

    private bool IsStuckBilling()
    {
        lock (_stateLock)
        {
            return _state == SessionState.Error && _hasActiveVm;
        }
    }

    /// <summary>
    /// F6: kills stale remote job_* dirs / translator processes left behind by a killed
    /// front-end (the in-memory queue is lost with the process, so their results are
    /// unrecoverable). Startup-recovery paths only — the queue is necessarily empty
    /// there. Non-fatal: a failure only logs a Warning.
    /// </summary>
    private async Task CleanStaleRemoteJobsAsync(CancellationToken ct)
    {
        ColabResult res;
        try
        {
            res = await _cli.RunSshAsync(
                VmScripts.SessionName,
                ["bash", "/content/vm_pack.sh", "clean_stale"],
                TimeSpan.FromSeconds(30),
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, $"清理云端遗留任务失败（不影响接管）：{ex.Message}");
            return;
        }

        if (!res.IsSuccess)
        {
            Log(LogLevel.Warning, $"清理云端遗留任务失败（不影响接管）：{TrimOneLine(res.CombinedOutput)}");
            return;
        }

        var tokens = res.StdOut.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var idx = Array.IndexOf(tokens, "STALE_CLEAN_OK");
        var cleaned = 0;
        if (idx >= 0)
        {
            for (var i = idx + 1; i < tokens.Length && i <= idx + 2; i++)
            {
                if (int.TryParse(tokens[i], out var n))
                {
                    cleaned += n;
                }
            }
        }

        if (cleaned > 0)
        {
            Log(LogLevel.Info, $"已清理上次遗留的 {cleaned} 个云端任务（本地队列已丢失，无法回收其结果）。");
        }
    }

    private static string FormatDelay(TimeSpan delay) =>
        delay.TotalMinutes >= 1 ? $"{delay.TotalMinutes:0.#} 分钟" : $"{delay.TotalSeconds:0.#} 秒";

    /// <summary>
    /// Polls /content/vm_status.sh (optionally passing jobDir or outDir) in a single SSH round-trip.
    /// </summary>
    public async Task<VmStatusSnapshot?> PollStatusAsync(string? activeJobOrOutDir = null, CancellationToken cancellationToken = default)
    {
        var remoteArgs = string.IsNullOrWhiteSpace(activeJobOrOutDir)
            ? new[] { "bash", "/content/vm_status.sh" }
            : new[] { "bash", "/content/vm_status.sh", activeJobOrOutDir };

        var res = await _cli.RunSshAsync(
            VmScripts.SessionName,
            remoteArgs,
            TimeSpan.FromSeconds(30),
            allowRateLimitRetry: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!res.IsSuccess)
        {
            return null;
        }

        var snapshot = VmScripts.ParseVmStatus(res.StdOut);
        lock (_stateLock)
        {
            _latestStatus = snapshot;
        }

        ApplyDetectedGpuFromStatus(snapshot, persistIfChanged: HasActiveVm);
        StateChanged?.Invoke();
        return snapshot;
    }

    /// <summary>
    /// Marks latest health snapshot as disconnected/stale when consecutive SSH health checks fail (A-07).
    /// </summary>
    public void MarkHealthDisconnected(string? reason = null)
    {
        lock (_stateLock)
        {
            _latestStatus = _latestStatus with
            {
                IsDisconnected = true,
                LlamaOk = false,
                MitOk = false,
                EngineReady = false
            };
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            Log(LogLevel.Warning, reason);
        }

        StateChanged?.Invoke();
    }

    public void ExtendPlannedTime(int extraMinutes = 30)
    {
        var updated = _billing.Extend(extraMinutes);
        if (updated is not null)
        {
            lock (_stateLock)
            {
                updated.AccountId = _activeSessionAccountId;
                updated.AccountName = _activeSessionAccountName;
                updated.BalanceBeforeStartCu = _balanceBeforeStartCu;
                updated.RequestedGpu = _activeRequestedGpuTier;
                updated.DetectedGpuProfile = _activeDetectedGpuProfile;
                updated.DetectedGpuName = _activeDetectedGpuName;
                updated.InpaintMode = _activeInpaintMode;
                if (_state == SessionState.Draining)
                {
                    _state = SessionState.Busy;
                    _statusDetail = "已延长时长，恢复正常队列翻译";
                }
            }

            TrySaveSessionWithWarning(updated);
            Log(LogLevel.Info, $"已延长计划时长 {extraMinutes} 分钟（新上限：{updated.PlannedMinutes} 分钟）。");
            StateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Stops an orphan `manga` session on a secondary account without disturbing the active primary session.
    /// </summary>
    public async Task<StopResult> StopOtherAccountSessionAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (_accounts is null)
        {
            return new StopResult(false, "多账号管理器未启用。");
        }

        var profile = _accounts.GetAccountById(accountId);
        if (profile is null)
        {
            return new StopResult(false, $"未找到账号：{accountId}");
        }

        var ctx = profile.ToContext();
        Log(LogLevel.Warning, $"[{ctx.DisplayName}] 正在停止该账号云端的 manga 会话...");
        var stopRes = await _cli.RunForAccountAsync(
            ctx,
            ["stop", "-s", VmScripts.SessionName],
            TimeSpan.FromSeconds(60),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var verifyRes = await _cli.RunForAccountAsync(
            ctx,
            ["sessions"],
            TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var query = VmScripts.ParseSessionPresence(verifyRes);
        if (query.Presence == SessionPresence.ExplicitlyAbsent)
        {
            lock (_stateLock)
            {
                _otherAccountWarnings.RemoveAll(w => string.Equals(w.AccountId, accountId, StringComparison.OrdinalIgnoreCase));
                if (query.UnnamedAssignments > 0)
                {
                    _otherAccountWarnings.Add(new OtherAccountSessionWarning(
                        ctx.AccountId,
                        ctx.DisplayName,
                        HasMangaSession: false,
                        UnnamedAssignments: query.UnnamedAssignments,
                        Message: $"账号「{ctx.DisplayName}」仍有 {query.UnnamedAssignments} 个未命名实例，请前往 Colab 网页「管理会话」终止。"));
                }
            }

            StateChanged?.Invoke();
            try
            {
                await _accounts.RefreshAccountQuotaAsync(accountId, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort refresh.
            }

            var msg = query.UnnamedAssignments > 0
                ? $"账号「{ctx.DisplayName}」的 manga 会话已停，但仍有 {query.UnnamedAssignments} 个未命名实例。"
                : $"已核实停止账号「{ctx.DisplayName}」的云端会话。";
            Log(LogLevel.Success, msg);
            return new StopResult(true, msg, RemainingUnnamedAssignments: query.UnnamedAssignments);
        }

        var hint = _accounts.BuildManualStopCommandHint(accountId);
        var failMsg = $"账号「{ctx.DisplayName}」停机核实失败（{query.Reason}），请手动执行：{hint}";
        Log(LogLevel.Error, failMsg);
        return new StopResult(false, failMsg, $"{stopRes.CombinedOutput}\n{verifyRes.CombinedOutput}");
    }

    private bool BindSavedRecordAccount(SessionRecord record, out string? error)
    {
        error = null;
        if (_accounts is null)
        {
            SetActiveAccountTracking(record.AccountId, record.AccountName ?? ColabAccountContext.Default.DisplayName, record.BalanceBeforeStartCu);
            return true;
        }

        if (string.IsNullOrWhiteSpace(record.AccountId))
        {
            var fallback = _accounts.ResolveContextOrDefault(null);
            _cli.ActiveAccount = fallback;
            SetActiveAccountTracking(fallback.AccountId, fallback.DisplayName, record.BalanceBeforeStartCu);
            return true;
        }

        var profile = _accounts.GetAccountById(record.AccountId);
        if (profile is null)
        {
            var displayName = string.IsNullOrWhiteSpace(record.AccountName) ? record.AccountId : record.AccountName;
            SetActiveAccountTracking(record.AccountId, displayName, record.BalanceBeforeStartCu);
            error = $"上次会话由账号「{displayName}」启动，但账号列表中已找不到该配置，无法核实云端是否仍在计费！请重新导入该账号凭据，或前往 Colab 网页「管理会话」手动停止。";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(profile.AdcCredentialsPath) && !System.IO.File.Exists(profile.AdcCredentialsPath))
        {
            SetActiveAccountTracking(profile.Id, profile.Name, record.BalanceBeforeStartCu);
            error = $"上次会话由账号「{profile.Name}」启动，但其 ADC 凭据文件已丢失（{profile.AdcCredentialsPath}），无法核实是否仍在计费！请在「账号管理」重新登录该账号，或前往 Colab 网页手动停止。";
            return false;
        }

        var ctx = profile.ToContext();
        _cli.ActiveAccount = ctx;
        SetActiveAccountTracking(ctx.AccountId, ctx.DisplayName, record.BalanceBeforeStartCu);
        return true;
    }

    private async Task<StartupProbeResult> ProbeAllAccountsWhenNoLocalRecordAsync(
        long myEpoch,
        SessionLoadResult loadResult,
        CancellationToken ct)
    {
        var enabled = _accounts!.GetEnabledAccounts();
        Log(LogLevel.Info, $"启动探测：正在并发检查 {enabled.Count} 个启用账号的云端会话状态...");

        var probeTasks = enabled.Select(async acct =>
        {
            var ctx = acct.ToContext();
            var res = await _cli.RunForAccountAsync(ctx, ["sessions"], TimeSpan.FromSeconds(30), cancellationToken: ct)
                .ConfigureAwait(false);
            return (Account: acct, Context: ctx, Query: VmScripts.ParseSessionPresence(res));
        });

        var results = await Task.WhenAll(probeTasks).ConfigureAwait(false);
        if (!IsEpochCurrent(myEpoch))
        {
            return new StartupProbeResult(StartupProbeOutcome.ProbeFailed, "启动探测已被更新的状态操作中断。");
        }

        var presentList = results.Where(r => r.Query.Presence == SessionPresence.Present).ToList();
        var unknownList = results.Where(r => r.Query.Presence == SessionPresence.Unknown).ToList();

        var secondaryWarnings = new List<OtherAccountSessionWarning>();
        foreach (var item in results)
        {
            bool isPrimaryOrphan = presentList.Count > 0
                && string.Equals(item.Account.Id, presentList[0].Account.Id, StringComparison.OrdinalIgnoreCase);
            if (!isPrimaryOrphan && (item.Query.Presence == SessionPresence.Present || item.Query.UnnamedAssignments > 0))
            {
                var detail = OtherAccountDetail(item.Account.Name, item.Query);
                secondaryWarnings.Add(new OtherAccountSessionWarning(
                    item.Account.Id,
                    item.Account.Name,
                    HasMangaSession: item.Query.Presence == SessionPresence.Present,
                    UnnamedAssignments: item.Query.UnnamedAssignments,
                    Message: detail));
            }
        }

        SetOtherAccountWarnings(secondaryWarnings);

        if (presentList.Count > 0)
        {
            var primary = presentList[0];
            _cli.ActiveAccount = primary.Context;
            SetActiveAccountTracking(primary.Account.Id, primary.Account.Name, _accounts.GetQuota(primary.Account.Id).CurrentBalanceCu);
            NoteUnnamedAssignments(primary.Query);
            _billing.Start(_utcNow(), plannedMinutes: null, autoStop: false, startTimeUnknown: true);
            var extraNote = presentList.Count > 1
                ? $"（另有 {presentList.Count - 1} 个账号也存在活跃会话，见顶部告警条）"
                : string.Empty;
            UpdateStateIfEpoch(myEpoch, SessionState.Error, DeployStage.None, hasActiveVm: true,
                detail: $"检测到孤儿会话（{primary.Account.Name} · 正在计费）",
                error: $"云端账号「{primary.Account.Name}」存在未由本应用记录的 manga 会话，请选择接管或立即停止计费。{extraNote}");
            Log(LogLevel.Warning, $"检测到云端孤儿会话 manga（账号：{primary.Account.Name}，正在计费）！{extraNote}");
            return new StartupProbeResult(StartupProbeOutcome.OrphanSessionDetected, $"检测到账号「{primary.Account.Name}」存在云端孤儿会话。");
        }

        if (unknownList.Count > 0)
        {
            var failedNames = string.Join("、", unknownList.Select(u => u.Account.Name));
            var first = unknownList[0];
            var err = $"无法确认账号「{failedNames}」的 Colab 会话状态：{first.Query.Reason}";
            Log(LogLevel.Warning, err);

            if (loadResult.Status is StoreLoadStatus.Corrupted or StoreLoadStatus.AccessDenied)
            {
                _billing.Start(_utcNow(), plannedMinutes: null, autoStop: false, startTimeUnknown: true);
                UpdateStateIfEpoch(myEpoch, SessionState.Error, DeployStage.None, hasActiveVm: true,
                    detail: "本地记录损坏且云端状态未知", error: $"{loadResult.ErrorMessage}\n{err}", rawOutput: first.Query.RawOutput);
            }
            else
            {
                UpdateStateIfEpoch(myEpoch, SessionState.Error, DeployStage.None, hasActiveVm: false,
                    detail: "部分账号会话状态未知",
                    error: $"{err}。请点「复查会话」重试；确认没有会话后可直接启动。",
                    rawOutput: first.Query.RawOutput);
            }

            return new StartupProbeResult(StartupProbeOutcome.ProbeFailed, err);
        }

        ClearActiveAccountTracking();
        if (loadResult.Status == StoreLoadStatus.Corrupted)
        {
            _billing.Stop();
            UpdateStateIfEpoch(myEpoch, SessionState.Idle, DeployStage.None, hasActiveVm: false, detail: "未运行");
            TryDeleteSessionWithWarning();
            return new StartupProbeResult(StartupProbeOutcome.CleanedStaleRecord, "本地损坏会话记录已清理。");
        }

        UpdateStateIfEpoch(myEpoch, SessionState.Idle, DeployStage.None, hasActiveVm: false, detail: "未运行");
        Log(LogLevel.Info, $"已核实全部 {enabled.Count} 个启用账号均无运行中的 manga 会话。");
        return new StartupProbeResult(StartupProbeOutcome.Idle, "当前无活动会话。");
    }

    private static string OtherAccountDetail(string name, SessionQueryResult query) => $"账号「{name}」："
        + (query.Presence == SessionPresence.Present ? "具名 manga 会话正在计费。" : "没有具名 manga 会话。")
        + (query.UnnamedAssignments > 0 ? $"另有 {query.UnnamedAssignments} 个未命名 [?] 实例。{VmScripts.UnnamedAssignmentHint}" : "");

    private async Task ScanOtherAccountsForOrphansAsync(string? excludeAccountId, CancellationToken ct)
    {
        if (_accounts is null) return;
        var others = _accounts.GetEnabledAccounts()
            .Where(a => !string.Equals(a.Id, excludeAccountId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (others.Count == 0)
        {
            SetOtherAccountWarnings([]);
            return;
        }

        var tasks = others.Select(async acct =>
        {
            var res = await _cli.RunForAccountAsync(acct.ToContext(), ["sessions"], TimeSpan.FromSeconds(25), cancellationToken: ct)
                .ConfigureAwait(false);
            return (Account: acct, Query: VmScripts.ParseSessionPresence(res));
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var warnings = new List<OtherAccountSessionWarning>();
        foreach (var r in results)
        {
            if (r.Query.Presence == SessionPresence.Present || r.Query.UnnamedAssignments > 0)
            {
                var msg = OtherAccountDetail(r.Account.Name, r.Query);
                warnings.Add(new OtherAccountSessionWarning(
                    r.Account.Id,
                    r.Account.Name,
                    HasMangaSession: r.Query.Presence == SessionPresence.Present,
                    UnnamedAssignments: r.Query.UnnamedAssignments,
                    Message: msg));
                Log(LogLevel.Warning, msg);
            }
        }

        SetOtherAccountWarnings(warnings);
    }

    private List<ColabAccountContext> ResolveStartCandidates(
        IReadOnlyList<ColabAccountProfile>? explicitCandidates,
        bool explicitAllowFailover,
        out bool allowFailover)
    {
        // Active VM redeploy is strictly pinned to the session's owning account.
        if (HasActiveVm)
        {
            allowFailover = false;
            if (!string.IsNullOrWhiteSpace(ActiveSessionAccountId) && _accounts is not null)
            {
                return [_accounts.ResolveContextOrDefault(ActiveSessionAccountId)];
            }

            return [_cli.ActiveAccount];
        }

        if (explicitCandidates is { Count: > 0 })
        {
            allowFailover = explicitAllowFailover && explicitCandidates.Count > 1;
            return explicitCandidates.Select(c => c.ToContext()).ToList();
        }

        if (_accounts is not null)
        {
            var plan = _accounts.ResolveCandidatesForStart();
            allowFailover = plan.AllowFailover;
            return plan.Candidates.Select(c => c.ToContext()).ToList();
        }

        allowFailover = false;
        return [_cli.ActiveAccount];
    }

    private void SetActiveAccountTracking(string? accountId, string displayName, double? balanceBeforeStartCu)
    {
        lock (_stateLock)
        {
            _activeSessionAccountId = accountId;
            _activeSessionAccountName = string.IsNullOrWhiteSpace(displayName)
                ? ColabAccountContext.Default.DisplayName
                : displayName;
            _balanceBeforeStartCu = balanceBeforeStartCu;
        }
    }

    private void ClearActiveAccountTracking()
    {
        lock (_stateLock)
        {
            _activeSessionAccountId = null;
            _balanceBeforeStartCu = null;
            _activeRequestedGpuTier = GpuTiers.L4;
            _activeDetectedGpuProfile = null;
            _activeDetectedGpuName = null;
            _activeInpaintMode = InpaintModes.LamaLarge;
            _hasLiveCalibratedRate = false;
            if (_accounts is not null)
            {
                var plan = _accounts.ResolveCandidatesForStart();
                _activeSessionAccountName = plan.Candidates.Count > 0
                    ? plan.Candidates[0].Name
                    : ColabAccountContext.Default.DisplayName;
            }
            else
            {
                _activeSessionAccountName = ColabAccountContext.Default.DisplayName;
            }
        }
    }

    private void SetOtherAccountWarnings(List<OtherAccountSessionWarning> warnings)
    {
        lock (_stateLock)
        {
            _otherAccountWarnings = warnings;
        }
        StateChanged?.Invoke();
    }

    public void TransitionToBusy(string detail)
    {
        lock (_stateLock)
        {
            if (_state == SessionState.Ready)
            {
                _state = SessionState.Busy;
                _statusDetail = detail;
            }
            else if (_state == SessionState.Draining)
            {
                _statusDetail = $"收尾中：{detail}";
            }
        }
        StateChanged?.Invoke();
    }

    public void TransitionToReadyFromBusy()
    {
        lock (_stateLock)
        {
            if (_state == SessionState.Busy)
            {
                _state = SessionState.Ready;
                _statusDetail = "服务就绪，队列空闲";
            }
        }
        StateChanged?.Invoke();
    }

    public void TransitionToDraining()
    {
        lock (_stateLock)
        {
            if (_state is SessionState.Busy or SessionState.Ready)
            {
                _state = SessionState.Draining;
                _statusDetail = "收尾中：翻完当前队列后将自动停止计费";
            }
        }
        Log(LogLevel.Warning, "已进入收尾模式（Draining）：当前队列完成后将自动停止计费。");
        StateChanged?.Invoke();
    }

    public void ResetErrorToIdleIfNoVm()
    {
        lock (_stateLock)
        {
            if (_state == SessionState.Error && !_hasActiveVm)
            {
                _state = SessionState.Idle;
                _lastError = null;
                _statusDetail = "未运行";
            }
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Engine selected in settings ("mtui" or "mit"); missing/invalid values resolve to "mtui".
    /// </summary>
    public string ConfiguredEngine => TranslationEngines.Normalize(_store.LoadSettings().TranslationEngine);

    /// <summary>Preferred GPU tier selected in settings ("l4" / "a100" / "a100-hm" / "g4").</summary>
    public string ConfiguredGpuTier => GpuTiers.Normalize(_store.LoadSettings().PreferredGpuTier);

    /// <summary>Preferred high-VRAM inpaint mode ("lama_large" or "flux2-klein").</summary>
    public string ConfiguredInpaintMode => InpaintModes.Normalize(_store.LoadSettings().HighQualityInpaintMode);

    /// <summary>Preferred G4 96G sub-mode ("flagship" or "balanced").</summary>
    public string ConfiguredG4SubMode => G4SubModes.Normalize(_store.LoadSettings().G4SubMode);

    private void SetActiveGpuTracking(string tier, string? profile, string? gpuName, string? inpaintMode, bool liveRate)
    {
        lock (_stateLock)
        {
            _activeRequestedGpuTier = GpuTiers.Normalize(tier);
            _activeDetectedGpuProfile = GpuProfiles.NormalizeOrNull(profile);
            _activeDetectedGpuName = string.IsNullOrWhiteSpace(gpuName) ? null : gpuName.Trim();
            _activeInpaintMode = InpaintModes.Normalize(inpaintMode);
            _hasLiveCalibratedRate = liveRate;
        }
    }

    private void ApplyDetectedGpuFromStatus(VmStatusSnapshot status, bool persistIfChanged, bool forcePersist = false)
    {
        bool changed = false;
        double? fallbackRateToApply = null;

        lock (_stateLock)
        {
            if (!string.IsNullOrWhiteSpace(status.GpuProfile)
                && !string.Equals(_activeDetectedGpuProfile, status.GpuProfile, StringComparison.OrdinalIgnoreCase))
            {
                _activeDetectedGpuProfile = status.GpuProfile;
                changed = true;
                if (!_hasLiveCalibratedRate)
                {
                    fallbackRateToApply = GpuProfiles.DefaultCuPerHourForProfile(status.GpuProfile);
                }
            }

            if (!string.IsNullOrWhiteSpace(status.GpuInfo))
            {
                var gpuName = status.GpuInfo.Split(',')[0].Trim();
                if (!string.IsNullOrWhiteSpace(gpuName)
                    && !string.Equals(_activeDetectedGpuName, gpuName, StringComparison.Ordinal))
                {
                    _activeDetectedGpuName = gpuName;
                    changed = true;
                }
            }

            if (!string.IsNullOrWhiteSpace(status.InpaintMode)
                && !string.Equals(_activeInpaintMode, status.InpaintMode, StringComparison.OrdinalIgnoreCase))
            {
                _activeInpaintMode = InpaintModes.Normalize(status.InpaintMode);
                changed = true;
            }
        }

        if (fallbackRateToApply is > 0)
        {
            _billing.UpdateCuPerHour(fallbackRateToApply.Value);
        }

        if ((persistIfChanged && changed) || forcePersist)
        {
            var cur = _billing.GetCurrentRecord();
            if (cur is not null)
            {
                lock (_stateLock)
                {
                    cur.AccountId = _activeSessionAccountId;
                    cur.AccountName = _activeSessionAccountName;
                    cur.BalanceBeforeStartCu = _balanceBeforeStartCu;
                    cur.RequestedGpu = _activeRequestedGpuTier;
                    cur.DetectedGpuProfile = _activeDetectedGpuProfile;
                    cur.DetectedGpuName = _activeDetectedGpuName;
                    cur.InpaintMode = _activeInpaintMode;
                }
                TrySaveSessionWithWarning(cur);
            }
        }
    }

    /// <summary>Deploy-complete display: engine name plus the pinned mtui revision.</summary>
    public static string FormatEngineDisplay(string? engineName) =>
        TranslationEngines.Normalize(engineName) == TranslationEngines.Mtui
            ? $"mtui {VmScripts.MtuiRevision}"
            : "mit";

    public static string FormatDeployStage(DeployStage stage) => stage switch
    {
        DeployStage.CheckingSession => "检查会话状态",
        DeployStage.CreatingRuntime => "创建 GPU Runtime",
        DeployStage.UploadingScripts => "上传管理脚本",
        DeployStage.Stage1TranslatorEnv => "STAGE 1/5：安装翻译引擎",
        DeployStage.Stage2LlamaModel => "STAGE 2/5：下载 llama.cpp 与翻译模型",
        DeployStage.Stage3EnvScript => "STAGE 3/5：写入 CUDA 运行环境变量",
        DeployStage.Stage4LlamaServer => "STAGE 4/5：启动模型服务 (:6006)",
        DeployStage.Stage5TranslatorWarmup => "STAGE 5/5：预热翻译引擎与视觉模型",
        DeployStage.VerifyingHealth => "自检核心服务健康状态",
        DeployStage.Completed => "部署完成",
        _ => "准备中"
    };

    private async Task<VmStatusSnapshot> CheckHealthWithFallbackAsync(CancellationToken cancellationToken)
    {
        var snapshot = await PollStatusAsync(null, cancellationToken).ConfigureAwait(false);
        if (snapshot is not null && snapshot.ProtocolVersion >= 2 && (snapshot.LlamaOk || snapshot.MitOk || !string.IsNullOrWhiteSpace(snapshot.GpuInfo)))
        {
            return snapshot;
        }

        // Upload bundled PROTO 2 helper scripts (vm_job.sh / vm_status.sh / vm_pack.sh) if missing or outdated on VM.
        var uploadRes = await _scripts.UploadBundledAssetsOnlyAsync(_cli, cancellationToken).ConfigureAwait(false);
        if (uploadRes.IsSuccess)
        {
            var retry = await PollStatusAsync(null, cancellationToken).ConfigureAwait(false);
            if (retry is not null)
            {
                return retry;
            }
        }

        return snapshot ?? new VmStatusSnapshot { IsDisconnected = true };
    }

    private long BeginLifecycleOperation(CancellationToken externalCt, out CancellationTokenSource linkedCts, bool isStartupProbe)
    {
        lock (_stateLock)
        {
            var epoch = Interlocked.Increment(ref _operationEpoch);
            CancelActiveLifecycleCtsUnsafe();
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            _activeLifecycleCts = linkedCts;
            if (isStartupProbe)
            {
                _isProbingStartup = true;
            }
            return epoch;
        }
    }

    private void EndLifecycleOperation(CancellationTokenSource? linkedCts, bool isStartupProbe)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(_activeLifecycleCts, linkedCts))
            {
                _activeLifecycleCts = null;
            }
            if (isStartupProbe)
            {
                _isProbingStartup = false;
            }
        }
        linkedCts?.Dispose();
        if (isStartupProbe)
        {
            StateChanged?.Invoke();
        }
    }

    private void CancelActiveLifecycleCts()
    {
        lock (_stateLock)
        {
            CancelActiveLifecycleCtsUnsafe();
        }
    }

    private void CancelActiveLifecycleCtsUnsafe()
    {
        try
        {
            _activeLifecycleCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }
    }

    private bool IsEpochCurrent(long epoch) => Interlocked.Read(ref _operationEpoch) == epoch;

    private void TrySaveSessionWithWarning(SessionRecord record)
    {
        var res = _store.TrySaveSession(record);
        if (!res.Success)
        {
            SetPersistenceWarning(res.ErrorMessage);
            Log(LogLevel.Warning, $"{res.ErrorMessage}（内存计费保护仍保持生效）");
        }
        else
        {
            SetPersistenceWarning(null);
        }
    }

    private void TryDeleteSessionWithWarning()
    {
        var res = _store.TryDeleteSession();
        if (!res.Success)
        {
            SetPersistenceWarning(res.ErrorMessage);
            Log(LogLevel.Warning, $"{res.ErrorMessage}（云端已确认停止，请稍后手动清理该文件）");
        }
        else
        {
            SetPersistenceWarning(null);
        }
    }

    private void NoteUnnamedAssignments(SessionQueryResult query)
    {
        var warning = query.UnnamedAssignments > 0
            ? $"云端有 {query.UnnamedAssignments} 个未命名实例。{VmScripts.UnnamedAssignmentHint}"
            : null;

        bool changed;
        lock (_stateLock)
        {
            changed = _unnamedAssignmentWarning != warning;
            _unnamedAssignmentWarning = warning;
        }

        if (!changed)
        {
            return;
        }

        if (warning is not null)
        {
            Log(LogLevel.Warning, warning);
        }

        StateChanged?.Invoke();
    }

    private void SetPersistenceWarning(string? warning)
    {
        lock (_stateLock)
        {
            _persistenceWarning = warning;
        }
        StateChanged?.Invoke();
    }

    private void FailDeployIfEpoch(long epoch, string message, string? rawOutput, bool vmAlive)
    {
        if (!IsEpochCurrent(epoch)) return;
        UpdateStateIfEpoch(epoch, SessionState.Error, DeployStage.None, hasActiveVm: vmAlive, detail: "部署失败", error: message, rawOutput: rawOutput);
        Log(LogLevel.Error, message);
    }

    private void UpdateStateIfEpoch(
        long epoch,
        SessionState state,
        DeployStage deployStage,
        bool hasActiveVm,
        string detail,
        string? error = null,
        string? rawOutput = null)
    {
        var recorded = false;
        lock (_stateLock)
        {
            if (Interlocked.Read(ref _operationEpoch) != epoch)
            {
                return;
            }

            _state = state;
            _deployStage = deployStage;
            _hasActiveVm = hasActiveVm;
            _statusDetail = detail;
            _lastError = error;
            if (rawOutput is not null)
            {
                _lastRawOutput = rawOutput;
                recorded = true;
            }
        }

        StateChanged?.Invoke();
        if (recorded)
        {
            RawOutputRecorded?.Invoke("会话错误原始输出", rawOutput!);
        }
    }

    private void Log(LogLevel level, string message) => LogMessage?.Invoke(level, message);

    private static string TrimOneLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "未知错误";
        }

        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var tail = lines.Length > 0 ? lines[^1].Trim() : text.Trim();
        return tail.Length > 180 ? tail[..180] + "..." : tail;
    }
}
