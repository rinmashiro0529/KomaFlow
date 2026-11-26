using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MangaGui.Models;
using MangaGui.Services;

namespace MangaGui.Tests;

/// <summary>
/// Shared binary test fixtures (minimal valid JPEG/PNG payloads & zip builders).
/// </summary>
public static class TestImageFixtures
{
    public static byte[] CreateValidJpeg(byte markerTag = 1) =>
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, markerTag, 0xFF, 0xD9];

    public static byte[] CreateValidPng(byte markerTag = 1) =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, markerTag];

    public static void WriteBytesEntry(ZipArchive archive, string entryPath, byte[] data)
    {
        var entry = archive.CreateEntry(entryPath, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(data, 0, data.Length);
    }

    public static (long Bytes, string Sha256) CreateValidTranslatedZip(string path, int pageCount)
    {
        if (File.Exists(path)) File.Delete(path);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            for (int i = 1; i <= pageCount; i++)
            {
                WriteBytesEntry(archive, $"{i:D4}.jpg", CreateValidJpeg((byte)i));
            }
        }

        var bytes = new FileInfo(path).Length;
        using var fs = File.OpenRead(path);
        var sha = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        return (bytes, sha);
    }
}

/// <summary>
/// Scriptable fake IColabCli supporting streaming stdout callbacks, custom handlers,
/// shim simulation, retry flag recording, and call tracking without touching real Colab billing.
/// </summary>
public sealed class FakeColabCli : IColabCli
{
    private readonly List<string[]> _calls = [];
    private readonly List<(string AccountId, string[] Args)> _accountCalls = [];
    private readonly List<(string[] RemoteArgs, bool AllowRateLimitRetry)> _sshCalls = [];
    private readonly Func<ColabAccountContext, IReadOnlyList<string>, Action<string>?, CancellationToken, Task<ColabResult>> _accountHandler;
    private ColabAccountContext _activeAccount = ColabAccountContext.Default;

    public bool ColabAvailable { get; set; } = true;
    public bool UsesBatchShim { get; set; }
    public ColabAccountContext ActiveAccount
    {
        get => Volatile.Read(ref _activeAccount);
        set => Volatile.Write(ref _activeAccount, value ?? ColabAccountContext.Default);
    }

    public IReadOnlyList<string[]> Calls => _calls;
    public IReadOnlyList<(string AccountId, string[] Args)> AccountCalls => _accountCalls;
    public IReadOnlyList<(string[] RemoteArgs, bool AllowRateLimitRetry)> SshCalls => _sshCalls;

    public FakeColabCli(Func<IReadOnlyList<string>, Action<string>?, CancellationToken, Task<ColabResult>> handler)
    {
        _accountHandler = (_, args, onOut, ct) => handler(args, onOut, ct);
    }

    public FakeColabCli(Func<ColabAccountContext, IReadOnlyList<string>, Action<string>?, CancellationToken, Task<ColabResult>> accountHandler)
    {
        _accountHandler = accountHandler;
    }

    public bool IsColabAvailable(out string? resolvedPath)
    {
        resolvedPath = ColabAvailable ? (UsesBatchShim ? @"C:\mock\colab.cmd" : @"C:\mock\colab.exe") : null;
        return ColabAvailable;
    }

    public Task<ColabResult> RunAsync(
        IReadOnlyList<string> args,
        TimeSpan timeout,
        Action<string>? onStdoutLine = null,
        Action<string>? onStderrLine = null,
        CancellationToken cancellationToken = default)
    {
        return RunForAccountAsync(ActiveAccount, args, timeout, onStdoutLine, onStderrLine, cancellationToken);
    }

    public async Task<ColabResult> RunForAccountAsync(
        ColabAccountContext account,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        Action<string>? onStdoutLine = null,
        Action<string>? onStderrLine = null,
        CancellationToken cancellationToken = default)
    {
        var arr = args.ToArray();
        lock (_calls)
        {
            _calls.Add(arr);
            _accountCalls.Add((account.AccountId, arr));
        }

        return await _accountHandler(account, args, onStdoutLine, cancellationToken).ConfigureAwait(false);
    }

    public Task<ColabResult> RunSshAsync(
        string sessionName,
        IReadOnlyList<string> remoteArgs,
        TimeSpan timeout,
        Action<string>? onStdoutLine = null,
        Action<string>? onStderrLine = null,
        CancellationToken cancellationToken = default,
        bool allowRateLimitRetry = true)
    {
        lock (_sshCalls)
        {
            _sshCalls.Add((remoteArgs.ToArray(), allowRateLimitRetry));
        }

        var full = new List<string> { "ssh", "-s", sessionName, "--" };
        full.AddRange(remoteArgs);
        return RunAsync(full, timeout, onStdoutLine, onStderrLine, cancellationToken);
    }
}

/// <summary>
/// Simulates the detached vm_setup.sh protocol: --detach starts/attaches to a virtual run and
/// --poll &lt;offset&gt; serves queued log chunks plus the framed __SIZE/__BEGIN..__END/__RC/__RUNNING
/// markers. Each poll releases the next queued chunk; when the queue is empty the run is
/// finished and reports the exit code. <c>neverFinish</c> keeps __RUNNING=1 forever.
/// </summary>
public sealed class FakeVmSetup
{
    private readonly StringBuilder _log = new();
    private readonly Queue<string> _chunks;
    private readonly int _exitCode;
    private readonly bool _neverFinish;
    private bool _started;

    public FakeVmSetup(IEnumerable<string>? chunks = null, int exitCode = 0, bool neverFinish = false)
    {
        _chunks = new Queue<string>(chunks ?? ["===== SUMMARY =====\nVM_SETUP_COMPLETE\n"]);
        _exitCode = exitCode;
        _neverFinish = neverFinish;
    }

    /// <summary>Returns the protocol result when <paramref name="args"/> invoke vm_setup.sh, else null.</summary>
    public ColabResult? Handle(IReadOnlyList<string> args)
    {
        var idx = -1;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "/content/vm_setup.sh")
            {
                idx = i;
                break;
            }
        }

        if (idx < 0)
        {
            return null;
        }

        var mode = idx + 1 < args.Count ? args[idx + 1] : null;
        if (mode == "--detach")
        {
            if (_started)
            {
                return new ColabResult(0, "SETUP_ALREADY_RUNNING\n", string.Empty);
            }

            _started = true;
            return new ColabResult(0, "SETUP_DETACHED\n", string.Empty);
        }

        if (mode == "--poll")
        {
            var offset = idx + 2 < args.Count && long.TryParse(args[idx + 2], out var o) ? o : 0;
            var maxBytes = idx + 3 < args.Count && long.TryParse(args[idx + 3], out var mb) ? mb : 131072;
            if (_chunks.Count > 0)
            {
                _log.Append(_chunks.Dequeue());
            }

            var finished = !_neverFinish && _chunks.Count == 0;
            var size = _log.Length;
            var end = Math.Min(size, offset + maxBytes);
            var content = end > offset ? _log.ToString((int)offset, (int)(end - offset)) : string.Empty;
            return new ColabResult(0,
                $"__SIZE={size}\n__NEXT={end}\n__BEGIN\n{content}\n__END\n" +
                $"__RC={(finished ? _exitCode.ToString() : string.Empty)}\n" +
                $"__RUNNING={(finished ? 0 : 1)}\n",
                string.Empty);
        }

        return new ColabResult(1, string.Empty, "unexpected vm_setup.sh invocation (missing --detach/--poll)");
    }
}
