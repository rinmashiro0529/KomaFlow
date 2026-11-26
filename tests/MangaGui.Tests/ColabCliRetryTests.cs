using System.IO;
using MangaGui.Services;
using Xunit;

namespace MangaGui.Tests;

public sealed class ColabCliRetryTests : IDisposable
{
    private readonly string _tempDir;

    public ColabCliRetryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"manga_gui_cli_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task RunSshAsync_RetriesUpTo3TimesOn429WithExponentialBackoff()
    {
        int attempts = 0;
        var delaysObserved = new List<TimeSpan>();

        var cli = new ColabCli(
            sshMinInterval: TimeSpan.Zero,
            retryDelays: [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)],
            executor: (args, timeout, onOut, onErr, ct) =>
            {
                attempts++;
                if (attempts <= 2)
                {
                    return Task.FromResult(new ColabResult(1, string.Empty, "HTTP 429 Too Many Requests"));
                }
                return Task.FromResult(new ColabResult(0, "LLAMA_OK\nENGINE mit ready\nMIT_OK", string.Empty));
            },
            delayAsync: (delay, ct) =>
            {
                delaysObserved.Add(delay);
                return Task.CompletedTask;
            });

        var result = await cli.RunSshAsync("manga", ["bash", "/content/vm_status.sh"], TimeSpan.FromSeconds(10));

