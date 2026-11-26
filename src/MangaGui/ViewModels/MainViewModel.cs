using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MangaGui.Models;
using MangaGui.Services;

namespace MangaGui.ViewModels;

public sealed record DiagnosticsSnapshot(
    string AppVersion,
    string DotNetVersion,
    string ColabCliStatus,
    string ColabLaunchMode,
    string ScriptsDirectory,
    string ScriptsSource,
    string BundledAssetsDirectory,
    string SessionStorePath,
    IReadOnlyDictionary<string, string> AssetChecksums,
    bool IsElevated = false,
    string? CrashLogPath = null,
    IReadOnlyList<string>? AccountSummaries = null);

/// <summary>
/// Main window ViewModel bridging SessionManager, TaskQueue, BillingTimer, and modal interactions.
/// Hardened with health-poll dedupe/staleness escalation (A-07), explicit expiry handling (A-09),
/// script/asset guards (A-10), task recovery (A-13), diagnostics (A-14), and output directory selection (E-01).
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxLogEntries = 300;
    private const int ConsecutiveHealthFailDisconnectedThreshold = 2;
    private const int ConsecutiveHealthFailReprobeThreshold = 3;

    private readonly IColabCli _cli;
    private readonly VmScripts _scripts;
    private readonly SessionManager _session;
    private readonly AccountManager? _accounts;
    private readonly TaskQueue _queue;
    private readonly ThemeService? _themeService;
    private readonly SessionStore? _store;
    private readonly PersistentLog? _persistentLog;
    private readonly IUiTimer _billingUiTimer;
    private readonly IUiTimer _idleHealthTimer;
    private readonly IUiTimer _quotaPollTimer;
    private readonly Action<Action> _dispatchToUi;

    private int _healthPollInFlight;
    private int _quotaPollInFlight;
    private DateTimeOffset _lastPeriodicQuotaPollUtc = DateTimeOffset.MinValue;
    private bool _lowBalanceNotifiedForSession;
    private int _consecutiveHealthFailures;
    private CancellationTokenSource? _healthPollCts;
    private int _precheckDepth;
    private bool _isHandlingExpiry;
    private int _startupProbeRetryRunning;
    private bool _disposed;

    /// <summary>How long the expiry dialog waits before draining the queue by itself (auto-stop sessions only).</summary>
    public TimeSpan BusyExpiryGrace { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long the expiry dialog waits before stopping an idle, deploying or failed session by itself.</summary>
    public TimeSpan IdleExpiryGrace { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Interval between repeated expiry reminders while the session stays over its limit.</summary>
    public TimeSpan ExpiryReminderInterval { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan[] StartupProbeRetryDelays { get; set; } =
    [
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(45),
        TimeSpan.FromSeconds(120)
    ];

    public ObservableCollection<TranslationTaskItem> Tasks => _queue.Items;
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public ObservableCollection<AccountBadgeItem> AccountBadges { get; } = [];

    [ObservableProperty] private string _billingRateDisplay = BillingConstants.L4CuPerHourDisplay;
    [ObservableProperty] private string _idleBillingHintText = $"默认 L4（参考 {BillingConstants.L4CuPerHourDisplay}，启动时可切换档位），运行环境存在即计费。";

    // Events handled by MainWindow (keeps ViewModel clean and testable)
    public event Func<Task<(bool Confirmed, int? PlannedMinutes, bool AutoStop)>>? RequestStartDialog;
    public event Func<StartSessionDialogViewModel, Task<bool>>? RequestStartDialogWithAccount;
    public event Func<ConfirmStopSummary, Task<bool>>? RequestConfirmStopDialog;
    public event Func<TimeExpiredRequest, Task<TimeExpiredAction>>? RequestTimeExpiredDialog;
    public event Func<Task<OrphanSessionAction>>? RequestOrphanSessionDialog;
    public event Func<string, Task<OrphanSessionAction>>? RequestOrphanSessionDialogForAccount;
    public event Func<AccountsDialogViewModel, Task>? RequestShowAccountsDialog;
    public event Action<string>? RequestFiveMinuteWarningToast;
    public event Action<string, string>? RequestSystemNotice;
    public event Action<string, string>? RequestShowLogDialog;
    public event Action<DiagnosticsSnapshot>? RequestShowDiagnosticsDialog;
    public event Func<string?>? RequestBrowseScriptsFolder;
    public event Func<string?>? RequestBrowseOutputFolder;
    public event Func<IReadOnlyList<string>?>? RequestBrowseZipFiles;

    [ObservableProperty] private SessionState _sessionState = SessionState.Idle;
    [ObservableProperty] private bool _hasActiveVm;
    [ObservableProperty] private bool _isProbingStartup;
    [ObservableProperty] private string _runtimeBadgeText = "未运行";
    [ObservableProperty] private string _statusDetailText = "点击「启动服务」申请 Colab GPU 运行环境";
    [ObservableProperty] private string _elapsedDisplay = "未计费";
    [ObservableProperty] private string _billingCuDisplay = "0.00 CU";
    [ObservableProperty] private string _billingPolicyDisplay = "空闲未计费";
    [ObservableProperty] private double _billingProgressPercent;
    [ObservableProperty] private bool _isTimedMode;
    [ObservableProperty] private BillingSeverity _billingSeverity = BillingSeverity.Normal;
    [ObservableProperty] private bool _startTimeUnknown;

    [ObservableProperty] private bool _llamaHealthy;
    [ObservableProperty] private bool _mitHealthy;
    [ObservableProperty] private bool _isHealthDisconnected;
    [ObservableProperty] private string _healthStatusDisplay = "未连接";
    [ObservableProperty] private string _gpuInfoDisplay = "未连接";

    [ObservableProperty] private bool _isDeploying;
    [ObservableProperty] private double _deployProgressPercent;
    [ObservableProperty] private string _deployStageText = string.Empty;

    [ObservableProperty] private bool _hasErrorBanner;
    [ObservableProperty] private string _errorBannerText = string.Empty;
    [ObservableProperty] private bool _hasPersistenceWarning;
    [ObservableProperty] private string _persistenceWarningText = string.Empty;
    [ObservableProperty] private bool _isQueuePaused;
    [ObservableProperty] private string _queuePauseReason = string.Empty;

    [ObservableProperty] private bool _canRetryDeploy;
    [ObservableProperty] private bool _canStartService = true;
    [ObservableProperty] private bool _canStopService;
    [ObservableProperty] private bool _canExtendTime;
    [ObservableProperty] private bool _isDropZoneActive = true;
    [ObservableProperty] private bool _isDragOver;
    [ObservableProperty] private bool _isPrechecking;
    [ObservableProperty] private string _precheckStatusText = string.Empty;
    [ObservableProperty] private string _dropZonePromptText = "拖入漫画 .zip / .cbz（文件夹仅扫描本层 ZIP/CBZ）到此处，或点击选择文件（后台离线预检，不卡界面）";
    [ObservableProperty] private string _environmentWarning = string.Empty;
    [ObservableProperty] private bool _hasEnvironmentWarning;
    [ObservableProperty] private bool _hasTasks;
    [ObservableProperty] private string _themeLabel = "跟随系统";
    [ObservableProperty] private ThemePreference _themePreference = ThemePreference.System;
    [ObservableProperty] private TranslationEngineChoice _enginePreference = TranslationEngineChoice.Mtui;
    [ObservableProperty] private bool _canChangeEngine = true;
    [ObservableProperty] private bool _hasEngineMismatch;
    [ObservableProperty] private string _engineMismatchText = string.Empty;
    [ObservableProperty] private bool _isDarkMode;
    [ObservableProperty] private bool _isFiveMinuteWarningVisible;
    [ObservableProperty] private string _fiveMinuteWarningText = string.Empty;
    [ObservableProperty] private string _latestLogLine = string.Empty;

    [ObservableProperty] private string? _outputDirectory;
    [ObservableProperty] private bool _hasCustomOutputDirectory;
    [ObservableProperty] private string _outputDirectoryDisplay = "成品默认保存至原文件目录";

    // Multi-account & quota UI state (v1.5.0)
    [ObservableProperty] private bool _hasAccountManager;
    [ObservableProperty] private bool _isAutoAccountMode = true;
    [ObservableProperty] private bool _canSwitchAccount = true;
    [ObservableProperty] private bool _isRefreshingQuotas;
    [ObservableProperty] private string _accountModeSummary = "自动按额度";
    [ObservableProperty] private string _activeAccountDisplay = "默认账号";
    [ObservableProperty] private bool _hasOtherAccountWarning;
    [ObservableProperty] private string _otherAccountWarningText = string.Empty;
    [ObservableProperty] private string? _firstOtherAccountWarningId;
    [ObservableProperty] private bool _canStopOtherAccount;
    [ObservableProperty] private bool _hasLowBalanceWarning;
    [ObservableProperty] private string _lowBalanceWarningText = string.Empty;

    public MainViewModel(
        IColabCli cli,
        VmScripts scripts,
        SessionManager session,
        TaskQueue queue,
        ThemeService? themeService = null,
        Action<Action>? dispatchToUi = null,
        SessionStore? store = null,
        Func<TimeSpan, IUiTimer>? timerFactory = null)
    {
        _cli = cli;
        _scripts = scripts;
        _session = session;
        _accounts = session.Accounts;
        _queue = queue;
        _themeService = themeService;
        _store = store;
        _dispatchToUi = dispatchToUi ?? (action => action());
        HasAccountManager = _accounts is not null;

        if (_themeService is not null)
        {
            _themeService.ThemeChanged += OnThemeChanged;
            IsDarkMode = _themeService.IsDarkMode;
            ThemePreference = _themeService.Preference;
            ThemeLabel = ThemeService.FormatPreferenceLabel(_themeService.Preference, _themeService.IsDarkMode);
        }

        if (_store is not null)
        {
            var settings = _store.LoadSettings();
            EnginePreference = TranslationEngines.ToChoice(settings.TranslationEngine);
            var savedOutDir = settings.OutputDir;
            if (!string.IsNullOrWhiteSpace(savedOutDir) && Directory.Exists(savedOutDir))
            {
                ApplyOutputDirectory(savedOutDir, persist: false);
            }
        }

        _persistentLog = store is null ? null : new PersistentLog(store.DataDirectory);

        _session.StateChanged += OnSessionStateChanged;
        _session.AutoStopRetryScheduled += OnAutoStopRetryScheduled;
        _session.LogMessage += OnLogMessage;
        _session.RawOutputRecorded += OnRawOutputRecorded;
        if (_accounts is not null)
        {
            _accounts.AccountsChanged += OnAccountsOrQuotasChanged;
            _accounts.QuotasChanged += OnAccountsOrQuotasChanged;
            _accounts.LogMessage += OnLogMessage;
        }
        _queue.LogMessage += OnLogMessage;
        _queue.RawOutputRecorded += OnRawOutputRecorded;
        _queue.QueueChanged += OnQueueChanged;
        _queue.TaskCompleted += OnTaskCompleted;
        _session.Billing.FiveMinuteWarningTriggered += OnFiveMinuteWarning;
        _session.Billing.TimeLimitReached += OnTimeLimitReached;

        timerFactory ??= interval => new NullUiTimer();
        _billingUiTimer = timerFactory(TimeSpan.FromSeconds(1));
        _billingUiTimer.Tick += (_, _) => RefreshBillingDisplay();
        _billingUiTimer.Start();

        // Single deduplicated polling loop: ONLY polls vm_status.sh when state == Ready (idle).
        // Cancelled immediately upon entering Busy/Deploying/Stopping to guarantee zero SSH contention (A-07).
        _idleHealthTimer = timerFactory(TimeSpan.FromSeconds(15));
        _idleHealthTimer.Tick += async (_, _) => await PollHealthIfIdleAsync();
        _idleHealthTimer.Start();

        // Low-frequency periodic quota check (checks every 60s whether PollIntervalMinutes elapsed).
        _quotaPollTimer = timerFactory(TimeSpan.FromMinutes(1));
        _quotaPollTimer.Tick += async (_, _) => await PollQuotasIfDueAsync();
        _quotaPollTimer.Start();

        RefreshAccountsDisplay();
    }

    [RelayCommand]
    private void SetTheme(ThemePreference preference)
    {
        _themeService?.Apply(preference);
    }

    [RelayCommand]
    private void SetEngine(TranslationEngineChoice engine)
    {
        EnginePreference = engine;
        if (_store is not null)
        {
            var settings = _store.LoadSettings();
            settings.TranslationEngine = engine == TranslationEngineChoice.Mit
                ? TranslationEngines.Mit
                : TranslationEngines.Mtui;
            _store.TrySaveSettings(settings);
        }

        RefreshEngineMismatch();
    }

    private void OnThemeChanged(ThemePreference preference, bool isDark)
    {
        _dispatchToUi(() =>
        {
            IsDarkMode = isDark;
            ThemePreference = preference;
            ThemeLabel = ThemeService.FormatPreferenceLabel(preference, isDark);
        });
    }

    public SessionManager Session => _session;
    public TaskQueue Queue => _queue;
    public AccountManager? Accounts => _accounts;

    public async Task InitializeAsync()
    {
        RunEnvironmentSelfCheck();
        await RunStartupProbeAsync(scheduleRetryOnFailure: true);

        if (_accounts is not null && !_disposed)
        {
            _ = RefreshQuotasBackgroundAsync(force: false);
        }
    }

    /// <summary>
    /// Full startup-style probe: recovers a recorded session, surfaces an orphan one, or cleans a stale
    /// record. Also used by the manual "复查会话" action while idle or in an error state.
    /// </summary>
    private async Task RunStartupProbeAsync(bool scheduleRetryOnFailure)
    {
        _dispatchToUi(() =>
        {
            IsProbingStartup = true;
            RefreshFromSession();
        });

        var outcome = StartupProbeOutcome.ProbeFailed;
        try
        {
            var probe = await _session.ProbeOnStartupAsync();
            outcome = probe.Outcome;

            switch (probe.Outcome)
            {
                case StartupProbeOutcome.RecoveredReady:
                    _queue.StartWorker();
                    _queue.NotifySessionReady();
                    RequestSystemNotice?.Invoke("会话已恢复", "检测到上次 Colab 会话仍在运行，已恢复连接并沿用原计费计时。");
                    break;

                case StartupProbeOutcome.OrphanSessionDetected:
                    var orphanAcctName = _session.ActiveSessionAccountName;
                    var action = RequestOrphanSessionDialogForAccount is not null
                        ? await RequestOrphanSessionDialogForAccount.Invoke(orphanAcctName)
                        : RequestOrphanSessionDialog is not null
                            ? await RequestOrphanSessionDialog.Invoke()
                            : OrphanSessionAction.Dismiss;
                    // Re-read after the awaited dialog: the session may have changed while it was open.
                    if (action == OrphanSessionAction.StopNow)
                    {
                        if (_session.HasActiveVm)
                        {
                            await _session.StopSessionAsync(isAutoStop: false, tryKillTranslate: false);
                        }
                    }
                    else if (action == OrphanSessionAction.Adopt)
                    {
                        var dialog = RequestStartDialog is null
                            ? (Confirmed: false, PlannedMinutes: (int?)null, AutoStop: true)
                            : await RequestStartDialog.Invoke();
                        if (dialog.Confirmed && _session.HasActiveVm)
                        {
                            var ok = await _session.AdoptOrphanSessionAsync(dialog.PlannedMinutes, dialog.AutoStop);
                            if (ok)
                            {
                                _queue.StartWorker();
                                _queue.NotifySessionReady();
                            }
                        }
                    }
                    break;
            }
        }
        finally
        {
            _dispatchToUi(() =>
            {
                IsProbingStartup = false;
                RefreshFromSession();
            });
        }

        if (outcome == StartupProbeOutcome.ProbeFailed && scheduleRetryOnFailure)
        {
            _ = RetryStartupProbeAsync();
        }
    }

    /// <summary>
    /// Re-runs the startup probe a few times after it could not determine the cloud state (network
    /// down, credentials expired), as long as nothing else has changed the session state meanwhile.
    /// </summary>
    private async Task RetryStartupProbeAsync()
    {
        if (Interlocked.Exchange(ref _startupProbeRetryRunning, 1) == 1)
        {
            return;
        }

        try
        {
            foreach (var delay in StartupProbeRetryDelays)
            {
                var errorBefore = _session.LastError;
                await Task.Delay(delay);
                if (_disposed || _session.State != SessionState.Error || _session.LastError != errorBefore)
                {
                    return;
                }

                AppendLog(LogLevel.Info, "正在重新探测云端 Colab 会话状态...");
                await RunStartupProbeAsync(scheduleRetryOnFailure: false);
                if (_session.State != SessionState.Error)
                {
                    return;
                }
            }

            AppendLog(LogLevel.Warning, "多次探测仍无法确认云端会话状态，请检查网络与 colab 登录后点击「复查会话」。");
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Warning, $"重新探测会话状态时发生异常：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _startupProbeRetryRunning, 0);
        }
    }

    public event Func<Task>? RequestEnvironmentCheck;
    [RelayCommand]
    private Task OpenEnvironmentAsync() => RequestEnvironmentCheck?.Invoke() ?? Task.CompletedTask;

    public EnvironmentCheckDialogViewModel CreateEnvironmentCheck() => new(_store ?? throw new InvalidOperationException("设置不可用"), _scripts, _cli, _accounts, RunEnvironmentSelfCheck);

    public void RunEnvironmentSelfCheck()
    {
        var issues = new List<string>();

        if (!_cli.IsColabAvailable(out var colabPath))
        {
            issues.Add("未检测到 colab CLI（google-colab-cli）");
        }
        else
        {
            var mode = _cli.UsesBatchShim ? "cmd 垫片模式" : "直连 exe 模式";
            AppendLog(LogLevel.Info, $"环境自检：已定位 colab CLI（{mode}）→ {colabPath}");
        }

        _scripts.RefreshLocations();
        var missing = _scripts.GetMissingScriptsReport(_session.ConfiguredEngine);
        if (missing.Count > 0)
        {
            issues.AddRange(missing);
        }
        else if (_scripts.ExistingScriptsDirectory is not null)
        {
            var sourceLabel = _scripts.ExistingScriptsSource == ScriptsSourceKind.UserSelected ? "用户指定" : "自动发现";
            AppendLog(LogLevel.Info, $"环境自检：脚本目录（{sourceLabel}）→ {_scripts.ExistingScriptsDirectory}");
        }
        else
        {
            AppendLog(LogLevel.Info, "环境自检：mtui 不需要 manga.json；可单独选择术语表");
        }

        issues.AddRange(_session.GetEnvironmentIssues());
        if (_accounts is not null && !_accounts.GetEnabledAccounts().Any(AccountManager.HasLocalCredentials)) issues.Add("尚未登录 Colab 账号或没有启用账号，请打开账号管理。");

        _dispatchToUi(() =>
        {
            HasEnvironmentWarning = issues.Count > 0;
            EnvironmentWarning = string.Join("；", issues);
        });
    }

    [RelayCommand]
    private async Task StartServiceAsync()
    {
        if (IsProbingStartup || _session.IsProbingStartup)
        {
            AppendLog(LogLevel.Info, "正在完成启动探测，请稍候...");
            return;
        }

        if (!_cli.IsColabAvailable(out _))
        {
            AppendLog(LogLevel.Error, "无法启动：未找到 colab CLI。");
            RunEnvironmentSelfCheck();
            return;
        }

        var engine = _session.ConfiguredEngine;
        if (!_scripts.HasRequiredFilesForEngine(engine))
        {
            _scripts.RefreshLocations();
        }

        // Only the mit engine needs manga.json → the browse prompt is mit-only.
        if (TranslationEngines.Normalize(engine) == TranslationEngines.Mit
            && _scripts.ExistingScriptsDirectory is null)
        {
            var chosen = RequestBrowseScriptsFolder?.Invoke();
            if (string.IsNullOrWhiteSpace(chosen) || !_scripts.TrySetExistingScriptsDirectory(chosen))
            {
                AppendLog(LogLevel.Warning, "未选择有效的 colab_manga 目录（需包含 manga.json）。");
                return;
            }
            RunEnvironmentSelfCheck();
        }

        if (!_scripts.HasRequiredFilesForEngine(engine))
        {
            var report = string.Join("；", _scripts.GetMissingScriptsReport(engine));
            AppendLog(LogLevel.Error, $"无法启动（未申请 GPU）：{report}");
            RunEnvironmentSelfCheck();
            return;
        }

        // Refresh quotas if stale (> 60s) before showing the start dialog so account balances are fresh.
        if (_accounts is not null && RequestStartDialogWithAccount is not null)
        {
            try
            {
                await _accounts.EnsureQuotasFreshAsync(TimeSpan.FromSeconds(60));
                _dispatchToUi(RefreshAccountsDisplay);
            }
            catch
            {
                // Non-fatal: still let user open the dialog and inspect cached/manual choices.
            }
        }

        bool confirmed;
        int? plannedMinutes;
        bool autoStop;
        string? selectedAccountSpec = null;
        string? requestedGpuTier = null;
        string? inpaintMode = null;
        string? g4SubMode = null;

        if (RequestStartDialogWithAccount is not null)
        {
            var dlgVm = BuildStartDialogViewModel();
            confirmed = await RequestStartDialogWithAccount.Invoke(dlgVm);
            plannedMinutes = dlgVm.PlannedMinutes;
            autoStop = dlgVm.EffectiveAutoStop;
            selectedAccountSpec = dlgVm.SelectedAccountSpec;
            requestedGpuTier = dlgVm.SelectedGpuTierId;
            inpaintMode = dlgVm.EffectiveInpaintMode;
            g4SubMode = dlgVm.EffectiveG4SubMode;
        }
        else if (RequestStartDialog is not null)
        {
            var result = await RequestStartDialog.Invoke();
            confirmed = result.Confirmed;
            plannedMinutes = result.PlannedMinutes;
            autoStop = result.AutoStop;
        }
        else
        {
            confirmed = false;
            plannedMinutes = null;
            autoStop = true;
        }

        if (!confirmed)
        {
            return;
        }

        // Persist the user's chosen GPU tier & quality preferences so subsequent sessions remember them.
        if (_store is not null && !string.IsNullOrWhiteSpace(requestedGpuTier))
        {
            var settings = _store.LoadSettings();
            settings.PreferredGpuTier = GpuTiers.Normalize(requestedGpuTier);
            if (!string.IsNullOrWhiteSpace(inpaintMode))
            {
                settings.HighQualityInpaintMode = InpaintModes.Normalize(inpaintMode);
            }
            if (!string.IsNullOrWhiteSpace(g4SubMode))
            {
                settings.G4SubMode = G4SubModes.Normalize(g4SubMode);
            }
            _store.TrySaveSettings(settings);
        }

        // Re-check after the awaited dialog: a session may have appeared while it was open.
        if (IsProbingStartup || _session.IsProbingStartup || _session.HasActiveVm)
        {
            AppendLog(LogLevel.Info, "等待启动确认期间云端会话状态已变化，本次不启动新会话。");
            return;
        }

        IReadOnlyList<ColabAccountProfile>? candidates = null;
        bool allowFailover = false;
        if (_accounts is not null)
        {
            var plan = _accounts.ResolveCandidatesForStart(selectedAccountSpec);
            if (plan.Candidates.Count == 0)
            {
                var err = plan.Warning ?? plan.Reason;
                AppendLog(LogLevel.Error, $"无法启动（未申请 GPU）：{err}");
                RequestSystemNotice?.Invoke("无法启动服务", err);
                return;
            }

            candidates = plan.Candidates;
            allowFailover = plan.AllowFailover;
            AppendLog(LogLevel.Info, $"账号选号：{plan.Reason}");
        }

        _lowBalanceNotifiedForSession = false;
        CancelIdleHealthPoll();
        var ok = await _session.StartOrDeployAsync(
            plannedMinutes,
            autoStop,
            candidateAccounts: candidates,
            allowFailover: allowFailover,
            requestedGpuTier: requestedGpuTier,
            inpaintMode: inpaintMode,
            g4SubMode: g4SubMode);
        if (ok)
        {
            _consecutiveHealthFailures = 0;
            _queue.StartWorker();
            _queue.NotifySessionReady();
        }
    }

    private StartSessionDialogViewModel BuildStartDialogViewModel()
    {
        var vm = new StartSessionDialogViewModel { MtuiEngine = _session.ConfiguredEngine == TranslationEngines.Mtui };
        if (_store is not null)
        {
            var settings = _store.LoadSettings();
            vm.LoadGpuSettings(settings.PreferredGpuTier, settings.HighQualityInpaintMode, settings.G4SubMode);
        }
        else
        {
            vm.LoadGpuSettings(_session.ConfiguredGpuTier, _session.ConfiguredInpaintMode, _session.ConfiguredG4SubMode);
        }

        if (_accounts is null)
        {
            return vm;
        }

        var options = new List<StartAccountOption>();
        var autoPlan = _accounts.ResolveCandidatesForStart(AccountSelectionModes.Auto);
        var topAuto = autoPlan.Candidates.FirstOrDefault();
        var topAutoQuota = topAuto is null ? null : _accounts.GetQuota(topAuto.Id);
        var autoLabel = topAuto is not null
            ? $"自动按最高余额轮询（当前推荐：{topAuto.Name} · {topAutoQuota!.FormatBalanceShort()}）"
            : "自动按最高余额轮询（暂无可用账号）";

        if (topAuto is not null && AccountManager.HasLocalCredentials(topAuto)) options.Add(new StartAccountOption(
            AccountSelectionModes.Auto,
            autoLabel,
            topAutoQuota?.CurrentBalanceCu,
            topAutoQuota?.Status ?? AccountQuotaStatus.Unknown,
            autoPlan.Warning));

        var enabledAccounts = _accounts.GetEnabledAccounts();
        foreach (var acct in enabledAccounts.Where(AccountManager.HasLocalCredentials))
        {
            var q = _accounts.GetQuota(acct.Id);
            var activeSuffix = q.ActiveAssignments is > 0 ? $" · 活跃 {q.ActiveAssignments}" : string.Empty;
            var emailSuffix = string.IsNullOrWhiteSpace(acct.MaskedEmail) ? string.Empty : $" · {acct.MaskedEmail}";
            options.Add(new StartAccountOption(
                acct.Id,
                $"{acct.Name}（{q.FormatBalanceShort()}{activeSuffix}{emailSuffix}）",
                q.CurrentBalanceCu,
                q.Status,
                q.Status == AccountQuotaStatus.AuthExpired ? $"账号「{acct.Name}」授权已失效，请重新登录。" : null));
        }

        var initialSpec = _accounts.SelectionMode == AccountSelectionMode.Manual
            ? _accounts.SelectedAccountId
            : AccountSelectionModes.Auto;
        vm.LoadAccountOptions(options, initialSpec);
        return vm;
    }

    [RelayCommand]
    private async Task RetryDeployAsync()
    {
        CancelIdleHealthPoll();
        var current = _session.Billing.GetCurrentRecord();
        var ok = await _session.StartOrDeployAsync(
            current is null ? 60 : current.PlannedMinutes,
            current?.AutoStop ?? true,
            preserveExistingStartTime: true);
        if (ok)
        {
            _consecutiveHealthFailures = 0;
            _queue.StartWorker();
            _queue.NotifySessionReady();
        }
    }

    [RelayCommand]
    private async Task StopServiceAsync()
    {
        var summary = GetCurrentStopSummary();

        bool confirmed = RequestConfirmStopDialog is null
            ? false
            : await RequestConfirmStopDialog.Invoke(summary);
        if (!confirmed)
        {
            return;
        }

        // Re-check after the awaited confirmation: another path may have stopped the session already.
        if (!_session.HasActiveVm)
        {
            AppendLog(LogLevel.Info, "会话已被停止，跳过重复的停止操作。");
            return;
        }

        await ExecuteStopAsync(isAutoStop: false);
    }

    public async Task<StopResult> ExecuteStopAsync(bool isAutoStop)
    {
        CancelIdleHealthPoll();
        bool hadActiveTask = _queue.HasActiveOrPendingWork;
        _queue.CancelPendingAndActive();
        return await _session.StopSessionAsync(isAutoStop: isAutoStop, tryKillTranslate: hadActiveTask);
    }

    [RelayCommand]
    private async Task ReprobeSessionAsync()
    {
        AppendLog(LogLevel.Info, "正在手动复查云端 Colab 会话状态...");
        if (_session.State is SessionState.Idle or SessionState.Error)
        {
            // Nothing is running locally, so do the full probe: it can recover a recorded session,
            // surface an orphan one, or clear a stale error.
            await RunStartupProbeAsync(scheduleRetryOnFailure: false);
            return;
        }

        var query = await _session.ReprobeSessionStatusAsync();
        if (query.Presence == SessionPresence.Present && _session.State == SessionState.Ready)
        {
            await PollHealthIfIdleAsync();
        }
        RefreshFromSession();
    }

    [RelayCommand]
    private void Extend30Minutes()
    {
        IsFiveMinuteWarningVisible = false;
        _session.ExtendPlannedTime(30);
        RefreshBillingDisplay();
    }

    [RelayCommand]
    private void ResumeQueue()
    {
        _queue.ResumeQueue();
        RefreshQueueState();
    }

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        var files = RequestBrowseZipFiles?.Invoke();
        if (files is { Count: > 0 })
        {
            await EnqueueDroppedFilesAsync(files);
        }
    }

    /// <summary>
    /// Runs folder expansion and ZIP offline pre-check asynchronously on a background thread
    /// so OLE drag-and-drop and the UI thread stay responsive even for large archives.
    /// </summary>
    public async Task EnqueueDroppedFilesAsync(IEnumerable<string> paths)
    {
        if (paths is null) return;
        var copied = paths.ToList();
        if (copied.Count == 0) return;

        _dispatchToUi(() =>
        {
            _precheckDepth++;
            IsPrechecking = true;
            PrecheckStatusText = $"正在离线预检 {copied.Count} 个项目...";
            UpdateDropZonePromptText();
        });

        try
        {
            var outputDir = OutputDirectory;
            var (added, rejectReason) = await _queue.EnqueueFilesAsync(
                copied,
                outputDir,
                onProgress: (index, total, fileName) => _dispatchToUi(() =>
                {
                    PrecheckStatusText = $"正在离线预检 ({index}/{total})：{fileName}";
                    UpdateDropZonePromptText();
                }));

            _dispatchToUi(() =>
            {
                if (!string.IsNullOrWhiteSpace(rejectReason))
                {
                    AppendLog(LogLevel.Warning, rejectReason);
                    RequestSystemNotice?.Invoke(added > 0 ? "部分文件未通过预检" : "无法添加任务", rejectReason);
                }
                else if (added > 0 && _session.State == SessionState.Idle)
                {
                    AppendLog(LogLevel.Info, $"已加入 {added} 个任务（已完成离线预检），启动服务后将自动开始串行翻译。");
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation during shutdown.
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Error, $"处理拖入文件时发生异常：{ex.Message}");
            _dispatchToUi(() => RequestSystemNotice?.Invoke("添加任务失败", ex.Message));
        }
        finally
        {
            _dispatchToUi(() =>
            {
                _precheckDepth = Math.Max(0, _precheckDepth - 1);
                if (_precheckDepth == 0)
                {
                    IsPrechecking = false;
                    PrecheckStatusText = string.Empty;
                }
                UpdateDropZonePromptText();
            });
        }
    }

    public void EnqueueDroppedFiles(IEnumerable<string> paths)
    {
        var (added, rejectReason) = _queue.EnqueueFiles(paths, OutputDirectory);
        if (!string.IsNullOrWhiteSpace(rejectReason))
        {
            AppendLog(LogLevel.Warning, rejectReason);
            RequestSystemNotice?.Invoke(added > 0 ? "部分文件未通过预检" : "无法添加任务", rejectReason);
        }
        else if (added > 0 && _session.State == SessionState.Idle)
        {
            AppendLog(LogLevel.Info, $"已加入 {added} 个任务（已完成离线预检），启动服务后将自动开始串行翻译。");
        }
    }

    [RelayCommand]
    private void RemoveTask(TranslationTaskItem? item)
    {
        if (item is null) return;
        _queue.RemovePendingTask(item);
    }

    [RelayCommand]
    private async Task RetryTaskAsync(TranslationTaskItem? item)
    {
        if (item is null) return;
        var (retried, reason) = await Task.Run(() => _queue.RetryTask(item));
        if (!retried && !string.IsNullOrWhiteSpace(reason))
        {
            AppendLog(LogLevel.Warning, reason);
            RequestSystemNotice?.Invoke("无法重试任务", reason);
        }
    }

    [RelayCommand]
    private void ClearCompletedTasks() => _queue.ClearCompleted();

    [RelayCommand]
    private void ClearFailedTasks() => _queue.ClearFailedAndCancelled();

    [RelayCommand]
    private void SelectOutputDirectory()
    {
        var chosen = RequestBrowseOutputFolder?.Invoke();
        if (!string.IsNullOrWhiteSpace(chosen) && Directory.Exists(chosen))
        {
            ApplyOutputDirectory(chosen, persist: true);
            AppendLog(LogLevel.Success, $"已设置成品输出目录：{chosen}");
        }
    }

    [RelayCommand]
    private void ResetOutputDirectory()
    {
        ApplyOutputDirectory(null, persist: true);
        AppendLog(LogLevel.Info, "已恢复默认输出位置（保存至源漫画同目录）。");
    }

    [RelayCommand]
    private void BrowseScriptsFolder()
    {
        var chosen = RequestBrowseScriptsFolder?.Invoke();
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            if (_scripts.TrySetExistingScriptsDirectory(chosen))
            {
                AppendLog(LogLevel.Success, $"已设置脚本目录（最高优先级）：{chosen}");
                RunEnvironmentSelfCheck();
            }
            else
            {
                AppendLog(LogLevel.Error, "所选目录缺少 manga.json。");
            }
        }
    }

    [RelayCommand]
    private void ViewTaskLog(TranslationTaskItem? item)
    {
        if (item is null) return;
        var content = string.IsNullOrWhiteSpace(item.RawLog) ? "（无详细日志输出）" : item.RawLog;
        RequestShowLogDialog?.Invoke($"失败日志 · {item.FileName}", content);
    }

    [RelayCommand]
    private void ViewLastErrorLog()
    {
        var content = string.IsNullOrWhiteSpace(_session.LastRawOutput)
            ? (_session.LastError ?? "（无原始输出）")
            : _session.LastRawOutput;
        RequestShowLogDialog?.Invoke("会话错误原始输出", content);
    }

    [RelayCommand]
    private void ShowDiagnostics()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
            ?? "1.3.0";
        int plusIdx = version.IndexOf('+');
        if (plusIdx > 0) version = version[..plusIdx];

        var colabAvailable = _cli.IsColabAvailable(out var colabPath);
        List<string>? accountSummaries = null;
        if (_accounts is not null)
        {
            accountSummaries = new List<string>
            {
                $"选号模式：{(IsAutoAccountMode ? "自动按剩余额度轮询" : "手动固定账号")} | 定时轮询：{(_accounts.AutoPollEnabled ? $"每 {_accounts.PollIntervalMinutes} 分钟" : "关闭")} | 低余额门槛：{_accounts.MinBalanceCu:F1} CU"
            };
            foreach (var acct in _accounts.GetAccounts())
            {
                var q = _accounts.GetQuota(acct.Id);
                var fileHint = string.IsNullOrWhiteSpace(acct.AdcCredentialsPath)
                    ? "系统默认 ADC"
                    : Path.GetFileName(acct.AdcCredentialsPath);
                var activeMark = HasActiveVm && string.Equals(_session.ActiveSessionAccountId, acct.Id, StringComparison.OrdinalIgnoreCase)
                    ? " [当前运行]"
                    : string.Empty;
                accountSummaries.Add(
                    $"{acct.Name} ({acct.Id}){activeMark}: {q.FormatBalanceShort()} | 活跃 {q.ActiveAssignments?.ToString() ?? "-"} | {(acct.Enabled ? "启用" : "停用")} | {acct.MaskedEmail ?? "-"} | {fileHint}");
            }
        }

        var snapshot = new DiagnosticsSnapshot(
            AppVersion: $"v{version}",
            DotNetVersion: RuntimeInformation.FrameworkDescription,
            ColabCliStatus: colabAvailable && !string.IsNullOrWhiteSpace(colabPath) ? colabPath : "未检测到 colab CLI",
            ColabLaunchMode: !colabAvailable ? "不可用" : (_cli.UsesBatchShim ? "cmd.exe 垫片回退模式" : "直接启动 colab.exe（无 cmd 注入风险）"),
            ScriptsDirectory: _scripts.ExistingScriptsDirectory ?? "未找到",
            ScriptsSource: _scripts.ExistingScriptsSource switch
            {
                ScriptsSourceKind.UserSelected => "用户手动指定（settings.json，最高优先级）",
                ScriptsSourceKind.AutoDiscovered => "上级目录自动发现",
                _ => "未配置"
            },
            BundledAssetsDirectory: _scripts.BundledAssetsDirectory ?? "未找到",
            SessionStorePath: _store?.SessionFilePath ?? "%APPDATA%\\manga_gui\\session.json",
            AssetChecksums: _scripts.GetBundledAssetChecksums(),
            IsElevated: IsProcessElevated(),
            CrashLogPath: App.CrashLogPath,
            AccountSummaries: accountSummaries);

        RequestShowDiagnosticsDialog?.Invoke(snapshot);
    }

    [RelayCommand]
    private async Task RefreshQuotasAsync()
    {
        if (_accounts is null) return;
        await RefreshQuotasBackgroundAsync(force: true);
    }

    [RelayCommand]
    private void SetAutoAccountMode()
    {
        if (_accounts is null) return;
        if (_session.HasActiveVm)
        {
            RequestSystemNotice?.Invoke("无法切换账号", $"当前由「{_session.ActiveSessionAccountName}」运行中，停止计费后可切换。");
            return;
        }

        _accounts.SetSelectionPolicy(AccountSelectionMode.AutoHighestBalance);
        AppendLog(LogLevel.Info, "已切换为「自动按剩余额度轮询」模式。");
    }

    [RelayCommand]
    private void SelectManualAccount(string? accountId)
    {
        if (_accounts is null || string.IsNullOrWhiteSpace(accountId)) return;
        if (_session.HasActiveVm)
        {
            RequestSystemNotice?.Invoke("无法切换账号", $"当前由「{_session.ActiveSessionAccountName}」运行中，停止计费后可切换。");
            return;
        }

        var acct = _accounts.GetAccountById(accountId);
        if (acct is null) return;
        _accounts.SetSelectionPolicy(AccountSelectionMode.Manual, acct.Id);
        AppendLog(LogLevel.Info, $"已切换为手动模式，当前账号：{acct.Name}。");
    }

    [RelayCommand]
    private async Task OpenAccountsDialogAsync()
    {
        if (_accounts is null || RequestShowAccountsDialog is null) return;
        var dlgVm = new AccountsDialogViewModel(
            _accounts,
            _store?.DataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "manga_gui"),
            () => _session.HasActiveVm,
            () => _session.ActiveSessionAccountId,
            _dispatchToUi);
        await RequestShowAccountsDialog.Invoke(dlgVm);
        RefreshAccountsDisplay();
    }

    [RelayCommand]
    private async Task StopOtherAccountSessionAsync()
    {
        var targetId = FirstOtherAccountWarningId;
        if (string.IsNullOrWhiteSpace(targetId)) return;

        CanStopOtherAccount = false;
        try
        {
            var res = await _session.StopOtherAccountSessionAsync(targetId);
            RequestSystemNotice?.Invoke(
                res.VerifiedStopped ? "已停止另一账号会话" : "停止另一账号会话失败",
                res.Message);
        }
        finally
        {
            _dispatchToUi(RefreshFromSession);
        }
    }

    private async Task RefreshQuotasBackgroundAsync(bool force)
    {
        if (_accounts is null || _disposed) return;
        if (Interlocked.CompareExchange(ref _quotaPollInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await _accounts.RefreshAllQuotasAsync(force: force);
            _lastPeriodicQuotaPollUtc = DateTimeOffset.UtcNow;
            _dispatchToUi(() =>
            {
                RefreshAccountsDisplay();
                EvaluateActiveLowBalanceWarning();
            });
        }
        catch (OperationCanceledException)
        {
            // Ignore during shutdown.
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Warning, $"刷新账号额度时发生异常：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _quotaPollInFlight, 0);
        }
    }

    private async Task PollQuotasIfDueAsync()
    {
        if (_accounts is null || _disposed || !_accounts.AutoPollEnabled)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(_accounts.PollIntervalMinutes);
        if (_lastPeriodicQuotaPollUtc != DateTimeOffset.MinValue
            && (DateTimeOffset.UtcNow - _lastPeriodicQuotaPollUtc) < interval)
        {
            return;
        }

        if (_session.HasActiveVm && !string.IsNullOrWhiteSpace(_session.ActiveSessionAccountId))
        {
            // While a session is running, only poll the active account's lightweight HTTPS usage endpoint
            // so we stay informed of low balance without extra background churn.
            if (Interlocked.CompareExchange(ref _quotaPollInFlight, 1, 0) != 0)
            {
                return;
            }

            try
            {
                await _accounts.RefreshAccountQuotaAsync(_session.ActiveSessionAccountId);
                _lastPeriodicQuotaPollUtc = DateTimeOffset.UtcNow;
                _dispatchToUi(() =>
                {
                    RefreshAccountsDisplay();
                    EvaluateActiveLowBalanceWarning();
                });
            }
            catch
            {
                // Ignore periodic polling failures.
            }
            finally
            {
                Interlocked.Exchange(ref _quotaPollInFlight, 0);
            }
        }
        else
        {
            await RefreshQuotasBackgroundAsync(force: false);
        }
    }

    private void EvaluateActiveLowBalanceWarning()
    {
        if (_accounts is null || !_session.HasActiveVm || string.IsNullOrWhiteSpace(_session.ActiveSessionAccountId))
        {
            HasLowBalanceWarning = false;
            LowBalanceWarningText = string.Empty;
            return;
        }

        var q = _accounts.GetQuota(_session.ActiveSessionAccountId);
        if (q.Status == AccountQuotaStatus.Ok && q.CurrentBalanceCu is { } bal && bal < _accounts.MinBalanceCu)
        {
            var backup = _accounts.FindHigherBalanceBackup(_session.ActiveSessionAccountId);
            var backupHint = backup.HasValue
                ? $"；备用账号「{backup.Value.Account.Name}」剩余 {backup.Value.Quota.CurrentBalanceCu:F2} CU，可待当前本完成后停机切换。"
                : "；请留意剩余额度，及时收尾停机。";
            LowBalanceWarningText = $"当前运行账号「{_session.ActiveSessionAccountName}」仅剩 {bal:F2} CU（低于 {_accounts.MinBalanceCu:F1} CU）{backupHint}";
            HasLowBalanceWarning = true;
            if (!_lowBalanceNotifiedForSession)
            {
                _lowBalanceNotifiedForSession = true;
                AppendLog(LogLevel.Warning, LowBalanceWarningText);
                RequestSystemNotice?.Invoke("账号余额偏低", LowBalanceWarningText);
            }
        }
        else
        {
            HasLowBalanceWarning = false;
            LowBalanceWarningText = string.Empty;
        }
    }

    private void OnAccountsOrQuotasChanged() => _dispatchToUi(RefreshAccountsDisplay);

    private void RefreshAccountsDisplay()
    {
        if (_accounts is null)
        {
            HasAccountManager = false;
            return;
        }

        HasAccountManager = true;
        IsRefreshingQuotas = _accounts.IsRefreshing;
        IsAutoAccountMode = _accounts.SelectionMode == AccountSelectionMode.AutoHighestBalance;
        CanSwitchAccount = !_session.HasActiveVm && !IsProbingStartup && !_session.IsProbingStartup;

        var activeOwnerId = _session.HasActiveVm ? _session.ActiveSessionAccountId : null;
        var plan = _accounts.ResolveCandidatesForStart();
        var recommendedId = plan.Candidates.FirstOrDefault()?.Id;
        var quotas = _accounts.GetAllQuotas();
        var minCu = _accounts.MinBalanceCu;

        var enabled = _accounts.GetEnabledAccounts();

        AccountBadges.Clear();
        foreach (var acct in enabled)
        {
            var q = quotas.TryGetValue(acct.Id, out var snap) ? snap : AccountQuotaSnapshot.Unknown(acct.Id);
            AccountBadges.Add(new AccountBadgeItem(
                acct,
                q,
                isActiveOwner: activeOwnerId is not null && string.Equals(activeOwnerId, acct.Id, StringComparison.OrdinalIgnoreCase),
                isRecommendedOrSelected: recommendedId is not null && string.Equals(recommendedId, acct.Id, StringComparison.OrdinalIgnoreCase),
                minBalanceCu: minCu));
        }

        if (_session.HasActiveVm)
        {
            ActiveAccountDisplay = _session.ActiveSessionAccountName;
            AccountModeSummary = $"当前计费：{_session.ActiveSessionAccountName}";
        }
        else
        {
            var target = plan.Candidates.FirstOrDefault();
            ActiveAccountDisplay = target is null ? "没有可用账号" : AccountManager.HasLocalCredentials(target) ? target.Name : "尚未登录 Colab 账号";
            AccountModeSummary = IsAutoAccountMode
                ? "自动按额度"
                : $"手动 · {ActiveAccountDisplay}";
        }

        EvaluateActiveLowBalanceWarning();
    }

    [RelayCommand]
    private void OpenOutputFolder(TranslationTaskItem? item)
    {
        if (item?.OutputZipPath is null || !File.Exists(item.OutputZipPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.OutputZipPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore shell launch issues.
        }
    }

    public ConfirmStopSummary GetCurrentStopSummary()
    {
        var snap = _session.Billing.Evaluate(fireEvents: false);
        return new ConfirmStopSummary(
            FormatElapsed(snap.Elapsed),
            $"约 {snap.EstimatedCu:F2} CU",
            _queue.UnfinishedCount,
            _session.State == SessionState.Deploying,
            _accounts is { HasMultipleAccounts: true } ? _session.ActiveSessionAccountName : null);
    }

    private void ApplyOutputDirectory(string? dir, bool persist)
    {
        var normalized = string.IsNullOrWhiteSpace(dir) ? null : dir.Trim();
        OutputDirectory = normalized;
        HasCustomOutputDirectory = normalized is not null;
        OutputDirectoryDisplay = normalized is null
            ? "成品默认保存至原文件目录"
            : $"成品输出：{normalized}";

        if (persist && _store is not null)
        {
            var settings = _store.LoadSettings();
            settings.OutputDir = normalized;
            _store.TrySaveSettings(settings);
        }
    }

    private void CancelIdleHealthPoll()
    {
        var cts = Interlocked.Exchange(ref _healthPollCts, null);
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
    }

    /// <summary>
    /// Deduplicated, cancellable idle health poll with staleness marking & automatic session reprobe (A-07).
    /// </summary>
    private async Task PollHealthIfIdleAsync()
    {
        if (_session.State != SessionState.Ready)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _healthPollInFlight, 1, 0) != 0)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _healthPollCts, cts);
        prev?.Dispose();

        try
        {
            var snapshot = await _session.PollStatusAsync(activeJobOrOutDir: null, cts.Token);
            if (cts.IsCancellationRequested || _session.State != SessionState.Ready)
            {
                return;
            }

            if (snapshot is not null)
            {
                _consecutiveHealthFailures = 0;
                return;
            }

            _consecutiveHealthFailures++;
            if (_consecutiveHealthFailures >= ConsecutiveHealthFailDisconnectedThreshold)
            {
                _session.MarkHealthDisconnected($"空闲健康检查连续 {_consecutiveHealthFailures} 次失败，连接可能已失联。");
            }

            if (_consecutiveHealthFailures >= ConsecutiveHealthFailReprobeThreshold)
            {
                _consecutiveHealthFailures = 0;
                AppendLog(LogLevel.Warning, "健康检查连续 3 次失联，自动执行 colab sessions 复查云端会话是否存活...");
                await _session.ReprobeSessionStatusAsync(cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled cleanly when transitioning out of Ready.
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Warning, $"空闲健康检查发生异常：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _healthPollInFlight, 0);
        }
    }

    private void OnSessionStateChanged()
    {
        if (_session.State != SessionState.Ready)
        {
            CancelIdleHealthPoll();
        }
        _dispatchToUi(RefreshFromSession);
    }

    private void OnQueueChanged() => _dispatchToUi(RefreshQueueState);

    /// <summary>
    /// Engine submenu stays enabled only while nothing is deploying or running (D10), and an
    /// InfoBar appears when the live session's engine differs from the configured one.
    /// </summary>
    private void RefreshEngineMismatch()
    {
        CanChangeEngine = _session.State != SessionState.Deploying && !_queue.HasActiveOrPendingWork;
        var status = _session.LatestStatus;
        var configured = EnginePreference == TranslationEngineChoice.Mit
            ? TranslationEngines.Mit
            : TranslationEngines.Mtui;
        var deployed = status.DeployedEngine;
        HasEngineMismatch = _session.HasActiveVm
            && !status.IsDisconnected
            && deployed is not null
            && deployed != configured;
        EngineMismatchText = HasEngineMismatch
            ? $"当前会话使用 {TranslationEngines.DisplayName(deployed)}，切换到 {TranslationEngines.DisplayName(configured)} 需要重试部署"
            : string.Empty;
        UpdateRetryDeploy();
    }

    /// <summary>
    /// G3: a live session may be redeployed in place — on Error as before, and also on Ready with an
    /// engine mismatch while the queue is idle (in-session engine switch; billing start preserved).
    /// Recomputed from every input source: session refresh, SetEngine, mismatch refresh, queue changes.
    /// </summary>
    private void UpdateRetryDeploy()
    {
        var probing = IsProbingStartup || _session.IsProbingStartup;
        var state = _session.State;
        CanRetryDeploy = !probing && _session.HasActiveVm
            && (state == SessionState.Error
                || (state == SessionState.Ready && HasEngineMismatch && !_queue.HasActiveOrPendingWork));
    }

    private void RefreshQueueState()
    {
        CanChangeEngine = _session.State != SessionState.Deploying && !_queue.HasActiveOrPendingWork;
        HasTasks = Tasks.Count > 0;
        IsQueuePaused = _queue.IsQueuePaused;
        QueuePauseReason = _queue.PauseReason ?? string.Empty;
        UpdateRetryDeploy();
    }

    private void OnTaskCompleted(TranslationTaskItem item)
    {
        _dispatchToUi(() =>
        {
            RequestSystemNotice?.Invoke("翻译完成", $"{item.FileName} 已通过校验并保存至本地。");
        });
    }

    private void OnFiveMinuteWarning(object? sender, BillingSnapshot snapshot)
    {
        _dispatchToUi(() =>
        {
            var msg = $"当前运行已 {FormatElapsed(snapshot.Elapsed)}（约 {snapshot.EstimatedCu:F2} CU），距离设定时长仅剩不足 5 分钟。";
            AppendLog(LogLevel.Warning, msg);
            FiveMinuteWarningText = msg;
            IsFiveMinuteWarningVisible = true;
            RequestFiveMinuteWarningToast?.Invoke(msg);
        });
    }

    private void OnTimeLimitReached(object? sender, BillingSnapshot snapshot)
    {
        // Avoid async void inside dispatcher lambda; route into a guarded Task method.
        _dispatchToUi(() => _ = HandleTimeLimitReachedSafeAsync(snapshot));
    }

    private async Task HandleTimeLimitReachedSafeAsync(BillingSnapshot snapshot)
    {
        if (_isHandlingExpiry || !_session.HasActiveVm)
        {
            return;
        }

        var stateAtExpiry = _session.State;
        if (stateAtExpiry is SessionState.Stopping or SessionState.AutoStopping or SessionState.Draining)
        {
            return;
        }

        if (stateAtExpiry == SessionState.Idle || _session.IsProbingStartup)
        {
            // The startup probe is still verifying a recorded session; ask again once it has settled.
            _session.Billing.SnoozeExpiry(TimeSpan.FromSeconds(30));
            return;
        }

        _isHandlingExpiry = true;
        bool remindLater = false;
        try
        {
            IsFiveMinuteWarningVisible = false;
            var record = _session.Billing.GetCurrentRecord();
            bool autoStopEnabled = record?.AutoStop ?? true;

            if (autoStopEnabled && !_queue.HasActiveOrPendingWork && _session.State == SessionState.Ready)
            {
                AppendLog(LogLevel.Warning, "已到达设定时长且任务队列空闲，正在自动停止计费...");
                var res = await _session.AutoStopUntilVerifiedAsync(tryKillTranslate: false);
                if (res.VerifiedStopped)
                {
                    RequestSystemNotice?.Invoke("已按时长停止计费", $"本次运行 {FormatElapsed(snapshot.Elapsed)}，估算 {snapshot.EstimatedCu:F2} CU。{res.Message}");
                }
                else
                {
                    remindLater = true;
                }
                return;
            }

            // Determine exact scenario for TimeExpiredDialog (A-09)
            var scenario = _session.State switch
            {
                SessionState.Deploying => TimeExpiredScenario.DeployingTimeout,
                SessionState.Error => TimeExpiredScenario.ErrorWhileBilling,
                SessionState.Ready when !_queue.HasActiveOrPendingWork => TimeExpiredScenario.IdleNotAutoStop,
                _ => TimeExpiredScenario.BusyWithTasks
            };

            // With auto-stop enabled the dialog falls back to the safe action when nobody answers:
            // drain the queue then stop while translating, stop outright otherwise. Reminder-only
            // sessions never act by themselves; they are reminded again later.
            var autoAction = TimeExpiredAction.None;
            var autoDelay = TimeSpan.Zero;
            if (autoStopEnabled)
            {
                if (scenario == TimeExpiredScenario.BusyWithTasks)
                {
                    autoAction = TimeExpiredAction.DrainThenStop;
                    autoDelay = BusyExpiryGrace;
                }
                else
                {
                    autoAction = TimeExpiredAction.StopImmediately;
                    autoDelay = IdleExpiryGrace;
                }
            }

            AppendLog(LogLevel.Warning, "已到达设定运行时长，弹出决策确认窗等待处理...");
            var choice = RequestTimeExpiredDialog is null
                ? TimeExpiredAction.None
                : await RequestTimeExpiredDialog.Invoke(new TimeExpiredRequest(
                    FormatElapsed(snapshot.Elapsed),
                    $"约 {snapshot.EstimatedCu:F2} CU",
                    _queue.UnfinishedCount,
                    scenario,
                    autoAction,
                    autoDelay,
                    _accounts is { HasMultipleAccounts: true } ? _session.ActiveSessionAccountName : null));

            // Re-read after the awaited dialog: the session may have been stopped while it was open.
            if (!_session.HasActiveVm)
            {
                return;
            }

            switch (choice)
            {
                case TimeExpiredAction.Extend30Minutes:
                    _session.ExtendPlannedTime(30);
                    break;

                case TimeExpiredAction.DrainThenStop:
                    _session.TransitionToDraining();
                    bool nothingCanRun = !_queue.HasActiveOrPendingWork
                                         || (_queue.IsQueuePaused && !_queue.HasActiveTask);
                    if (nothingCanRun && _session.State == SessionState.Draining)
                    {
                        var drained = await _session.AutoStopUntilVerifiedAsync(tryKillTranslate: false);
                        remindLater = !drained.VerifiedStopped;
                    }
                    break;

                case TimeExpiredAction.StopImmediately:
                    var stopped = await ExecuteStopAsync(isAutoStop: false);
                    if (!stopped.VerifiedStopped)
                    {
                        stopped = await _session.AutoStopUntilVerifiedAsync(tryKillTranslate: false);
                        remindLater = !stopped.VerifiedStopped;
                    }
                    break;

                case TimeExpiredAction.None:
                default:
                    // A-09: NEVER silently extend when user closes the dialog with X!
                    AppendLog(LogLevel.Warning, $"已关闭到期弹窗（未自动延长时长），Colab 运行环境仍在持续计费；{ExpiryReminderInterval.TotalMinutes:0.#} 分钟后会再次提醒。");
                    remindLater = true;
                    break;
            }
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Error, $"处理运行时长到期事件时发生异常：{ex.Message}");
            remindLater = true;
        }
        finally
        {
            _isHandlingExpiry = false;
            if (remindLater && _session.HasActiveVm)
            {
                _session.Billing.SnoozeExpiry(ExpiryReminderInterval);
            }
        }
    }

    private void OnAutoStopRetryScheduled(int attempt, TimeSpan delay)
    {
        _dispatchToUi(() => RequestSystemNotice?.Invoke(
            "自动停止未成功",
            $"云端可能仍在计费，将在 {(delay.TotalMinutes >= 1 ? $"{delay.TotalMinutes:0.#} 分钟" : $"{delay.TotalSeconds:0} 秒")}后进行第 {attempt} 次重试；也可以手动点击「停止并结束计费」。"));
    }

    private void RefreshFromSession()
    {
        var state = _session.State;
        var hasVm = _session.HasActiveVm;
        var status = _session.LatestStatus;
        var probing = IsProbingStartup || _session.IsProbingStartup;

        SessionState = state;
        HasActiveVm = hasVm;
        if (!hasVm)
        {
            IsFiveMinuteWarningVisible = false;
        }
        StatusDetailText = probing
            ? "正在探测云端 Colab 会话状态..."
            : !hasVm && state == SessionState.Idle
                ? "点击「启动服务」申请 Colab GPU 运行环境"
                : _session.StatusDetail;
        LlamaHealthy = status.LlamaOk && !status.IsDisconnected;
        MitHealthy = status.MitOk && !status.IsDisconnected;
        IsHealthDisconnected = hasVm && status.IsDisconnected;

        HealthStatusDisplay = !hasVm
            ? "未连接"
            : status.IsDisconnected
                ? $"失联（上次 {status.CheckedAt:HH:mm:ss}）"
                : $"已更新 {status.CheckedAt:HH:mm:ss}";

        GpuInfoDisplay = FormatGpuInfoDisplay(hasVm, status);

        RuntimeBadgeText = probing
            ? "探测中"
            : state switch
            {
                SessionState.Idle => "未运行",
                SessionState.Deploying => "部署中",
                SessionState.Ready => status.IsDisconnected ? "连接失联" : "服务就绪",
                SessionState.Busy => "翻译中",
                SessionState.Draining => "收尾中",
                SessionState.Stopping => "停止中",
                SessionState.AutoStopping => "自动停止中",
                SessionState.Error => hasVm ? "异常（仍在计费）" : "错误",
                _ => state.ToString()
            };

        IsDeploying = state == SessionState.Deploying;
        RefreshEngineMismatch();
        DeployStageText = SessionManager.FormatDeployStage(_session.DeployStage);
        DeployProgressPercent = _session.DeployStage switch
        {
            DeployStage.CheckingSession => 5,
            DeployStage.CreatingRuntime => 15,
            DeployStage.UploadingScripts => 25,
            DeployStage.Stage1TranslatorEnv => 40,
            DeployStage.Stage2LlamaModel => 62,
            DeployStage.Stage3EnvScript => 75,
            DeployStage.Stage4LlamaServer => 85,
            DeployStage.Stage5TranslatorWarmup => 92,
            DeployStage.VerifyingHealth => 97,
            DeployStage.Completed => 100,
            _ => 0
        };

        HasErrorBanner = state == SessionState.Error && !string.IsNullOrWhiteSpace(_session.LastError);
        ErrorBannerText = _session.LastError ?? string.Empty;

        var accountWarnings = new[] { _session.UnnamedAssignmentWarning, _session.PersistenceWarning }
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .ToArray();
        HasPersistenceWarning = accountWarnings.Length > 0;
        PersistenceWarningText = string.Join("；", accountWarnings);

        var otherWarnings = _session.OtherAccountWarnings;
        HasOtherAccountWarning = otherWarnings.Count > 0;
        OtherAccountWarningText = HasOtherAccountWarning
            ? string.Join(" ", otherWarnings.Select(w => w.Message))
            : string.Empty;
        var stoppableOther = otherWarnings.FirstOrDefault(w => w.HasMangaSession);
        FirstOtherAccountWarningId = stoppableOther?.AccountId;
        CanStopOtherAccount = stoppableOther is not null;

        CanStartService = !probing && (state == SessionState.Idle || (state == SessionState.Error && !hasVm));
        UpdateRetryDeploy();
        CanStopService = hasVm && state is not (SessionState.Stopping or SessionState.AutoStopping);
        CanExtendTime = hasVm && state is SessionState.Ready or SessionState.Busy or SessionState.Draining;
        IsDropZoneActive = state != SessionState.Draining;
        UpdateDropZonePromptText();

        RefreshAccountsDisplay();
        RefreshQueueState();
        RefreshBillingDisplay();
    }

    private void UpdateDropZonePromptText()
    {
        if (IsPrechecking && !string.IsNullOrWhiteSpace(PrecheckStatusText))
        {
            DropZonePromptText = PrecheckStatusText;
        }
        else if (_session.State == SessionState.Draining)
        {
            DropZonePromptText = "收尾模式：当前队列完成后将自动停止计费，暂不接受新任务（点击「+30 分钟」可恢复添加）";
        }
        else
        {
            DropZonePromptText = "拖入漫画 .zip / .cbz（文件夹仅扫描本层 ZIP/CBZ）到此处，或点击选择文件（后台离线预检，不卡界面）";
        }
    }

    private string FormatGpuInfoDisplay(bool hasVm, VmStatusSnapshot status)
    {
        if (string.IsNullOrWhiteSpace(status.GpuInfo))
        {
            if (!hasVm) return "未连接";
            if (status.IsDisconnected) return "失联（无响应）";
            var reqSpec = GpuTiers.GetSpec(_session.ActiveRequestedGpuTier);
            return $"{reqSpec.ShortName} (检测中)";
        }

        var parts = new List<string> { status.GpuInfo };
        var badge = GpuProfiles.ShortBadge(status.GpuProfile ?? _session.ActiveDetectedGpuProfile);
        if (!string.IsNullOrWhiteSpace(badge))
        {
            parts.Add(badge);
        }

        if (status.FallbackLlamaOk == true)
        {
            parts.Add("兜底 :6007 ✓");
        }
        else if (status.FallbackLlamaOk == false)
        {
            parts.Add("兜底 :6007 ⚠");
        }

        if (string.Equals(status.InpaintMode, InpaintModes.Flux2Klein, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("Flux2");
        }

        return string.Join(" · ", parts);
    }

    private void RefreshBillingDisplay()
    {
        var snap = _session.Billing.Evaluate(fireEvents: _session.HasActiveVm);

        if (!snap.IsRunning)
        {
            var prefSpec = GpuTiers.GetSpec(_session.ConfiguredGpuTier);
            BillingRateDisplay = prefSpec.RateDisplay;
            IdleBillingHintText = $"默认 {prefSpec.ShortName}（参考 {prefSpec.RateDisplay}，启动时可切换档位），运行环境存在即计费。";
            ElapsedDisplay = "未计费";
            BillingCuDisplay = "0.00 CU";
            BillingPolicyDisplay = "空闲未计费";
            BillingProgressPercent = 0;
            IsTimedMode = false;
            BillingSeverity = BillingSeverity.Normal;
            StartTimeUnknown = false;
            return;
        }

        BillingRateDisplay = BillingConstants.FormatRate(snap.CuPerHour);

        StartTimeUnknown = snap.StartTimeUnknown;
        var elapsedStr = FormatElapsed(snap.Elapsed);
        var record = _session.Billing.GetCurrentRecord();
        var deadlineLocal = snap.DeadlineUtc?.ToLocalTime();

        if (snap.PlannedMinutes is int planned)
        {
            ElapsedDisplay = snap.StartTimeUnknown
                ? $"接管 {elapsedStr} / {planned} min"
                : $"{elapsedStr} / {planned} min";
            BillingProgressPercent = snap.ProgressRatio * 100.0;
            IsTimedMode = true;
            var autoText = (record?.AutoStop ?? true) ? "到期空闲自动停" : "到期仅提醒";
            BillingPolicyDisplay = deadlineLocal.HasValue
                ? $"{autoText} · 预计 {deadlineLocal.Value:HH:mm}"
                : autoText;
        }
        else
        {
            ElapsedDisplay = snap.StartTimeUnknown
                ? $"接管 {elapsedStr}（不限）"
                : $"{elapsedStr}（不限）";
            BillingProgressPercent = 0;
            IsTimedMode = false;
            BillingPolicyDisplay = "不限时长 · 需手动停止计费";
        }

        BillingCuDisplay = snap.StartTimeUnknown
            ? $"接管后 ≈ {snap.EstimatedCu:F2} CU"
            : $"≈ {snap.EstimatedCu:F2} CU";
        BillingSeverity = snap.Severity;
    }

    private void OnLogMessage(LogLevel level, string message) => AppendLog(level, message);

    private void OnRawOutputRecorded(string title, string content) =>
        _persistentLog?.AppendRawOutput(title, content);

    private void AppendLog(LogLevel level, string message)
    {
        _persistentLog?.Append(level, message);
        _dispatchToUi(() =>
        {
            var entry = new LogEntry(DateTimeOffset.Now, level, message);
            Logs.Add(entry);
            LatestLogLine = entry.FormattedLine;
            while (Logs.Count > MaxLogEntries)
            {
                Logs.RemoveAt(0);
            }
        });
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        int totalMinutes = (int)Math.Floor(elapsed.TotalMinutes);
        if (totalMinutes < 1)
        {
            return $"{Math.Max(0, (int)elapsed.TotalSeconds)}s";
        }
        return $"{totalMinutes} min";
    }

    private static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelIdleHealthPoll();
        _billingUiTimer.Stop();
        _idleHealthTimer.Stop();
        _quotaPollTimer.Stop();
        _session.StateChanged -= OnSessionStateChanged;
        _session.AutoStopRetryScheduled -= OnAutoStopRetryScheduled;
        _session.LogMessage -= OnLogMessage;
        _session.RawOutputRecorded -= OnRawOutputRecorded;
        if (_accounts is not null)
        {
            _accounts.AccountsChanged -= OnAccountsOrQuotasChanged;
            _accounts.QuotasChanged -= OnAccountsOrQuotasChanged;
            _accounts.LogMessage -= OnLogMessage;
        }
        _queue.LogMessage -= OnLogMessage;
        _queue.RawOutputRecorded -= OnRawOutputRecorded;
        _queue.QueueChanged -= OnQueueChanged;
        _queue.TaskCompleted -= OnTaskCompleted;
        _session.Billing.FiveMinuteWarningTriggered -= OnFiveMinuteWarning;
        _session.Billing.TimeLimitReached -= OnTimeLimitReached;
        if (_themeService is not null)
        {
            _themeService.ThemeChanged -= OnThemeChanged;
        }
    }
}
