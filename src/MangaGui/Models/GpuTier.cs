namespace MangaGui.Models;

/// <summary>
/// Descriptor for a selectable Google Colab GPU tier.
/// </summary>
public sealed record GpuTierSpec(
    string Id,
    string ColabGpuArg,
    bool UseHighMem,
    string ShortName,
    string DisplayName,
    string WorkflowSummary,
    double DefaultCuPerHour,
    bool SupportsFlux2Inpaint,
    bool SupportsG4SubMode)
{
    public string RateDisplay => BillingConstants.FormatRate(DefaultCuPerHour);
}

/// <summary>
/// Supported inpainting backends for high-VRAM tiers (&gt;= 40 GiB).
/// </summary>
public static class InpaintModes
{
    public const string LamaLarge = "lama_large";
    public const string Flux2Klein = "flux2-klein";

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Flux2Klein, StringComparison.OrdinalIgnoreCase)
            ? Flux2Klein
            : LamaLarge;

    public static string DisplayName(string? value) =>
        Normalize(value) == Flux2Klein ? "Flux2-Klein 4B 高精扩散" : "LaMa Large 2048（快速）";
}

/// <summary>
/// Sub-modes for the 96 GiB G4 tier (flagship 122B MoE vs balanced 32B Q8 + Sakura fallback).
/// </summary>
public static class G4SubModes
{
    public const string Flagship = "flagship";
    public const string Balanced = "balanced";

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Balanced, StringComparison.OrdinalIgnoreCase)
            ? Balanced
            : Flagship;
}

/// <summary>
/// Runtime GPU profiles resolved on the VM by probing nvidia-smi.
/// Decouples requested Colab tier from actual allocated hardware (e.g. A100 40G vs 80G).
/// </summary>
public static class GpuProfiles
{
    public const string Auto = "auto";
    public const string L424G = "l4_24g";
    public const string A10040G = "a100_40g";
    public const string Gpu80G = "gpu_80g";
    public const string G496G = "g4_96g";
    public const string G496GBalanced = "g4_96g_balanced";

    public static string? NormalizeOrNull(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        return v switch
        {
            L424G => L424G,
            A10040G => A10040G,
            Gpu80G => Gpu80G,
            G496G => G496G,
            G496GBalanced => G496GBalanced,
            _ => null
        };
    }

    public static string DisplayName(string? profile) => profile?.Trim().ToLowerInvariant() switch
    {
        L424G => "L4 24G 基线（Sakura 14B）",
        A10040G => "A100 40G（Qwen3.5-27B 多模态）",
        Gpu80G => "80G 双模型（Qwen3-VL-32B Q8 + Sakura 热备）",
        G496G => "G4 96G 旗舰（Qwen3.5-122B MoE）",
        G496GBalanced => "G4 96G 均衡（Qwen3-VL-32B Q8 + Sakura 热备）",
        _ => "自动自适应"
    };

    public static string ShortBadge(string? profile) => profile?.Trim().ToLowerInvariant() switch
    {
        L424G => "l4_24g · Sakura 14B",
        A10040G => "a100_40g · Qwen3.5-27B",
        Gpu80G => "gpu_80g · 32B Q8+Sakura",
        G496G => "g4_96g · 122B MoE",
        G496GBalanced => "g4_96g_bal · 32B+Sakura",
        _ => string.Empty
    };

    public static double DefaultCuPerHourForProfile(string? profile) => profile?.Trim().ToLowerInvariant() switch
    {
        L424G => BillingConstants.L4CuPerHour,
        A10040G => BillingConstants.A10040GCuPerHour,
        Gpu80G => BillingConstants.A10080GCuPerHour,
        G496G or G496GBalanced => BillingConstants.G4CuPerHour,
        _ => BillingConstants.L4CuPerHour
    };
}

/// <summary>
/// Single source of truth for Colab GPU tier choices, CLI args, and adaptive setup hints.
/// </summary>
public static class GpuTiers
{
    public const string L4 = "l4";
    public const string A100 = "a100";
    public const string A100HighMem = "a100-hm";
    public const string G4 = "g4";

    public static readonly GpuTierSpec L4Spec = new(
        Id: L4,
        ColabGpuArg: "L4",
        UseHighMem: false,
        ShortName: "L4",
        DisplayName: "L4（经济标准 · 22.5G · ~2.33 CU/h）",
        WorkflowSummary: "Sakura 14B 纯文本 + mocr + LaMa，性价比最高",
        DefaultCuPerHour: BillingConstants.L4CuPerHour,
        SupportsFlux2Inpaint: false,
        SupportsG4SubMode: false);

