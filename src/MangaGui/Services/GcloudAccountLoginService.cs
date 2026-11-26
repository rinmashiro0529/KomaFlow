using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MangaGui.Services;

public sealed record GcloudLoginResult(
    bool Success,
    string? TempCredentialsPath = null,
    string? TempConfigDirectory = null,
    string? ErrorMessage = null);

/// <summary>
/// Launches an isolated `gcloud auth application-default login` with a dedicated CLOUDSDK_CONFIG
/// directory so adding a new Google account never overwrites the machine's default ADC credentials.
/// Captures the OAuth authorization URL for copy-to-incognito convenience and cleanly terminates
/// the subprocess tree on cancellation.
/// </summary>
public static class GcloudAccountLoginService
{
    public const string RequiredScopes =
        "openid,https://www.googleapis.com/auth/cloud-platform,https://www.googleapis.com/auth/userinfo.email,https://www.googleapis.com/auth/colaboratory";

    private static readonly Regex AuthUrlRegex = new(
        @"https://accounts\.google\.com/o/oauth2/auth\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool TryFindGcloud(out string? resolvedPath)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var raw in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = raw.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(dir)) continue;

            var cmd = Path.Combine(dir, "gcloud.cmd");
            if (File.Exists(cmd))
            {
                resolvedPath = cmd;
                return true;
            }

            var exe = Path.Combine(dir, "gcloud.exe");
            if (File.Exists(exe))
            {
                resolvedPath = exe;
                return true;
            }
        }

        var localSdk = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Google", "Cloud SDK", "google-cloud-sdk", "bin", "gcloud.cmd");
        if (File.Exists(localSdk))
        {
            resolvedPath = localSdk;
            return true;
        }

        resolvedPath = null;
        return false;
    }

    public static string? ExtractAuthUrl(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var match = AuthUrlRegex.Match(line);
        return match.Success ? match.Value.TrimEnd(')', ']', '.', ',') : null;
    }

    public static async Task<GcloudLoginResult> RunIsolatedLoginAsync(
        string dataDirectory,
        Action<string>? onAuthUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryFindGcloud(out var gcloudPath) || string.IsNullOrWhiteSpace(gcloudPath))
        {
            return new GcloudLoginResult(false, ErrorMessage: "未找到 gcloud CLI（请确认已安装 Google Cloud SDK，或使用「导入凭据 JSON」）。");
        }

        var tempDir = Path.Combine(dataDirectory, $"gcloud_login_{Guid.NewGuid():N}"[..24]);
        Directory.CreateDirectory(tempDir);

        var psi = new ProcessStartInfo
        {
            FileName = gcloudPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.Environment["CLOUDSDK_CONFIG"] = tempDir;
        psi.Environment["PYTHONUTF8"] = "1";

        psi.ArgumentList.Add("auth");
        psi.ArgumentList.Add("application-default");
        psi.ArgumentList.Add("login");
        psi.ArgumentList.Add("--disable-quota-project");
        psi.ArgumentList.Add($"--scopes={RequiredScopes}");

        using var process = new Process { StartInfo = psi };
        var stderrLines = new List<string>();
        bool urlNotified = false;

        try
        {
            if (!process.Start())
            {
                CleanupTempDirectory(tempDir);
                return new GcloudLoginResult(false, ErrorMessage: "无法启动 gcloud 登录进程。");
            }

            void HandleLine(string? line, bool isErr)
            {
                if (line is null) return;
                if (isErr && stderrLines.Count < 40)
                {
                    lock (stderrLines) stderrLines.Add(line);
                }

                if (!urlNotified)
                {
                    var url = ExtractAuthUrl(line);
                    if (!string.IsNullOrEmpty(url))
                    {
                        urlNotified = true;
                        onAuthUrl?.Invoke(url);
                    }
                }
            }

            var outTask = ReadAllLinesAsync(process.StandardOutput, l => HandleLine(l, false), cancellationToken);
            var errTask = ReadAllLinesAsync(process.StandardError, l => HandleLine(l, true), cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);

            var credPath = Path.Combine(tempDir, "application_default_credentials.json");
            if (process.ExitCode == 0 && File.Exists(credPath))
            {
                return new GcloudLoginResult(true, credPath, tempDir);
            }

            CleanupTempDirectory(tempDir);
            var reason = stderrLines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? $"gcloud 退出码 {process.ExitCode}";
            return new GcloudLoginResult(false, ErrorMessage: $"浏览器登录未完成：{reason}");
        }
        catch (OperationCanceledException)
        {
            TryKillTree(process);
            CleanupTempDirectory(tempDir);
            return new GcloudLoginResult(false, ErrorMessage: "已取消浏览器登录。");
        }
        catch (Exception ex)
        {
            TryKillTree(process);
            CleanupTempDirectory(tempDir);
            return new GcloudLoginResult(false, ErrorMessage: $"启动浏览器登录失败：{ex.Message}");
        }
    }

    public static void CleanupTempDirectory(string? tempDir)
    {
        if (string.IsNullOrWhiteSpace(tempDir))
        {
            return;
        }

        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static async Task ReadAllLinesAsync(StreamReader reader, Action<string> onLine, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                onLine(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
