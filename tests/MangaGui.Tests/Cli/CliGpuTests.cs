using System.IO;
using MangaCli;
using MangaGui.Models;
using MangaGui.Services;
using Xunit;

namespace MangaGui.Tests.Cli;

public sealed class CliGpuTests
{
    [Fact]
    public async Task SettingsSetGpuInpaintG4Mode_ValidatesAndRoundTrips()
    {
        var fx = new CliFixture();
        var cli = new FakeColabCli((_, _, _) => Task.FromResult(new ColabResult(0, "", "")));

        var sw1 = new StringWriter();
        Assert.Equal(0, await CliApp.RunAsync(["settings", "set", "gpu", "a100-hm", "--data-dir", fx.DataDir], fx.Env(cli, sw1)));
        Ndjson.AssertNdjsonResult(sw1.ToString());

        var sw2 = new StringWriter();
        Assert.Equal(0, await CliApp.RunAsync(["settings", "set", "inpaint", "flux2-klein", "--data-dir", fx.DataDir], fx.Env(cli, sw2)));
        Ndjson.AssertNdjsonResult(sw2.ToString());

        var sw3 = new StringWriter();
        Assert.Equal(0, await CliApp.RunAsync(["settings", "set", "g4-mode", "balanced", "--data-dir", fx.DataDir], fx.Env(cli, sw3)));
        Ndjson.AssertNdjsonResult(sw3.ToString());

        // Invalid values rejected with Usage exit code 2.
        var swBadGpu = new StringWriter();
        Assert.Equal(ExitCodes.Usage, await CliApp.RunAsync(["settings", "set", "gpu", "h100", "--data-dir", fx.DataDir], fx.Env(cli, swBadGpu)));

        var swBadInpaint = new StringWriter();
        Assert.Equal(ExitCodes.Usage, await CliApp.RunAsync(["settings", "set", "inpaint", "sdxl", "--data-dir", fx.DataDir], fx.Env(cli, swBadInpaint)));

        var swGet = new StringWriter();
        Assert.Equal(0, await CliApp.RunAsync(["settings", "get", "--data-dir", fx.DataDir], fx.Env(cli, swGet)));
        var res = Ndjson.AssertNdjsonResult(swGet.ToString());
        var settings = res.GetProperty("settings");
        Assert.Equal("a100-hm", settings.GetProperty("preferred_gpu_tier").GetString());
        Assert.Equal("flux2-klein", settings.GetProperty("high_quality_inpaint_mode").GetString());
        Assert.Equal("balanced", settings.GetProperty("g4_sub_mode").GetString());
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task RunWithGpuFlag_RejectsInvalidGpuBeforeTouchingCloud()
    {
        var fx = new CliFixture();
        var cli = new FakeColabCli((_, _, _) => Task.FromResult(new ColabResult(0, "", "")));
        var sw = new StringWriter();

        var code = await CliApp.RunAsync(
            ["run", "--minutes", "60", "--gpu", "bogus", "--data-dir", fx.DataDir],
            fx.Env(cli, sw));

        Assert.Equal(ExitCodes.Usage, code);
        Assert.Empty(cli.Calls);
        Ndjson.AssertNdjsonResult(sw.ToString());
    }

    [Fact]
    public async Task RunWithGpuAndInpaintFlags_PassesExpectedColabNewArgsAndDetachedSetupArgs()
    {
        var fx = new CliFixture();
        string[]? seenNewArgs = null;
        string[]? seenDetachArgs = null;
        var remoteAlive = false;
        var setup = new FakeVmSetup();

        var cli = new FakeColabCli((args, _, _) =>
        {
            if (args[0] == "sessions")
                return Task.FromResult(new ColabResult(0, remoteAlive ? "manga A100" : "No active sessions found.", ""));
            if (args[0] == "new")
            {
                seenNewArgs = args.ToArray();
                remoteAlive = true;
                return Task.FromResult(new ColabResult(0, "READY", ""));
            }
            if (args[0] == "ssh" && args.Contains("/content/vm_setup.sh"))
            {
                if (args.Contains("--detach")) seenDetachArgs = args.ToArray();
                return Task.FromResult(setup.Handle(args)!);
            }
            if (args[0] == "ssh" && args.Contains("/content/vm_status.sh"))
                return Task.FromResult(new ColabResult(0,
                    "PROTO 2\nLLAMA_OK\nENGINE mtui ready\nGPU_PROFILE a100_40g\nGPU_VRAM_MB 40960\nINPAINT_MODE flux2-klein\nGPU NVIDIA A100-SXM4-40GB, 26111 MiB, 40960 MiB\n", ""));
            if (args[0] == "stop")
            {
                remoteAlive = false;
                return Task.FromResult(new ColabResult(0, "Stopped", ""));
            }
            return Task.FromResult(new ColabResult(0, "OK", ""));
        });

        var sw = new StringWriter();
        var code = await CliApp.RunAsync(
            ["run", "--minutes", "60", "--gpu", "a100", "--inpaint", "flux2-klein", "--data-dir", fx.DataDir],
            fx.Env(cli, sw));

        Assert.Equal(0, code);
        Assert.NotNull(seenNewArgs);
        Assert.Equal(["new", "-s", "manga", "--gpu", "A100"], seenNewArgs!);
        Assert.NotNull(seenDetachArgs);
        Assert.Contains("auto", seenDetachArgs!);
        Assert.Contains("flux2-klein", seenDetachArgs!);

        var res = Ndjson.AssertNdjsonResult(sw.ToString());
        Assert.True(res.GetProperty("ok").GetBoolean());
        var billing = res.GetProperty("billing");
        Assert.Equal(BillingConstants.A10040GCuPerHour, billing.GetProperty("cu_per_hour").GetDouble(), precision: 2);
    }
}