    public static readonly GpuTierSpec A100Spec = new(
        Id: A100,
        ColabGpuArg: "A100",
        UseHighMem: false,
        ShortName: "A100",
        DisplayName: "A100（高质量 · 40G/80G 自适应 · ~8.16 CU/h）",
        WorkflowSummary: "开机探测显存：40G 跑 Qwen3.5-27B 看图多模态，抽中 80G 自动升双模型",
        DefaultCuPerHour: BillingConstants.A10040GCuPerHour,
        SupportsFlux2Inpaint: true,
        SupportsG4SubMode: false);

    public static readonly GpuTierSpec A100HighMemSpec = new(
        Id: A100HighMem,
        ColabGpuArg: "A100",
        UseHighMem: true,
        ShortName: "A100 High-RAM",
        DisplayName: "A100 High-RAM（旗舰 · 80G 优先自适应 · ~10.99 CU/h）",
        WorkflowSummary: "优先分配 80G：Qwen3-VL-32B Q8 多模态 + Sakura 14B 零拒答热备（落 40G 自动降档）",
        DefaultCuPerHour: BillingConstants.A10080GCuPerHour,
        SupportsFlux2Inpaint: true,
        SupportsG4SubMode: false);

    public static readonly GpuTierSpec G4Spec = new(
        Id: G4,
        ColabGpuArg: "G4",
        UseHighMem: false, // Verified: `--gpu G4 --high-mem` returns HTTP 400 Bad Request on Colab.
        ShortName: "G4 (RTX PRO 6000)",
        DisplayName: "G4 / RTX PRO 6000（顶配 · 96G · ~10.00 CU/h）",
        WorkflowSummary: "96G 顶配：Qwen3.5-122B MoE 旗舰译文或 32B Q8+Sakura 双模型均衡模式",
        DefaultCuPerHour: BillingConstants.G4CuPerHour,
        SupportsFlux2Inpaint: true,
        SupportsG4SubMode: true);

    public static readonly IReadOnlyList<GpuTierSpec> All =
    [
        L4Spec,
        A100Spec,
        A100HighMemSpec,
        G4Spec
    ];

    public static string Normalize(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        return v switch
        {
            L4 => L4,
            A100 or "a100-40g" or "a100_40g" => A100,
            A100HighMem or "a100_hm" or "a100-80g" or "a100_80g" => A100HighMem,
            G4 or "pro6000" or "rtx6000" => G4,
            _ => L4
        };
    }

    public static bool TryParseStrict(string? value, out string normalized)
    {
        var v = value?.Trim().ToLowerInvariant();
        switch (v)
        {
            case L4:
                normalized = L4;
                return true;
            case A100:
            case "a100-40g":
                normalized = A100;
                return true;
            case A100HighMem:
            case "a100-80g":
                normalized = A100HighMem;
                return true;
            case G4:
                normalized = G4;
                return true;
            default:
                normalized = L4;
                return false;
        }
    }

    public static GpuTierSpec GetSpec(string? tierId) => Normalize(tierId) switch
    {
        A100 => A100Spec,
        A100HighMem => A100HighMemSpec,
        G4 => G4Spec,
        _ => L4Spec
    };

    /// <summary>
    /// Builds exact `colab new` arguments for the requested tier.
    /// Never passes `--high-mem` for L4 or G4 (G4 rejects it with HTTP 400).
    /// </summary>
    public static string[] BuildCreateArgs(string? tierId, string sessionName)
    {
        var spec = GetSpec(tierId);
        return spec.UseHighMem
            ? ["new", "-s", sessionName, "--gpu", spec.ColabGpuArg, "--high-mem"]
            : ["new", "-s", sessionName, "--gpu", spec.ColabGpuArg];
    }

    /// <summary>
    /// Resolves the vm_setup.sh profile hint (`auto` or `g4_96g_balanced`) so runtime nvidia-smi
    /// probing always adapts safely to actual allocated VRAM.
    /// </summary>
    public static string ResolveSetupProfileHint(string? tierId, string? g4SubMode)
    {
        if (Normalize(tierId) == G4 && G4SubModes.Normalize(g4SubMode) == G4SubModes.Balanced)
        {
            return GpuProfiles.G496GBalanced;
        }

        return GpuProfiles.Auto;
    }

    /// <summary>
    /// Effective inpaint mode for the requested tier (L4 always stays lama_large).
    /// </summary>
    public static string ResolveEffectiveInpaintMode(string? tierId, string? inpaintMode)
    {
        var spec = GetSpec(tierId);
        if (!spec.SupportsFlux2Inpaint)
        {
            return InpaintModes.LamaLarge;
        }

        return InpaintModes.Normalize(inpaintMode);
    }
}
