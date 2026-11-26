using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MangaGui.Models;

namespace MangaGui.Services;

/// <summary>
/// Manages multiple Colab ADC accounts, concurrent quota polling (`colab usage`),
/// smart highest-balance account selection with safe failover ordering, and duplicate-safe
/// credential import/validation. Shared by both MangaGui and MangaCli through AppServices.
/// </summary>
public sealed class AccountManager
{
    public const double DefaultMinBalanceCu = 2.0;
    public const int DefaultPollIntervalMinutes = 10;
    public const int MinPollIntervalMinutes = 5;
    public const int MaxPollIntervalMinutes = 60;

    private static readonly TimeSpan MinRefreshCooldown = TimeSpan.FromSeconds(10);

    private static readonly Regex BalanceRegex = new(
        @"Current\s+balance\s*:\s*([+-]?\d+(?:\.\d+)?)\s*compute\s+units",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UsageRateRegex = new(
        @"Usage\s+rate\s*:\s*([+-]?\d+(?:\.\d+)?)\s*/\s*hr",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AssignmentsRegex = new(
        @"Active\s+assignments\s*:\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WhoamiEmailRegex = new(
        @"^\s*Email\s*:\s*([^\s@]+@[^\s@]+)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex AuthExpiredRegex = new(
        @"(?i)(invalid_grant|RefreshError|Reauthentication\s+is\s+needed|No\s+valid\s+default\s+credentials|HTTP/?[\d.]*\s*401|status[\s:=]+401|UNAUTHENTICATED|token\s+has\s+been\s+expired\s+or\s+revoked)",
        RegexOptions.Compiled);

    private readonly IColabCli _cli;
    private readonly SessionStore _store;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private readonly List<ColabAccountProfile> _accounts = [];
    private readonly Dictionary<string, AccountQuotaSnapshot> _quotas = new(StringComparer.OrdinalIgnoreCase);

    private AccountSelectionMode _selectionMode = AccountSelectionMode.AutoHighestBalance;
    private string? _selectedAccountId;
    private bool _autoPollEnabled = true;
    private int _pollIntervalMinutes = DefaultPollIntervalMinutes;
    private double _minBalanceCu = DefaultMinBalanceCu;
    private DateTimeOffset _lastFullRefreshUtc = DateTimeOffset.MinValue;
    private bool _isRefreshing;

    public event Action? AccountsChanged;
    public event Action? QuotasChanged;
    public event Action<LogLevel, string>? LogMessage;

    public string AccountsDirectory => Path.Combine(_store.DataDirectory, "accounts");

    public AccountManager(
        IColabCli cli,
        SessionStore store,
        Func<DateTimeOffset>? utcNow = null)
    {
        _cli = cli;
        _store = store;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        ReloadFromStore();
    }

    public bool IsRefreshing
    {
        get { lock (_lock) return _isRefreshing; }
    }

    public AccountSelectionMode SelectionMode
    {
        get { lock (_lock) return _selectionMode; }
    }

    public string? SelectedAccountId
    {
        get { lock (_lock) return _selectedAccountId; }
    }

    public bool AutoPollEnabled
    {
        get { lock (_lock) return _autoPollEnabled; }
    }

    public int PollIntervalMinutes
    {
        get { lock (_lock) return _pollIntervalMinutes; }
    }

    public double MinBalanceCu
    {
        get { lock (_lock) return _minBalanceCu; }
    }

    public IReadOnlyList<ColabAccountProfile> GetAccounts()
    {
        lock (_lock)
        {
            return _accounts.Select(CloneProfile).ToList();
        }
    }

    public IReadOnlyList<ColabAccountProfile> GetEnabledAccounts()
    {
        lock (_lock)
        {
            return _accounts.Where(a => a.Enabled).Select(CloneProfile).ToList();
        }
    }

    public bool HasMultipleAccounts
    {
        get { lock (_lock) return _accounts.Count > 1; }
    }

    public ColabAccountProfile? GetAccountById(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return null;
        }

        lock (_lock)
        {
            var found = _accounts.FirstOrDefault(a => string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
            return found is null ? null : CloneProfile(found);
        }
    }

    public ColabAccountContext ResolveContextOrDefault(string? accountId)
    {
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            var found = GetAccountById(accountId);
            if (found is not null)
            {
                return found.ToContext();
            }
        }

        var enabled = GetEnabledAccounts();
        if (enabled.Count > 0)
        {
            return enabled[0].ToContext();
        }

        var all = GetAccounts();
        return all.Count > 0 ? all[0].ToContext() : ColabAccountContext.Default;
    }

    public AccountQuotaSnapshot GetQuota(string accountId)
    {
        lock (_lock)
        {
            return _quotas.TryGetValue(accountId, out var snap)
                ? snap
                : AccountQuotaSnapshot.Unknown(accountId);
        }
    }

    public IReadOnlyDictionary<string, AccountQuotaSnapshot> GetAllQuotas()
    {
        lock (_lock)
        {
            return new Dictionary<string, AccountQuotaSnapshot>(_quotas, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Reloads account profiles and preferences from settings.json, auto-discovering
    /// existing ADC JSON files under %APPDATA%\manga_gui\accounts\ on first upgrade.
    /// </summary>
    public void ReloadFromStore()
    {
        var settings = _store.LoadSettings();
        bool needPersistDiscovered = false;

        List<ColabAccountProfile> loaded;
        if (settings.Accounts is { Count: > 0 })
        {
            loaded = settings.Accounts
                .Where(a => !string.IsNullOrWhiteSpace(a.Id))
                .Select(NormalizeProfile)
                .ToList();
        }
        else
        {
            loaded = AutoDiscoverAccountsFromDirectory();
            if (loaded.Count > 0)
            {
                needPersistDiscovered = true;
            }
            else
            {
                loaded.Add(new ColabAccountProfile
                {
                    Id = ColabAccountContext.DefaultAccountId,
                    Name = "默认账号",
                    AdcCredentialsPath = null,
                    ColabConfigPath = null,
                    Enabled = true
                });
            }
        }

        lock (_lock)
        {
            _accounts.Clear();
            _accounts.AddRange(loaded);

            _selectionMode = AccountSelectionModes.ToEnum(settings.AccountSelectionMode);
            _selectedAccountId = ResolveValidSelectedIdLocked(settings.SelectedAccountId);
            _autoPollEnabled = settings.QuotaAutoPollEnabled ?? true;
            _pollIntervalMinutes = Math.Clamp(
                settings.QuotaPollIntervalMinutes ?? DefaultPollIntervalMinutes,
                MinPollIntervalMinutes,
                MaxPollIntervalMinutes);
            _minBalanceCu = settings.AccountMinBalanceCu is > 0 and <= 1000
                ? settings.AccountMinBalanceCu.Value
                : DefaultMinBalanceCu;

            foreach (var acct in _accounts)
            {
                if (!_quotas.ContainsKey(acct.Id))
                {
                    _quotas[acct.Id] = AccountQuotaSnapshot.Unknown(acct.Id);
                }
            }
        }

        if (needPersistDiscovered)
        {
            PersistAccountsToSettings();
            Log(LogLevel.Info, $"已自动发现并载入 {loaded.Count} 个 Colab 账号配置（{AccountsDirectory}）。");
        }
    }

    public void SetSelectionPolicy(AccountSelectionMode mode, string? selectedAccountId = null)
    {
        lock (_lock)
        {
            _selectionMode = mode;
            if (!string.IsNullOrWhiteSpace(selectedAccountId))
            {
                var found = _accounts.FirstOrDefault(a =>
                    string.Equals(a.Id, selectedAccountId, StringComparison.OrdinalIgnoreCase));
                if (found is not null)
                {
                    _selectedAccountId = found.Id;
                }
            }

            _selectedAccountId = ResolveValidSelectedIdLocked(_selectedAccountId);
        }

        PersistAccountsToSettings();
        AccountsChanged?.Invoke();
    }

    public void UpdatePollingPreferences(bool autoPollEnabled, int intervalMinutes, double minBalanceCu)
    {
        lock (_lock)
        {
            _autoPollEnabled = autoPollEnabled;
            _pollIntervalMinutes = Math.Clamp(intervalMinutes, MinPollIntervalMinutes, MaxPollIntervalMinutes);
            _minBalanceCu = Math.Clamp(minBalanceCu, 0.1, 1000.0);
        }

        PersistAccountsToSettings();
        AccountsChanged?.Invoke();
    }

    public bool RenameAccount(string accountId, string newName)
    {
        var trimmed = newName?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        lock (_lock)
        {
            var found = _accounts.FirstOrDefault(a => string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                return false;
            }

            found.Name = trimmed;
        }

        PersistAccountsToSettings();
        AccountsChanged?.Invoke();
        return true;
    }

    public (bool Success, string? Error) SetAccountEnabled(string accountId, bool enabled, string? activeSessionAccountId = null)
    {
        lock (_lock)
        {
            var found = _accounts.FirstOrDefault(a => string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                return (false, $"未找到账号：{accountId}");
            }

            if (!enabled)
            {
                if (!string.IsNullOrWhiteSpace(activeSessionAccountId)
                    && string.Equals(activeSessionAccountId, found.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, $"账号「{found.Name}」当前正在运行会话，请停止计费后再停用。");
                }

                if (_accounts.Count(a => a.Enabled) <= 1 && found.Enabled)
                {
                    return (false, "至少需要保留 1 个启用中的账号。");
                }
            }

            found.Enabled = enabled;
            _selectedAccountId = ResolveValidSelectedIdLocked(_selectedAccountId);
        }

        PersistAccountsToSettings();
        AccountsChanged?.Invoke();
        return (true, null);
    }

    public (bool Success, string? Error) RemoveAccount(string accountId, bool deleteFiles, string? activeSessionAccountId = null)
    {
        ColabAccountProfile? removed;
        lock (_lock)
        {
            var idx = _accounts.FindIndex(a => string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                return (false, $"未找到账号：{accountId}");
            }

            removed = _accounts[idx];
            if (!string.IsNullOrWhiteSpace(activeSessionAccountId)
                && string.Equals(activeSessionAccountId, removed.Id, StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"账号「{removed.Name}」当前正在运行会话，请先停止计费再移除。");
            }

            if (_accounts.Count <= 1)
            {
                return (false, "至少需要保留 1 个账号配置。");
            }

            _accounts.RemoveAt(idx);
            _quotas.Remove(removed.Id);
            if (_accounts.All(a => !a.Enabled))
            {
                _accounts[0].Enabled = true;
            }
            _selectedAccountId = ResolveValidSelectedIdLocked(_selectedAccountId);
        }

        if (deleteFiles && removed is not null)
        {
            TryDeleteIfInsideAccountsDir(removed.AdcCredentialsPath);
            TryDeleteIfInsideAccountsDir(removed.ColabConfigPath);
            if (!string.IsNullOrWhiteSpace(removed.ColabConfigPath))
            {
                TryDeleteIfInsideAccountsDir(removed.ColabConfigPath + ".lock");
            }
        }

        PersistAccountsToSettings();
        AccountsChanged?.Invoke();
        QuotasChanged?.Invoke();
        return (true, null);
    }

    /// <summary>
    /// Validates an ADC JSON file via `colab --auth=adc whoami`, copies it into %APPDATA%\manga_gui\accounts\,
    /// deduplicates by email hash (updating existing profile credentials if the same Google account is re-added),
    /// and queries initial CU balance.
    /// </summary>
    public async Task<AccountImportResult> ImportAccountAsync(
        string sourceJsonPath,
        string? preferredName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceJsonPath) || !File.Exists(sourceJsonPath))
        {
            return new AccountImportResult(false, ErrorMessage: "所选凭据文件不存在。");
        }

        if (!TryValidateAdcJsonStructure(sourceJsonPath, out var jsonErr))
        {
            return new AccountImportResult(false, ErrorMessage: jsonErr);
        }

        // Verify with colab whoami BEFORE mutating saved profiles.
        var probeCtx = new ColabAccountContext(
            "probe",
            preferredName ?? "待验证账号",
            sourceJsonPath,
            ColabConfigPath: null);

        var whoamiRes = await _cli.RunForAccountAsync(
            probeCtx,
            ["whoami"],
            TimeSpan.FromSeconds(25),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var verify = ParseWhoamiOutput(whoamiRes);
        if (!verify.IsValid)
        {
            return new AccountImportResult(false, ErrorMessage: verify.ErrorMessage ?? "账号权限验证失败。");
        }

        Directory.CreateDirectory(AccountsDirectory);

        ColabAccountProfile? duplicate = null;
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(verify.EmailHash))
            {
                duplicate = _accounts.FirstOrDefault(a =>
                    string.Equals(a.EmailHash, verify.EmailHash, StringComparison.OrdinalIgnoreCase));
            }
        }

        ColabAccountProfile targetProfile;
        bool updatedExisting;
        if (duplicate is not null)
        {
            var destAdc = string.IsNullOrWhiteSpace(duplicate.AdcCredentialsPath)
                ? Path.Combine(AccountsDirectory, $"{duplicate.Id}.json")
                : duplicate.AdcCredentialsPath;
            var destCfg = string.IsNullOrWhiteSpace(duplicate.ColabConfigPath)
                ? Path.Combine(AccountsDirectory, $"{duplicate.Id}_sessions.json")
                : duplicate.ColabConfigPath;

            CopyFileIfDifferent(sourceJsonPath, destAdc);

            lock (_lock)
            {
                var live = _accounts.First(a => string.Equals(a.Id, duplicate.Id, StringComparison.OrdinalIgnoreCase));
                live.AdcCredentialsPath = destAdc;
                live.ColabConfigPath = destCfg;
                live.MaskedEmail = verify.MaskedEmail ?? live.MaskedEmail;
                live.EmailHash = verify.EmailHash ?? live.EmailHash;
                live.Enabled = true;
                if (!string.IsNullOrWhiteSpace(preferredName))
                {
                    live.Name = preferredName.Trim();
                }
                targetProfile = CloneProfile(live);
            }

            updatedExisting = true;
        }
        else
        {
            string newId;
            string displayName;
            lock (_lock)
            {
                // Replace the placeholder system-default entry once the user imports explicit ADC accounts?
                // Keep explicit entries cleanly numbered without colliding with existing IDs.
                newId = GenerateNextAccountIdLocked();
                displayName = string.IsNullOrWhiteSpace(preferredName)
                    ? GenerateNextAccountNameLocked()
                    : preferredName.Trim();
            }

            var destAdc = Path.Combine(AccountsDirectory, $"{newId}.json");
            var destCfg = Path.Combine(AccountsDirectory, $"{newId}_sessions.json");
            CopyFileIfDifferent(sourceJsonPath, destAdc);

            var created = new ColabAccountProfile
            {
                Id = newId,
                Name = displayName,
                AdcCredentialsPath = destAdc,
                ColabConfigPath = destCfg,
                MaskedEmail = verify.MaskedEmail,
                EmailHash = verify.EmailHash,
                Enabled = true
            };

            lock (_lock)
            {
                _accounts.Add(created);
                _quotas[created.Id] = AccountQuotaSnapshot.Unknown(created.Id);
                _selectedAccountId = ResolveValidSelectedIdLocked(_selectedAccountId);
                targetProfile = CloneProfile(created);
            }

            updatedExisting = false;
        }

        PersistAccountsToSettings();
        AccountsChanged?.Invoke();

        var quota = await RefreshAccountQuotaAsync(targetProfile.Id, cancellationToken).ConfigureAwait(false);
        Log(LogLevel.Success, updatedExisting
            ? $"已更新账号「{targetProfile.Name}」（{targetProfile.MaskedEmail ?? "ADC"}）凭据，当前余额：{quota.FormatBalanceShort()}。"
            : $"已添加账号「{targetProfile.Name}」（{targetProfile.MaskedEmail ?? "ADC"}），当前余额：{quota.FormatBalanceShort()}。");

        return new AccountImportResult(true, targetProfile, quota, updatedExisting);
    }

    /// <summary>
    /// Refreshes CU balances for all enabled accounts concurrently (bounded to 4 parallel subprocesses).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, AccountQuotaSnapshot>> RefreshAllQuotasAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (!force)
        {
            lock (_lock)
            {
                if (_lastFullRefreshUtc != DateTimeOffset.MinValue
                    && (_utcNow() - _lastFullRefreshUtc) < MinRefreshCooldown)
                {
                    return GetAllQuotas();
                }
            }
        }

        if (!await _refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            // Another refresh is already running; wait for it to finish and return its results.
            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            _refreshGate.Release();
            return GetAllQuotas();
        }

        try
        {
            var targets = GetEnabledAccounts();
            if (targets.Count == 0)
            {
                targets = GetAccounts();
            }

            lock (_lock)
            {
                _isRefreshing = true;
                foreach (var acct in targets)
                {
                    var prev = _quotas.TryGetValue(acct.Id, out var existing) ? existing : null;
                    _quotas[acct.Id] = prev is not null
                        ? prev with { Status = AccountQuotaStatus.Refreshing }
                        : new AccountQuotaSnapshot(acct.Id, AccountQuotaStatus.Refreshing);
                }
            }
            QuotasChanged?.Invoke();

            using var concurrency = new SemaphoreSlim(4, 4);
            var tasks = targets.Select(async acct =>
            {
                await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await QuerySingleAccountQuotaCoreAsync(acct, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    concurrency.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);

            lock (_lock)
            {
                _lastFullRefreshUtc = _utcNow();
            }
        }
        finally
        {
            lock (_lock)
            {
                _isRefreshing = false;
            }
            _refreshGate.Release();
            QuotasChanged?.Invoke();
        }

        return GetAllQuotas();
    }

    public async Task<AccountQuotaSnapshot> RefreshAccountQuotaAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        var acct = GetAccountById(accountId);
        if (acct is null)
        {
            return AccountQuotaSnapshot.Unknown(accountId);
        }

        lock (_lock)
        {
            var prev = _quotas.TryGetValue(acct.Id, out var existing) ? existing : null;
            _quotas[acct.Id] = prev is not null
                ? prev with { Status = AccountQuotaStatus.Refreshing }
                : new AccountQuotaSnapshot(acct.Id, AccountQuotaStatus.Refreshing);
        }
        QuotasChanged?.Invoke();

        var snapshot = await QuerySingleAccountQuotaCoreAsync(acct, cancellationToken).ConfigureAwait(false);
        QuotasChanged?.Invoke();
        return snapshot;
    }

    public async Task EnsureQuotasFreshAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        bool stale;
        lock (_lock)
        {
            var now = _utcNow();
            stale = _accounts.Where(a => a.Enabled).Any(a =>
                !_quotas.TryGetValue(a.Id, out var q)
                || q.Status == AccountQuotaStatus.Unknown
                || !q.UpdatedAt.HasValue
                || (now - q.UpdatedAt.Value) > maxAge);
        }

        if (stale)
        {
            await RefreshAllQuotasAsync(force: true, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resolves ordered candidate account(s) for starting a new session.
    /// In Auto mode: enabled + non-expired accounts ordered by (balance >= min) → (active == 0) → balance desc.
    /// In Manual mode (or explicit override): only the chosen account, with failover disabled.
    /// </summary>
    public AccountSelectionPlan ResolveCandidatesForStart(string? overrideAccountSpec = null)
    {
        lock (_lock)
        {
            bool explicitAuto = string.Equals(overrideAccountSpec, AccountSelectionModes.Auto, StringComparison.OrdinalIgnoreCase);
            bool explicitAccount = !string.IsNullOrWhiteSpace(overrideAccountSpec) && !explicitAuto;

            if (explicitAccount)
            {
                var target = _accounts.FirstOrDefault(a =>
                    string.Equals(a.Id, overrideAccountSpec, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                {
                    return new AccountSelectionPlan([], $"未找到指定账号：{overrideAccountSpec}", AllowFailover: false,
                        Warning: $"未找到指定账号：{overrideAccountSpec}");
                }

                var q = _quotas.TryGetValue(target.Id, out var snap) ? snap : AccountQuotaSnapshot.Unknown(target.Id);
                string? warn = BuildSingleAccountWarning(target, q, _minBalanceCu);
                return new AccountSelectionPlan(
                    [CloneProfile(target)],
                    $"已指定账号「{target.Name}」（{q.FormatBalanceShort()}）",
                    AllowFailover: false,
                    Warning: warn);
            }

            var mode = explicitAuto ? AccountSelectionMode.AutoHighestBalance : _selectionMode;
            if (mode == AccountSelectionMode.Manual)
            {
                var targetId = ResolveValidSelectedIdLocked(_selectedAccountId);
                var target = _accounts.FirstOrDefault(a => string.Equals(a.Id, targetId, StringComparison.OrdinalIgnoreCase))
                    ?? _accounts.FirstOrDefault();
                if (target is null)
                {
                    return new AccountSelectionPlan([], "未配置任何 Colab 账号。", AllowFailover: false,
                        Warning: "请先添加一个 Colab 账号。");
                }

                var q = _quotas.TryGetValue(target.Id, out var snap) ? snap : AccountQuotaSnapshot.Unknown(target.Id);
                return new AccountSelectionPlan(
                    [CloneProfile(target)],
                    $"手动选择「{target.Name}」（{q.FormatBalanceShort()}）",
                    AllowFailover: false,
                    Warning: BuildSingleAccountWarning(target, q, _minBalanceCu));
            }

            // AutoHighestBalance mode
            var enabled = _accounts.Where(a => a.Enabled).ToList();
            if (enabled.Count == 0)
            {
                enabled = _accounts.ToList();
            }

            if (enabled.Count == 0)
            {
                return new AccountSelectionPlan([], "未配置任何 Colab 账号。", AllowFailover: false,
                    Warning: "请先添加一个 Colab 账号。");
            }

            var scored = enabled
                .Select(a => (Account: a, Quota: _quotas.TryGetValue(a.Id, out var q) ? q : AccountQuotaSnapshot.Unknown(a.Id)))
                .Where(x => x.Quota.Status != AccountQuotaStatus.AuthExpired)
                .OrderByDescending(x => x.Quota.Status == AccountQuotaStatus.Ok && x.Quota.CurrentBalanceCu >= _minBalanceCu)
                .ThenByDescending(x => x.Quota.Status == AccountQuotaStatus.Ok)
                .ThenByDescending(x => (x.Quota.ActiveAssignments ?? 0) == 0)
                .ThenByDescending(x => x.Quota.CurrentBalanceCu ?? double.MinValue)
                .ThenBy(x => x.Account.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (scored.Count == 0)
            {
                return new AccountSelectionPlan(
                    [],
                    "所有启用账号的授权均已失效，无法自动选号。",
                    AllowFailover: false,
                    Warning: "所有启用账号授权均已失效，请在「账号管理」中重新登录。");
            }

            var top = scored[0];
            string reason;
            if (top.Quota.Status == AccountQuotaStatus.Ok && top.Quota.CurrentBalanceCu.HasValue)
            {
                var idleHint = (top.Quota.ActiveAssignments ?? 0) == 0 ? "空闲" : $"活跃 {top.Quota.ActiveAssignments}";
                reason = $"自动轮询推荐「{top.Account.Name}」（剩余 {top.Quota.CurrentBalanceCu.Value:F2} CU · {idleHint}）";
            }
            else
            {
                reason = $"自动轮询默认选用「{top.Account.Name}」（额度尚未获取）";
            }

            string? warning = null;
            if (scored.All(x => x.Quota.Status == AccountQuotaStatus.Ok && x.Quota.CurrentBalanceCu < _minBalanceCu))
            {
                warning = $"所有启用账号剩余额度均低于 {_minBalanceCu:F1} CU（最高「{top.Account.Name}」剩余 {top.Quota.CurrentBalanceCu:F2} CU），请确认后再启动。";
            }
            else if (top.Quota.Status == AccountQuotaStatus.Error)
            {
                warning = $"额度查询失败（{top.Quota.ErrorMessage}），启动时将直接尝试「{top.Account.Name}」。";
            }

            return new AccountSelectionPlan(
                scored.Select(x => CloneProfile(x.Account)).ToList(),
                reason,
                AllowFailover: scored.Count > 1,
                Warning: warning);
        }
    }

    /// <summary>
    /// Builds an actionable manual stop command scoped to the given account so users never
    /// accidentally run `colab stop -s manga` against the wrong default account.
    /// </summary>
    public string BuildManualStopCommandHint(string? accountId)
    {
        var acct = GetAccountById(accountId);
        if (acct is null || string.IsNullOrWhiteSpace(acct.AdcCredentialsPath))
        {
            return "colab stop -s manga";
        }

        var cfgArg = string.IsNullOrWhiteSpace(acct.ColabConfigPath)
            ? string.Empty
            : $" --config \"{acct.ColabConfigPath}\"";
        return $"MangaCli stop --account {acct.Id}（或 PowerShell：$env:GOOGLE_APPLICATION_CREDENTIALS=\"{acct.AdcCredentialsPath}\"; colab --auth=adc{cfgArg} stop -s manga）";
    }

    /// <summary>
    /// Checks whether a higher-balance idle backup account exists when the active account is running low.
    /// </summary>
    public (ColabAccountProfile Account, AccountQuotaSnapshot Quota)? FindHigherBalanceBackup(string activeAccountId)
    {
        lock (_lock)
        {
            return _accounts
                .Where(a => a.Enabled && !string.Equals(a.Id, activeAccountId, StringComparison.OrdinalIgnoreCase))
                .Select(a => (Account: CloneProfile(a), Quota: _quotas.TryGetValue(a.Id, out var q) ? q : AccountQuotaSnapshot.Unknown(a.Id)))
                .Where(x => x.Quota.Status == AccountQuotaStatus.Ok && x.Quota.CurrentBalanceCu >= _minBalanceCu)
                .OrderByDescending(x => (x.Quota.ActiveAssignments ?? 0) == 0)
                .ThenByDescending(x => x.Quota.CurrentBalanceCu ?? 0)
                .FirstOrDefault() is { Account: not null } best
                ? best
                : null;
        }
    }

    // ---------- Pure static parsers & helpers (unit-tested directly) ----------

    public static AccountQuotaSnapshot ParseUsageOutput(string accountId, ColabResult result, DateTimeOffset now)
    {
        if (result.TimedOut)
        {
            return new AccountQuotaSnapshot(accountId, AccountQuotaStatus.Error, UpdatedAt: now, ErrorMessage: "查询超时");
        }

        var combined = result.CombinedOutput;
        if (!result.IsSuccess)
        {
            if (AuthExpiredRegex.IsMatch(combined))
            {
                return new AccountQuotaSnapshot(accountId, AccountQuotaStatus.AuthExpired, UpdatedAt: now,
                    ErrorMessage: "ADC 授权已过期或被撤销，请重新登录");
            }

            var err = ColabCli.IsRateLimited(result)
                ? "触发 Google 429 限流，请稍后再试"
                : TrimFirstLine(combined, "colab usage 执行失败");
            return new AccountQuotaSnapshot(accountId, AccountQuotaStatus.Error, UpdatedAt: now, ErrorMessage: err);
        }

        var balMatch = BalanceRegex.Match(result.StdOut);
        if (!balMatch.Success
            || !double.TryParse(balMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var balance))
        {
            return new AccountQuotaSnapshot(accountId, AccountQuotaStatus.Error, UpdatedAt: now,
                ErrorMessage: "无法解析 colab usage 余额输出");
        }

        double? rate = null;
        var rateMatch = UsageRateRegex.Match(result.StdOut);
        if (rateMatch.Success
            && double.TryParse(rateMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedRate))
        {
            rate = parsedRate;
        }

        int? assignments = null;
        var assignMatch = AssignmentsRegex.Match(result.StdOut);
        if (assignMatch.Success
            && int.TryParse(assignMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedAssign))
        {
            assignments = parsedAssign;
        }

        return new AccountQuotaSnapshot(
            accountId,
            AccountQuotaStatus.Ok,
            CurrentBalanceCu: balance,
            UsageRateHourly: rate,
            ActiveAssignments: assignments,
            UpdatedAt: now);
    }

    public static WhoamiVerificationResult ParseWhoamiOutput(ColabResult result)
    {
        if (!result.IsSuccess)
        {
            var combined = result.CombinedOutput;
            var msg = AuthExpiredRegex.IsMatch(combined)
                ? "凭据授权无效或已过期，请重新执行 Google 登录。"
                : $"凭据校验失败：{TrimFirstLine(combined, $"exit={result.ExitCode}")}";
            return new WhoamiVerificationResult(false, ErrorMessage: msg);
        }

        var text = result.StdOut ?? string.Empty;
        bool hasColabScope = text.Contains("https://www.googleapis.com/auth/colaboratory", StringComparison.OrdinalIgnoreCase);
        bool hasEmailScope = text.Contains("https://www.googleapis.com/auth/userinfo.email", StringComparison.OrdinalIgnoreCase)
            || text.Contains("- email", StringComparison.OrdinalIgnoreCase);

        if (!hasColabScope)
        {
            return new WhoamiVerificationResult(
                false,
                ErrorMessage: "该 ADC 凭据缺少 Colab 必需权限（colaboratory scope），请通过应用内登录或带上完整 scopes 重新授权。");
        }

        if (!hasEmailScope)
        {
            return new WhoamiVerificationResult(
                false,
                ErrorMessage: "该 ADC 凭据缺少 userinfo.email 权限，Colab 后端将拒绝请求。");
        }

        string? maskedEmail = null;
        string? emailHash = null;
        var emailMatch = WhoamiEmailRegex.Match(text);
        if (emailMatch.Success)
        {
            var rawEmail = emailMatch.Groups[1].Value.Trim();
            maskedEmail = MaskEmail(rawEmail);
            emailHash = ComputeEmailHash(rawEmail);
        }

        return new WhoamiVerificationResult(true, maskedEmail, emailHash);
    }

    public static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0 || at == email.Length - 1)
        {
            return "<EMAIL>";
        }

        var local = email[..at];
        var domain = email[(at + 1)..];
        var prefix = local.Length <= 2 ? local[..1] : local[..2];
        return $"{prefix}***@{domain}";
    }

    public static string ComputeEmailHash(string email)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    public static bool TryValidateAdcJsonStructure(string path, out string? error)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "凭据 JSON 根节点必须是对象。";
                return false;
            }

            bool hasType = doc.RootElement.TryGetProperty("type", out var typeProp)
                && typeProp.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(typeProp.GetString());
            if (!hasType)
            {
                error = "凭据 JSON 缺少 type 字段，不是有效的 Google ADC 凭据文件。";
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"无法解析凭据 JSON：{ex.Message}";
            return false;
        }
    }

    // ---------- Private helpers ----------

    private async Task<AccountQuotaSnapshot> QuerySingleAccountQuotaCoreAsync(
        ColabAccountProfile acct,
        CancellationToken cancellationToken)
    {
        var ctx = acct.ToContext();
        var usageRes = await _cli.RunForAccountAsync(
            ctx,
            ["usage"],
            TimeSpan.FromSeconds(25),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var snapshot = ParseUsageOutput(acct.Id, usageRes, _utcNow());

        lock (_lock)
        {
            _quotas[acct.Id] = snapshot;
        }

        // Backfill masked email & duplicate hash once for auto-discovered profiles that lack metadata.
        if (snapshot.Status == AccountQuotaStatus.Ok && string.IsNullOrWhiteSpace(acct.MaskedEmail))
        {
            try
            {
                var whoRes = await _cli.RunForAccountAsync(
                    ctx,
                    ["whoami"],
                    TimeSpan.FromSeconds(15),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var verify = ParseWhoamiOutput(whoRes);
                if (verify.IsValid && !string.IsNullOrWhiteSpace(verify.MaskedEmail))
                {
                    bool changed = false;
                    lock (_lock)
                    {
                        var live = _accounts.FirstOrDefault(a =>
                            string.Equals(a.Id, acct.Id, StringComparison.OrdinalIgnoreCase));
                        if (live is not null && string.IsNullOrWhiteSpace(live.MaskedEmail))
                        {
                            live.MaskedEmail = verify.MaskedEmail;
                            live.EmailHash = verify.EmailHash;
                            changed = true;
                        }
                    }

                    if (changed)
                    {
                        PersistAccountsToSettings();
                        AccountsChanged?.Invoke();
                    }
                }
            }
            catch
            {
                // Best-effort metadata backfill only.
            }
        }

        return snapshot;
    }

    private List<ColabAccountProfile> AutoDiscoverAccountsFromDirectory()
    {
        var discovered = new List<ColabAccountProfile>();
        if (!Directory.Exists(AccountsDirectory))
        {
            return discovered;
        }

        var files = Directory.EnumerateFiles(AccountsDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .Where(p =>
            {
                var name = Path.GetFileName(p);
                return !name.EndsWith("_sessions.json", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("sessions.json", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
            .ToList();

        int index = 1;
        foreach (var file in files)
        {
            if (!TryValidateAdcJsonStructure(file, out _))
            {
                continue;
            }

            var rawId = Path.GetFileNameWithoutExtension(file);
            var safeId = SanitizeAccountId(rawId, $"acct{index}");
            if (discovered.Any(d => string.Equals(d.Id, safeId, StringComparison.OrdinalIgnoreCase)))
            {
                safeId = $"acct{index}";
            }

            discovered.Add(new ColabAccountProfile
            {
                Id = safeId,
                Name = $"账号 {index}",
                AdcCredentialsPath = file,
                ColabConfigPath = Path.Combine(AccountsDirectory, $"{safeId}_sessions.json"),
                Enabled = true
            });
            index++;
        }

        return discovered;
    }

    private ColabAccountProfile NormalizeProfile(ColabAccountProfile raw)
    {
        var id = raw.Id.Trim();
        var adcPath = string.IsNullOrWhiteSpace(raw.AdcCredentialsPath) ? null : raw.AdcCredentialsPath.Trim();
        var cfgPath = string.IsNullOrWhiteSpace(raw.ColabConfigPath)
            ? (adcPath is not null ? Path.Combine(AccountsDirectory, $"{id}_sessions.json") : null)
            : raw.ColabConfigPath.Trim();

        return new ColabAccountProfile
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(raw.Name) ? id : raw.Name.Trim(),
            AdcCredentialsPath = adcPath,
            ColabConfigPath = cfgPath,
            MaskedEmail = string.IsNullOrWhiteSpace(raw.MaskedEmail) ? null : raw.MaskedEmail.Trim(),
            EmailHash = string.IsNullOrWhiteSpace(raw.EmailHash) ? null : raw.EmailHash.Trim(),
            Enabled = raw.Enabled
        };
    }

    private string? ResolveValidSelectedIdLocked(string? candidateId)
    {
        if (!string.IsNullOrWhiteSpace(candidateId)
            && _accounts.Any(a => string.Equals(a.Id, candidateId, StringComparison.OrdinalIgnoreCase) && a.Enabled))
        {
            return _accounts.First(a => string.Equals(a.Id, candidateId, StringComparison.OrdinalIgnoreCase)).Id;
        }

        return _accounts.FirstOrDefault(a => a.Enabled)?.Id ?? _accounts.FirstOrDefault()?.Id;
    }

    private string GenerateNextAccountIdLocked()
    {
        for (int i = 1; i < 1000; i++)
        {
            var id = $"acct{i}";
            if (_accounts.All(a => !string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                return id;
            }
        }

        return $"acct_{Guid.NewGuid():N}"[..12];
    }

    private string GenerateNextAccountNameLocked()
    {
        for (int i = 1; i < 1000; i++)
        {
            var name = $"账号 {i}";
            if (_accounts.All(a => !string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return name;
            }
        }

        return $"账号 {_accounts.Count + 1}";
    }

    private void PersistAccountsToSettings()
    {
        var settings = _store.LoadSettings();
        lock (_lock)
        {
            settings.Accounts = _accounts.Select(CloneProfile).ToList();
            settings.AccountSelectionMode = AccountSelectionModes.ToConfigValue(_selectionMode);
            settings.SelectedAccountId = _selectedAccountId;
            settings.QuotaAutoPollEnabled = _autoPollEnabled;
            settings.QuotaPollIntervalMinutes = _pollIntervalMinutes;
            settings.AccountMinBalanceCu = _minBalanceCu;
        }

        _store.TrySaveSettings(settings);
    }

    private void TryDeleteIfInsideAccountsDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var fullAccounts = Path.GetFullPath(AccountsDirectory);
            var fullTarget = Path.GetFullPath(path);
            if (fullTarget.StartsWith(fullAccounts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && File.Exists(fullTarget))
            {
                File.Delete(fullTarget);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static void CopyFileIfDifferent(string source, string dest)
    {
        var srcFull = Path.GetFullPath(source);
        var dstFull = Path.GetFullPath(dest);
        if (!string.Equals(srcFull, dstFull, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(srcFull, dstFull, overwrite: true);
        }
    }

    private static string? BuildSingleAccountWarning(ColabAccountProfile acct, AccountQuotaSnapshot quota, double minBalanceCu)
    {
        if (quota.Status == AccountQuotaStatus.AuthExpired)
        {
            return $"账号「{acct.Name}」的授权已失效，请在「账号管理」中重新登录。";
        }

        if (quota.Status == AccountQuotaStatus.Ok && quota.CurrentBalanceCu < minBalanceCu)
        {
            return $"账号「{acct.Name}」剩余额度仅 {quota.CurrentBalanceCu:F2} CU（低于建议门槛 {minBalanceCu:F1} CU）。";
        }

        return null;
    }

    private static ColabAccountProfile CloneProfile(ColabAccountProfile a) => new()
    {
        Id = a.Id,
        Name = a.Name,
        AdcCredentialsPath = a.AdcCredentialsPath,
        ColabConfigPath = a.ColabConfigPath,
        MaskedEmail = a.MaskedEmail,
        EmailHash = a.EmailHash,
        Enabled = a.Enabled
    };

    private static string SanitizeAccountId(string raw, string fallback)
    {
        var sb = new StringBuilder();
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.Length > 0 ? sb.ToString() : fallback;
    }

    private static string TrimFirstLine(string text, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(line) ? fallback : (line.Length > 140 ? line[..140] + "…" : line);
    }

    private void Log(LogLevel level, string message) => LogMessage?.Invoke(level, message);
}
