using System.Text.Json.Serialization;

namespace MangaGui.Models;

/// <summary>
/// Runtime execution context identifying which Google ADC credential and colab-cli session state
/// file a subprocess invocation should target. Null paths fall back to the machine's default ADC
/// and ~/.config/colab-cli/sessions.json.
/// </summary>
public sealed record ColabAccountContext(
    string AccountId,
    string DisplayName,
    string? AdcCredentialsPath,
    string? ColabConfigPath)
{
    public const string DefaultAccountId = "default";

    public static ColabAccountContext Default { get; } = new(
        DefaultAccountId,
        "默认账号",
        AdcCredentialsPath: null,
        ColabConfigPath: null);

    public bool IsDefaultMachineAdc => string.IsNullOrWhiteSpace(AdcCredentialsPath);
}

public enum AccountSelectionMode
{
    AutoHighestBalance,
    Manual
}

public static class AccountSelectionModes
{
    public const string Auto = "auto";
    public const string Manual = "manual";

    public static string Normalize(string? value) =>
        string.Equals(value, Manual, StringComparison.OrdinalIgnoreCase) ? Manual : Auto;

    public static AccountSelectionMode ToEnum(string? value) =>
        Normalize(value) == Manual ? AccountSelectionMode.Manual : AccountSelectionMode.AutoHighestBalance;

    public static string ToConfigValue(AccountSelectionMode mode) =>
        mode == AccountSelectionMode.Manual ? Manual : Auto;
}

/// <summary>
/// Persisted Colab account entry in %APPDATA%\manga_gui\settings.json.
/// Never stores raw OAuth tokens or plaintext email addresses.
/// </summary>
public sealed class ColabAccountProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = ColabAccountContext.DefaultAccountId;

    [JsonPropertyName("name")]
    public string Name { get; set; } = "默认账号";

    /// <summary>
    /// Absolute path to this account's ADC JSON file. Null means system default ADC.
    /// </summary>
    [JsonPropertyName("adc_credentials_path")]
    public string? AdcCredentialsPath { get; set; }

    /// <summary>
    /// Isolated colab-cli sessions.json path passed via --config. Null means default ~/.config/colab-cli/sessions.json.
    /// </summary>
    [JsonPropertyName("colab_config_path")]
    public string? ColabConfigPath { get; set; }

    /// <summary>
    /// Masked email for UI identification only (e.g. "ri***@gmail.com").
    /// </summary>
    [JsonPropertyName("masked_email")]
    public string? MaskedEmail { get; set; }

    /// <summary>
    /// Truncated SHA256 fingerprint of lowercased email for duplicate detection without storing plaintext email.
    /// </summary>
    [JsonPropertyName("email_hash")]
    public string? EmailHash { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    public ColabAccountContext ToContext() => new(
        Id,
        string.IsNullOrWhiteSpace(Name) ? Id : Name,
        string.IsNullOrWhiteSpace(AdcCredentialsPath) ? null : AdcCredentialsPath,
        string.IsNullOrWhiteSpace(ColabConfigPath) ? null : ColabConfigPath);
}

public enum AccountQuotaStatus
{
    Unknown,
    Refreshing,
    Ok,
    AuthExpired,
    Error
}

/// <summary>
/// Live balance and assignment snapshot parsed from `colab usage`.
/// </summary>
public sealed record AccountQuotaSnapshot(
    string AccountId,
    AccountQuotaStatus Status,
    double? CurrentBalanceCu = null,
    double? UsageRateHourly = null,
    int? ActiveAssignments = null,
    DateTimeOffset? UpdatedAt = null,
    string? ErrorMessage = null)
{
    public static AccountQuotaSnapshot Unknown(string accountId) =>
        new(accountId, AccountQuotaStatus.Unknown);

    public string FormatBalanceShort() => Status switch
    {
        AccountQuotaStatus.Ok when CurrentBalanceCu.HasValue => $"{CurrentBalanceCu.Value:F2} CU",
        AccountQuotaStatus.Refreshing => "刷新中…",
        AccountQuotaStatus.AuthExpired => "授权已失效",
        AccountQuotaStatus.Error => "查询失败",
        _ => "未查询"
    };
}

public sealed record WhoamiVerificationResult(
    bool IsValid,
    string? MaskedEmail = null,
    string? EmailHash = null,
    string? ErrorMessage = null);

public sealed record AccountSelectionPlan(
    IReadOnlyList<ColabAccountProfile> Candidates,
    string Reason,
    bool AllowFailover,
    string? Warning = null);

public sealed record AccountImportResult(
    bool Success,
    ColabAccountProfile? Profile = null,
    AccountQuotaSnapshot? Quota = null,
    bool UpdatedExisting = false,
    string? ErrorMessage = null);

public sealed record OtherAccountSessionWarning(
    string AccountId,
    string AccountName,
    bool HasMangaSession,
    int UnnamedAssignments,
    string Message);
