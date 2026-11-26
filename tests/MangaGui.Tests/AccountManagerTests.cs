using System.IO;
using MangaGui.Models;
using MangaGui.Services;
using Xunit;

namespace MangaGui.Tests;

public sealed class AccountManagerTests
{
    private const string ValidWhoamiA = """
        Auth provider: adc
        Email:         alpha.user@gmail.com
        Scopes:
          - email
          - https://www.googleapis.com/auth/cloud-platform
          - https://www.googleapis.com/auth/colaboratory
          - https://www.googleapis.com/auth/userinfo.email
          - openid
        """;

    private const string ValidWhoamiB = """
        Auth provider: adc
        Email:         beta.user@gmail.com
        Scopes:
          - email
          - https://www.googleapis.com/auth/cloud-platform
          - https://www.googleapis.com/auth/colaboratory
          - https://www.googleapis.com/auth/userinfo.email
          - openid
        """;

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"manga_acct_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteValidAdcJson(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"type":"authorized_user","client_id":"test","refresh_token":"dummy"}""");
        return path;
    }

    [Fact]
    public void ParseUsageOutput_ParsesBalanceRateAndAssignments()
    {
        var now = DateTimeOffset.UtcNow;
        var res = new ColabResult(0,
            "Current balance: 185.48 compute units\nUsage rate: 1.94/hr\nActive assignments: 1\n",
            string.Empty);

        var snap = AccountManager.ParseUsageOutput("acct1", res, now);
        Assert.Equal(AccountQuotaStatus.Ok, snap.Status);
        Assert.Equal(185.48, snap.CurrentBalanceCu);
        Assert.Equal(1.94, snap.UsageRateHourly);
        Assert.Equal(1, snap.ActiveAssignments);
        Assert.Equal(now, snap.UpdatedAt);
    }

    [Fact]
    public void ParseUsageOutput_DetectsAuthExpiredAndRateLimitAndTimeout()
    {
        var now = DateTimeOffset.UtcNow;

        var expired = AccountManager.ParseUsageOutput("acct1",
            new ColabResult(1, "", "google.auth.exceptions.RefreshError: ('invalid_grant: Token has been expired or revoked.')"),
            now);
        Assert.Equal(AccountQuotaStatus.AuthExpired, expired.Status);

        var rateLimited = AccountManager.ParseUsageOutput("acct1",
            new ColabResult(1, "", "429 Too Many Requests"),
            now);
        Assert.Equal(AccountQuotaStatus.Error, rateLimited.Status);
        Assert.Contains("429", rateLimited.ErrorMessage);

        var timedOut = AccountManager.ParseUsageOutput("acct1",
            new ColabResult(-1, "", "timeout", TimedOut: true),
            now);
        Assert.Equal(AccountQuotaStatus.Error, timedOut.Status);
    }

    [Fact]
    public void ParseWhoamiOutput_ValidatesScopesAndMasksEmailWithoutPlaintext()
    {
        var ok = AccountManager.ParseWhoamiOutput(new ColabResult(0, ValidWhoamiA, ""));
        Assert.True(ok.IsValid);
        Assert.Equal("al***@gmail.com", ok.MaskedEmail);
        Assert.False(string.IsNullOrWhiteSpace(ok.EmailHash));
        Assert.DoesNotContain("alpha.user", ok.MaskedEmail!);

        var missingColabScope = AccountManager.ParseWhoamiOutput(new ColabResult(0,
            "Auth provider: adc\nEmail: test@gmail.com\nScopes:\n  - https://www.googleapis.com/auth/userinfo.email\n",
            ""));
        Assert.False(missingColabScope.IsValid);
        Assert.Contains("colaboratory", missingColabScope.ErrorMessage);
    }

    [Fact]
    public void ColabCli_BuildEffectiveArgs_InjectsAdcAndConfigOnlyWhenNeeded()
    {
        var acct = new ColabAccountContext("acct2", "账号 2", @"C:\data\acct2.json", @"C:\data\acct2_sessions.json");
        var args = ColabCli.BuildEffectiveArgs(acct, ["usage"], injectAdcFallback: false);
        Assert.Equal(["--auth=adc", "--config", @"C:\data\acct2_sessions.json", "usage"], args);

        var defaultArgs = ColabCli.BuildEffectiveArgs(ColabAccountContext.Default, ["usage"], injectAdcFallback: true);
        Assert.Equal(["--auth=adc", "usage"], defaultArgs);

        var alreadyHas = ColabCli.BuildEffectiveArgs(acct, ["--auth=adc", "--config", "x.json", "sessions"], injectAdcFallback: true);
        Assert.Equal(["--auth=adc", "--config", "x.json", "sessions"], alreadyHas);
    }

    [Fact]
    public void AutoDiscovery_ImportsValidAdcFilesAndIgnoresSessionsAndInvalidJson()
    {
        var dir = CreateTempDir();
        try
        {
            var acctDir = Path.Combine(dir, "accounts");
            WriteValidAdcJson(Path.Combine(acctDir, "acct1.json"));
            WriteValidAdcJson(Path.Combine(acctDir, "acct2.json"));
            File.WriteAllText(Path.Combine(acctDir, "acct1_sessions.json"), "{}");
            File.WriteAllText(Path.Combine(acctDir, "broken.json"), "not json");

            var store = new SessionStore(dir);
            var cli = new FakeColabCli((_, _, _) => Task.FromResult(new ColabResult(0, "", "")));
            var mgr = new AccountManager(cli, store);

            var accounts = mgr.GetAccounts();
            Assert.Equal(2, accounts.Count);
            Assert.Equal("acct1", accounts[0].Id);
            Assert.Equal("acct2", accounts[1].Id);
            Assert.EndsWith("acct1_sessions.json", accounts[0].ColabConfigPath, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("acct2_sessions.json", accounts[1].ColabConfigPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task AutoSelection_PrefersIdleHighestBalanceAndSkipsExpiredAccounts()
    {
        var dir = CreateTempDir();
        try
        {
            var acctDir = Path.Combine(dir, "accounts");
            WriteValidAdcJson(Path.Combine(acctDir, "acct1.json"));
            WriteValidAdcJson(Path.Combine(acctDir, "acct2.json"));
            WriteValidAdcJson(Path.Combine(acctDir, "acct3.json"));

            var cli = new FakeColabCli((acct, args, _, _) =>
            {
                if (args[0] == "usage")
                {
                    return Task.FromResult(acct.AccountId switch
                    {
                        // Highest balance, but already has an active assignment (e.g., web notebook)
                        "acct1" => new ColabResult(0, "Current balance: 250.00 compute units\nUsage rate: 1.94/hr\nActive assignments: 1\n", ""),
                        // Idle with 180 CU -> should be preferred over busy acct1 to avoid HTTP 412
                        "acct2" => new ColabResult(0, "Current balance: 180.00 compute units\nUsage rate: 0.00/hr\nActive assignments: 0\n", ""),
                        // Expired -> must be filtered out
                        _ => new ColabResult(1, "", "invalid_grant: Token has been expired or revoked")
                    });
                }

                return Task.FromResult(new ColabResult(0, ValidWhoamiA, ""));
            });

            var mgr = new AccountManager(cli, new SessionStore(dir));
            await mgr.RefreshAllQuotasAsync(force: true);

            var plan = mgr.ResolveCandidatesForStart();
            Assert.True(plan.AllowFailover);
            Assert.Equal(2, plan.Candidates.Count);
            Assert.Equal("acct2", plan.Candidates[0].Id);
            Assert.Equal("acct1", plan.Candidates[1].Id);

            // Manual mode disables failover
            mgr.SetSelectionPolicy(AccountSelectionMode.Manual, "acct1");
            var manualPlan = mgr.ResolveCandidatesForStart();
            Assert.False(manualPlan.AllowFailover);
            Assert.Single(manualPlan.Candidates);
            Assert.Equal("acct1", manualPlan.Candidates[0].Id);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task ImportAccount_DeduplicatesByEmailHashAndBlocksRemovingActiveSessionAccount()
    {
        var dir = CreateTempDir();
        try
        {
            var srcA = WriteValidAdcJson(Path.Combine(dir, "src_a.json"));
            var srcA2 = WriteValidAdcJson(Path.Combine(dir, "src_a_renewed.json"));
            var srcB = WriteValidAdcJson(Path.Combine(dir, "src_b.json"));

            var cli = new FakeColabCli((acct, args, _, _) =>
            {
                if (args[0] == "whoami")
                {
                    var isB = acct.AdcCredentialsPath?.Contains("src_b", StringComparison.OrdinalIgnoreCase) == true;
                    return Task.FromResult(new ColabResult(0, isB ? ValidWhoamiB : ValidWhoamiA, ""));
                }

                return Task.FromResult(new ColabResult(0,
                    "Current balance: 120.00 compute units\nUsage rate: 0.00/hr\nActive assignments: 0\n", ""));
            });

            var mgr = new AccountManager(cli, new SessionStore(dir));
            var r1 = await mgr.ImportAccountAsync(srcA, "主账号");
            Assert.True(r1.Success);
            Assert.False(r1.UpdatedExisting);

            // Importing another file for the same Google email updates existing profile instead of duplicating.
            var r2 = await mgr.ImportAccountAsync(srcA2, "主账号-新");
            Assert.True(r2.Success);
            Assert.True(r2.UpdatedExisting);
            Assert.Equal(r1.Profile!.Id, r2.Profile!.Id);
            Assert.Equal("主账号-新", mgr.GetAccountById(r1.Profile.Id)!.Name);

            var r3 = await mgr.ImportAccountAsync(srcB, "副账号");
            Assert.True(r3.Success);
            Assert.False(r3.UpdatedExisting);

            // Cannot disable or remove the account owning an active session.
            var (disOk, disErr) = mgr.SetAccountEnabled(r3.Profile!.Id, false, activeSessionAccountId: r3.Profile.Id);
            Assert.False(disOk);
            Assert.Contains("正在运行", disErr);

            var (remOk, remErr) = mgr.RemoveAccount(r3.Profile.Id, deleteFiles: false, activeSessionAccountId: r3.Profile.Id);
            Assert.False(remOk);
            Assert.Contains("正在运行", remErr);

            // Manual stop hint for explicit account includes credentials and MangaCli command.
            var hint = mgr.BuildManualStopCommandHint(r3.Profile.Id);
            Assert.Contains("MangaCli stop --account", hint);
            Assert.Contains("GOOGLE_APPLICATION_CREDENTIALS", hint);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
