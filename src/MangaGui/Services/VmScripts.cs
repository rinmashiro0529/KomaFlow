using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MangaGui.Models;

namespace MangaGui.Services;

/// <summary>
/// Parsed output of vm_pack.sh pack (PROTO 2: PACK_OK bytes sha256 image_count).
/// </summary>
public sealed record PackOutputResult(
    long SizeBytes,
    string? Sha256,
    int? ImageCount);

/// <summary>
/// Locates existing colab_manga scripts & bundled GUI shell assets with user-selected priority,
/// uploads them with LF endings, and parses VM/CLI protocol outputs strictly (A-01, A-10, A-14).
/// </summary>
public sealed class VmScripts
{
    public const string SessionName = "manga";

    /// <summary>
    /// Only manga.json (the user-edited pipeline config) is read from the parent colab_manga folder.
    /// Deployment and translation use the GUI's own bundled scripts.
    /// manga.json is required for the mit engine only; mtui ships its own mtui_config.json.
    /// </summary>
    public static readonly string[] RequiredExistingScripts =
    [
        "manga.json"
    ];

    /// <summary>Optional glossary in the scripts dir that overrides the bundled empty sakura_dict.txt.</summary>
    public const string UserSakuraDictFileName = "sakura_dict.txt";

    /// <summary>Pinned manga-translator-ui commit deployed by vm_setup.sh (short form for logs).</summary>
    public const string MtuiRevision = "8cd6fee";

    public static readonly string[] BundledGuiAssets =
    [
        "vm_setup.sh",
        "vm_job.sh",
        "vm_status.sh",
        "vm_pack.sh",
        "mtui_patch.py",
        "mtui_config.json",
        "sakura_dict.txt",
        "prompt_hq.yaml"
    ];

