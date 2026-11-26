using MangaGui.Services;
using Xunit;

namespace MangaGui.Tests;

/// <summary>
/// VmScripts.RunDetachedSetupAsync: detached launch + framed byte-offset polling for vm_setup.sh.
/// Frames are scripted verbatim so chunk boundaries (incl. a line split across polls) are exact.
/// </summary>
public sealed class DetachedSetupTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(10);

    private static FakeColabCli ScriptedCli(Func<IReadOnlyList<string>, int, ColabResult> pollFrame)
    {
        var pollIndex = 0;
        return new FakeColabCli((args, _, _) =>
        {
            var result = args[0] == "ssh" && args.Contains("--detach")
                ? new ColabResult(0, "SETUP_DETACHED\n", string.Empty)
                : args[0] == "ssh" && args.Contains("--poll")
                    ? pollFrame(args, pollIndex++)
                    : new ColabResult(0, "OK", string.Empty);
            return Task.FromResult(result);
        });
    }

    private static ColabResult Frame(long next, string content, string rc = "", int running = 1) =>
        new(0, $"__SIZE={next}\n__NEXT={next}\n__BEGIN\n{content}\n__END\n__RC={rc}\n__RUNNING={running}\n", string.Empty);

    [Fact]
    public async Task HappyPath_ChunkedLinesAndSplitLineAreEmittedOnceInOrder()
    {
        // chunk1 ends mid-line ("partial-li"), chunk2 completes it ("ne split\n").
        var chunk1 = "===== STAGE1 mit environment =====\nSTAGE1_OK\npartial-li";
        var chunk2 = "ne split\n===== STAGE2 model =====\n";
        var chunk3 = "===== SUMMARY =====\nVM_SETUP_COMPLETE\n";

        var cli = ScriptedCli((args, i) => i switch
        {
            0 => Frame(chunk1.Length, chunk1),
            1 => Frame(chunk1.Length + chunk2.Length, chunk2),
            _ => Frame(chunk1.Length + chunk2.Length + chunk3.Length, chunk3, rc: "0", running: 0)
        });

        var lines = new List<string>();
        var res = await VmScripts.RunDetachedSetupAsync(
            cli, "mit", lines.Add, CancellationToken.None, pollInterval: Poll);

        Assert.True(res.IsSuccess, res.CombinedOutput);
        Assert.Equal(0, res.ExitCode);
        Assert.Contains("VM_SETUP_COMPLETE", res.StdOut);
        Assert.DoesNotContain("__BEGIN", res.StdOut);
        Assert.Equal(
            ["===== STAGE1 mit environment =====", "STAGE1_OK", "partial-line split",
             "===== STAGE2 model =====", "===== SUMMARY =====", "VM_SETUP_COMPLETE"],
            lines);
    }

    [Fact]
    public async Task TransientPollFailures_BelowMax_StillSucceeds()
    {
        var chunk = "VM_SETUP_COMPLETE\n";
        var cli = ScriptedCli((_, i) => i < 3
            ? new ColabResult(1, string.Empty, "ssh: connect timeout")
            : Frame(chunk.Length, chunk, rc: "0", running: 0));

        var res = await VmScripts.RunDetachedSetupAsync(
            cli, "mit", null, CancellationToken.None, pollInterval: Poll, maxConsecutivePollFailures: 4);

        Assert.True(res.IsSuccess, res.CombinedOutput);
        Assert.Contains("VM_SETUP_COMPLETE", res.StdOut);
    }

    [Fact]
    public async Task PollFailures_ExceedingMax_FailsWithPollingMessage()
    {
        var cli = ScriptedCli((_, _) => new ColabResult(1, string.Empty, "broken pipe"));

        var res = await VmScripts.RunDetachedSetupAsync(
            cli, "mit", null, CancellationToken.None, pollInterval: Poll, maxConsecutivePollFailures: 2);

        Assert.False(res.IsSuccess);
        Assert.Contains("部署状态轮询连续失败 3 次", res.CombinedOutput);
    }

    [Fact]
    public async Task AlreadyRunningLaunch_IsAcceptedAsAttach()
    {
        var chunk = "VM_SETUP_COMPLETE\n";
        var cli = new FakeColabCli((args, _, _) => Task.FromResult(
            args[0] == "ssh" && args.Contains("--detach")
                ? new ColabResult(0, "SETUP_ALREADY_RUNNING\n", string.Empty)
                : args[0] == "ssh" && args.Contains("--poll")
                    ? Frame(chunk.Length, chunk, rc: "0", running: 0)
                    : new ColabResult(0, "OK", string.Empty)));

        var res = await VmScripts.RunDetachedSetupAsync(
            cli, "mit", null, CancellationToken.None, pollInterval: Poll);

        Assert.True(res.IsSuccess, res.CombinedOutput);
    }

    [Fact]
    public async Task NonZeroRemoteExitCode_IsNotSuccess_OutputPassesThrough()
    {
        var chunk = "STAGE1_FAIL\nsome pip error\n";
        var cli = ScriptedCli((_, _) => Frame(chunk.Length, chunk, rc: "1", running: 0));

        var res = await VmScripts.RunDetachedSetupAsync(
            cli, "mit", null, CancellationToken.None, pollInterval: Poll);

        Assert.False(res.IsSuccess);
        Assert.Equal(1, res.ExitCode);
        Assert.Contains("STAGE1_FAIL", res.StdOut);
        Assert.Contains("some pip error", res.StdOut);
    }

    [Fact]
    public async Task ExitedWithoutExitCode_FailsAfterTwoConfirmingPolls()
    {
        var cli = ScriptedCli((_, i) => i == 0
            ? Frame(10, "partial...\n")          // still running, no rc
            : Frame(10, string.Empty, running: 0)); // not running, still no rc — twice

        var res = await VmScripts.RunDetachedSetupAsync(
            cli, "mit", null, CancellationToken.None, pollInterval: Poll);

        Assert.False(res.IsSuccess);
        Assert.Contains("部署脚本意外退出", res.CombinedOutput);
    }

    [Fact]
    public async Task Cancellation_DuringPolling_ThrowsPromptly()
    {
        var cli = ScriptedCli((_, _) => Frame(0, string.Empty)); // runs forever, empty log
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            VmScripts.RunDetachedSetupAsync(cli, "mit", null, cts.Token, pollInterval: Poll));
    }

    [Fact]
    public async Task CrlfJoinedFrame_StillParses()
    {
        // Defense in depth: even if a capture layer ever re-joins lines with CRLF,
        // the LF-framed poll protocol must still parse.
        var cli = new FakeColabCli((args, _, _) => Task.FromResult(
            args[0] == "ssh" && args.Contains("--detach")
                ? new ColabResult(0, "SETUP_DETACHED\r\n", string.Empty)
                : args[0] == "ssh" && args.Contains("--poll")
                    ? new ColabResult(0,
                        "__SIZE=18\r\n__NEXT=18\r\n__BEGIN\r\nVM_SETUP_COMPLETE\r\n\r\n" +
                        "__END\r\n__RC=0\r\n__RUNNING=0\r\n",
                        string.Empty)
                    : new ColabResult(0, "OK", string.Empty)));

        var res = await VmScripts.RunDetachedSetupAsync(
            cli, "mit", null, CancellationToken.None, pollInterval: Poll);

        Assert.True(res.IsSuccess, res.CombinedOutput);
        Assert.Contains("VM_SETUP_COMPLETE", res.StdOut);
    }

    [Fact]
    public async Task PollChunkCap_LargeReplayIsDrainedAcrossPolls()
    {
        // One >131072-byte chunk exceeds the --poll maxBytes cap; __NEXT must advance
        // in bounded steps until the replay is fully drained.
        var big = string.Concat(Enumerable.Repeat("replayed setup output line\n", 8000));
        var setup = new FakeVmSetup(chunks: [big, "VM_SETUP_COMPLETE\n"], exitCode: 0);
        var cli = new FakeColabCli((args, _, _) =>
            Task.FromResult(setup.Handle(args) ?? new ColabResult(0, "OK", string.Empty)));

        var res = await VmScripts.RunDetachedSetupAsync(
            cli, "mit", null, CancellationToken.None, pollInterval: Poll);

        Assert.True(res.IsSuccess, res.CombinedOutput);
        Assert.Contains("VM_SETUP_COMPLETE", res.StdOut);
        Assert.True(res.StdOut.Length >= big.Length);
    }

    [Fact]
    public async Task LaunchFailure_RetriesThenFails()
    {
        var launches = 0;
        var cli = new FakeColabCli((args, _, _) =>
        {
            if (args[0] == "ssh" && args.Contains("--detach"))
            {
                launches++;
                return Task.FromResult(new ColabResult(1, string.Empty, "ssh refused"));
            }
            return Task.FromResult(new ColabResult(0, "OK", string.Empty));
        });

        var res = await VmScripts.RunDetachedSetupAsync(
            cli, "mit", null, CancellationToken.None, pollInterval: Poll);

        Assert.False(res.IsSuccess);
        Assert.Equal(3, launches);
    }
}
