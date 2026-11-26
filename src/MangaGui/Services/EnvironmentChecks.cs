using System.IO;

namespace MangaGui.Services;

/// <summary>
/// Local prerequisites that <c>colab ssh</c> needs but the CLI only reports once a runtime is already
/// billing: an <c>ssh</c> client on PATH and an Ed25519 or ECDSA public key in <c>~/.ssh</c>
/// (RSA keys are rejected by the Colab backend).
/// </summary>
public static class EnvironmentChecks
{
    private static readonly string[] AcceptedPublicKeys = ["id_ed25519.pub", "id_ecdsa.pub"];

    public static IReadOnlyList<string> GetSshIssues() =>
        GetSshIssues(
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ColabCli.WindowsOpenSshDirectory);

    public static IReadOnlyList<string> GetSshIssues(string? pathVariable, string userProfileDirectory) =>
        GetSshIssues(pathVariable, userProfileDirectory, windowsOpenSshDir: null);

    public static IReadOnlyList<string> GetSshIssues(
        string? pathVariable, string userProfileDirectory, string? windowsOpenSshDir)
    {
        var issues = new List<string>();

        // The child PATH is rewritten to put Windows OpenSSH first (ColabCli.BuildChildPath),
        // so an MSYS/Git ssh ahead of it on our own PATH is not a problem. It only matters
        // when Windows OpenSSH is absent: then the colab child resolves the same ssh we do,
        // and an MSYS ssh.exe first on PATH breaks `colab ssh` (ProxyCommand quoting).
        if (windowsOpenSshDir == null)
        {
            var firstSsh = (pathVariable ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(dir => dir.Trim().Trim('"'))
                .Where(dir => dir.Length > 0)
                .Select(dir => Path.Combine(dir, "ssh.exe"))
                .FirstOrDefault(File.Exists);
            if (firstSsh == null)
            {
                issues.Add("未在 PATH 中找到 ssh.exe（colab ssh 需要 OpenSSH 客户端，可在「设置 → 可选功能」中安装）");
            }
            else if (IsMsysBinDirectory(Path.GetDirectoryName(firstSsh) ?? string.Empty))
            {
                issues.Add($"PATH 中优先的 ssh 来自 Git/MSYS（{firstSsh}），colab ssh 需要 Windows OpenSSH，请在「设置 → 可选功能」中安装 OpenSSH 客户端");
            }
        }

        var sshDir = Path.Combine(userProfileDirectory, ".ssh");
        if (!AcceptedPublicKeys.Any(name => File.Exists(Path.Combine(sshDir, name))))
        {
            issues.Add("~/.ssh 下没有 id_ed25519.pub 或 id_ecdsa.pub（Colab 不接受 RSA 密钥，请先运行 ssh-keygen -t ed25519）");
        }

        return issues;
    }

    private static bool IsMsysBinDirectory(string dir)
    {
        var trimmed = dir.TrimEnd('\\', '/');
        return trimmed.EndsWith("\\usr\\bin", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("/usr/bin", StringComparison.OrdinalIgnoreCase);
    }
}
