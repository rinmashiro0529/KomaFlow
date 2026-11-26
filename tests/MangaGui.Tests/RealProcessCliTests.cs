using System.IO;
using MangaGui.Models;
using MangaGui.Services;
using Xunit;

namespace MangaGui.Tests;

/// <summary>
/// Drives the REAL <see cref="ColabCli"/> (process spawn + BoundedOutputBuffer) against a
/// fixture colab.cmd so framing/encoding regressions cannot hide behind fake IColabCli
/// results. The fixture prints LF-only output, matching the real remote protocol.
/// </summary>
public sealed class RealProcessCliTests : IDisposable
{
    private readonly string _dir;
    private readonly string _cmdPath;
    private readonly ColabCli _cli;

    public RealProcessCliTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "manga_realcli_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _cmdPath = Path.Combine(_dir, "colab.cmd");

        File.WriteAllText(_cmdPath, string.Join("\r\n",
            "@echo off",
            "setlocal EnableDelayedExpansion",
            "set \"A=%*\"",
            "if not \"!A:--detach=!\"==\"!A!\" (",
            "  echo SETUP_DETACHED",
            "  exit /b 0",
            ")",
            "if not \"!A:--poll=!\"==\"!A!\" (",
            "  type \"%~dp0poll_frame.txt\"",
            "  exit /b 0",
            ")",
            "if not \"!A:sessions=!\"==\"!A!\" (",
            "  type \"%~dp0sessions.txt\"",
            "  exit /b 0",
            ")",
            "exit /b 1") + "\r\n");

        // LF-only on purpose: this is what the remote script emits over ssh.
        File.WriteAllText(Path.Combine(_dir, "poll_frame.txt"),
            "__SIZE=119\n__NEXT=119\n__BEGIN\n" +
            "===== STAGE1 mit environment =====\nSTAGE1_OK\n" +
            "===== SUMMARY =====\nVM_SETUP_COMPLETE\n\n" +
            "__END\n__RC=0\n__RUNNING=0\n");

        _cli = new ColabCli(
            sshMinInterval: TimeSpan.FromMilliseconds(10),
            configuredExePathProvider: () => _cmdPath);
    }

    [Fact]
    public async Task DetachedSetup_ThroughRealProcessPath_Succeeds()
    {
        Assert.True(_cli.IsColabAvailable(out var resolved));
        Assert.Equal(_cmdPath, resolved);
        Assert.True(_cli.UsesBatchShim);

        var lines = new List<string>();
        var res = await VmScripts.RunDetachedSetupAsync(
            _cli, "mit", lines.Add, CancellationToken.None,
            pollInterval: TimeSpan.FromMilliseconds(10));

        Assert.True(res.IsSuccess, res.CombinedOutput);
        Assert.Equal(0, res.ExitCode);
        Assert.Contains("VM_SETUP_COMPLETE", res.StdOut);
        Assert.Contains("STAGE1_OK", lines);
        Assert.Contains("VM_SETUP_COMPLETE", lines);
    }

    [Fact]
    public async Task Sessions_ThroughRealProcessPath_ParsesRealFormats()
    {
        var sessionsPath = Path.Combine(_dir, "sessions.txt");

        File.WriteAllText(sessionsPath,
            "[manga] m-s-use1b2-239w49qwaydwd | Hardware: CPU | Shape: Standard | Variant: DEFAULT\n");
        var present = await _cli.RunAsync(["sessions"], TimeSpan.FromSeconds(30));
        Assert.Equal(
            SessionPresence.Present,
            VmScripts.ParseSessionPresence(present).Presence);

        File.WriteAllText(sessionsPath,
            "[colab] No active sessions found on server.\n");
        var absent = await _cli.RunAsync(["sessions"], TimeSpan.FromSeconds(30));
        Assert.Equal(
            SessionPresence.ExplicitlyAbsent,
            VmScripts.ParseSessionPresence(absent).Presence);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Temp cleanup is best-effort.
        }
    }
}
