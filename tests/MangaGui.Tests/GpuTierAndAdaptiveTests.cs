using System.IO;
using MangaGui.Models;
using MangaGui.Services;
using MangaGui.ViewModels;
using Xunit;

namespace MangaGui.Tests;

/// <summary>
/// Stage 2 (v1.6.0) multi-tier GPU adaptive workflow tests:
/// GPU tier specs & colab new args, dynamic BillingTimer rates, VmStatusSnapshot extended fields,
/// SessionManager adaptive deployment & live UsageRatePerHour calibration, and StartSessionDialogViewModel.
/// </summary>
public sealed class GpuTierAndAdaptiveTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _dataDir;
    private readonly string _scriptsDir;

    public GpuTierAndAdaptiveTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"manga_gui_gpu_test_{Guid.NewGuid():N}");
        _dataDir = Path.Combine(_tempRoot, "appdata");
        _scriptsDir = Path.Combine(_tempRoot, "scripts");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(Path.Combine(_scriptsDir, "assets"));

        foreach (var f in VmScripts.RequiredExistingScripts)
            File.WriteAllText(Path.Combine(_scriptsDir, f), "{}\n");
        foreach (var f in VmScripts.BundledGuiAssets)
            File.WriteAllText(Path.Combine(_scriptsDir, "assets", f), "#!/bin/bash\necho ok\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public void BuildCreateArgs_MatchesVerifiedColabCliFlags_AndNeverPassesHighMemOnG4OrL4()
    {
        Assert.Equal(["new", "-s", "manga", "--gpu", "L4"], GpuTiers.BuildCreateArgs("l4", "manga"));
        Assert.Equal(["new", "-s", "manga", "--gpu", "A100"], GpuTiers.BuildCreateArgs("a100", "manga"));
        Assert.Equal(["new", "-s", "manga", "--gpu", "A100", "--high-mem"], GpuTiers.BuildCreateArgs("a100-hm", "manga"));

        // Verified against Colab backend: `--gpu G4 --high-mem` returns HTTP 400 Bad Request.
        var g4Args = GpuTiers.BuildCreateArgs("g4", "manga");
        Assert.Equal(["new", "-s", "manga", "--gpu", "G4"], g4Args);
        Assert.DoesNotContain("--high-mem", g4Args);

        // Unknown tier falls back safely to L4.
        Assert.Equal(["new", "-s", "manga", "--gpu", "L4"], GpuTiers.BuildCreateArgs("unknown-tier", "manga"));
    }

    [Fact]
    public void GpuTierHelpers_ResolveSetupHintsAndEffectiveInpaint()
    {
        Assert.Equal(GpuProfiles.Auto, GpuTiers.ResolveSetupProfileHint("l4", "balanced"));
        Assert.Equal(GpuProfiles.Auto, GpuTiers.ResolveSetupProfileHint("a100", "flagship"));
        Assert.Equal(GpuProfiles.Auto, GpuTiers.ResolveSetupProfileHint("a100-hm", "balanced"));
        Assert.Equal(GpuProfiles.Auto, GpuTiers.ResolveSetupProfileHint("g4", "flagship"));
        Assert.Equal(GpuProfiles.G496GBalanced, GpuTiers.ResolveSetupProfileHint("g4", "balanced"));

        // L4 always stays on lama_large even if flux2-klein was selected earlier.
        Assert.Equal(InpaintModes.LamaLarge, GpuTiers.ResolveEffectiveInpaintMode("l4", "flux2-klein"));
        Assert.Equal(InpaintModes.Flux2Klein, GpuTiers.ResolveEffectiveInpaintMode("a100", "flux2-klein"));
        Assert.Equal(InpaintModes.Flux2Klein, GpuTiers.ResolveEffectiveInpaintMode("a100-hm", "flux2-klein"));
        Assert.Equal(InpaintModes.Flux2Klein, GpuTiers.ResolveEffectiveInpaintMode("g4", "flux2-klein"));
        Assert.Equal(InpaintModes.LamaLarge, GpuTiers.ResolveEffectiveInpaintMode("a100", "invalid"));
    }

    [Fact]
    public void ParseVmStatus_ExtractsAdaptiveGpuProfileVramInpaintAndFallbackStatus()
    {
        var raw = string.Join('\n',
            "PROTO 2",
            "LLAMA_OK",
            "FALLBACK_LLAMA_OK",
            "ENGINE mtui ready",
            "GPU_PROFILE gpu_80g",
            "GPU_VRAM_MB 81920",
            "INPAINT_MODE flux2-klein",
            "GPU NVIDIA A100-SXM4-80GB, 50059 MiB, 81920 MiB");

        var snap = VmScripts.ParseVmStatus(raw);
        Assert.True(snap.LlamaOk);
        Assert.True(snap.FallbackLlamaOk);
        Assert.Equal("mtui", snap.DeployedEngine);
        Assert.Equal("gpu_80g", snap.GpuProfile);
        Assert.Equal(81920, snap.GpuVramMb);
        Assert.Equal("flux2-klein", snap.InpaintMode);
        Assert.Contains("A100-SXM4-80GB", snap.GpuInfo);

        var downFallback = VmScripts.ParseVmStatus("PROTO 2\nLLAMA_OK\nFALLBACK_LLAMA_DOWN\nENGINE mtui ready\nGPU_PROFILE g4_96g_balanced\n");
        Assert.False(downFallback.FallbackLlamaOk);
        Assert.Equal("g4_96g_balanced", downFallback.GpuProfile);

        // Backward-compatible old vm_status output without new fields.
        var legacy = VmScripts.ParseVmStatus("PROTO 2\nLLAMA_OK\nENGINE mtui ready\nGPU NVIDIA L4");
        Assert.Null(legacy.GpuProfile);
        Assert.Null(legacy.GpuVramMb);
        Assert.Null(legacy.InpaintMode);
        Assert.Null(legacy.FallbackLlamaOk);
    }

    [Fact]
    public void BillingTimer_SupportsDynamicRateAndLiveCalibration()
    {
        var now = new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.FromHours(8));
        var timer = new BillingTimer(() => now);
        timer.Start(now, plannedMinutes: 60, autoStop: true, cuPerHour: BillingConstants.A10040GCuPerHour);

        now = now.AddMinutes(30);
        var s1 = timer.Evaluate(fireEvents: false);
        Assert.Equal(BillingConstants.A10040GCuPerHour, s1.CuPerHour, precision: 3);
        Assert.Equal(4.08, s1.EstimatedCu, precision: 2);

        // Live calibration discovers A100-80G rate (10.99 CU/h).
        timer.UpdateCuPerHour(10.99);
        var s2 = timer.Evaluate(fireEvents: false);
        Assert.Equal(10.99, s2.CuPerHour, precision: 3);
        Assert.Equal(5.495, s2.EstimatedCu, precision: 3);
        Assert.Equal(10.99, timer.GetCurrentRecord()?.BillingCuPerHour);
    }

    [Fact]
    public void StartSessionDialogViewModel_UpdatesCostsAndOptionVisibilityPerGpuTier()
    {
        var vm = new StartSessionDialogViewModel();
        Assert.Equal(GpuTiers.L4, vm.SelectedGpuTierId);
        Assert.False(vm.ShowFlux2Option);
        Assert.False(vm.ShowG4SubModeOption);
        Assert.Contains("2.33", vm.Duration60Label);
        Assert.Equal(InpaintModes.LamaLarge, vm.EffectiveInpaintMode);

        vm.SelectedGpuTier = GpuTiers.A100HighMemSpec;
        Assert.True(vm.ShowFlux2Option);
        Assert.False(vm.ShowG4SubModeOption);
        Assert.Contains("10.99", vm.Duration60Label);
        vm.UseFlux2Inpaint = true;
        Assert.Equal(InpaintModes.Flux2Klein, vm.EffectiveInpaintMode);

        // Balance warning dynamically compares against A100-80G 1h estimate (10.99 CU).
        vm.LoadAccountOptions(
            [new StartAccountOption("acct1", "账号 1（8.00 CU）", 8.0, AccountQuotaStatus.Ok, null)],
            "acct1");
        Assert.True(vm.HasAccountBalanceWarning);
        Assert.Contains("10.99", vm.AccountBalanceWarningMessage);

        // Switching to G4 exposes G4 balanced sub-mode toggle.
        vm.SelectedGpuTier = GpuTiers.G4Spec;
        Assert.True(vm.ShowG4SubModeOption);
        Assert.Equal(G4SubModes.Flagship, vm.EffectiveG4SubMode);
        vm.UseG4BalancedMode = true;
        Assert.Equal(G4SubModes.Balanced, vm.EffectiveG4SubMode);
    }

    [Fact]
    public async Task SessionManager_A100HighMem_AdaptsTo80GStatus_CalibratesLiveRate_AndForwardsInpaint()
    {
        var acctDir = Path.Combine(_dataDir, "accounts");
        Directory.CreateDirectory(acctDir);
        File.WriteAllText(Path.Combine(acctDir, "acct1.json"), """{"type":"authorized_user","refresh_token":"r1"}""");

        bool remoteAlive = false;
        string[]? seenNewArgs = null;
        string[]? seenDetachArgs = null;
        var setup = new FakeVmSetup();

        var cli = new FakeColabCli((acct, args, _, _) =>
        {
            var cmd = args[0];
            if (cmd == "usage")
            {
                var rate = remoteAlive ? "10.99" : "0.00";
                var active = remoteAlive ? "1" : "0";
                return Task.FromResult(new ColabResult(0,
                    $"Current balance: 176.95 compute units\nUsage rate: {rate}/hr\nActive assignments: {active}\n", ""));
            }
            if (cmd == "sessions")
            {
                return Task.FromResult(new ColabResult(0,
                    remoteAlive ? "[manga] gpu-a100-s-111 | Hardware: A100 | Shape: High-RAM | Variant: GPU\n" : "[colab] No active sessions found on server.\n", ""));
            }
            if (cmd == "new")
            {
                seenNewArgs = args.ToArray();
                remoteAlive = true;
                return Task.FromResult(new ColabResult(0, "[colab] Session READY.\n", ""));
            }
            if (cmd == "ssh" && args.Contains("/content/vm_setup.sh"))
            {
                if (args.Contains("--detach")) seenDetachArgs = args.ToArray();
                return Task.FromResult(setup.Handle(args)!);
            }
            if (cmd == "ssh" && args.Contains("/content/vm_status.sh"))
            {
                return Task.FromResult(new ColabResult(0,
                    "PROTO 2\nLLAMA_OK\nFALLBACK_LLAMA_OK\nENGINE mtui ready\nGPU_PROFILE gpu_80g\nGPU_VRAM_MB 81920\nINPAINT_MODE flux2-klein\nGPU NVIDIA A100-SXM4-80GB, 50059 MiB, 81920 MiB\n", ""));
            }
            return Task.FromResult(new ColabResult(0, "OK", ""));
        });

        var store = new SessionStore(_dataDir);
        var accounts = new AccountManager(cli, store);
        var scripts = new VmScripts(store, _scriptsDir);
        var billing = new BillingTimer();
        var mgr = new SessionManager(cli, scripts, store, billing, accounts: accounts);

        var ok = await mgr.StartOrDeployAsync(
            plannedMinutes: 60,
            autoStop: true,
            requestedGpuTier: GpuTiers.A100HighMem,
            inpaintMode: InpaintModes.Flux2Klein);

        Assert.True(ok);
        Assert.NotNull(seenNewArgs);
        Assert.Equal(["new", "-s", "manga", "--gpu", "A100", "--high-mem"], seenNewArgs!);
        Assert.NotNull(seenDetachArgs);
        Assert.Contains("auto", seenDetachArgs!);
        Assert.Contains("flux2-klein", seenDetachArgs!);

        Assert.Equal(GpuTiers.A100HighMem, mgr.ActiveRequestedGpuTier);
        Assert.Equal(GpuProfiles.Gpu80G, mgr.ActiveDetectedGpuProfile);
        Assert.Equal(InpaintModes.Flux2Klein, mgr.ActiveInpaintMode);
        Assert.Equal(10.99, billing.CurrentCuPerHour, precision: 2);

        var persisted = store.LoadSession();
        Assert.NotNull(persisted);
        Assert.Equal(GpuTiers.A100HighMem, persisted!.RequestedGpu);
        Assert.Equal(GpuProfiles.Gpu80G, persisted.DetectedGpuProfile);
        Assert.Equal("NVIDIA A100-SXM4-80GB", persisted.DetectedGpuName);
        Assert.Equal(InpaintModes.Flux2Klein, persisted.InpaintMode);
        Assert.Equal(10.99, persisted.BillingCuPerHour!.Value, precision: 2);
    }

    [Fact]
    public async Task SessionManager_A100Standard_FallbackRateCalibratesToDetectedProfileWhenUsageUnavailable()
    {
        bool remoteAlive = false;
        var setup = new FakeVmSetup();

        var cli = new FakeColabCli((args, _, _) =>
        {
            if (args[0] == "sessions")
                return Task.FromResult(new ColabResult(0, remoteAlive ? "manga A100" : "No active sessions found.", ""));
            if (args[0] == "new")
            {
                remoteAlive = true;
                return Task.FromResult(new ColabResult(0, "READY", ""));
            }
            if (args[0] == "ssh" && args.Contains("/content/vm_setup.sh"))
                return Task.FromResult(setup.Handle(args)!);
            if (args[0] == "ssh" && args.Contains("/content/vm_status.sh"))
                // Requested a100 standard (8.16), randomly allocated an 80G card -> adaptive gpu_80g!
                return Task.FromResult(new ColabResult(0,
                    "PROTO 2\nLLAMA_OK\nFALLBACK_LLAMA_OK\nENGINE mtui ready\nGPU_PROFILE gpu_80g\nGPU_VRAM_MB 81920\nINPAINT_MODE lama_large\nGPU NVIDIA A100-SXM4-80GB, 49000 MiB, 81920 MiB\n", ""));
            return Task.FromResult(new ColabResult(0, "OK", ""));
        });

        var store = new SessionStore(_dataDir);
        var billing = new BillingTimer();
        var mgr = new SessionManager(cli, new VmScripts(store, _scriptsDir), store, billing);

        Assert.True(await mgr.StartOrDeployAsync(60, true, requestedGpuTier: GpuTiers.A100));
        Assert.Equal(GpuProfiles.Gpu80G, mgr.ActiveDetectedGpuProfile);
        Assert.Equal(BillingConstants.A10080GCuPerHour, billing.CurrentCuPerHour, precision: 2);
    }
}
