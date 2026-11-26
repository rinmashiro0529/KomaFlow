using MangaGui.Models;

namespace MangaCli;

/// <summary>
/// Minimal hand-rolled argument parser (no extra dependencies). Options accept both
/// <c>--name value</c> and <c>--name=value</c>. Everything after the command token that is not an
/// option is a positional argument.
/// </summary>
internal static class ArgParser
{
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "--data-dir", "--events-file", "--minutes", "--engine", "--gpu", "--inpaint", "--g4-mode",
        "--account", "--output-dir", "--scripts-dir", "--on-orphan", "--on-expiry", "--deploy-retries", "--tail"
    };

    private static readonly HashSet<string> FlagOptions = new(StringComparer.Ordinal)
    {
        "--quiet-logs", "--probe", "--refresh", "--crash", "--no-auto-stop", "--keep-session"
    };

    private static readonly HashSet<string> Commands = new(StringComparer.Ordinal)
    {
        "doctor", "precheck", "settings", "accounts", "status", "run", "stop", "logs"
    };

    public static ParseResult Parse(string[] args)
    {
        var opt = new CliOptions();
        if (args.Length == 0)
        {
            return new ParseResult(opt, "缺少命令。");
        }

        var first = args[0];
        if (first is "--help" or "-h" or "help")
        {
            opt.Command = "help";
            return new ParseResult(opt, null);
        }

        if (first is "--version" or "version")
        {
            opt.Command = "version";
            return new ParseResult(opt, null);
        }

        if (first.StartsWith('-'))
        {
            return new ParseResult(opt, $"无法识别的参数：{first}");
        }

        opt.Command = first;
        if (!Commands.Contains(first))
        {
            return new ParseResult(opt, $"未知命令：{first}");
        }

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            string? inlineValue = null;
            var name = arg;
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var eq = arg.IndexOf('=');
                if (eq > 0)
                {
                    name = arg[..eq];
                    inlineValue = arg[(eq + 1)..];
                }
            }

            if (FlagOptions.Contains(name))
            {
                if (inlineValue is not null)
                {
                    return new ParseResult(opt, $"选项 {name} 不接受参数值。");
                }

                switch (name)
                {
                    case "--quiet-logs": opt.QuietLogs = true; break;
                    case "--probe": opt.Probe = true; break;
                    case "--refresh": opt.Refresh = true; break;
                    case "--crash": opt.Crash = true; break;
                    case "--no-auto-stop": opt.NoAutoStop = true; break;
                    case "--keep-session": opt.KeepSession = true; break;
                }
                continue;
            }

            if (ValueOptions.Contains(name))
            {
                var value = inlineValue;
                if (value is null)
                {
                    if (i + 1 >= args.Length)
                    {
                        return new ParseResult(opt, $"选项 {name} 缺少参数值。");
                    }

                    value = args[++i];
                }

                string? error = name switch
                {
                    "--data-dir" => Set(v => opt.DataDir = v, value),
                    "--events-file" => Set(v => opt.EventsFile = v, value),
                    "--scripts-dir" => Set(v => opt.ScriptsDir = v, value),
                    "--output-dir" => Set(v => opt.OutputDir = v, value),
                    "--minutes" => int.TryParse(value, out var m) ? Set(v => opt.Minutes = int.Parse(v), value) : "--minutes 必须是整数。",
                    "--deploy-retries" => int.TryParse(value, out var r) && r is >= 0 and <= 2 ? Set(v => opt.DeployRetries = int.Parse(v), value) : "--deploy-retries 必须是 0–2 的整数。",
                    "--tail" => int.TryParse(value, out var t) && t > 0 ? Set(v => opt.Tail = int.Parse(v), value) : "--tail 必须是正整数。",
                    "--engine" => value is "mtui" or "mit" ? Set(v => opt.Engine = v, value) : "--engine 只接受 mtui 或 mit。",
                    "--gpu" => GpuTiers.TryParseStrict(value, out var g) ? Set(_ => opt.Gpu = g, value) : "--gpu 只接受 l4、a100、a100-hm 或 g4。",
                    "--inpaint" => value is "lama_large" or "flux2-klein" ? Set(v => opt.Inpaint = v, value) : "--inpaint 只接受 lama_large 或 flux2-klein。",
                    "--g4-mode" => value is "flagship" or "balanced" ? Set(v => opt.G4Mode = v, value) : "--g4-mode 只接受 flagship 或 balanced。",
                    "--account" => !string.IsNullOrWhiteSpace(value) ? Set(v => opt.Account = v.Trim(), value) : "--account 不能为空。",
                    "--on-orphan" => value is "fail" or "adopt" or "stop" ? Set(v => opt.OnOrphan = v, value) : "--on-orphan 只接受 fail、adopt 或 stop。",
                    "--on-expiry" => value is "auto" or "drain" or "stop" ? Set(v => opt.OnExpiry = v, value) : "--on-expiry 只接受 auto、drain 或 stop。",
                    _ => null
                };
                if (error is not null)
                {
                    return new ParseResult(opt, error);
                }
                continue;
            }

            if (arg.StartsWith('-'))
            {
                return new ParseResult(opt, $"无法识别的选项：{arg}");
            }

            opt.Positionals.Add(arg);
        }

        return new ParseResult(opt, null);
    }

    private static string? Set(Action<string> apply, string value)
    {
        apply(value);
        return null;
    }
}
