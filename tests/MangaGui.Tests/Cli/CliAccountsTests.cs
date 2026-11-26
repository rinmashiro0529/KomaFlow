using System.IO;
using System.Text.Json;
using MangaCli;
using MangaGui.Services;
using Xunit;

namespace MangaGui.Tests.Cli;

public sealed class CliAccountsTests
{
    private static void SeedTwoAccounts(CliFixture fx)
    {
        var acctDir = Path.Combine(fx.DataDir, "accounts");
        Directory.CreateDirectory(acctDir);
        File.WriteAllText(Path.Combine(acctDir, "acct1.json"), """{"type":"authorized_user","refresh_token":"r1"}""");
        File.WriteAllText(Path.Combine(acctDir, "acct2.json"), """{"type":"authorized_user","refresh_token":"r2"}""");
    }

    private static JsonElement ParseResultLine(string stdout)
    {
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEmpty(lines);
        using var doc = JsonDocument.Parse(lines[^1]);
        var root = doc.RootElement.Clone();
        Assert.Equal("result", root.GetProperty("type").GetString());
        return root;
    }

    [Fact]
    public async Task AccountsList_Offline_DoesNotTouchCloudAndListsDiscoveredAccounts()
    {
        var fx = new CliFixture();
        SeedTwoAccounts(fx);
        var cli = new FakeColabCli((_, _, _) => Task.FromResult(new ColabResult(0, "", "")));
        var sw = new StringWriter();

        var code = await CliApp.RunAsync(["accounts", "list", "--data-dir", fx.DataDir], fx.Env(cli, sw));
        Assert.Equal(0, code);
        Assert.Empty(cli.Calls);

        var res = ParseResultLine(sw.ToString());
        Assert.Equal(0, res.GetProperty("cloud_calls_blocked").GetInt32());
        Assert.Equal(2, res.GetProperty("accounts").GetArrayLength());
    }

    [Fact]
    public async Task AccountsList_Refresh_PollsBothAccountsAndRecommendsHighestBalance()
    {
        var fx = new CliFixture();
        SeedTwoAccounts(fx);
        var cli = new FakeColabCli((acct, args, _, _) =>
        {
            if (args[0] == "usage")
            {
                var bal = acct.AccountId == "acct2" ? "200.00" : "185.48";
                return Task.FromResult(new ColabResult(0,
                    $"Current balance: {bal} compute units\nUsage rate: 0.00/hr\nActive assignments: 0\n", ""));
            }
            return Task.FromResult(new ColabResult(0, "", ""));
        });
        var sw = new StringWriter();

        var code = await CliApp.RunAsync(["accounts", "list", "--refresh", "--data-dir", fx.DataDir], fx.Env(cli, sw));
        Assert.Equal(0, code);

        var res = ParseResultLine(sw.ToString());
        Assert.Equal("acct2", res.GetProperty("recommended_account_id").GetString());
        Assert.Equal("auto", res.GetProperty("selection_mode").GetString());
    }

    [Fact]
    public async Task SettingsSetAccount_ValidatesAndPersistsSelectionMode()
    {
        var fx = new CliFixture();
        SeedTwoAccounts(fx);
        var cli = new FakeColabCli((_, _, _) => Task.FromResult(new ColabResult(0, "", "")));

        var sw1 = new StringWriter();
        var code1 = await CliApp.RunAsync(["settings", "set", "account", "acct2", "--data-dir", fx.DataDir], fx.Env(cli, sw1));
        Assert.Equal(0, code1);

        var swBad = new StringWriter();
        var codeBad = await CliApp.RunAsync(["settings", "set", "account", "nonexistent", "--data-dir", fx.DataDir], fx.Env(cli, swBad));
        Assert.Equal(2, codeBad);

        var swGet = new StringWriter();
        var codeGet = await CliApp.RunAsync(["settings", "get", "--data-dir", fx.DataDir], fx.Env(cli, swGet));
        Assert.Equal(0, codeGet);
        var resGet = ParseResultLine(swGet.ToString());
        var settings = resGet.GetProperty("settings");
        Assert.Equal("manual", settings.GetProperty("account_selection_mode").GetString());
        Assert.Equal("acct2", settings.GetProperty("selected_account_id").GetString());
    }

    [Fact]
    public async Task StopWithAccountFlag_StopsTargetedAccountDirectly()
    {
        var fx = new CliFixture();
        SeedTwoAccounts(fx);
        bool acct2Alive = true;
        var cli = new FakeColabCli((acct, args, _, _) =>
        {
            if (args[0] == "stop" && acct.AccountId == "acct2")
            {
                acct2Alive = false;
                return Task.FromResult(new ColabResult(0, "Stopped\n", ""));
            }
            if (args[0] == "sessions")
            {
                return Task.FromResult(new ColabResult(0,
                    acct2Alive && acct.AccountId == "acct2" ? "[manga] m-s-222 | Hardware: L4\n" : "[colab] No active sessions found on server.\n", ""));
            }
            if (args[0] == "usage")
            {
                return Task.FromResult(new ColabResult(0,
                    "Current balance: 199.00 compute units\nUsage rate: 0.00/hr\nActive assignments: 0\n", ""));
            }
            return Task.FromResult(new ColabResult(0, "", ""));
        });

        var sw = new StringWriter();
        var code = await CliApp.RunAsync(["stop", "--account", "acct2", "--data-dir", fx.DataDir], fx.Env(cli, sw));
        Assert.Equal(0, code);
        Assert.Contains(cli.AccountCalls, c => c.AccountId == "acct2" && c.Args[0] == "stop");
        Assert.DoesNotContain(cli.AccountCalls, c => c.AccountId == "acct1" && c.Args[0] == "stop");
    }
}
