using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MangaGui.Models;

namespace MangaGui.Services;

/// <summary>
/// Sanitizes sensitive tokens and local user paths before exporting/copying logs (A-12).
/// </summary>
public static class LogSanitizer
{
    private static readonly Regex BearerRegex = new(
        @"(?i)((?:authorization[=:\s]+(?:bearer|basic)\s+)|(?:bearer\s+)|(?:(?:access[_-]?|refresh[_-]?|id[_-]?)?token[=:\s]+)|(?:api[_-]?key[=:\s]+)|(?:authorization[=:\s]+))([A-Za-z0-9\-._~+/]+=*)",
        RegexOptions.Compiled);

    // Covers JSON-style `"access_token": "..."` pairs that the pattern above misses.
    private static readonly Regex KeyedSecretRegex = new(
        @"(?i)(""?(?:[\w-]*token|client_secret|api[_-]?key|password|code_verifier)""?\s*[:=]\s*""?)([^""\s,}]+)",
        RegexOptions.Compiled);

    // Google OAuth access tokens, refresh tokens and client secrets, wherever they appear.
    private static readonly Regex GoogleSecretRegex = new(
        @"(?:ya29\.[A-Za-z0-9_\-.]+|1//[A-Za-z0-9_\-]+|GOCSPX-[A-Za-z0-9_\-]+)",
        RegexOptions.Compiled);

    private static readonly Regex EmailRegex = new(
        @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}",
        RegexOptions.Compiled);

    private static readonly Regex UserProfileRegex = new(
        @"(?i)([A-Z]:[\\/]Users[\\/])([^\\/\r\n""']+)",
        RegexOptions.Compiled);

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = BearerRegex.Replace(text, "$1<REDACTED>");
        result = KeyedSecretRegex.Replace(result, "$1<REDACTED>");
        result = GoogleSecretRegex.Replace(result, "<REDACTED>");
        result = EmailRegex.Replace(result, "<EMAIL>");
        result = UserProfileRegex.Replace(result, "$1<USER>");
        return result;
    }
}

