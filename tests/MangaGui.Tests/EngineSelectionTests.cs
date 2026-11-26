using System.IO;
using System.IO.Compression;
using MangaGui.Models;
using MangaGui.Services;
using MangaGui.ViewModels;
using Xunit;

namespace MangaGui.Tests;

/// <summary>
/// T3 engine-selection coverage: settings normalization, ENGINE status line parsing,
/// --detach &lt;engine&gt; argv, per-engine upload lists, stage labels, session-takeover
/// ordering + mismatch hint, and mtui-only filename rejection.
/// </summary>
public sealed class EngineSelectionTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _dataDir;
    private readonly string _scriptsDir;

    public EngineSelectionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"manga_gui_engine_test_{Guid.NewGuid():N}");
        _dataDir = Path.Combine(_tempRoot, "appdata");
        _scriptsDir = Path.Combine(_tempRoot, "scripts");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(Path.Combine(_scriptsDir, "assets"));
        Directory.CreateDirectory(_scriptsDir);

        foreach (var f in VmScripts.RequiredExistingScripts)
            File.WriteAllText(Path.Combine(_scriptsDir, f), "{}\n");
        foreach (var f in VmScripts.BundledGuiAssets)
            File.WriteAllText(Path.Combine(_scriptsDir, "assets", f), "#!/bin/bash\necho ok\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private void WriteValidZip(string path, int pages = 1)
    {
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        for (var i = 0; i < pages; i++)
            TestImageFixtures.WriteBytesEntry(z, $"{i + 1:D3}.jpg", TestImageFixtures.CreateValidJpeg((byte)(i + 1)));
    }

    private static SessionManager NewManager(SessionStore store, IColabCli cli, string scriptsBase, out VmScripts scripts)
    {
        scripts = new VmScripts(store, scriptsBase);
        return new SessionManager(cli, scripts, store, new BillingTimer());
    }

    // ---------- Settings ----------

    [Fact]
    public void TranslationEngineSetting_RoundTrips_AndInvalidResolvesToMtui()
    {
        Assert.Equal("mtui", TranslationEngines.Normalize(null));
        Assert.Equal("mtui", TranslationEngines.Normalize("bogus"));
        Assert.Equal("mit", TranslationEngines.Normalize("mit"));
        Assert.Equal("mit", TranslationEngines.Normalize("MIT"));
        Assert.Equal(TranslationEngineChoice.Mit, TranslationEngines.ToChoice("mit"));
        Assert.Equal(TranslationEngineChoice.Mtui, TranslationEngines.ToChoice("anything-else"));

        var store = new SessionStore(_dataDir);
        var settings = store.LoadSettings();
        Assert.Equal("mtui", TranslationEngines.Normalize(settings.TranslationEngine));
        settings.TranslationEngine = "mit";
        Assert.True(store.TrySaveSettings(settings).Success);
        Assert.Equal("mit", store.LoadSettings().TranslationEngine);
    }

    // ---------- Status parsing ----------

    [Fact]
    public void ParseVmStatus_EngineLine_AllThreeCases()
    {
        var ready = VmScripts.ParseVmStatus("PROTO 2\nLLAMA_OK\nENGINE mtui ready\nGPU L4");
        Assert.Equal("mtui", ready.EngineName);
        Assert.True(ready.EngineReady);
        Assert.True(ready.IsReadyForTranslation);

        var missing = VmScripts.ParseVmStatus("PROTO 2\nLLAMA_OK\nENGINE mtui missing\nGPU L4");
        Assert.Equal("mtui", missing.EngineName);
        Assert.False(missing.EngineReady);
        Assert.False(missing.IsReadyForTranslation);

        // Old vm_status.sh without an ENGINE line: NOT ready.
        var absent = VmScripts.ParseVmStatus("PROTO 2\nLLAMA_OK\nGPU L4");
        Assert.Equal(string.Empty, absent.EngineName);
        Assert.False(absent.EngineReady);
        Assert.False(absent.IsReadyForTranslation);

        // Engine ready but llama down: still not ready.
        var llamaDown = VmScripts.ParseVmStatus("PROTO 2\nENGINE mit ready\nGPU L4");
        Assert.False(llamaDown.IsReadyForTranslation);
    }

    // ---------- Deploy argv ----------

    [Theory]
    [InlineData("mtui")]
    [InlineData("mit")]
    public async Task RunDetachedSetup_PassesEngineArgument(string engine)
    {
        var seenDetachArgs = new List<IReadOnlyList<string>>();
        var setup = new FakeVmSetup();
        var cli = new FakeColabCli((args, _, _) =>
        {
            if (args[0] == "ssh" && args.Contains("--detach"))
            {
                seenDetachArgs.Add(args);
            }
            return Task.FromResult(args[0] == "ssh" && args.Contains("/content/vm_setup.sh")
                ? setup.Handle(args)!
                : new ColabResult(0, "OK", string.Empty));
        });

        var res = await VmScripts.RunDetachedSetupAsync(
            cli, engine, null, CancellationToken.None, pollInterval: TimeSpan.FromMilliseconds(5));

        Assert.True(res.IsSuccess, res.CombinedOutput);
        Assert.Single(seenDetachArgs);
        var idx = seenDetachArgs[0].ToList().IndexOf("--detach");
        Assert.True(idx >= 0 && idx + 1 < seenDetachArgs[0].Count);
        Assert.Equal(engine, seenDetachArgs[0][idx + 1]);
    }

    // ---------- Upload lists ----------

    [Fact]
    public async Task UploadAllScripts_Mtui_DoesNotRequireMangaJson()
    {
        // Scripts dir WITHOUT manga.json: mtui must deploy fine, mit must fail preflight.
        var dirNoJson = Path.Combine(_tempRoot, "nojson");
        Directory.CreateDirectory(Path.Combine(dirNoJson, "assets"));
        foreach (var f in VmScripts.BundledGuiAssets)
            File.WriteAllText(Path.Combine(dirNoJson, "assets", f), "#x\n");

        var store = new SessionStore(_dataDir);
        var scripts = new VmScripts(store, dirNoJson);

        Assert.True(scripts.HasRequiredFilesForEngine("mtui"));
        Assert.False(scripts.HasRequiredFilesForEngine("mit"));
        Assert.Contains(scripts.GetMissingScriptsReport("mit"), m => m.Contains("manga.json"));

        var remotes = new List<string>();
        var cli = new FakeColabCli((args, _, _) =>
        {
            if (args[0] == "upload") remotes.Add(args[^1]);
            return Task.FromResult(new ColabResult(0, "OK", string.Empty));
        });

        var mtuiRes = await scripts.UploadAllScriptsAsync(cli, "mtui");
        Assert.True(mtuiRes.IsSuccess, mtuiRes.CombinedOutput);
        Assert.Equal(VmScripts.BundledGuiAssets.Length, remotes.Count);
        Assert.DoesNotContain("/content/manga.json", remotes);
        Assert.Contains("/content/mtui_config.json", remotes);
        Assert.Contains("/content/mtui_patch.py", remotes);
        Assert.Contains("/content/sakura_dict.txt", remotes);
    }

    [Fact]
    public async Task UploadAllScripts_Mit_UploadsMangaJson_AndUserDictOverridesBundled()
    {
        File.WriteAllText(Path.Combine(_scriptsDir, "sakura_dict.txt"), "USER_GLOSSARY_LINE\n");
        // Bundled empty dict must differ from the user file for the override assertion.
        Assert.NotEqual("USER_GLOSSARY_LINE\n",
            File.ReadAllText(Path.Combine(_scriptsDir, "assets", "sakura_dict.txt")));

        var store = new SessionStore(_dataDir);
        var scripts = new VmScripts(store, _scriptsDir);
        Assert.True(scripts.HasRequiredFilesForEngine("mit"));

        // Read content inside the handler: the normalized staging copy is deleted afterwards.
        var uploads = new List<(string Remote, string Content)>();
        var cli = new FakeColabCli((args, _, _) =>
        {
            if (args[0] == "upload") uploads.Add((args[^1], File.ReadAllText(args[^2])));
            return Task.FromResult(new ColabResult(0, "OK", string.Empty));
        });

        var res = await scripts.UploadAllScriptsAsync(cli, "mit");
        Assert.True(res.IsSuccess, res.CombinedOutput);
        Assert.Equal(VmScripts.BundledGuiAssets.Length + 1, uploads.Count);
        Assert.Contains(uploads, u => u.Remote == "/content/manga.json");

        var dictUpload = uploads.Single(u => u.Remote == "/content/sakura_dict.txt");
        Assert.Equal("USER_GLOSSARY_LINE\n", dictUpload.Content);
    }

    // ---------- Stage labels ----------

    [Fact]
    public void DeployStageLabels_MatchEngineNeutralCopy()
    {
        Assert.Equal("STAGE 1/5：安装翻译引擎", SessionManager.FormatDeployStage(DeployStage.Stage1TranslatorEnv));
        Assert.Equal("STAGE 2/5：下载 llama.cpp 与翻译模型", SessionManager.FormatDeployStage(DeployStage.Stage2LlamaModel));
        Assert.Equal("STAGE 3/5：写入 CUDA 运行环境变量", SessionManager.FormatDeployStage(DeployStage.Stage3EnvScript));
        Assert.Equal("STAGE 4/5：启动模型服务 (:6006)", SessionManager.FormatDeployStage(DeployStage.Stage4LlamaServer));
        Assert.Equal("STAGE 5/5：预热翻译引擎与视觉模型", SessionManager.FormatDeployStage(DeployStage.Stage5TranslatorWarmup));
    }

    // ---------- Session takeover ----------

    [Fact]
    public async Task AdoptOrphan_ReuploadsAssetsBeforeHealthCheck_AndMismatchHintStaysReady()
    {
        var calls = new List<string>();
        var cli = new FakeColabCli((args, _, _) =>
        {
            calls.Add(args[0] == "ssh" ? string.Join(" ", args.Skip(1)) : args[0]);
            return Task.FromResult(args[0] switch
            {
                // Cloud session runs the OLD mit engine while settings default to mtui.
                "ssh" when args.Contains("/content/vm_status.sh") =>
                    new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mit ready\nGPU NVIDIA L4", string.Empty),
                _ => new ColabResult(0, "OK", string.Empty)
            });
        });

        var store = new SessionStore(_dataDir);
        var mgr = NewManager(store, cli, _scriptsDir, out var scripts);
        var queue = new TaskQueue(cli, mgr, a => a());
        using var vm = new MainViewModel(cli, scripts, mgr, queue, null, a => a(), store);

        Assert.True(await mgr.AdoptOrphanSessionAsync(60, autoStop: false));

        var firstUpload = calls.FindIndex(c => c == "upload");
        var firstStatus = calls.FindIndex(c => c.Contains("/content/vm_status.sh"));
        Assert.True(firstUpload >= 0, "bundled assets were not re-uploaded during takeover");
        Assert.True(firstStatus > firstUpload,
            $"health check ran before asset re-upload (upload@{firstUpload}, status@{firstStatus})");

        var status = mgr.LatestStatus;
        Assert.Equal("mit", status.EngineName);
        Assert.True(status.IsReadyForTranslation);

        // VM hint: configured mtui vs cloud mit -> mismatch InfoBar with re-deploy guidance.
        Assert.True(vm.HasEngineMismatch);
        Assert.Contains("重试部署", vm.EngineMismatchText);
        Assert.Contains("旧引擎", vm.EngineMismatchText);
    }

    // ---------- Enqueue rejection ----------

    private (TaskQueue Queue, SessionManager Mgr) NewQueue(IColabCli cli, SessionStore store)
    {
        var mgr = NewManager(store, cli, _scriptsDir, out _);
        return (new TaskQueue(cli, mgr, a => a()), mgr);
    }

    [Theory]
    [InlineData("[Artist] 本 (English ver.).zip", "English")]
    [InlineData("sample (Eng+ไทย).zip", "Eng")]
    [InlineData("my thai book.zip", "thai")]
    public void Enqueue_Mtui_RejectsNonJapaneseHintNames(string fileName, string keyword)
    {
        var path = Path.Combine(_tempRoot, fileName);
        WriteValidZip(path);
        var store = new SessionStore(_dataDir); // default engine: mtui
        var cli = new FakeColabCli((_, _, _) => Task.FromResult(new ColabResult(0, "OK", string.Empty)));
        var (queue, _) = NewQueue(cli, store);

        var (added, reason) = queue.EnqueueFiles([path]);
        Assert.Equal(0, added);
        Assert.NotNull(reason);
        Assert.Contains(keyword, reason);
        Assert.Contains("新引擎只支持日文原文", reason);
        Assert.Contains("旧引擎", reason);
    }

    [Theory]
    [InlineData("Sejyo.zip")]
    [InlineData("[Artist] 本 (日本語 ver.).zip")]
    [InlineData("Engage.zip")]
    [InlineData("Length.zip")]
    public void Enqueue_Mtui_AcceptsJapaneseOrUnrelatedNames(string fileName)
    {
        var path = Path.Combine(_tempRoot, fileName);
        WriteValidZip(path);
        var store = new SessionStore(_dataDir);
        var cli = new FakeColabCli((_, _, _) => Task.FromResult(new ColabResult(0, "OK", string.Empty)));
        var (queue, _) = NewQueue(cli, store);

        var (added, reason) = queue.EnqueueFiles([path]);
        Assert.Equal(1, added);
    }

    [Fact]
    public void Enqueue_Mit_AcceptsEnglishHintName()
    {
        var path = Path.Combine(_tempRoot, "[Artist] 本 (English ver.).zip");
        WriteValidZip(path);
        var store = new SessionStore(_dataDir);
        var settings = store.LoadSettings();
        settings.TranslationEngine = "mit";
        Assert.True(store.TrySaveSettings(settings).Success);

        var cli = new FakeColabCli((_, _, _) => Task.FromResult(new ColabResult(0, "OK", string.Empty)));
        var (queue, _) = NewQueue(cli, store);

        var (added, _) = queue.EnqueueFiles([path]);
        Assert.Equal(1, added);
    }

    // ---------- G1: scripts dir only required for mit ----------

    /// <summary>A scripts dir whose assets/ qualify but which has no manga.json.</summary>
    private string MakeNoMangaScriptsDir()
    {
        var dir = Path.Combine(_tempRoot, $"scripts_nomanga_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "assets"));
        foreach (var f in VmScripts.BundledGuiAssets)
            File.WriteAllText(Path.Combine(dir, "assets", f), "#!/bin/bash\necho ok\n");
        return dir;
    }

    [Fact]
    public void SelfCheck_MtuiWithoutScriptsDir_AddsNoIssue()
    {
        var noManga = MakeNoMangaScriptsDir();
        var cli = new FakeColabCli((_, _, _) => Task.FromResult(new ColabResult(0, "OK", string.Empty)));
        var store = new SessionStore(_dataDir); // engine default: mtui
        var mgr = NewManager(store, cli, noManga, out var scripts);
        var queue = new TaskQueue(cli, mgr, a => a());
        using var vm = new MainViewModel(cli, scripts, mgr, queue, null, a => a(), store);

        vm.RunEnvironmentSelfCheck();

        Assert.False(vm.HasEnvironmentWarning);
    }

    [Fact]
    public void SelfCheck_MitWithoutScriptsDir_FlagsMangaJson()
    {
        var noManga = MakeNoMangaScriptsDir();
        var cli = new FakeColabCli((_, _, _) => Task.FromResult(new ColabResult(0, "OK", string.Empty)));
        var store = new SessionStore(_dataDir);
        var settings = store.LoadSettings();
        settings.TranslationEngine = "mit";
        Assert.True(store.TrySaveSettings(settings).Success);
        var mgr = NewManager(store, cli, noManga, out var scripts);
        var queue = new TaskQueue(cli, mgr, a => a());
        using var vm = new MainViewModel(cli, scripts, mgr, queue, null, a => a(), store);

        vm.RunEnvironmentSelfCheck();

        Assert.True(vm.HasEnvironmentWarning);
        Assert.Contains("manga.json", vm.EnvironmentWarning);
    }

    [Fact]
    public async Task StartService_MtuiWithoutScriptsDir_SkipsBrowse_ReachesDialog()
    {
        var noManga = MakeNoMangaScriptsDir();
        var cli = new FakeColabCli((_, _, _) => Task.FromResult(new ColabResult(0, "OK", string.Empty)));
        var store = new SessionStore(_dataDir);
        var mgr = NewManager(store, cli, noManga, out var scripts);
        var queue = new TaskQueue(cli, mgr, a => a());
        using var vm = new MainViewModel(cli, scripts, mgr, queue, null, a => a(), store);

        var browseCalls = 0;
        var dialogCalls = 0;
        vm.RequestBrowseScriptsFolder += () => { browseCalls++; return null; };
        vm.RequestStartDialog += () =>
        {
            dialogCalls++;
            return Task.FromResult((false, (int?)null, true));
        };

        await vm.StartServiceCommand.ExecuteAsync(null);

        Assert.Equal(0, browseCalls);
        Assert.Equal(1, dialogCalls);
    }

    [Fact]
    public async Task StartService_MitWithoutScriptsDir_RequestsBrowse()
    {
        var noManga = MakeNoMangaScriptsDir();
        var cli = new FakeColabCli((_, _, _) => Task.FromResult(new ColabResult(0, "OK", string.Empty)));
        var store = new SessionStore(_dataDir);
        var settings = store.LoadSettings();
        settings.TranslationEngine = "mit";
        Assert.True(store.TrySaveSettings(settings).Success);
        var mgr = NewManager(store, cli, noManga, out var scripts);
        var queue = new TaskQueue(cli, mgr, a => a());
        using var vm = new MainViewModel(cli, scripts, mgr, queue, null, a => a(), store);

        var browseCalls = 0;
        var dialogCalls = 0;
        vm.RequestBrowseScriptsFolder += () => { browseCalls++; return null; };
        vm.RequestStartDialog += () =>
        {
            dialogCalls++;
            return Task.FromResult((false, (int?)null, true));
        };

        await vm.StartServiceCommand.ExecuteAsync(null);

        Assert.Equal(1, browseCalls);
        Assert.Equal(0, dialogCalls);
    }

    // ---------- G2: DeployedEngine ----------

    [Fact]
    public void DeployedEngine_OnlyWhenReadyAndKnown()
    {
        Assert.Null(new VmStatusSnapshot { EngineName = "none", EngineReady = false }.DeployedEngine);
        Assert.Null(new VmStatusSnapshot { EngineName = "none", EngineReady = true }.DeployedEngine);
        Assert.Null(new VmStatusSnapshot { EngineName = "", EngineReady = false }.DeployedEngine);
        Assert.Null(new VmStatusSnapshot { EngineName = "weird", EngineReady = true }.DeployedEngine);
        Assert.Equal("mit", new VmStatusSnapshot { EngineName = "mit", EngineReady = true }.DeployedEngine);
        Assert.Equal("mtui", new VmStatusSnapshot { EngineName = "mtui", EngineReady = true }.DeployedEngine);
    }

    [Fact]
    public async Task EngineNoneMissing_IsNotAMismatch()
    {
        // Status reports ENGINE none missing (new deploy in-flight or broken): configured mit
        // must NOT produce an engine mismatch — under the old Normalize("none")→mtui it did.
        var cli = new FakeColabCli((args, _, _) => Task.FromResult(
            args[0] == "ssh" && args.Contains("/content/vm_status.sh")
                ? new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE none missing\nGPU L4", string.Empty)
                : new ColabResult(0, "OK", string.Empty)));
        var store = new SessionStore(_dataDir);
        var settings = store.LoadSettings();
        settings.TranslationEngine = "mit";
        Assert.True(store.TrySaveSettings(settings).Success);
        var mgr = NewManager(store, cli, _scriptsDir, out var scripts);
        var queue = new TaskQueue(cli, mgr, a => a());
        using var vm = new MainViewModel(cli, scripts, mgr, queue, null, a => a(), store);

        // Not ready → adopt fails but HasActiveVm stays true with the parsed status.
        Assert.False(await mgr.AdoptOrphanSessionAsync(60, autoStop: false));
        Assert.True(mgr.HasActiveVm);
        Assert.Null(mgr.LatestStatus.DeployedEngine);
        Assert.False(vm.HasEngineMismatch);
        Assert.Equal(string.Empty, vm.EngineMismatchText);
    }

    // ---------- G3: in-session engine switch ----------

    [Fact]
    public async Task CanRetryDeploy_ReadyMismatchIdleQueue_IsTrue()
    {
        var cli = new FakeColabCli((args, _, _) => Task.FromResult(
            args[0] == "ssh" && args.Contains("/content/vm_status.sh")
                ? new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mit ready\nGPU L4", string.Empty)
                : new ColabResult(0, "OK", string.Empty)));
        var store = new SessionStore(_dataDir); // configured: mtui
        var mgr = NewManager(store, cli, _scriptsDir, out var scripts);
        var queue = new TaskQueue(cli, mgr, a => a());
        using var vm = new MainViewModel(cli, scripts, mgr, queue, null, a => a(), store);

        Assert.True(await mgr.AdoptOrphanSessionAsync(60, autoStop: false));
        Assert.True(vm.HasEngineMismatch);
        Assert.True(vm.CanRetryDeploy);
    }

    [Fact]
    public async Task CanRetryDeploy_ReadyNoMismatch_IsFalse()
    {
        var cli = new FakeColabCli((args, _, _) => Task.FromResult(
            args[0] == "ssh" && args.Contains("/content/vm_status.sh")
                ? new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mtui ready\nGPU L4", string.Empty)
                : new ColabResult(0, "OK", string.Empty)));
        var store = new SessionStore(_dataDir);
        var mgr = NewManager(store, cli, _scriptsDir, out var scripts);
        var queue = new TaskQueue(cli, mgr, a => a());
        using var vm = new MainViewModel(cli, scripts, mgr, queue, null, a => a(), store);

        Assert.True(await mgr.AdoptOrphanSessionAsync(60, autoStop: false));
        Assert.False(vm.HasEngineMismatch);
        Assert.False(vm.CanRetryDeploy);
    }

    [Fact]
    public async Task CanRetryDeploy_ReadyMismatchPendingTask_IsFalse()
    {
        var cli = new FakeColabCli((args, _, _) => Task.FromResult(
            args[0] == "ssh" && args.Contains("/content/vm_status.sh")
                ? new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mit ready\nGPU L4", string.Empty)
                : new ColabResult(0, "OK", string.Empty)));
        var store = new SessionStore(_dataDir);
        var mgr = NewManager(store, cli, _scriptsDir, out var scripts);
        var queue = new TaskQueue(cli, mgr, a => a());
        using var vm = new MainViewModel(cli, scripts, mgr, queue, null, a => a(), store);

        Assert.True(await mgr.AdoptOrphanSessionAsync(60, autoStop: false));
        Assert.True(vm.CanRetryDeploy);

        var book = Path.Combine(_tempRoot, "pending_book.zip");
        WriteValidZip(book);
        Assert.Equal(1, queue.EnqueueFiles([book]).AddedCount);

        Assert.False(vm.CanRetryDeploy);
    }

    [Fact]
    public async Task RetryDeploy_OnLiveSession_SkipsNewRuntimeAndPreservesStartedAt()
    {
        var t0 = DateTimeOffset.UtcNow.AddHours(-1);
        var store = new SessionStore(_dataDir);
        store.SaveSession(new SessionRecord
        {
            SessionName = "manga",
            StartedAt = t0,
            PlannedMinutes = 60,
            AutoStop = false
        });

        var calls = new List<string>();
        var setup = new FakeVmSetup();
        var cli = new FakeColabCli((args, _, _) =>
        {
            calls.Add(string.Join(" ", args));
            return Task.FromResult(args[0] switch
            {
                "sessions" => new ColabResult(0, "manga L4", string.Empty),
                "ssh" when args.Contains("/content/vm_setup.sh") => setup.Handle(args)!,
                "ssh" when args.Contains("/content/vm_status.sh") =>
                    new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mtui ready\nGPU L4", string.Empty),
                _ => new ColabResult(0, "OK", string.Empty)
            });
        });
        var mgr = NewManager(store, cli, _scriptsDir, out _);

        // Same call the VM's RetryDeployCommand makes.
        Assert.True(await mgr.StartOrDeployAsync(60, autoStop: false, preserveExistingStartTime: true));

        Assert.DoesNotContain(calls, c => c.StartsWith("new", StringComparison.Ordinal));
        var record = mgr.Billing.GetCurrentRecord();
        Assert.NotNull(record);
        Assert.Equal(t0, record!.StartedAt);
    }
}
