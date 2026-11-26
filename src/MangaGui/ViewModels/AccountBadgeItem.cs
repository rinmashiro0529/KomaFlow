using CommunityToolkit.Mvvm.ComponentModel;
using MangaGui.Models;

namespace MangaGui.ViewModels;

/// <summary>
/// UI-bindable projection of a Colab account profile combined with its live quota snapshot.
/// </summary>
public sealed partial class AccountBadgeItem : ObservableObject
{
    public string Id { get; }
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _maskedEmail;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private AccountQuotaStatus _status;
    [ObservableProperty] private double? _balanceCu;
    [ObservableProperty] private double? _usageRateHourly;
    [ObservableProperty] private int? _activeAssignments;
    [ObservableProperty] private DateTimeOffset? _updatedAt;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isActiveOwner;
    [ObservableProperty] private bool _isRecommendedOrSelected;
    [ObservableProperty] private bool _isLowBalance;

    public AccountBadgeItem(
        ColabAccountProfile profile,
        AccountQuotaSnapshot quota,
        bool isActiveOwner,
        bool isRecommendedOrSelected,
        double minBalanceCu)
    {
        Id = profile.Id;
        _name = profile.AdcCredentialsPath is null && !Services.AccountManager.HasLocalCredentials(profile) ? "尚未登录 Colab 账号" : profile.Name;
        _maskedEmail = profile.MaskedEmail ?? (profile.AdcCredentialsPath is null ? "系统默认 ADC" : "ADC 凭据");
        _enabled = profile.Enabled;
        _status = quota.Status;
        _balanceCu = quota.CurrentBalanceCu;
        _usageRateHourly = quota.UsageRateHourly;
        _activeAssignments = quota.ActiveAssignments;
        _updatedAt = quota.UpdatedAt;
        _errorMessage = quota.ErrorMessage;
        _isActiveOwner = isActiveOwner;
        _isRecommendedOrSelected = isRecommendedOrSelected;
        _isLowBalance = quota.Status == AccountQuotaStatus.Ok && quota.CurrentBalanceCu.HasValue && quota.CurrentBalanceCu.Value < minBalanceCu;
    }

    public string BalanceShortText => Status switch
    {
        AccountQuotaStatus.Ok when BalanceCu.HasValue => $"{BalanceCu.Value:F2} CU",
        AccountQuotaStatus.Refreshing => "刷新中…",
        AccountQuotaStatus.AuthExpired => "授权失效",
        AccountQuotaStatus.Error => "暂不可确认",
        _ => "未验证"
    };

    public string CompactLabel => ActiveAssignments is > 0
        ? $"{Name} · {BalanceShortText} (活跃 {ActiveAssignments})"
        : $"{Name} · {BalanceShortText}";

    public string MenuLabel => $"{Name}（{BalanceShortText}）";

    public bool IsErrorOrExpired => Status is AccountQuotaStatus.AuthExpired or AccountQuotaStatus.Error;

    public bool HasActiveAssignments => ActiveAssignments is > 0;

    public string DetailSubtitle
    {
        get
        {
            var parts = new List<string> { MaskedEmail };
            if (UsageRateHourly.HasValue)
            {
                parts.Add($"费率 {UsageRateHourly.Value:F2}/hr");
            }
            if (ActiveAssignments.HasValue)
            {
                parts.Add($"活跃实例 {ActiveAssignments.Value}");
            }
            if (UpdatedAt.HasValue)
            {
                parts.Add($"更新 {UpdatedAt.Value.ToLocalTime():HH:mm:ss}");
            }
            if (!string.IsNullOrWhiteSpace(ErrorMessage))
            {
                parts.Add(ErrorMessage);
            }
            return string.Join(" · ", parts);
        }
    }

    public string TooltipText =>
        $"{Name}（{MaskedEmail}）\n余额：{BalanceShortText}\n{(UsageRateHourly.HasValue ? $"当前费率：{UsageRateHourly.Value:F2}/hr\n" : "")}" +
        $"{(ActiveAssignments.HasValue ? $"活跃实例：{ActiveAssignments.Value}\n" : "")}" +
        $"{(UpdatedAt.HasValue ? $"更新时间：{UpdatedAt.Value.ToLocalTime():HH:mm:ss}" : "尚未查询")}" +
        $"{(!string.IsNullOrWhiteSpace(ErrorMessage) ? $"\n状态：{ErrorMessage}" : "")}";
}
