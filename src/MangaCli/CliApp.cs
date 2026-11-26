namespace MangaCli;

/// <summary>
/// Testable entry point. Parses arguments, creates the <see cref="EventWriter"/>, runs the command
/// and always emits a final <c>result</c> NDJSON line — including usage errors and exceptions.
/// </summary>
public static class CliApp
{
    public static async Task<int> RunAsync(
        string[] args,
        CliEnvironment env,
        CancellationToken interrupt = default)
    {
        var parse = ArgParser.Parse(args);
        using var writer = new EventWriter(env.Stdout, parse.Options.EventsFile, parse.Options.QuietLogs);

        if (!parse.Success)
        {
            var usage = new CommandResult
            {
                Command = string.IsNullOrEmpty(parse.Options.Command) ? "(none)" : parse.Options.Command,
                ExitCode = ExitCodes.Usage,
                Message = parse.Error!,
                Fields = { ["usage"] = CliCommands.UsageText }
            };
            writer.Result(usage);
            return usage.ExitCode;
        }

        var opt = parse.Options;
        if (opt.Command == "help")
        {
            var h = CliCommands.Help();
            writer.Result(h);
            return h.ExitCode;
        }

        if (opt.Command == "version")
        {
            var v = CliCommands.Version();
            writer.Result(v);
            return v.ExitCode;
        }

        await using var c = new CommandContext
        {
            Env = env,
            Options = opt,
            Writer = writer,
            Interrupt = interrupt
        };

        CommandResult result;
        try
        {
            result = opt.Command switch
            {
                "doctor" => await CliCommands.DoctorAsync(c),
                "precheck" => await CliCommands.PrecheckAsync(c),
                "settings" => await CliCommands.SettingsAsync(c),
                "accounts" => await CliCommands.AccountsAsync(c),
                "status" => await CliCommands.StatusAsync(c),
                "stop" => await CliCommands.StopAsync(c),
                "logs" => await CliCommands.LogsAsync(c),
                "run" => await RunCommand.ExecuteAsync(c),
                _ => new CommandResult { Command = opt.Command }
                    .Fail(ExitCodes.Usage, $"未知命令：{opt.Command}")
            };
        }
        catch (Exception ex)
        {
            env.Stderr.WriteLine(ex.ToString());
            result = new CommandResult
            {
                Command = opt.Command,
                ExitCode = ExitCodes.UnexpectedError,
                Message = $"未预期异常：{ex.Message}（堆栈已写 stderr）"
            };
        }

        writer.Result(result);
        return result.ExitCode;
    }
}
