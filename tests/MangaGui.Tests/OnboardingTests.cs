using System.IO;
using MangaGui.Models;
using MangaGui.Services;
using MangaGui.ViewModels;
using Xunit;

namespace MangaGui.Tests;

public sealed class OnboardingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("Koma Flow & test ").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsolatedBatchLoginSupportsSpacesAndCancellation(bool cancel)
    {
        var script = Path.Combine(_dir, "gcloud.cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\nif not \"%1\"==\"auth\" exit /b 4\r\necho https://accounts.google.com/o/oauth2/auth?test\r\n" + (cancel ? "ping -n 30 127.0.0.1 >nul\r\n" : "echo {} >\"%CLOUDSDK_CONFIG%\\application_default_credentials.json\"\r\n"));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        bool urlSeen = false;
        var result = await GcloudAccountLoginService.RunLoginProcessAsync(_dir, script, _ => { urlSeen = true; if (cancel) stop.Cancel(); }, stop.Token);
        Assert.True(urlSeen);
        if (cancel) { Assert.False(result.Success); Assert.Contains("取消", result.ErrorMessage); Assert.Empty(Directory.GetDirectories(_dir)); }
        else { Assert.True(result.Success, result.ErrorMessage); Assert.True(File.Exists(result.TempCredentialsPath)); }
    }

    [Fact]
    public void StartBlocksMissingAndExpiredButNotNetworkFailure()
    {
        var vm = new StartSessionDialogViewModel { MtuiEngine = false };
        vm.LoadGpuSettings(GpuTiers.G4, InpaintModes.Flux2Klein, G4SubModes.Balanced);
        Assert.False(vm.ShowFlux2Option); Assert.False(vm.ShowG4SubModeOption);
        vm.LoadAccountOptions([], null); Assert.False(vm.CanConfirm);
        foreach (var status in new[] { AccountQuotaStatus.AuthExpired, AccountQuotaStatus.Unknown, AccountQuotaStatus.Error })
        {
            vm.LoadAccountOptions([new("id", "测试", null, status, null)], "id");
            Assert.Equal(status != AccountQuotaStatus.AuthExpired, vm.CanConfirm);
        }
    }

    [Fact]
    public async Task RefreshCountsOnlyEnabledAndRemovingLastEnabledDoesNotReassign()
    {
        var store = new SessionStore(_dir);
        store.SaveSettings(new AppSettings { Accounts = [new() { Id = "a", Name = "A", Enabled = true }, new() { Id = "b", Name = "B", Enabled = true }] });
        var cli = new FakeColabCli((acct, args, _, _) => Task.FromResult(acct.AccountId == "a" ? new ColabResult(0, "Current balance: 50 compute units", "") : new ColabResult(1, "", "429")));
        var manager = new AccountManager(cli, store);
        using var vm = new AccountsDialogViewModel(manager, _dir, () => false, () => null);
        await vm.RefreshAllCommand.ExecuteAsync(null);
        Assert.Contains("成功 1 / 失败 1", vm.FeedbackMessage);
        Assert.True(manager.SetAccountEnabled("b", false).Success);
        Assert.True(manager.RemoveAccount("a", false).Success);
        manager.ReloadFromStore();
        Assert.Empty(manager.GetEnabledAccounts()); Assert.Empty(manager.ResolveCandidatesForStart().Candidates);
    }

    [Fact]
    public async Task IndependentGlossaryUploadsWithoutMangaJsonAnd503OffersLowerTier()
    {
        var store = new SessionStore(_dir);
        var glossary = Path.Combine(_dir, "names.txt");
        File.WriteAllText(glossary, "原文->译文");
        store.SaveSettings(new AppSettings { TranslationEngine = "mtui", CustomSakuraDictPath = glossary });
        var assets = Directory.CreateDirectory(Path.Combine(_dir, "assets"));
        foreach (var name in VmScripts.BundledGuiAssets) File.WriteAllText(Path.Combine(assets.FullName, name), "");
        var scripts = new VmScripts(store, _dir);
        string? uploaded = null;
        var cli = new FakeColabCli((args, _, _) => {
            if (args[0] == "upload" && args[^1] == "/content/sakura_dict.txt") uploaded = File.ReadAllText(args[^2]);
            return Task.FromResult(args[0] switch { "new" => new ColabResult(1, "", "503"), "sessions" => new ColabResult(0, "No active sessions found.", ""), _ => new ColabResult(0, "OK", "") });
        });
        Assert.True((await scripts.UploadAllScriptsAsync(cli, "mtui")).IsSuccess);
        Assert.Equal("原文->译文", uploaded);
        var manager = new SessionManager(cli, scripts, store, new BillingTimer(), environmentIssuesProvider: () => []);
        Assert.False(await manager.StartOrDeployAsync(60, true)); Assert.Contains("A100 标准 / L4", manager.LastError);
    }
}
