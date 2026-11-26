using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using MangaGui.Models;
using MangaGui.Services;

namespace MangaGui.ViewModels;

public sealed partial class EnvironmentCheckDialogViewModel(SessionStore store, VmScripts scripts, IColabCli cli, AccountManager? accounts, Action refreshMain) : ObservableObject
{
    [ObservableProperty] private string _assets = "";
    [ObservableProperty] private string _colab = "";
    [ObservableProperty] private string _ssh = "";
    [ObservableProperty] private string _account = "";
    [ObservableProperty] private string _glossary = "";
    [ObservableProperty] private string _mit = "";
    [ObservableProperty] private bool _canGenerate;
    private static string Key => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_ed25519");
    internal static bool KeyAbsent(string key) => !File.Exists(key) && !File.Exists(key + ".pub");

    public void Refresh()
    {
        var settings = store.LoadSettings();
        scripts.RefreshLocations();
        Assets = scripts.BundledAssetsDirectory is null ? "阻断 · 缺少内置资产，请重新解压完整发行包。" : "通过 · 内置资产完整";
        Colab = cli.IsColabAvailable(out var path) ? $"通过 · {path}" : "阻断 · 未找到 google-colab-cli";
        var issues = EnvironmentChecks.GetSshIssues();
        Ssh = issues.Count == 0 ? "通过 · SSH 客户端与公钥已就绪" : "阻断 · " + string.Join("；", issues);
        foreach (var name in new[] { "id_ed25519", "id_ecdsa" })
        {
            var privateKey = Path.Combine(Path.GetDirectoryName(Key)!, name);
            if (File.Exists(privateKey + ".pub") && !File.Exists(privateKey)) Ssh += $"；警告 · 只有 {name}.pub，没有对应私钥。";
        }
        CanGenerate = KeyAbsent(Key);
        var enabled = accounts?.GetEnabledAccounts().Where(AccountManager.HasLocalCredentials).ToArray() ?? [];
        Account = enabled.Length == 0 ? "阻断 · 尚未登录 Colab 账号，或无启用的账号。" : string.Join("；", enabled.Select(a => $"{a.Name}：{accounts!.GetQuota(a.Id).Status switch { AccountQuotaStatus.Ok => "通过 · 已验证", AccountQuotaStatus.AuthExpired => "阻断 · 授权已失效", AccountQuotaStatus.Error => "警告 · 暂不可确认", _ => "警告 · 未验证" }}"));
        Account += GcloudAccountLoginService.TryFindGcloud(out _) ? "；gcloud 已安装" : "；警告 · 未找到 gcloud（仍可导入 ADC JSON）";
        Glossary = settings.CustomSakuraDictPath is null ? "通过 · 未指定独立术语表" : File.Exists(settings.CustomSakuraDictPath) ? $"通过 · {settings.CustomSakuraDictPath}" : "警告 · 所选术语表已不存在";
        Mit = TranslationEngines.Normalize(settings.TranslationEngine) != TranslationEngines.Mit ? "通过 · mtui 不需要 manga.json" : scripts.ExistingScriptsDirectory is null ? "阻断 · 请选择含 manga.json 的目录（参考 assets/manga.example.json）" : "通过 · mit 配置已就绪";
        refreshMain();
    }

    public void SelectPath(string kind, string path)
    {
        if (kind == "mit") { if (!scripts.TrySetExistingScriptsDirectory(path)) throw new IOException("目录缺少 manga.json。"); }
        else { var settings = store.LoadSettings(); if (kind == "colab") settings.ColabExePath = path; else settings.CustomSakuraDictPath = path; if (!store.TrySaveSettings(settings).Success) throw new IOException("无法保存设置。"); }
        Refresh();
    }

    public async Task GenerateAsync()
    {
        var key = Key;
        if (!KeyAbsent(key)) throw new IOException("已有私钥或公钥，不会覆盖。");
        Directory.CreateDirectory(Path.GetDirectoryName(key)!);
        var dir = Directory.CreateTempSubdirectory("komaflow-key-");
        try
        {
            var temp = Path.Combine(dir.FullName, "id_ed25519");
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keygen.exe")) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-q", "-t", "ed25519", "-N", "", "-f", temp }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new IOException("无法启动 ssh-keygen。");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException("生成失败，请确认已安装 Windows OpenSSH。");
            File.Move(temp, key, overwrite: false);
            File.Move(temp + ".pub", key + ".pub", overwrite: false);
        }
        finally { dir.Delete(true); Refresh(); }
    }
}