/// <summary>
/// Wraps google-colab-cli with direct colab.exe resolution (bypassing cmd.exe batch parsing when possible),
/// UTF-8 streaming output with bounded memory buffers, global SSH serialization + cooldown,
/// and opt-in transport-level HTTP 429 exponential backoff retries (A-12).
/// </summary>
public sealed class ColabCli : IColabCli
{
    public const int MaxRetainedCharsPerStream = 512 * 1024;

    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8)
    ];

    private static readonly TimeSpan[] DefaultSshBusyDelays =
    [
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(12),
        TimeSpan.FromSeconds(20)
    ];

    private static readonly Regex CmdColabExeRegex = new(
        @"set\s+""?COLAB_EXE=([^""\r\n]+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StrictRateLimitRegex = new(
        @"(?i)(HTTP/?[\d.]*\s*429|status[\s:=]+429|code[\s:=]+429|429\s+Client\s+Error|429\s+Too\s+Many\s+Requests|Too\s+Many\s+Requests|RESOURCE_EXHAUSTED)",
        RegexOptions.Compiled);

    private static readonly Lazy<string?> s_windowsOpenSshDir = new(ResolveWindowsOpenSshDirectory);

    /// <summary>
    /// Directory of the Windows (Win32) OpenSSH client, or null when it is not installed.
    /// </summary>
    internal static string? WindowsOpenSshDirectory => s_windowsOpenSshDir.Value;

    private static string? ResolveWindowsOpenSshDirectory()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH");
        return File.Exists(Path.Combine(dir, "ssh.exe")) ? dir : null;
    }

    /// <summary>
    /// Returns a PATH value that resolves <paramref name="openSshDir"/> first, so the colab
    /// child's `ssh` lookup picks Windows OpenSSH instead of an earlier MSYS/Git ssh.
    /// </summary>
    public static string BuildChildPath(string? currentPath, string? openSshDir)
    {
        if (string.IsNullOrEmpty(openSshDir))
        {
            return currentPath ?? string.Empty;
        }

        static bool Matches(string entry, string dir) =>
            string.Equals(
                entry.Trim().Trim('"').TrimEnd('\\', '/'),
                dir.Trim().Trim('"').TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);

        var entries = (currentPath ?? string.Empty).Split(';');
        var firstNonEmpty = entries.FirstOrDefault(e => e.Trim().Trim('"').Length > 0);
        if (firstNonEmpty != null && Matches(firstNonEmpty, openSshDir))
        {
            return currentPath!;
        }

        var rest = string.Join(';', entries.Where(e => !Matches(e, openSshDir)));
        return rest.Length == 0 ? openSshDir : openSshDir + ";" + rest;
    }

    private readonly SemaphoreSlim _sshGate = new(1, 1);
    private readonly TimeSpan _sshMinInterval;
    private readonly TimeSpan[] _retryDelays;
    private readonly TimeSpan[] _sshBusyDelays;
    private readonly Func<ColabAccountContext, IReadOnlyList<string>, TimeSpan, Action<string>?, Action<string>?, CancellationToken, Task<ColabResult>> _accountExecutor;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string?>? _configuredExePathProvider;

    private ColabAccountContext _activeAccount = ColabAccountContext.Default;
    private DateTimeOffset _lastSshCompletedUtc = DateTimeOffset.MinValue;
    private string? _cachedExecutablePath;
    private bool _cachedInjectAdcAuth;
    private bool _cachedUsesBatchShim;

    public ColabAccountContext ActiveAccount
    {
        get => Volatile.Read(ref _activeAccount);
        set => Volatile.Write(ref _activeAccount, value ?? ColabAccountContext.Default);
    }

    public bool UsesBatchShim
    {
        get
        {
            IsColabAvailable(out _);
            return _cachedUsesBatchShim;
        }
    }

    public ColabCli(
        TimeSpan? sshMinInterval = null,
        TimeSpan[]? retryDelays = null,
        TimeSpan[]? sshBusyDelays = null,
        Func<IReadOnlyList<string>, TimeSpan, Action<string>?, Action<string>?, CancellationToken, Task<ColabResult>>? executor = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<string?>? configuredExePathProvider = null,
        Func<ColabAccountContext, IReadOnlyList<string>, TimeSpan, Action<string>?, Action<string>?, CancellationToken, Task<ColabResult>>? accountExecutor = null)
    {
        _sshMinInterval = sshMinInterval ?? TimeSpan.FromSeconds(3);
        _retryDelays = retryDelays ?? DefaultRetryDelays;
        _sshBusyDelays = sshBusyDelays ?? DefaultSshBusyDelays;
        _accountExecutor = accountExecutor ?? (executor is not null
            ? (_, args, timeout, onOut, onErr, ct) => executor(args, timeout, onOut, onErr, ct)
            : ExecuteSubprocessAsync);
        _delayAsync = delayAsync ?? ((d, ct) => Task.Delay(d, ct));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _configuredExePathProvider = configuredExePathProvider;
    }

    public bool IsColabAvailable(out string? resolvedPath)
    {
        if (!string.IsNullOrEmpty(_cachedExecutablePath) && File.Exists(_cachedExecutablePath))
        {
            resolvedPath = _cachedExecutablePath;
            return true;
        }

        // 1. Explicit user-configured colab.exe path
        var configured = _configuredExePathProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            var isColabExe = Path.GetFileName(configured).Equals("colab.exe", StringComparison.OrdinalIgnoreCase);
            return CacheResolved(configured, injectAdcAuth: isColabExe, out resolvedPath);
        }

        // 2. Direct colab.exe on PATH (avoids cmd.exe batch parsing)
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var pathDirs = pathEnv
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim().Trim('"'))
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .ToArray();

        foreach (var dir in pathDirs)
        {
            var exe = Path.Combine(dir, "colab.exe");
            if (File.Exists(exe))
            {
                return CacheResolved(exe, injectAdcAuth: true, out resolvedPath);
            }
        }

        // 3. Standard user venv colab.exe (directly behind the common .local\bin\colab.cmd shim)
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var venvExe = Path.Combine(userProfile, ".venvs", "colab", "Scripts", "colab.exe");
        if (File.Exists(venvExe))
        {
            return CacheResolved(venvExe, injectAdcAuth: true, out resolvedPath);
        }

        // 4. Inspect .cmd/.bat shim on PATH or ~/.local/bin; if it wraps COLAB_EXE, unwrap to direct .exe!
        var shimCandidates = new List<string>();
        foreach (var dir in pathDirs)
        {
            shimCandidates.Add(Path.Combine(dir, "colab.cmd"));
            shimCandidates.Add(Path.Combine(dir, "colab.bat"));
        }
        shimCandidates.Add(Path.Combine(userProfile, ".local", "bin", "colab.cmd"));

        foreach (var shim in shimCandidates)
        {
            if (!File.Exists(shim))
            {
                continue;
            }

            if (TryUnwrapBatchShim(shim, out var targetExe, out var wantsAdc))
            {
                return CacheResolved(targetExe, injectAdcAuth: wantsAdc, out resolvedPath);
            }

            return CacheResolved(shim, injectAdcAuth: false, out resolvedPath);
        }

        resolvedPath = null;
        return false;
    }

    public Task<ColabResult> RunAsync(
        IReadOnlyList<string> args,
        TimeSpan timeout,
        Action<string>? onStdoutLine = null,
        Action<string>? onStderrLine = null,
        CancellationToken cancellationToken = default)
    {
        return _accountExecutor(ActiveAccount, args, timeout, onStdoutLine, onStderrLine, cancellationToken);
    }

    public Task<ColabResult> RunForAccountAsync(
        ColabAccountContext account,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        Action<string>? onStdoutLine = null,
        Action<string>? onStderrLine = null,
        CancellationToken cancellationToken = default)
    {
        return _accountExecutor(account ?? ColabAccountContext.Default, args, timeout, onStdoutLine, onStderrLine, cancellationToken);
    }

    public async Task<ColabResult> RunSshAsync(
        string sessionName,
        IReadOnlyList<string> remoteArgs,
        TimeSpan timeout,
        Action<string>? onStdoutLine = null,
        Action<string>? onStderrLine = null,
        CancellationToken cancellationToken = default,
        bool allowRateLimitRetry = true)
    {
        var fullArgs = new List<string>(remoteArgs.Count + 4)
        {
            "ssh", "-s", sessionName, "--"
        };
        fullArgs.AddRange(remoteArgs);

        var targetAccount = ActiveAccount;
        await _sshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int retryCount = 0;
            int rateLimitRetries = 0;
            int busyRetries = 0;
            while (true)
            {
                var elapsedSinceLast = _utcNow() - _lastSshCompletedUtc;
                if (_lastSshCompletedUtc != DateTimeOffset.MinValue && elapsedSinceLast < _sshMinInterval)
                {
                    var cooldown = _sshMinInterval - elapsedSinceLast;
                    if (cooldown > TimeSpan.Zero)
                    {
                        await _delayAsync(cooldown, cancellationToken).ConfigureAwait(false);
                    }
                }

                ColabResult result;
                try
                {
                    result = await _accountExecutor(targetAccount, fullArgs, timeout, onStdoutLine, onStderrLine, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _lastSshCompletedUtc = _utcNow();
                }

                if (result.IsSuccess)
                {
                    return result with { RetryCount = retryCount };
                }

                // SSH-slot-busy rejections mean the remote command never started, so
                // retrying is safe even for non-idempotent commands.
                if (IsSshSlotBusy(result) && busyRetries < _sshBusyDelays.Length)
                {
                    var busyBackoff = _sshBusyDelays[busyRetries];
                    busyRetries++;
                    retryCount++;
                    onStderrLine?.Invoke($"[ssh busy retry {busyRetries}/{_sshBusyDelays.Length}] waiting {busyBackoff.TotalSeconds:0.#}s...");
                    await _delayAsync(busyBackoff, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (allowRateLimitRetry && IsRateLimited(result) && rateLimitRetries < _retryDelays.Length)
                {
                    var backoff = _retryDelays[rateLimitRetries];
                    rateLimitRetries++;
                    retryCount++;
                    onStderrLine?.Invoke($"[429 retry {rateLimitRetries}/{_retryDelays.Length}] waiting {backoff.TotalSeconds:0.#}s...");
                    await _delayAsync(backoff, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return result with { RetryCount = retryCount };
            }
        }
        finally
        {
            _sshGate.Release();
        }
    }

    /// <summary>
    /// Detects genuine transport-level HTTP 429 / RESOURCE_EXHAUSTED responses without
    /// misclassifying arbitrary "429" numbers printed by remote scripts (A-12).
    /// </summary>
    public static bool IsRateLimited(ColabResult result)
    {
        if (result.IsSuccess || result.TimedOut)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(result.StdErr) && StrictRateLimitRegex.IsMatch(result.StdErr))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(result.StdOut) && StrictRateLimitRegex.IsMatch(result.StdOut))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Detects the "already-active SSH session / session limit" rejection google-colab-cli
    /// emits when the WebSocket upgrade is refused. The remote command never started, so
    /// retrying is safe even for non-idempotent commands.
    /// </summary>
    public static bool IsSshSlotBusy(ColabResult result)
    {
        if (result.IsSuccess || result.TimedOut)
        {
            return false;
        }

        static bool HasMarker(string text) =>
            text.Contains("Already-active SSH session", StringComparison.OrdinalIgnoreCase)
            || text.Contains("SSH session limit reached", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(result.StdErr) && HasMarker(result.StdErr))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(result.StdOut) && HasMarker(result.StdOut))
        {
            return true;
        }

        return false;
    }

    private bool CacheResolved(string path, bool injectAdcAuth, out string? resolvedPath)
    {
        _cachedExecutablePath = path;
        _cachedInjectAdcAuth = injectAdcAuth;
        _cachedUsesBatchShim = IsBatchFile(path);
        resolvedPath = path;
        return true;
    }

    private static bool IsBatchFile(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    public static bool TryUnwrapBatchShim(string shimPath, out string targetExe, out bool wantsAdc)
    {
        targetExe = string.Empty;
        wantsAdc = false;

        try
        {
            var content = File.ReadAllText(shimPath);
            var match = CmdColabExeRegex.Match(content);
            if (!match.Success)
            {
                return false;
            }

            var candidate = match.Groups[1].Value.Trim();
            if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
            {
                targetExe = candidate;
                wantsAdc = content.Contains("--auth=adc", StringComparison.OrdinalIgnoreCase);
                return true;
            }
        }
        catch
        {
            // Fall back to executing shim directly.
        }

        return false;
    }

    /// <summary>
    /// Builds the effective colab CLI argument list for <paramref name="account"/>, injecting
    /// <c>--auth=adc</c> and <c>--config &lt;path&gt;</c> before subcommands when not already specified.
    /// </summary>
    public static IReadOnlyList<string> BuildEffectiveArgs(
        ColabAccountContext account,
        IReadOnlyList<string> args,
        bool injectAdcFallback)
    {
        var list = new List<string>(args.Count + 3);
        bool hasAuth = args.Any(a => a.StartsWith("--auth", StringComparison.OrdinalIgnoreCase));
        bool hasConfig = args.Any(a =>
            a.Equals("--config", StringComparison.OrdinalIgnoreCase)
            || a.StartsWith("--config=", StringComparison.OrdinalIgnoreCase));

        bool needAdc = !string.IsNullOrWhiteSpace(account.AdcCredentialsPath) || injectAdcFallback;
        if (needAdc && !hasAuth)
        {
            list.Add("--auth=adc");
        }

        if (!string.IsNullOrWhiteSpace(account.ColabConfigPath) && !hasConfig)
        {
            list.Add("--config");
            list.Add(account.ColabConfigPath);
        }

        list.AddRange(args);
        return list;
    }

    private async Task<ColabResult> ExecuteSubprocessAsync(
        ColabAccountContext account,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        Action<string>? onStdoutLine,
        Action<string>? onStderrLine,
        CancellationToken cancellationToken)
    {
        if (!IsColabAvailable(out var exePath) || string.IsNullOrEmpty(exePath))
        {
            return new ColabResult(-1, string.Empty, "未找到 google-colab-cli（colab.exe / colab.cmd），请确认已正确安装。");
        }

        if (!string.IsNullOrWhiteSpace(account.AdcCredentialsPath) && !File.Exists(account.AdcCredentialsPath))
        {
            return new ColabResult(
                -1,
                string.Empty,
                $"账号「{account.DisplayName}」的 ADC 凭据文件不存在：{account.AdcCredentialsPath}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // Ensure Python / colab CLI outputs UTF-8 without buffering delays.
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        // Isolate ADC credentials per-process so concurrent accounts never collide on OS keyring or env.
        if (!string.IsNullOrWhiteSpace(account.AdcCredentialsPath))
        {
            psi.Environment["GOOGLE_APPLICATION_CREDENTIALS"] = account.AdcCredentialsPath;
        }
        else
        {
            psi.Environment.Remove("GOOGLE_APPLICATION_CREDENTIALS");
        }

        // google-colab-cli spawns `ssh` from PATH for `colab ssh` and passes a Windows-quoted
        // -o ProxyCommand=... built with list2cmdline. An MSYS ssh (Git Bash) runs that via
        // /bin/sh, eats the backslashes and fails AFTER billing starts. Force Windows OpenSSH
        // to win the child's PATH lookup (covers both colab.exe and the colab.cmd shim).
        if (WindowsOpenSshDirectory is { } openSshDir)
        {
            psi.Environment["PATH"] = BuildChildPath(
                psi.Environment["PATH"] ?? Environment.GetEnvironmentVariable("PATH"), openSshDir);
        }

        foreach (var arg in BuildEffectiveArgs(account, args, _cachedInjectAdcAuth))
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        var stdoutBuffer = new BoundedOutputBuffer(MaxRetainedCharsPerStream);
        var stderrBuffer = new BoundedOutputBuffer(MaxRetainedCharsPerStream);

        try
        {
            if (!process.Start())
            {
                return new ColabResult(-1, string.Empty, $"无法启动 CLI 进程：{exePath}");
            }
        }
        catch (Exception ex)
        {
            return new ColabResult(-1, string.Empty, $"启动 CLI 失败: {ex.Message}");
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var stdoutTask = ReadStreamLinesAsync(process.StandardOutput, stdoutBuffer, onStdoutLine, linkedCts.Token);
        var stderrTask = ReadStreamLinesAsync(process.StandardError, stderrBuffer, onStderrLine, linkedCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            return new ColabResult(process.ExitCode, stdoutBuffer.Build(), stderrBuffer.Build(), TimedOut: false);
        }
        catch (OperationCanceledException)
        {
            TryKillTree(process);
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // Ignore stream abort errors after killing subprocess.
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new ColabResult(
                -1,
                stdoutBuffer.Build(),
                $"{stderrBuffer.Build()}\n[TIMEOUT] 命令执行超过 {timeout.TotalSeconds:0} 秒已中止。".Trim(),
                TimedOut: true);
        }
    }

    private static async Task ReadStreamLinesAsync(
        StreamReader reader,
        BoundedOutputBuffer sink,
        Action<string>? onLine,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                sink.AppendLine(line);
                onLine?.Invoke(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on timeout or cancellation.
        }
        catch (IOException)
        {
            // Expected if subprocess terminates abruptly.
        }
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
            // Best-effort process cleanup.
        }
    }

    /// <summary>
    /// Retains head + tail lines up to maxChars so runaway subprocess output cannot exhaust memory (A-12).
    /// </summary>
    public sealed class BoundedOutputBuffer
    {
        private readonly int _headLimit;
        private readonly int _tailLimit;
        private readonly StringBuilder _head = new();
        private readonly Queue<string> _tailLines = new();
        private int _tailChars;
        private bool _headFull;
        private bool _truncated;

        public bool IsTruncated => _truncated;

        public BoundedOutputBuffer(int maxChars)
        {
            _headLimit = Math.Max(64, maxChars / 4);
            _tailLimit = Math.Max(128, maxChars - _headLimit);
        }

        public void AppendLine(string line)
        {
            if (!_headFull && _head.Length + line.Length + 1 <= _headLimit)
            {
                // Always join with LF: parsers treat StdOut as the remote byte stream,
                // where '\n' anchors frame markers (e.g. vm_setup.sh --poll).
                if (_head.Length > 0) _head.Append('\n');
                _head.Append(line);
                return;
            }

            _headFull = true;
            _tailLines.Enqueue(line);
            _tailChars += line.Length + 1;

            while (_tailChars > _tailLimit && _tailLines.Count > 1)
            {
                var dropped = _tailLines.Dequeue();
                _tailChars -= dropped.Length + 1;
                _truncated = true;
            }
        }

        public string Build()
        {
            if (!_headFull)
            {
                return _head.ToString();
            }

            var sb = new StringBuilder(_head.Length + _tailChars + 64);
            sb.Append(_head);
            sb.Append('\n');
            if (_truncated)
            {
                sb.Append("... [OUTPUT TRUNCATED TO BOUND MEMORY] ...").Append('\n');
            }
            bool first = true;
            foreach (var line in _tailLines)
            {
                if (!first) sb.Append('\n');
                sb.Append(line);
                first = false;
            }
            return sb.ToString();
        }
    }
}
