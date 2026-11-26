using System.IO;
using System.IO.Compression;
using System.Text.Json;
using MangaCli;
using MangaGui.Services;
using Xunit;

namespace MangaGui.Tests.Cli;

/// <summary>
/// Temp directories + script/asset fixture layout identical to TaskQueueTests: a data dir, and a
/// scripts dir whose <c>assets\</c> subdir satisfies both RequiredExistingScripts and
/// BundledGuiAssets.
/// </summary>
/// <remarks>
/// Temp dirs are intentionally kept after the test run (artifacts for inspection), so no
/// IDisposable cleanup here — each test creates its own instance for isolation.
/// </remarks>
public sealed class CliFixture
{
    /// <param name="includeMangaJson">
    /// G1: false simulates "no usable scripts dir" — manga.json is the only required existing
    /// file, so omitting it leaves the dir unqualified for mit while bundled assets stay intact.
    /// </param>
    public CliFixture(bool includeMangaJson = true)
    {
        TempRoot = Path.Combine(Path.GetTempPath(), $"manga_cli_test_{Guid.NewGuid():N}");
        DataDir = Path.Combine(TempRoot, "appdata");
        ScriptsDir = Path.Combine(TempRoot, "scripts");
        var assetsDir = Path.Combine(ScriptsDir, "assets");
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(assetsDir);

        if (includeMangaJson)
        {
            foreach (var f in VmScripts.RequiredExistingScripts)
            {
                File.WriteAllText(Path.Combine(ScriptsDir, f), "#!/bin/bash\n");
            }
        }

        foreach (var f in VmScripts.BundledGuiAssets)
        {
            File.WriteAllText(Path.Combine(assetsDir, f), "#!/bin/bash\n");
        }
    }

    public string TempRoot { get; }
    public string DataDir { get; }
    public string ScriptsDir { get; }

    public string NewMutexName() => $@"Local\MangaCli_Test_{Guid.NewGuid():N}";

    /// <summary>Creates a valid one-page cbz/zip at <paramref name="name"/> under TempRoot.</summary>
    public string CreateBookZip(string name = "book01.zip", int pages = 2)
    {
        var path = Path.Combine(TempRoot, name);
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        for (var i = 1; i <= pages; i++)
        {
            TestImageFixtures.WriteBytesEntry(z, $"{i:D4}.jpg", TestImageFixtures.CreateValidJpeg((byte)i));
        }

        return path;
    }

    public CliEnvironment Env(
        FakeColabCli? cli,
        StringWriter stdout,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? stopVerifyTimeout = null,
        TimeSpan[]? autoStopRetryDelays = null,
        TimeSpan? busyExpiryGrace = null,
        TimeSpan? interruptCleanupBound = null)
    {
        return new CliEnvironment
        {
            Stdout = stdout,
            Stderr = new StringWriter(),
            ColabFactory = cli is null ? null : () => cli,
            MutexName = NewMutexName(),
            UtcNow = utcNow,
            AppBaseDirectory = ScriptsDir,
            TaskPollInterval = TimeSpan.FromMilliseconds(15),
            BusyExpiryGrace = busyExpiryGrace,
            IdleExpiryGrace = TimeSpan.FromMilliseconds(50),
            AutoStopRetryDelays = autoStopRetryDelays,
            BillingEventInterval = TimeSpan.FromMilliseconds(200),
            StopVerifyTimeout = stopVerifyTimeout ?? TimeSpan.FromSeconds(15),
            InterruptCleanupBound = interruptCleanupBound ?? TimeSpan.FromSeconds(90)
        };
    }

