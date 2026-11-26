using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MangaGui.Models;

namespace MangaGui.ViewModels;

public enum DurationChoice
{
    Minutes30,
    Minutes60,
    Minutes120,
    Unlimited
}

public sealed record StartAccountOption(
    string Spec,
    string DisplayText,
    double? BalanceCu,
    AccountQuotaStatus Status,
    string? WarningNote);

/// <summary>
/// ViewModel for the start-session dialog (GPU tier, quality/inpaint mode, billing account,
/// and duration commitment). Enforces explicit acknowledgment when Unlimited mode is selected.
/// </summary>
public partial class StartSessionDialogViewModel : ObservableObject
{
    public IReadOnlyList<GpuTierSpec> GpuOptions => GpuTiers.All;

    public ObservableCollection<StartAccountOption> AccountOptions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedGpuTierId))]
    [NotifyPropertyChangedFor(nameof(SelectedGpuRate))]
    [NotifyPropertyChangedFor(nameof(ShowFlux2Option))]
    [NotifyPropertyChangedFor(nameof(ShowG4SubModeOption))]
    [NotifyPropertyChangedFor(nameof(EffectiveInpaintMode))]
    [NotifyPropertyChangedFor(nameof(EffectiveG4SubMode))]
    [NotifyPropertyChangedFor(nameof(BillingHintText))]
    [NotifyPropertyChangedFor(nameof(Duration30Label))]
    [NotifyPropertyChangedFor(nameof(Duration60Label))]
    [NotifyPropertyChangedFor(nameof(Duration120Label))]
    [NotifyPropertyChangedFor(nameof(EstimatedCuForChoice))]
    [NotifyPropertyChangedFor(nameof(HasAccountBalanceWarning))]
    [NotifyPropertyChangedFor(nameof(AccountBalanceWarningMessage))]
    private GpuTierSpec _selectedGpuTier = GpuTiers.L4Spec;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveInpaintMode))]
    private bool _useFlux2Inpaint;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveG4SubMode))]
    private bool _useG4BalancedMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnlimited))]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyPropertyChangedFor(nameof(IsAutoStopEnabled))]
    [NotifyPropertyChangedFor(nameof(EstimatedCuForChoice))]
    [NotifyPropertyChangedFor(nameof(HasAccountBalanceWarning))]
    [NotifyPropertyChangedFor(nameof(AccountBalanceWarningMessage))]
    private DurationChoice _selectedChoice = DurationChoice.Minutes60;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private bool _acknowledgeUnlimitedBilling;

    [ObservableProperty]
    private bool _autoStopOnExpiry = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccountBalanceWarning))]
    [NotifyPropertyChangedFor(nameof(AccountBalanceWarningMessage))]
    private StartAccountOption? _selectedAccountOption;

    public string SelectedGpuTierId => SelectedGpuTier.Id;

    public double SelectedGpuRate => SelectedGpuTier.DefaultCuPerHour;

    public bool ShowFlux2Option => SelectedGpuTier.SupportsFlux2Inpaint;

    public bool ShowG4SubModeOption => SelectedGpuTier.SupportsG4SubMode;

    public string EffectiveInpaintMode => GpuTiers.ResolveEffectiveInpaintMode(
        SelectedGpuTier.Id,
        UseFlux2Inpaint ? InpaintModes.Flux2Klein : InpaintModes.LamaLarge);

    public string EffectiveG4SubMode => UseG4BalancedMode ? G4SubModes.Balanced : G4SubModes.Flagship;

    public string BillingHintText =>
        $"{SelectedGpuTier.ShortName} 参考 {SelectedGpuTier.RateDisplay}（启动后按 Colab 实际费率校准），运行环境存在即计费。";

    public string Duration30Label => $"30 分钟（约 {SelectedGpuRate * 0.5:F2} CU）";

    public string Duration60Label => $"1 小时（约 {SelectedGpuRate:F2} CU，推荐）";

    public string Duration120Label => $"2 小时（约 {SelectedGpuRate * 2.0:F2} CU）";

    public bool ShowAccountPicker => AccountOptions.Count > 0;

    public bool IsUnlimited => SelectedChoice == DurationChoice.Unlimited;

    public bool IsAutoStopEnabled => !IsUnlimited;

    public bool CanConfirm => !IsUnlimited || AcknowledgeUnlimitedBilling;

    public int? PlannedMinutes => SelectedChoice switch
    {
        DurationChoice.Minutes30 => 30,
        DurationChoice.Minutes60 => 60,
        DurationChoice.Minutes120 => 120,
        DurationChoice.Unlimited => null,
        _ => 60
    };

    public double EstimatedCuForChoice => PlannedMinutes is int mins
        ? mins / 60.0 * SelectedGpuRate
        : SelectedGpuRate * 2.0;

    public bool EffectiveAutoStop => !IsUnlimited && AutoStopOnExpiry;

    public string? SelectedAccountSpec => SelectedAccountOption?.Spec;

    public bool HasAccountBalanceWarning => !string.IsNullOrWhiteSpace(AccountBalanceWarningMessage);

    public string AccountBalanceWarningMessage
    {
        get
        {
            var opt = SelectedAccountOption;
            if (opt is null) return string.Empty;

            if (!string.IsNullOrWhiteSpace(opt.WarningNote))
            {
                return opt.WarningNote;
            }

            if (opt.Status == AccountQuotaStatus.AuthExpired)
            {
                return "所选账号授权已失效，请在「账号管理」中重新登录后再启动。";
            }

            if (opt.Status == AccountQuotaStatus.Ok && opt.BalanceCu is { } bal)
            {
                if (bal < 0.5)
                {
                    return $"所选账号仅剩 {bal:F2} CU，可能无法完成部署或翻译，建议切换其他账号。";
                }

                if (PlannedMinutes is int mins && bal < EstimatedCuForChoice)
                {
                    return $"所选 {SelectedGpuTier.ShortName} 时长（{mins} 分钟）预计约需 {EstimatedCuForChoice:F2} CU，高于当前账号剩余额度（{bal:F2} CU），建议缩短时长、降档 GPU 或切换账号。";
                }
            }

            return string.Empty;
        }
    }

    public void LoadGpuSettings(string? preferredTier, string? inpaintMode, string? g4SubMode)
    {
        SelectedGpuTier = GpuTiers.GetSpec(preferredTier);
        UseFlux2Inpaint = InpaintModes.Normalize(inpaintMode) == InpaintModes.Flux2Klein;
        UseG4BalancedMode = G4SubModes.Normalize(g4SubMode) == G4SubModes.Balanced;
    }

    public void LoadAccountOptions(
        IReadOnlyList<StartAccountOption> options,
        string? initialSpec)
    {
        AccountOptions.Clear();
        foreach (var opt in options)
        {
            AccountOptions.Add(opt);
        }

        SelectedAccountOption = AccountOptions.FirstOrDefault(o =>
                string.Equals(o.Spec, initialSpec, StringComparison.OrdinalIgnoreCase))
            ?? AccountOptions.FirstOrDefault();

        OnPropertyChanged(nameof(ShowAccountPicker));
        OnPropertyChanged(nameof(HasAccountBalanceWarning));
        OnPropertyChanged(nameof(AccountBalanceWarningMessage));
    }
}
