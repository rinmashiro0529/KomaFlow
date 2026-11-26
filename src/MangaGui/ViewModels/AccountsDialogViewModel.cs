using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MangaGui.Models;
using MangaGui.Services;

namespace MangaGui.ViewModels;

/// <summary>
/// ViewModel for the in-window AccountsDialog: displays per-account CU balances, active assignments,
/// isolated browser login with copyable OAuth URL, JSON credential import, rename/enable/remove,
/// and polling settings.
/// </summary>
public sealed partial class AccountsDialogViewModel : ObservableObject, IDisposable
{
    private readonly AccountManager _accounts;
    private readonly Func<bool> _hasActiveVmProvider;
    private readonly Func<string?> _activeSessionAccountIdProvider;
    private readonly string _dataDirectory;
    private readonly Action<Action> _dispatchToUi;

    private CancellationTokenSource? _loginCts;

    public ObservableCollection<AccountBadgeItem> Accounts { get; } = [];

    public int[] PollIntervalChoices { get; } = [5, 10, 15, 30, 60];

    public event Func<string?>? RequestBrowseAdcJson;
    public event Action<string>? RequestCopyToClipboard;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefreshAll))]
    private bool _isRefreshing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddAccount))]
    private bool _isLoggingIn;

    public bool CanRefreshAll => !IsRefreshing;
    public bool CanAddAccount => !IsLoggingIn;
    [ObservableProperty] private string _loginStatusText = string.Empty;
    [ObservableProperty] private string _loginAuthUrl = string.Empty;
    [ObservableProperty] private bool _hasLoginAuthUrl;
    [ObservableProperty] private string _feedbackMessage = string.Empty;
    [ObservableProperty] private bool _isFeedbackError;
    [ObservableProperty] private bool _hasFeedback;

    [ObservableProperty] private bool _isAutoMode;
    [ObservableProperty] private bool _autoPollEnabled;
    [ObservableProperty] private int _pollIntervalMinutes;
    [ObservableProperty] private double _minBalanceCu;
    [ObservableProperty] private bool _canModifyActiveSelection;

    public AccountsDialogViewModel(
        AccountManager accounts,
        string dataDirectory,
        Func<bool> hasActiveVmProvider,
        Func<string?> activeSessionAccountIdProvider,
        Action<Action>? dispatchToUi = null)
    {
        _accounts = accounts;
        _dataDirectory = dataDirectory;
        _hasActiveVmProvider = hasActiveVmProvider;
        _activeSessionAccountIdProvider = activeSessionAccountIdProvider;
        _dispatchToUi = dispatchToUi ?? (a => a());

        _accounts.AccountsChanged += OnBackendChanged;
        _accounts.QuotasChanged += OnBackendChanged;
        Reload();
    }

    public void Reload()
    {
        var profiles = _accounts.GetAccounts();
        var quotas = _accounts.GetAllQuotas();
        var activeId = _hasActiveVmProvider() ? _activeSessionAccountIdProvider() : null;
        var plan = _accounts.ResolveCandidatesForStart();
        var recId = plan.Candidates.FirstOrDefault()?.Id;
        var minCu = _accounts.MinBalanceCu;

        Accounts.Clear();
        foreach (var p in profiles)
        {
            var q = quotas.TryGetValue(p.Id, out var snap) ? snap : AccountQuotaSnapshot.Unknown(p.Id);
            Accounts.Add(new AccountBadgeItem(
                p,
                q,
                isActiveOwner: activeId is not null && string.Equals(activeId, p.Id, StringComparison.OrdinalIgnoreCase),
                isRecommendedOrSelected: recId is not null && string.Equals(recId, p.Id, StringComparison.OrdinalIgnoreCase),
                minBalanceCu: minCu));
        }

        IsRefreshing = _accounts.IsRefreshing;
        IsAutoMode = _accounts.SelectionMode == AccountSelectionMode.AutoHighestBalance;
        AutoPollEnabled = _accounts.AutoPollEnabled;
        PollIntervalMinutes = _accounts.PollIntervalMinutes;
        MinBalanceCu = _accounts.MinBalanceCu;
        CanModifyActiveSelection = !_hasActiveVmProvider();
    }

    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        ClearFeedback();
        IsRefreshing = true;
        try
        {
            await _accounts.RefreshAllQuotasAsync(force: true);
            SetFeedback("已刷新全部启用账号的剩余额度。", isError: false);
        }
        catch (Exception ex)
        {
            SetFeedback($"刷新额度失败：{ex.Message}", isError: true);
        }
        finally
        {
            IsRefreshing = false;
            Reload();
        }
    }

    [RelayCommand]
    private async Task RefreshSingleAsync(AccountBadgeItem? item)
    {
        if (item is null) return;
        ClearFeedback();
        var snap = await _accounts.RefreshAccountQuotaAsync(item.Id);
        Reload();
        SetFeedback(
            snap.Status == AccountQuotaStatus.Ok
                ? $"账号「{item.Name}」当前剩余 {snap.FormatBalanceShort()}。"
                : $"账号「{item.Name}」查询异常：{snap.ErrorMessage ?? snap.FormatBalanceShort()}",
            isError: snap.Status != AccountQuotaStatus.Ok);
    }

    [RelayCommand]
    private async Task AddByBrowserLoginAsync()
    {
        if (IsLoggingIn) return;
        ClearFeedback();

        _loginCts?.Cancel();
        _loginCts?.Dispose();
        _loginCts = new CancellationTokenSource();
        var ct = _loginCts.Token;

        IsLoggingIn = true;
        LoginAuthUrl = string.Empty;
        HasLoginAuthUrl = false;
        LoginStatusText = "正在启动隔离的 Google 授权流程（不会影响现有账号）...";

        GcloudLoginResult? loginResult = null;
        try
        {
            loginResult = await GcloudAccountLoginService.RunIsolatedLoginAsync(
                _dataDirectory,
                url => _dispatchToUi(() =>
                {
                    LoginAuthUrl = url;
                    HasLoginAuthUrl = true;
                    LoginStatusText = "请在浏览器中选择要添加的 Google 账号并点击同意（也可复制下方链接到无痕窗口打开）：";
                }),
                ct);

            if (!loginResult.Success || string.IsNullOrWhiteSpace(loginResult.TempCredentialsPath))
            {
                SetFeedback(loginResult.ErrorMessage ?? "浏览器登录未完成。", isError: true);
                return;
            }

            LoginStatusText = "授权完成，正在验证 Colab 权限并获取剩余额度...";
            var importRes = await _accounts.ImportAccountAsync(loginResult.TempCredentialsPath, preferredName: null, ct);
            if (!importRes.Success || importRes.Profile is null)
            {
                SetFeedback(importRes.ErrorMessage ?? "导入新账号凭据失败。", isError: true);
                return;
            }

            var verb = importRes.UpdatedExisting ? "已更新已有账号凭据" : "已成功添加新账号";
            SetFeedback($"{verb}：「{importRes.Profile.Name}」（{importRes.Quota?.FormatBalanceShort() ?? "未查询"}）。", isError: false);
        }
        finally
        {
            GcloudAccountLoginService.CleanupTempDirectory(loginResult?.TempConfigDirectory);
            IsLoggingIn = false;
            LoginAuthUrl = string.Empty;
            HasLoginAuthUrl = false;
            LoginStatusText = string.Empty;
            Reload();
        }
    }

    [RelayCommand]
    private void CancelBrowserLogin()
    {
        _loginCts?.Cancel();
    }

    [RelayCommand]
    private void CopyLoginUrl()
    {
        if (!string.IsNullOrWhiteSpace(LoginAuthUrl))
        {
            RequestCopyToClipboard?.Invoke(LoginAuthUrl);
            SetFeedback("已复制授权链接，可粘贴到无痕窗口或另一浏览器打开。", isError: false);
        }
    }

    [RelayCommand]
    private async Task ImportJsonAsync()
    {
        ClearFeedback();
        var file = RequestBrowseAdcJson?.Invoke();
        if (string.IsNullOrWhiteSpace(file)) return;

        var res = await _accounts.ImportAccountAsync(file);
        Reload();
        if (res.Success && res.Profile is not null)
        {
            var verb = res.UpdatedExisting ? "检测到同一 Google 账号，已更新凭据" : "已导入账号";
            SetFeedback($"{verb}：「{res.Profile.Name}」（{res.Quota?.FormatBalanceShort() ?? "未查询"}）。", isError: false);
        }
        else
        {
            SetFeedback(res.ErrorMessage ?? "导入失败。", isError: true);
        }
    }

    [RelayCommand]
    private void ToggleEnabled(AccountBadgeItem? item)
    {
        if (item is null) return;
        ClearFeedback();
        var activeId = _hasActiveVmProvider() ? _activeSessionAccountIdProvider() : null;
        var (ok, err) = _accounts.SetAccountEnabled(item.Id, !item.Enabled, activeId);
        Reload();
        if (!ok && !string.IsNullOrWhiteSpace(err))
        {
            SetFeedback(err, isError: true);
        }
    }

    [RelayCommand]
    private void SelectManual(AccountBadgeItem? item)
    {
        if (item is null) return;
        if (_hasActiveVmProvider())
        {
            SetFeedback("当前有运行中的 Colab 会话，停止计费后才能切换账号。", isError: true);
            return;
        }

        _accounts.SetSelectionPolicy(AccountSelectionMode.Manual, item.Id);
        Reload();
        SetFeedback($"已切换为手动模式，当前选中「{item.Name}」。", isError: false);
    }

    [RelayCommand]
    private void SwitchToAutoMode()
    {
        if (_hasActiveVmProvider())
        {
            SetFeedback("当前有运行中的 Colab 会话，停止计费后才能切换模式。", isError: true);
            return;
        }

        _accounts.SetSelectionPolicy(AccountSelectionMode.AutoHighestBalance);
        Reload();
        SetFeedback("已切换为「自动按剩余额度轮询」模式。", isError: false);
    }

    public void CommitRename(AccountBadgeItem item, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName.Trim(), item.Name, StringComparison.Ordinal))
        {
            return;
        }

        if (_accounts.RenameAccount(item.Id, newName))
        {
            Reload();
            SetFeedback($"已重命名为「{newName.Trim()}」。", isError: false);
        }
    }

    [RelayCommand]
    private void RemoveAccount(AccountBadgeItem? item)
    {
        if (item is null) return;
        ClearFeedback();
        var activeId = _hasActiveVmProvider() ? _activeSessionAccountIdProvider() : null;
        var (ok, err) = _accounts.RemoveAccount(item.Id, deleteFiles: false, activeId);
        Reload();
        if (ok)
        {
            SetFeedback($"已从列表移除「{item.Name}」（磁盘凭据文件已保留以便随时重新导入）。", isError: false);
        }
        else if (!string.IsNullOrWhiteSpace(err))
        {
            SetFeedback(err, isError: true);
        }
    }

    public void SavePollingPreferences()
    {
        _accounts.UpdatePollingPreferences(AutoPollEnabled, PollIntervalMinutes, MinBalanceCu);
        SetFeedback(
            AutoPollEnabled
                ? $"已保存：每 {PollIntervalMinutes} 分钟自动低频轮询额度，低余额提醒门槛 {MinBalanceCu:F1} CU。"
                : $"已保存：已关闭后台定时轮询（仍会在启动、开机前、停机后自动刷新），提醒门槛 {MinBalanceCu:F1} CU。",
            isError: false);
    }

    private void OnBackendChanged() => _dispatchToUi(Reload);

    private void SetFeedback(string message, bool isError)
    {
        FeedbackMessage = message;
        IsFeedbackError = isError;
        HasFeedback = !string.IsNullOrWhiteSpace(message);
    }

    private void ClearFeedback()
    {
        FeedbackMessage = string.Empty;
        HasFeedback = false;
    }

    public void Dispose()
    {
        _loginCts?.Cancel();
        _loginCts?.Dispose();
        _accounts.AccountsChanged -= OnBackendChanged;
        _accounts.QuotasChanged -= OnBackendChanged;
    }
}