    /// <summary>
    /// Full-pipeline fake: sessions → new/upload → detached vm_setup → vm_job → polls → pack →
    /// download (copies a prebuilt verified zip) → clean → stop. <paramref name="failSetup"/>
    /// makes vm_setup exit 1 (deploy failure). <paramref name="onJobStart"/> fires once when
    /// vm_job.sh is invoked (used by the expiry test to jump the injected clock).
    /// </summary>
    public FakeColabCli FullPipelineCli(
        out Func<string?> outputPath,
        bool failSetup = false,
        bool sessionsAlwaysAlive = false,
        Action? onJobStart = null,
        bool startAlive = false,
        bool firstStatusNotReady = false,
        bool pollFails = false)
    {
        var remoteAlive = startAlive;
        var pollStep = 0;
        var statusCalls = 0;
        var prebuilt = Path.Combine(TempRoot, "remote_pack_fixture.zip");
        var (outBytes, outSha) = TestImageFixtures.CreateValidTranslatedZip(prebuilt, pageCount: 2);
        // Deployment is declared failed when vm_setup output lacks the VM_SETUP_COMPLETE marker.
        var setup = failSetup
            ? new FakeVmSetup(chunks: ["ERROR: simulated setup failure\n"], exitCode: 1)
            : new FakeVmSetup();
        string? downloadedTo = null;
        outputPath = () => downloadedTo;

        return new FakeColabCli((args, onOut, _) =>
        {
            var cmd = args[0];
            if (cmd == "sessions")
            {
                return Task.FromResult(new ColabResult(0,
                    remoteAlive || sessionsAlwaysAlive ? "manga L4" : "No active sessions found.",
                    string.Empty));
            }

            if (cmd == "new")
            {
                remoteAlive = true;
                return Task.FromResult(new ColabResult(0, "OK", string.Empty));
            }

            if (cmd == "upload")
            {
                return Task.FromResult(new ColabResult(0, "OK", string.Empty));
            }

            if (cmd == "ssh" && args.Contains("/content/vm_setup.sh"))
            {
                return Task.FromResult(setup.Handle(args)!);
            }

            if (cmd == "ssh" && args.Contains("/content/vm_job.sh"))
            {
                onJobStart?.Invoke();
                return Task.FromResult(new ColabResult(0, "PROTO 2\nIMAGES 2\nTRANSLATE_PID 999", string.Empty));
            }

            if (cmd == "ssh" && args.Contains("/content/vm_status.sh"))
            {
                if (args[^1] == "/content/vm_status.sh")
                {
                    statusCalls++;
                    if (firstStatusNotReady && statusCalls == 1)
                    {
                        // Runtime alive but translation services not up → RecoveredNeedsDeploy.
                        return Task.FromResult(new ColabResult(0, "PROTO 2\nGPU NVIDIA L4", string.Empty));
                    }

                    return Task.FromResult(new ColabResult(0,
                        "PROTO 2\nLLAMA_OK\nENGINE mtui ready\nMIT_OK\nGPU NVIDIA L4", string.Empty));
                }

                if (pollFails)
                {
                    return Task.FromResult(new ColabResult(1, string.Empty, "SSH broken pipe"));
                }

                pollStep++;
                return pollStep < 2
                    ? Task.FromResult(new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mtui ready\nDONE_COUNT 1\nJOB_ALIVE 1\nJOB_EXIT_CODE RUNNING", string.Empty))
                    : Task.FromResult(new ColabResult(0, "PROTO 2\nLLAMA_OK\nENGINE mtui ready\nDONE_COUNT 2\nJOB_ALIVE 0\nJOB_EXIT_CODE 0", string.Empty));
            }

            if (cmd == "ssh" && args.Contains("pack"))
            {
                return Task.FromResult(new ColabResult(0, $"PACK_OK {outBytes} {outSha} 2", string.Empty));
            }

            if (cmd == "download")
            {
                downloadedTo = args[^1];
                File.Copy(prebuilt, args[^1], overwrite: true);
                return Task.FromResult(new ColabResult(0, "DOWNLOAD_OK", string.Empty));
            }

            if (cmd == "ssh" && args.Contains("clean"))
            {
                return Task.FromResult(new ColabResult(0, "CLEAN_OK", string.Empty));
            }

            if (cmd == "stop")
            {
                remoteAlive = false;
                return Task.FromResult(new ColabResult(0, "Session terminated", string.Empty));
            }

            return Task.FromResult(new ColabResult(0, "OK", string.Empty));
        });
    }
}

public static class Ndjson
{
    /// <summary>
    /// Every stdout line must parse as JSON and the last line must be type=result; returns the
    /// parsed result object. Call this in every CLI test.
    /// </summary>
    public static JsonElement AssertNdjsonResult(string stdout)
    {
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        JsonElement last = default;
        foreach (var line in lines)
        {
            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException ex)
            {
                Assert.Fail($"stdout line is not valid JSON: {line}\n{ex.Message}");
            }

            Assert.True(doc!.RootElement.TryGetProperty("type", out _),
                $"line missing \"type\": {line}");
            last = doc.RootElement.Clone();
            doc.Dispose();
        }

        Assert.Equal("result", last.GetProperty("type").GetString());
        return last;
    }

    /// <summary>All events of a given type in the stream.</summary>
    public static List<JsonElement> Events(string stdout, string type)
    {
        var found = new List<JsonElement>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == type)
            {
                found.Add(doc.RootElement.Clone());
            }
        }

        return found;
    }

    public static bool HasCallStartingWith(FakeColabCli cli, string command) =>
        cli.Calls.Any(c => c.Length > 0 && c[0] == command);
}
