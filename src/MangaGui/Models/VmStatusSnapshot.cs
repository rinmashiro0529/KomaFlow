namespace MangaGui.Models;

/// <summary>
/// Parsed snapshot returned by /content/vm_status.sh (PROTO 2).
/// </summary>
public sealed record VmStatusSnapshot
{
    public int ProtocolVersion { get; init; } = 1;
    public bool LlamaOk { get; init; }
    public bool MitOk { get; init; }
    public string GpuInfo { get; init; } = string.Empty;
    public int? DoneCount { get; init; }
    public int? TranslateAliveCount { get; init; }
    /// <summary>
    /// Numeric exit code reported by vm_job.sh's atomic exit_code file, or null if still running/absent.
    /// </summary>
    public int? JobExitCode { get; init; }
    public string? LastSavingLine { get; init; }
    /// <summary>Engine reported by vm_status.sh's ENGINE line ("mtui"/"mit"); empty when absent.</summary>
    public string EngineName { get; init; } = string.Empty;
    /// <summary>False when vm_status.sh omits the ENGINE line (old scripts) or reports "missing".</summary>
    public bool EngineReady { get; init; }
    /// <summary>
    /// Deployed engine name ("mit"/"mtui") only when the ENGINE line reports ready; null for
    /// "none", "missing", absent lines, or unrecognized names — never feed EngineName into
    /// TranslationEngines.Normalize for comparisons (Normalize maps unknown values to mtui).
    /// </summary>
    public string? DeployedEngine =>
        EngineReady && EngineName is "mit" or "mtui" ? EngineName : null;

    /// <summary>Adaptive GPU profile reported by vm_status.sh (e.g. "l4_24g", "a100_40g", "gpu_80g", "g4_96g").</summary>
    public string? GpuProfile { get; init; }

    /// <summary>Total GPU VRAM in MiB reported by vm_status.sh, if present.</summary>
    public int? GpuVramMb { get; init; }

    /// <summary>Inpainting backend reported by vm_status.sh ("lama_large" / "flux2-klein"), if present.</summary>
    public string? InpaintMode { get; init; }

    /// <summary>Health of secondary fallback llama-server (:6007) on dual-model tiers; null when single-model.</summary>
    public bool? FallbackLlamaOk { get; init; }

    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;
    public bool IsDisconnected { get; init; }

    /// <summary>
    /// GUI translation runs manga_translator local against llama-server :6006;
    /// mit API :8001 is optional and must not block GUI readiness (A-11).
    /// The deployed engine marker (ENGINE ready) is required: a missing ENGINE line means not ready.
    /// </summary>
    public bool IsReadyForTranslation => !IsDisconnected && LlamaOk && EngineReady;

    public bool BothServicesHealthy => !IsDisconnected && LlamaOk && MitOk;
}
