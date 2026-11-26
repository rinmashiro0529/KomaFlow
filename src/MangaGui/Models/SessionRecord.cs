using System.Text.Json.Serialization;

namespace MangaGui.Models;

/// <summary>
/// Persisted in %APPDATA%\manga_gui\session.json for crash recovery and wall-clock billing.
/// </summary>
public sealed class SessionRecord
{
    [JsonPropertyName("session_name")]
    public string SessionName { get; set; } = "manga";

    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>
    /// Planned runtime limit in minutes. Null means unlimited mode.
    /// </summary>
    [JsonPropertyName("planned_minutes")]
    public int? PlannedMinutes { get; set; }

    [JsonPropertyName("auto_stop")]
    public bool AutoStop { get; set; } = true;

    /// <summary>
    /// True when the session was adopted without a local session.json so start time is approximate.
    /// </summary>
    [JsonPropertyName("start_time_unknown")]
    public bool StartTimeUnknown { get; set; }

    /// <summary>
    /// Account identifier that owns this cloud runtime (null for legacy v1.4 records → default account).
    /// </summary>
    [JsonPropertyName("account_id")]
    public string? AccountId { get; set; }

    /// <summary>
    /// Display name snapshot at creation time so warnings remain readable even if the profile is removed.
    /// </summary>
    [JsonPropertyName("account_name")]
    public string? AccountName { get; set; }

    /// <summary>
    /// Account CU balance snapshot taken right before creating the runtime, used to report actual deduction on stop.
    /// </summary>
    [JsonPropertyName("balance_before_start_cu")]
    public double? BalanceBeforeStartCu { get; set; }

    /// <summary>
    /// Requested Colab GPU tier id ("l4", "a100", "a100-hm", "g4"). Null for legacy records → "l4".
    /// </summary>
    [JsonPropertyName("requested_gpu")]
    public string? RequestedGpu { get; set; }

    /// <summary>
    /// Runtime GPU profile probed on the VM ("l4_24g", "a100_40g", "gpu_80g", "g4_96g", "g4_96g_balanced").
    /// </summary>
    [JsonPropertyName("detected_gpu_profile")]
    public string? DetectedGpuProfile { get; set; }

    /// <summary>
    /// Raw GPU hardware name reported by nvidia-smi (e.g. "NVIDIA A100-SXM4-40GB").
    /// </summary>
    [JsonPropertyName("detected_gpu_name")]
    public string? DetectedGpuName { get; set; }

    /// <summary>
    /// Live or tier-calibrated hourly CU rate used for wall-clock billing estimates.
    /// </summary>
    [JsonPropertyName("billing_cu_per_hour")]
    public double? BillingCuPerHour { get; set; }

    /// <summary>
    /// Inpainting backend configured for this session ("lama_large" or "flux2-klein").
    /// </summary>
    [JsonPropertyName("inpaint_mode")]
    public string? InpaintMode { get; set; }
}

public enum ThemePreference
{
    System,
    Light,
    Dark
}

public enum TranslationEngineChoice
{
    Mtui,
    Mit
}

/// <summary>
/// Cloud translation engine identifiers. "mtui" (manga-translator-ui, Japanese-only) is the default;
/// "mit" (manga-image-translator) is kept as the fallback that also handles English/Thai sources.
/// </summary>
public static class TranslationEngines
{
    public const string Mtui = "mtui";
    public const string Mit = "mit";

    /// <summary>Missing or invalid configured values resolve to <see cref="Mtui"/>.</summary>
    public static string Normalize(string? value) =>
        string.Equals(value, Mit, StringComparison.OrdinalIgnoreCase) ? Mit : Mtui;

    public static TranslationEngineChoice ToChoice(string? value) =>
        Normalize(value) == Mit ? TranslationEngineChoice.Mit : TranslationEngineChoice.Mtui;

    public static string DisplayName(string? engine) =>
        Normalize(engine) == Mit ? "旧引擎 mit" : "新引擎 mtui";
}

/// <summary>
/// Persisted in %APPDATA%\manga_gui\settings.json.
/// </summary>
public sealed class AppSettings
{
    [JsonPropertyName("scripts_dir")]
    public string? ScriptsDir { get; set; }

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = nameof(ThemePreference.System);

    /// <summary>
    /// Optional custom output folder for translated zips. Null/empty writes beside source file (E-01).
    /// </summary>
    [JsonPropertyName("output_dir")]
    public string? OutputDir { get; set; }

    /// <summary>
    /// Optional explicit colab.exe path overriding PATH/venv discovery (A-12).
    /// </summary>
    [JsonPropertyName("colab_exe_path")]
    public string? ColabExePath { get; set; }

    [JsonPropertyName("custom_sakura_dict_path")]
    public string? CustomSakuraDictPath { get; set; }

    /// <summary>
    /// Cloud translation engine: "mtui" (default) or "mit". Missing/invalid values resolve to "mtui".
    /// </summary>
    [JsonPropertyName("translation_engine")]
    public string? TranslationEngine { get; set; }

    /// <summary>
    /// Configured Colab ADC accounts for multi-account quota polling and switching (v1.5.0).
    /// Empty/null falls back to auto-discovered %APPDATA%\manga_gui\accounts\*.json or default machine ADC.
    /// </summary>
    [JsonPropertyName("accounts")]
    public List<ColabAccountProfile>? Accounts { get; set; }

    /// <summary>
    /// Account selection strategy: "auto" (highest remaining CU) or "manual".
    /// </summary>
    [JsonPropertyName("account_selection_mode")]
    public string? AccountSelectionMode { get; set; }

    /// <summary>
    /// Preferred account ID when in manual mode (also remembered as fallback).
    /// </summary>
    [JsonPropertyName("selected_account_id")]
    public string? SelectedAccountId { get; set; }

    /// <summary>
    /// Whether the GUI runs periodic low-frequency quota polling in the background.
    /// </summary>
    [JsonPropertyName("quota_auto_poll_enabled")]
    public bool? QuotaAutoPollEnabled { get; set; }

    /// <summary>
    /// Periodic quota polling interval in minutes (clamped to 5..60, default 10).
    /// </summary>
    [JsonPropertyName("quota_poll_interval_minutes")]
    public int? QuotaPollIntervalMinutes { get; set; }

    /// <summary>
    /// Minimum remaining CU threshold for auto-selection preference and low-balance alerts (default 2.0).
    /// </summary>
    [JsonPropertyName("account_min_balance_cu")]
    public double? AccountMinBalanceCu { get; set; }

    /// <summary>
    /// Preferred Colab GPU tier ("l4", "a100", "a100-hm", "g4"). Null/invalid resolves to "l4".
    /// </summary>
    [JsonPropertyName("preferred_gpu_tier")]
    public string? PreferredGpuTier { get; set; }

    /// <summary>
    /// Preferred inpaint backend on high-VRAM tiers ("lama_large" default or "flux2-klein").
    /// </summary>
    [JsonPropertyName("high_quality_inpaint_mode")]
    public string? HighQualityInpaintMode { get; set; }

    /// <summary>
    /// Preferred G4 96G sub-mode ("flagship" for 122B MoE or "balanced" for 32B Q8 + Sakura fallback).
    /// </summary>
    [JsonPropertyName("g4_sub_mode")]
    public string? G4SubMode { get; set; }
}