    private static readonly Regex ImagesRegex = new(@"\bIMAGES\s+(\d+)", RegexOptions.Compiled);
    private static readonly Regex ProtoRegex = new(@"^PROTO\s+(\d+)", RegexOptions.Compiled);
    private static readonly Regex DoneCountRegex = new(@"^DONE_COUNT\s+(\d+)", RegexOptions.Compiled);
    private static readonly Regex JobAliveRegex = new(@"^(?:JOB_ALIVE|TRANSLATE_ALIVE)\s+(\d+)", RegexOptions.Compiled);
    private static readonly Regex JobExitCodeRegex = new(@"^JOB_EXIT_CODE\s+(-?\d+)", RegexOptions.Compiled);
    private static readonly Regex EngineLineRegex = new(@"^ENGINE\s+(\S+)\s+(ready|missing)\s*$", RegexOptions.Compiled);
    private static readonly Regex GpuProfileRegex = new(@"^GPU_PROFILE\s+(\S+)\s*$", RegexOptions.Compiled);
    private static readonly Regex GpuVramMbRegex = new(@"^GPU_VRAM_MB\s+(\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex InpaintModeRegex = new(@"^INPAINT_MODE\s+(\S+)\s*$", RegexOptions.Compiled);
    private static readonly Regex PackOkV2Regex = new(@"^PACK_OK\s+(\d+)\s+([0-9a-fA-F]{64})\s+(\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex SplitOkRegex = new(@"^SPLIT_OK\s+(\d+)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex PackOkLegacyRegex = new(@"^PACK_OK\s+(\d+)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// google-colab-cli session line: <c>[name] endpoint | Hardware: X | Shape: Y | Variant: Z</c>.
    /// <c>[?]</c> marks a server-side assignment that has no local session name.
    /// </summary>
    private static readonly Regex SessionLineRegex = new(
        @"^\[(?<name>[^\]]+)\]\s+\S+\s*\|\s*Hardware:",
        RegexOptions.Compiled);

    public const string UnnamedAssignmentHint =
        "这些实例同样在计费，但 CLI 无法按名称停止；请打开 Colab 网页 → 资源 → 管理会话，逐个核对并终止不需要的实例。";

    private readonly SessionStore _store;
    private readonly string _appBaseDirectory;

    public string? ExistingScriptsDirectory { get; private set; }
    public string? BundledAssetsDirectory { get; private set; }
    public ScriptsSourceKind ExistingScriptsSource { get; private set; } = ScriptsSourceKind.None;

    public VmScripts(SessionStore store, string? appBaseDirectory = null)
    {
        _store = store;
        _appBaseDirectory = appBaseDirectory ?? AppContext.BaseDirectory;
        RefreshLocations();
    }

    /// <summary>
    /// Bundled assets are always required; the existing scripts dir (manga.json) is required
    /// for the mit engine only. Under mtui the scripts dir stays optional (user sakura_dict.txt).
    /// </summary>
    public bool HasRequiredFilesForEngine(string engine) =>
        BundledAssetsDirectory is not null
        && ContainsAllFiles(BundledAssetsDirectory, BundledGuiAssets)
        && (TranslationEngines.Normalize(engine) == TranslationEngines.Mtui
            || (ExistingScriptsDirectory is not null
                && ContainsAllFiles(ExistingScriptsDirectory, RequiredExistingScripts)));

    public bool RefreshLocations()
    {
        (ExistingScriptsDirectory, ExistingScriptsSource) = FindExistingScriptsDirectory();
        BundledAssetsDirectory = FindBundledAssetsDirectory();
        return HasRequiredFilesForEngine(TranslationEngines.Normalize(_store.LoadSettings().TranslationEngine));
    }

    public bool TrySetExistingScriptsDirectory(string directory)
    {
        if (!ContainsAllFiles(directory, RequiredExistingScripts))
        {
            return false;
        }

        ExistingScriptsDirectory = directory;
        ExistingScriptsSource = ScriptsSourceKind.UserSelected;
        var settings = _store.LoadSettings();
        settings.ScriptsDir = directory;
        _store.TrySaveSettings(settings);
        return true;
    }

    public IReadOnlyList<string> GetMissingScriptsReport(string engine)
    {
        var missing = new List<string>();
        if (TranslationEngines.Normalize(engine) == TranslationEngines.Mit
            && (ExistingScriptsDirectory is null || !ContainsAllFiles(ExistingScriptsDirectory, RequiredExistingScripts)))
        {
            missing.Add("未找到翻译配置目录（需包含 manga.json）");
        }

        if (BundledAssetsDirectory is null || !ContainsAllFiles(BundledAssetsDirectory, BundledGuiAssets))
        {
            missing.Add("未找到内置资产目录（需包含 assets 下的 vm_setup.sh、vm_job.sh、vm_status.sh、vm_pack.sh、mtui_patch.py、mtui_config.json、sakura_dict.txt、prompt_hq.yaml）");
        }

        return missing;
    }

    public IReadOnlyDictionary<string, string> GetBundledAssetChecksums()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (BundledAssetsDirectory is null)
        {
            return map;
        }

        foreach (var file in BundledGuiAssets)
        {
            var full = Path.Combine(BundledAssetsDirectory, file);
            if (!File.Exists(full)) continue;
            try
            {
                using var stream = File.OpenRead(full);
                var hash = SHA256.HashData(stream);
                map[file] = Convert.ToHexString(hash).ToLowerInvariant()[..16];
            }
            catch
            {
                map[file] = "unreadable";
            }
        }

        return map;
    }

    public async Task<ColabResult> UploadAllScriptsAsync(
        IColabCli cli,
        string engine,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedEngine = TranslationEngines.Normalize(engine);
        if (!HasRequiredFilesForEngine(normalizedEngine))
        {
            RefreshLocations();
        }

        if (normalizedEngine == TranslationEngines.Mit
            && (ExistingScriptsDirectory is null || !ContainsAllFiles(ExistingScriptsDirectory, RequiredExistingScripts)))
        {
            return new ColabResult(-1, string.Empty, "找不到翻译配置目录（需包含 manga.json）。");
        }

        if (BundledAssetsDirectory is null || !ContainsAllFiles(BundledAssetsDirectory, BundledGuiAssets))
        {
            return new ColabResult(-1, string.Empty, "找不到内置资产目录（需包含 vm_setup.sh / vm_job.sh / vm_status.sh / vm_pack.sh）。");
        }

        var uploads = new List<(string LocalPath, string RemotePath, bool NormalizeLf)>();
        if (normalizedEngine == TranslationEngines.Mit && ExistingScriptsDirectory is not null)
        {
            foreach (var file in RequiredExistingScripts)
            {
                uploads.Add((Path.Combine(ExistingScriptsDirectory, file), $"/content/{file}", file.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)));
            }
        }

        uploads.AddRange(BuildBundledUploads());

        var tempDir = Path.Combine(Path.GetTempPath(), "manga_gui_scripts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            for (int i = 0; i < uploads.Count; i++)
            {
                var (localPath, remotePath, normalizeLf) = uploads[i];
                var fileName = Path.GetFileName(localPath);
                onProgress?.Invoke($"正在上传脚本 ({i + 1}/{uploads.Count}): {fileName}");

                var uploadSource = localPath;
                if (normalizeLf)
                {
                    var normalizedPath = Path.Combine(tempDir, fileName);
                    var text = await File.ReadAllTextAsync(localPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                    var lfText = text.Replace("\r\n", "\n").Replace("\r", "\n");
                    await File.WriteAllTextAsync(normalizedPath, lfText, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                    uploadSource = normalizedPath;
                }

                var res = await cli.RunAsync(
                    ["upload", "-s", SessionName, uploadSource, remotePath],
                    TimeSpan.FromMinutes(3),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!res.IsSuccess)
                {
                    return res;
                }
            }

            return new ColabResult(0, "UPLOAD_ALL_OK", string.Empty);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    public async Task<ColabResult> UploadBundledAssetsOnlyAsync(
        IColabCli cli,
        CancellationToken cancellationToken = default)
    {
        if (BundledAssetsDirectory is null || !ContainsAllFiles(BundledAssetsDirectory, BundledGuiAssets))
        {
            RefreshLocations();
        }

        if (BundledAssetsDirectory is null || !ContainsAllFiles(BundledAssetsDirectory, BundledGuiAssets))
        {
            return new ColabResult(-1, string.Empty, "找不到内置资产目录（vm_setup.sh / vm_job.sh / vm_status.sh / vm_pack.sh）。");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "manga_gui_assets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            foreach (var (localPath, remotePath, _) in BuildBundledUploads())
            {
                var normalizedPath = Path.Combine(tempDir, Path.GetFileName(remotePath));
                var text = await File.ReadAllTextAsync(localPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                var lfText = text.Replace("\r\n", "\n").Replace("\r", "\n");
                await File.WriteAllTextAsync(normalizedPath, lfText, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

                var res = await cli.RunAsync(
                    ["upload", "-s", SessionName, normalizedPath, remotePath],
                    TimeSpan.FromMinutes(2),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!res.IsSuccess)
                {
                    return res;
                }
            }

            return new ColabResult(0, "UPLOAD_ASSETS_OK", string.Empty);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Bundled-asset upload list. A user-provided sakura_dict.txt in the scripts dir replaces the
    /// bundled empty glossary (both engines read /content/sakura_dict.txt).
    /// </summary>
    private List<(string LocalPath, string RemotePath, bool NormalizeLf)> BuildBundledUploads()
    {
        var uploads = new List<(string, string, bool)>();
        foreach (var file in BundledGuiAssets)
        {
            var localPath = Path.Combine(BundledAssetsDirectory!, file);
            if (string.Equals(file, UserSakuraDictFileName, StringComparison.OrdinalIgnoreCase)
                && ExistingScriptsDirectory is not null)
            {
                var userDict = Path.Combine(ExistingScriptsDirectory, UserSakuraDictFileName);
                if (File.Exists(userDict))
                {
                    localPath = userDict;
                }
            }

            uploads.Add((localPath, $"/content/{file}", true));
        }

        return uploads;
    }

    /// <summary>
    /// Three-state parser for `colab sessions` output (A-01).
    /// Returns ExplicitlyAbsent ONLY when CLI succeeded AND emitted either an explicit empty-list phrase
    /// or recognized `[name] endpoint | Hardware: ...` session lines that do not include sessionName.
    /// Empty stdout, errors, or unrecognized output ALWAYS return Unknown.
    /// </summary>
    public static SessionQueryResult ParseSessionPresence(ColabResult result, string sessionName = SessionName)
    {
        if (!result.IsSuccess)
        {
            return new SessionQueryResult(
                SessionPresence.Unknown,
                result.TimedOut ? "查询会话超时" : $"查询会话失败（退出码 {result.ExitCode}）",
                result.CombinedOutput);
        }

        var output = result.StdOut;
        if (string.IsNullOrWhiteSpace(output))
        {
            return new SessionQueryResult(
                SessionPresence.Unknown,
                "colab sessions 返回空输出，无法确认云端是否仍有活动会话",
                result.CombinedOutput);
        }

        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var looseTokenRegex = new Regex($@"(^|[\s|│]){Regex.Escape(sessionName)}($|[\s|│:])");
        bool sawTarget = false;
        bool sawLooseTarget = false;
        int unnamed = 0;
        int otherNamed = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var match = SessionLineRegex.Match(line);
            if (match.Success)
            {
                var name = match.Groups["name"].Value.Trim();
                if (string.Equals(name, sessionName, StringComparison.Ordinal))
                {
                    sawTarget = true;
                }
                else if (name == "?")
                {
                    unnamed++;
                }
                else
                {
                    otherNamed++;
                }
                continue;
            }

            // Safe-side fallback for unknown layouts: a bare session-name token counts as present.
            if (looseTokenRegex.IsMatch(line))
            {
                sawLooseTarget = true;
            }
        }

        if (sawTarget || sawLooseTarget)
        {
            return new SessionQueryResult(SessionPresence.Present, $"检测到活动会话 {sessionName}", output, unnamed);
        }

        if (unnamed > 0 || otherNamed > 0)
        {
            var reason = unnamed > 0
                ? $"会话列表中不存在 {sessionName}，但有 {unnamed} 个未命名实例"
                : $"会话列表中不存在 {sessionName}";
            return new SessionQueryResult(SessionPresence.ExplicitlyAbsent, reason, output, unnamed);
        }

        if (output.Contains("No active sessions", StringComparison.OrdinalIgnoreCase)
            || output.Contains("0 active sessions", StringComparison.OrdinalIgnoreCase)
            || output.Contains("No sessions found", StringComparison.OrdinalIgnoreCase))
        {
            return new SessionQueryResult(SessionPresence.ExplicitlyAbsent, "云端明确返回无活动会话", output);
        }

        return new SessionQueryResult(
            SessionPresence.Unknown,
            "无法识别 colab sessions 输出格式，拒绝假设会话已停止",
            result.CombinedOutput);
    }

    public static bool HasActiveSession(string sessionsOutput, string sessionName = SessionName)
    {
        return ParseSessionPresence(new ColabResult(0, sessionsOutput, string.Empty), sessionName).Presence
            == SessionPresence.Present;
    }

    public static DeployStage ParseDeployStageFromLine(string line, DeployStage current)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return current;
        }

        var parsed =
            line.Contains("VM_SETUP_COMPLETE", StringComparison.OrdinalIgnoreCase) ? DeployStage.Completed
            : line.Contains("STAGE5", StringComparison.OrdinalIgnoreCase) ? DeployStage.Stage5TranslatorWarmup
            : line.Contains("STAGE4", StringComparison.OrdinalIgnoreCase) ? DeployStage.Stage4LlamaServer
            : line.Contains("STAGE3", StringComparison.OrdinalIgnoreCase) ? DeployStage.Stage3EnvScript
            : line.Contains("STAGE2", StringComparison.OrdinalIgnoreCase) ? DeployStage.Stage2LlamaModel
            : line.Contains("STAGE1", StringComparison.OrdinalIgnoreCase) ? DeployStage.Stage1TranslatorEnv
            : line.Contains("SUMMARY", StringComparison.OrdinalIgnoreCase) ? DeployStage.VerifyingHealth
            : current;

        // A re-attached poll replays the whole log from offset 0 — never report a stage
        // regression; DeployStage is declared in progression order.
        return parsed > current ? parsed : current;
    }

    /// <summary>
    /// Runs vm_setup.sh DETACHED on the VM and polls its byte-offset log with short ssh calls.
    /// A single long-lived ssh dies silently mid-deploy ("client_loop: send disconnect") while
    /// billing continues; short polls survive link drops, and a retry re-attaches through
    /// SETUP_ALREADY_RUNNING (offset 0 replays the log — the stage parser is monotonic).
    /// </summary>
    /// <returns>
    /// A <see cref="ColabResult"/> whose <c>ExitCode</c> is the remote script's exit code and
    /// whose <c>StdOut</c> is the aggregated setup output (frame markers excluded).
    /// </returns>
    public static async Task<ColabResult> RunDetachedSetupAsync(
        IColabCli cli,
        string engine,
        Action<string>? onStdoutLine,
        CancellationToken ct,
        TimeSpan? pollInterval = null,
        TimeSpan? overallTimeout = null,
        int maxConsecutivePollFailures = 8,
        string? profileHint = null,
        string? inpaintMode = null)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(15);
        // A cold high-tier deploy or flock -w may need extended time.
        var timeout = overallTimeout ?? TimeSpan.FromMinutes(50);
        var deadline = DateTimeOffset.UtcNow + timeout;

        var detachArgs = new List<string>
        {
            "bash", "/content/vm_setup.sh", "--detach", TranslationEngines.Normalize(engine)
        };
        if (!string.IsNullOrWhiteSpace(profileHint) || !string.IsNullOrWhiteSpace(inpaintMode))
        {
            detachArgs.Add(string.IsNullOrWhiteSpace(profileHint) ? GpuProfiles.Auto : profileHint.Trim());
            if (!string.IsNullOrWhiteSpace(inpaintMode))
            {
                detachArgs.Add(InpaintModes.Normalize(inpaintMode));
            }
        }

        // Launch detached; SETUP_ALREADY_RUNNING means an earlier attempt is still going —
        // we simply re-attach and read the log from offset 0.
        ColabResult? launch = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            launch = await cli.RunSshAsync(
                SessionName,
                detachArgs,
                TimeSpan.FromMinutes(2),
                cancellationToken: ct).ConfigureAwait(false);
            if (launch.IsSuccess && HasLaunchAck(launch.StdOut))
            {
                break;
            }

            if (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }

        if (launch is null || !launch.IsSuccess || !HasLaunchAck(launch.StdOut))
        {
            return new ColabResult(-1, string.Empty,
                $"无法启动部署脚本：{launch?.CombinedOutput ?? "no response"}");
        }

        long offset = 0;
        var aggregate = new StringBuilder();
        var carry = new StringBuilder();
        int consecutiveFailures = 0;
        int notRunningNoRcPolls = 0;
        string lastError = string.Empty;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline)
            {
                FlushCarry(carry, onStdoutLine);
                return new ColabResult(-1, aggregate.ToString(),
                    $"部署脚本轮询超时：{timeout.TotalMinutes:F0} 分钟内未完成");
            }

            var poll = await cli.RunSshAsync(
                SessionName,
                ["bash", "/content/vm_setup.sh", "--poll", offset.ToString(),
                    SetupPollMaxBytes.ToString()],
                TimeSpan.FromMinutes(1),
                cancellationToken: ct).ConfigureAwait(false);

            if (!poll.IsSuccess
                || !TryParsePollFrame(poll.StdOut, out var next, out var chunk, out var rc, out var running))
            {
                consecutiveFailures++;
                lastError = poll.IsSuccess ? "轮询输出无法解析" : poll.CombinedOutput;
                if (consecutiveFailures > maxConsecutivePollFailures)
                {
                    FlushCarry(carry, onStdoutLine);
                    return new ColabResult(-1, aggregate.ToString(),
                        $"部署状态轮询连续失败 {consecutiveFailures} 次：{lastError}");
                }

                await Task.Delay(interval, ct).ConfigureAwait(false);
                continue;
            }

            consecutiveFailures = 0;
            offset = next;
            aggregate.Append(chunk);
            EmitCompletedLines(chunk, carry, onStdoutLine);

            if (rc.Length > 0)
            {
                FlushCarry(carry, onStdoutLine);
                return int.TryParse(rc, out var exitCode)
                    ? new ColabResult(exitCode, aggregate.ToString(), string.Empty)
                    : new ColabResult(-1, aggregate.ToString(), $"部署脚本退出码无法解析：{rc}");
            }

            // Lock free but no exit code file: the detached run died without finishing.
            // Require two consecutive confirmations to rule out a probe racing the .rc write.
            notRunningNoRcPolls = running ? 0 : notRunningNoRcPolls + 1;
            if (notRunningNoRcPolls >= 2)
            {
                FlushCarry(carry, onStdoutLine);
                return new ColabResult(-1, aggregate.ToString(), "部署脚本意外退出（无退出码）");
            }

            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    private static bool HasLaunchAck(string stdout) =>
        stdout.Contains("SETUP_DETACHED", StringComparison.Ordinal)
        || stdout.Contains("SETUP_ALREADY_RUNNING", StringComparison.Ordinal);

    /// <summary>Max bytes a single --poll may return; keeps frames under the output buffer bound.</summary>
    internal const long SetupPollMaxBytes = 131072;

    private static bool TryParsePollFrame(
        string stdout,
        out long next,
        out string content,
        out string rc,
        out bool running)
    {
        next = 0;
        content = string.Empty;
        rc = string.Empty;
        running = false;

        if (string.IsNullOrEmpty(stdout))
        {
            return false;
        }

        // Defense in depth: a CRLF-joined capture must not break the LF-framed protocol.
        stdout = stdout.Replace("\r\n", "\n");

        const string beginTag = "__BEGIN\n";
        var beginIdx = stdout.IndexOf(beginTag, StringComparison.Ordinal);
        if (beginIdx < 0)
        {
            return false;
        }

        var contentStart = beginIdx + beginTag.Length;
        var endIdx = stdout.IndexOf("\n__END", contentStart, StringComparison.Ordinal);
        if (endIdx < 0)
        {
            return false;
        }

        var sawNext = false;
        foreach (var line in stdout[..beginIdx].Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("__NEXT=", StringComparison.Ordinal)
                && long.TryParse(t["__NEXT=".Length..], out var parsed))
            {
                next = parsed;
                sawNext = true;
            }
        }

        var sawEnd = false;
        var sawRunning = false;
        foreach (var line in stdout[endIdx..].Split('\n'))
        {
            var t = line.Trim();
            if (t == "__END")
            {
                sawEnd = true;
            }
            else if (t.StartsWith("__RC=", StringComparison.Ordinal))
            {
                rc = t["__RC=".Length..].Trim();
            }
            else if (t.StartsWith("__RUNNING=", StringComparison.Ordinal))
            {
                running = t["__RUNNING=".Length..].Trim() == "1";
                sawRunning = true;
            }
        }

        if (!sawNext || !sawEnd || !sawRunning)
        {
            return false;
        }

        content = stdout[contentStart..endIdx];
        return true;
    }

    /// <summary>Feeds a chunk into a carry buffer; emits only lines completed by '\n'.</summary>
    private static void EmitCompletedLines(string chunk, StringBuilder carry, Action<string>? onLine)
    {
        carry.Append(chunk);
        int idx;
        while ((idx = carry.ToString().IndexOf('\n')) >= 0)
        {
            onLine?.Invoke(carry.ToString(0, idx).TrimEnd('\r'));
            carry.Remove(0, idx + 1);
        }
    }

    /// <summary>Emits any trailing unterminated line left in the carry buffer.</summary>
    private static void FlushCarry(StringBuilder carry, Action<string>? onLine)
    {
        if (carry.Length == 0)
        {
            return;
        }

        onLine?.Invoke(carry.ToString().TrimEnd('\r'));
        carry.Clear();
    }

    public static int ParseExpectedImageCount(string runTranslateOutput)
    {
        if (string.IsNullOrWhiteSpace(runTranslateOutput))
        {
            return 0;
        }

        var match = ImagesRegex.Match(runTranslateOutput);
        return match.Success && int.TryParse(match.Groups[1].Value, out var count) ? count : 0;
    }

    /// <summary>Parses "SPLIT_OK &lt;count&gt;" from vm_pack.sh split_out; 0 when absent.</summary>
    public static int ParseSplitCount(string output)
    {
        var match = SplitOkRegex.Match(output ?? string.Empty);
        return match.Success && int.TryParse(match.Groups[1].Value, out var count) ? count : 0;
    }

    public static PackOutputResult? ParsePackOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var m2 = PackOkV2Regex.Match(line);
            if (m2.Success
                && long.TryParse(m2.Groups[1].Value, out var bytes2)
                && int.TryParse(m2.Groups[3].Value, out var count2))
            {
                return new PackOutputResult(bytes2, m2.Groups[2].Value.ToLowerInvariant(), count2);
            }

            var m1 = PackOkLegacyRegex.Match(line);
            if (m1.Success && long.TryParse(m1.Groups[1].Value, out var bytes1))
            {
                return new PackOutputResult(bytes1, null, null);
            }
        }

        return null;
    }

    public static VmStatusSnapshot ParseVmStatus(string output)
    {
        int proto = 1;
        bool llamaOk = false;
        bool mitOk = false;
        string gpuInfo = string.Empty;
        int? doneCount = null;
        int? aliveCount = null;
        int? exitCode = null;
        string? lastSaving = null;
        string engineName = string.Empty;
        bool engineReady = false;
        string? gpuProfile = null;
        int? gpuVramMb = null;
        string? inpaintMode = null;
        bool? fallbackLlamaOk = null;

        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (ProtoRegex.Match(line) is { Success: true } protoMatch
                && int.TryParse(protoMatch.Groups[1].Value, out var pv))
            {
                proto = pv;
            }
            else if (line.Contains("FALLBACK_LLAMA_OK", StringComparison.Ordinal))
            {
                fallbackLlamaOk = true;
            }
            else if (line.Contains("FALLBACK_LLAMA_DOWN", StringComparison.Ordinal))
            {
                fallbackLlamaOk = false;
            }
            else if (line.Contains("LLAMA_OK", StringComparison.Ordinal))
            {
                llamaOk = true;
            }
            else if (line.Contains("MIT_OK", StringComparison.Ordinal))
            {
                mitOk = true;
            }
            else if (EngineLineRegex.Match(line) is { Success: true } engineMatch)
            {
                engineName = engineMatch.Groups[1].Value;
                engineReady = string.Equals(engineMatch.Groups[2].Value, "ready", StringComparison.OrdinalIgnoreCase);
            }
            else if (GpuProfileRegex.Match(line) is { Success: true } gpMatch)
            {
                gpuProfile = GpuProfiles.NormalizeOrNull(gpMatch.Groups[1].Value);
            }
            else if (GpuVramMbRegex.Match(line) is { Success: true } vramMatch
                     && int.TryParse(vramMatch.Groups[1].Value, out var vramVal))
            {
                gpuVramMb = vramVal;
            }
            else if (InpaintModeRegex.Match(line) is { Success: true } impMatch)
            {
                inpaintMode = InpaintModes.Normalize(impMatch.Groups[1].Value);
            }
            else if (line.StartsWith("GPU ", StringComparison.Ordinal))
            {
                gpuInfo = line[4..].Trim();
            }
            else if (DoneCountRegex.Match(line) is { Success: true } doneMatch
                     && int.TryParse(doneMatch.Groups[1].Value, out var dc))
            {
                doneCount = dc;
            }
            else if (JobAliveRegex.Match(line) is { Success: true } aliveMatch
                     && int.TryParse(aliveMatch.Groups[1].Value, out var ac))
            {
                aliveCount = ac;
            }
            else if (JobExitCodeRegex.Match(line) is { Success: true } rcMatch
                     && int.TryParse(rcMatch.Groups[1].Value, out var rc))
            {
                exitCode = rc;
            }
            else if (line.StartsWith("LAST_SAVING ", StringComparison.Ordinal))
            {
                lastSaving = line["LAST_SAVING ".Length..].Trim();
            }
        }

        return new VmStatusSnapshot
        {
            ProtocolVersion = proto,
            LlamaOk = llamaOk,
            MitOk = mitOk,
            GpuInfo = gpuInfo,
            DoneCount = doneCount,
            TranslateAliveCount = aliveCount,
            JobExitCode = exitCode,
            LastSavingLine = lastSaving,
            EngineName = engineName,
            EngineReady = engineReady,
            GpuProfile = gpuProfile,
            GpuVramMb = gpuVramMb,
            InpaintMode = inpaintMode,
            FallbackLlamaOk = fallbackLlamaOk,
            CheckedAt = DateTimeOffset.Now,
            IsDisconnected = false
        };
    }

    private (string? Dir, ScriptsSourceKind Source) FindExistingScriptsDirectory()
    {
        // 1. User-selected scripts_dir in settings.json ALWAYS takes highest precedence (A-10).
        var saved = _store.LoadSettings().ScriptsDir;
        if (!string.IsNullOrWhiteSpace(saved) && ContainsAllFiles(saved, RequiredExistingScripts))
        {
            return (saved, ScriptsSourceKind.UserSelected);
        }

        // 2. Fallback: walk up from application base directory (bounded depth)
        var dir = new DirectoryInfo(_appBaseDirectory);
        for (int depth = 0; depth <= 6 && dir is not null; depth++)
        {
            if (ContainsAllFiles(dir.FullName, RequiredExistingScripts))
            {
                return (dir.FullName, ScriptsSourceKind.AutoDiscovered);
            }

            dir = dir.Parent;
        }

        return (null, ScriptsSourceKind.None);
    }

    private string? FindBundledAssetsDirectory()
    {
        var dir = new DirectoryInfo(_appBaseDirectory);
        for (int depth = 0; depth <= 6 && dir is not null; depth++)
        {
            var candidate = Path.Combine(dir.FullName, "assets");
            if (ContainsAllFiles(candidate, BundledGuiAssets))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static bool ContainsAllFiles(string directory, IReadOnlyList<string> requiredFiles)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        foreach (var file in requiredFiles)
        {
            if (!File.Exists(Path.Combine(directory, file)))
            {
                return false;
            }
        }

        return true;
    }
}
