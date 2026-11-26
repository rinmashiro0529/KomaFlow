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

            var bat = Path.Combine(dir, "gcloud.bat");
            if (File.Exists(bat)) { resolvedPath = bat; return true; }
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

        return await RunLoginProcessAsync(dataDirectory, gcloudPath, onAuthUrl, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<GcloudLoginResult> RunLoginProcessAsync(string dataDirectory, string gcloudPath, Action<string>? onAuthUrl, CancellationToken cancellationToken)
    {
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

        if (!Path.GetExtension(gcloudPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            psi.Environment["KOMAFLOW_LOGIN_SCRIPT"] = Path.GetFullPath(gcloudPath);
            psi.Arguments = $"/d /v:off /s /c \"\"%KOMAFLOW_LOGIN_SCRIPT%\" auth application-default login --disable-quota-project --scopes={RequiredScopes}\"";
        }
        else
        {
            psi.ArgumentList.Add("auth");
            psi.ArgumentList.Add("application-default");
            psi.ArgumentList.Add("login");
            psi.ArgumentList.Add("--disable-quota-project");
            psi.ArgumentList.Add($"--scopes={RequiredScopes}");
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var token = deadline.Token;
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

            var outTask = ReadAllLinesAsync(process.StandardOutput, l => HandleLine(l, false), token);
            var errTask = ReadAllLinesAsync(process.StandardError, l => HandleLine(l, true), token);

            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);

            var credPath = Path.Combine(tempDir, "application_default_credentials.json");
            if (process.ExitCode == 0 && File.Exists(credPath))
            {
                return new GcloudLoginResult(true, credPath, tempDir);
            }

            CleanupTempDirectory(tempDir);
            var output = string.Join(" ", stderrLines).ToLowerInvariant();
            var reason = output.Contains("429") ? "请求限流，请稍后重试。" : output.Contains("invalid_grant") ? "凭据失效，请重新登录。" : output.Contains("403") || output.Contains("permission") ? "缺少所需权限。" : output.Contains("json") ? "凭据 JSON 格式有误。" : output.Contains("network") || output.Contains("proxy") || output.Contains("connection") ? "网络或代理不可达。" : $"gcloud 退出码 {process.ExitCode}。";
            return new GcloudLoginResult(false, ErrorMessage: $"浏览器登录未完成：{reason}");
        }
        catch (OperationCanceledException)
        {
            TryKillTree(process);
            CleanupTempDirectory(tempDir);
            return new GcloudLoginResult(false, ErrorMessage: cancellationToken.IsCancellationRequested ? "已取消浏览器登录。" : "浏览器登录超过 5 分钟，请检查网络或代理后重试。");
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
                process.WaitForExit(3000);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