        Assert.True(result.IsSuccess);
        Assert.Equal(3, attempts);
        Assert.Equal(2, result.RetryCount);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delaysObserved);
    }

    [Fact]
    public async Task RunSshAsync_StopsAfterMaxRetriesAndReturnsFailure()
    {
        int attempts = 0;
        var delaysObserved = new List<TimeSpan>();

        var cli = new ColabCli(
            sshMinInterval: TimeSpan.Zero,
            retryDelays: [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)],
            executor: (_, _, _, _, _) =>
            {
                attempts++;
                return Task.FromResult(new ColabResult(1, string.Empty, "429 RESOURCE_EXHAUSTED"));
            },
            delayAsync: (delay, _) =>
            {
                delaysObserved.Add(delay);
                return Task.CompletedTask;
            });

        var result = await cli.RunSshAsync("manga", ["bash", "/content/vm_status.sh"], TimeSpan.FromSeconds(10));

        Assert.False(result.IsSuccess);
        Assert.Equal(4, attempts);
        Assert.Equal(3, result.RetryCount);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)], delaysObserved);
    }

    [Fact]
    public async Task RunSshAsync_DoesNotRetryNonIdempotentCommandOrArbitrary429Text()
    {
        // A-12: allowRateLimitRetry: false MUST NOT retry even on HTTP 429
        int nonIdempotentAttempts = 0;
        var cli1 = new ColabCli(
            sshMinInterval: TimeSpan.Zero,
            executor: (_, _, _, _, _) =>
            {
                nonIdempotentAttempts++;
                return Task.FromResult(new ColabResult(1, string.Empty, "HTTP 429 Too Many Requests"));
            },
            delayAsync: (_, _) => Task.CompletedTask);

        var r1 = await cli1.RunSshAsync("manga", ["bash", "/content/vm_job.sh", "start"], TimeSpan.FromSeconds(10), allowRateLimitRetry: false);
        Assert.False(r1.IsSuccess);
        Assert.Equal(1, nonIdempotentAttempts);

        // A-12: arbitrary "429" inside ordinary error output (e.g. "page 429 failed") MUST NOT trigger retry
        int ordinaryAttempts = 0;
        var cli2 = new ColabCli(
            sshMinInterval: TimeSpan.Zero,
            executor: (_, _, _, _, _) =>
            {
                ordinaryAttempts++;
                return Task.FromResult(new ColabResult(1, "processed 429 images", "exit 1 at page 429"));
            },
            delayAsync: (_, _) => Task.CompletedTask);

        var r2 = await cli2.RunSshAsync("manga", ["bash", "/content/vm_status.sh"], TimeSpan.FromSeconds(10), allowRateLimitRetry: true);
        Assert.False(r2.IsSuccess);
        Assert.Equal(1, ordinaryAttempts);
    }

    [Fact]
    public async Task RunSshAsync_RetriesSshSlotBusyEvenWhenRateLimitRetryDisabled()
    {
        // Real failure: "Already-active SSH session (HTTP 429)" means the remote command
        // never started, so retrying is safe even for non-idempotent calls.
        int attempts = 0;
        var delaysObserved = new List<TimeSpan>();
        var stderrLines = new List<string>();

        var cli = new ColabCli(
            sshMinInterval: TimeSpan.Zero,
            sshBusyDelays: [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2)],
            executor: (_, _, _, _, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    return Task.FromResult(new ColabResult(1, string.Empty,
                        "[colab] Already-active SSH session (HTTP 429): another `colab ssh` is connected to this runtime."));
                }
                return Task.FromResult(new ColabResult(0, "JOB_STARTED", string.Empty));
            },
            delayAsync: (d, _) =>
            {
                delaysObserved.Add(d);
                return Task.CompletedTask;
            });

        var result = await cli.RunSshAsync(
            "manga", ["bash", "/content/vm_job.sh", "start"], TimeSpan.FromSeconds(10),
            onStderrLine: stderrLines.Add, allowRateLimitRetry: false);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, attempts);
        Assert.Equal(1, result.RetryCount);
        Assert.Equal([TimeSpan.FromMilliseconds(1)], delaysObserved);
        Assert.Contains(stderrLines, l => l.Contains("ssh busy retry"));
    }

    [Fact]
    public async Task RunSshAsync_SlotBusyEveryTime_FailsAfterExhaustingBusyRetries()
    {
        int attempts = 0;
        var delaysObserved = new List<TimeSpan>();
        var busyDelays = new[]
        {
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(3), TimeSpan.FromMilliseconds(4),
            TimeSpan.FromMilliseconds(5)
        };

        var cli = new ColabCli(
            sshMinInterval: TimeSpan.Zero,
            sshBusyDelays: busyDelays,
            executor: (_, _, _, _, _) =>
            {
                attempts++;
                return Task.FromResult(new ColabResult(1, string.Empty,
                    "[colab] SSH session limit reached (HTTP 429)"));
            },
            delayAsync: (d, _) =>
            {
                delaysObserved.Add(d);
                return Task.CompletedTask;
            });

        var result = await cli.RunSshAsync("manga", ["echo", "hi"], TimeSpan.FromSeconds(10), allowRateLimitRetry: false);

        Assert.False(result.IsSuccess);
        Assert.Equal(6, attempts); // 1 initial + 5 busy retries
        Assert.Equal(5, result.RetryCount);
        Assert.Equal(busyDelays, delaysObserved);
    }

    [Fact]
    public async Task RunSshAsync_Generic429StillNotRetriedWhenRateLimitRetryDisabled()
    {
        int attempts = 0;
        var cli = new ColabCli(
            sshMinInterval: TimeSpan.Zero,
            executor: (_, _, _, _, _) =>
            {
                attempts++;
                return Task.FromResult(new ColabResult(1, string.Empty, "HTTP 429 Too Many Requests"));
            },
            delayAsync: (_, _) => Task.CompletedTask);

        var result = await cli.RunSshAsync("manga", ["echo", "hi"], TimeSpan.FromSeconds(10), allowRateLimitRetry: false);

        Assert.False(result.IsSuccess);
        Assert.Equal(1, attempts);
        Assert.Equal(0, result.RetryCount);
    }

    [Fact]
    public async Task RunSshAsync_TimedOutResultContainingBusyText_NotRetried()
    {
        int attempts = 0;
        var cli = new ColabCli(
            sshMinInterval: TimeSpan.Zero,
            executor: (_, _, _, _, _) =>
            {
                attempts++;
                return Task.FromResult(new ColabResult(1, string.Empty,
                    "[colab] Already-active SSH session (HTTP 429)", TimedOut: true));
            },
            delayAsync: (_, _) => Task.CompletedTask);

        var result = await cli.RunSshAsync("manga", ["echo", "hi"], TimeSpan.FromSeconds(10), allowRateLimitRetry: false);

        Assert.False(result.IsSuccess);
        Assert.True(result.TimedOut);
        Assert.Equal(1, attempts);
        Assert.Equal(0, result.RetryCount);
    }

    [Fact]
    public async Task RunSshAsync_SerializesConcurrentCallsNeverExceedingOneInFlight()
    {
        int inFlight = 0;
        int maxObservedInFlight = 0;

        var cli = new ColabCli(
            sshMinInterval: TimeSpan.FromMilliseconds(10),
            executor: async (_, _, _, _, ct) =>
            {
                var current = Interlocked.Increment(ref inFlight);
                if (current > maxObservedInFlight)
                {
                    maxObservedInFlight = current;
                }

                await Task.Delay(25, ct);
                Interlocked.Decrement(ref inFlight);
                return new ColabResult(0, "OK", string.Empty);
            });

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => cli.RunSshAsync("manga", ["echo", "hi"], TimeSpan.FromSeconds(10)))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(1, maxObservedInFlight);
    }

    [Fact]
    public void TryUnwrapBatchShim_ResolvesDirectExeAndInjectsAdcAuth()
    {
        // A-12: Unwrap colab.cmd pointing to real colab.exe so cmd.exe is bypassed
        var fakeExe = Path.Combine(_tempDir, "colab.exe");
        File.WriteAllText(fakeExe, "stub");
        var fakeCmd = Path.Combine(_tempDir, "colab.cmd");
        File.WriteAllText(fakeCmd, $"""
            @echo off
            set "COLAB_EXE={fakeExe}"
            "%COLAB_EXE%" --auth=adc %*
            """);

        var unwrapped = ColabCli.TryUnwrapBatchShim(fakeCmd, out var resolvedExe, out var injectAdc);

        Assert.True(unwrapped);
        Assert.Equal(fakeExe, resolvedExe);
        Assert.True(injectAdc);
    }

    [Fact]
    public void LogSanitizer_RedactsTokensAndUserPaths()
    {
        var raw = @"Authorization: Bearer ya29.secret_token_ABC123 failed at C:\Users\Mashiro\Documents\book.zip token=xyz987";
        var redacted = LogSanitizer.Redact(raw);

        Assert.DoesNotContain("ya29.secret_token_ABC123", redacted);
        Assert.DoesNotContain("Mashiro", redacted);
        Assert.DoesNotContain("xyz987", redacted);
        Assert.Contains("<REDACTED>", redacted);
        Assert.Contains(@"C:\Users\<USER>\", redacted);
    }

    [Fact]
    public void BoundedOutputBuffer_CapsRetainedMemory()
    {
        var buf = new ColabCli.BoundedOutputBuffer(maxChars: 256);
        for (int i = 0; i < 40; i++)
        {
            buf.AppendLine($"line_{i:D2}_0123456789abcdef");
        }

        var text = buf.Build();
        Assert.True(buf.IsTruncated);
        Assert.Contains("TRUNCATED", text);
        Assert.True(text.Length < 400);
    }

    [Fact]
    public async Task DirectSubprocessExecution_PreservesCjkSpacesAndShellMetacharacters()
    {
        // A-12: Verify direct .exe launch via ArgumentList preserves &, |, %, spaces & CJK untouched
        var pwsh = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(pwsh))
        {
            return;
        }

        var cli = new ColabCli(configuredExePathProvider: () => pwsh);
        Assert.True(cli.IsColabAvailable(out var resolved));
        Assert.Equal(pwsh, resolved);
        Assert.False(cli.UsesBatchShim);

        var scriptPath = Path.Combine(_tempDir, "echo_arg.ps1");
        File.WriteAllText(scriptPath, "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\nWrite-Output $args[0]\n");

        var trickyArg = "[作者 & 测试] 第1卷 100% (Raw)";
        var res = await cli.RunAsync(
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath, trickyArg],
            TimeSpan.FromSeconds(15));

        Assert.True(res.IsSuccess, res.CombinedOutput);
        Assert.Contains(trickyArg, res.StdOut);
    }

    [Fact]
    public void BuildChildPath_NullDir_ReturnsCurrentUnchanged()
    {
        var path = @"C:\Program Files\Git\usr\bin;C:\Tools";
        Assert.Equal(path, ColabCli.BuildChildPath(path, null));
        Assert.Equal(path, ColabCli.BuildChildPath(path, string.Empty));
        Assert.Equal(string.Empty, ColabCli.BuildChildPath(null, null));
    }

    [Fact]
    public void BuildChildPath_DirAlreadyFirst_ReturnsUnchanged()
    {
        var dir = @"C:\WINDOWS\System32\OpenSSH";
        var path = dir + @";C:\Program Files\Git\usr\bin";
        Assert.Equal(path, ColabCli.BuildChildPath(path, dir));

        // Same directory spelled differently (casing, trailing slash) still counts as first.
        var sloppy = @"c:\windows\system32\openssh\;C:\Tools";
        Assert.Equal(sloppy, ColabCli.BuildChildPath(sloppy, dir));
    }

    [Fact]
    public void BuildChildPath_GitSshFirst_PrependsAndRemovesDuplicate()
    {
        var dir = @"C:\WINDOWS\System32\OpenSSH";
        var path = @"C:\Program Files\Git\usr\bin;C:\Tools;C:\WINDOWS\System32\OpenSSH;C:\Other";
        Assert.Equal(
            dir + @";C:\Program Files\Git\usr\bin;C:\Tools;C:\Other",
            ColabCli.BuildChildPath(path, dir));
    }

    [Fact]
    public void BuildChildPath_NullCurrentPath_ReturnsJustDir()
    {
        var dir = @"C:\WINDOWS\System32\OpenSSH";
        Assert.Equal(dir, ColabCli.BuildChildPath(null, dir));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
