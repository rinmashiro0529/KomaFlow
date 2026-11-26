using System.IO;
using System.Reflection;
using MangaGui;
using MangaGui.Models;
using MangaGui.Services;
using MangaGui.ViewModels;

namespace MangaCli;

/// <summary>
/// Short commands. <c>run</c> lives in <see cref="RunCommand"/>.
/// </summary>
internal static class CliCommands
{
    public const string UsageText = """
        MangaCli —— 漫画翻译 Colab 多档位 GPU 命令行（与 GUI 共用会话、数据目录和互斥锁）

        用法: MangaCli <命令> [选项] [路径...]

        命令:
          doctor                            环境自检与诊断（离线）
          precheck <路径...>                离线预检漫画 zip/cbz（不部署不计费）
          accounts list [--refresh]         查看已配置 Colab 账号（--refresh 联网查各号剩余 CU）
          settings get                      查看设置与解析后的脚本/资产目录
          settings set engine mtui|mit      设置翻译引擎
          settings set gpu l4|a100|a100-hm|g4
                                            设置默认 GPU 档位（开机按显存自适应最佳工作流）
          settings set inpaint lama_large|flux2-klein
                                            设置高显存档位重绘模型（默认 lama_large）
          settings set g4-mode flagship|balanced
                                            设置 G4 96G 子模式（flagship=122B / balanced=32B+Sakura）
          settings set account auto|<id>    设置账号选号模式（auto=按余额轮询，或指定账号 ID）
          settings set output-dir <dir|default>
          settings set scripts-dir <dir>    设置 colab_manga 脚本目录（仅旧引擎 mit 需要 manga.json；自定义术语表 sakura_dict.txt 也用此目录；设置时目录仍需含 manga.json）
          status [--probe]                  会话与计费状态（--probe 联网探测，持锁）
          run [路径...] --minutes N [选项]  部署→翻译→停机 全流程
          stop [--account <id>]             停止会话并核实计费结束（持锁）
          logs [--tail N] [--crash]         查看本地日志（--crash 读 crash.log）
          help | --help                     本帮助
          --version                         版本

        通用选项:
          --data-dir <dir>        数据目录（默认 %APPDATA%\manga_gui）
          --events-file <path>    把 NDJSON 事件同时追加写入文件
          --quiet-logs            不输出 log 事件

        run 选项:
          --minutes N             必填，计划时长 1–720 分钟
          --no-auto-stop          到期仅提醒（不自动停机）
          --keep-session          结束后保留会话（需 GUI 接管或 MangaCli stop）
          --engine mtui|mit       本次使用的翻译引擎（写入设置）
          --gpu l4|a100|a100-hm|g4
                                  本次申请的 GPU 档位（写入设置，开机按实际显存自适应）
          --inpaint lama_large|flux2-klein
                                  高显存档位（>=40G）重绘模型（写入设置）
          --g4-mode flagship|balanced
                                  G4 96G 子模式（写入设置）
          --account auto|<id>     本次选号模式或指定账号 ID（写入设置）
          --output-dir <dir>      成品输出目录（仅本次，不写设置）
          --on-orphan P           孤儿会话策略：fail（默认）| adopt | stop
          --on-expiry P           时长到期策略：auto（默认）| drain | stop
          --scripts-dir <dir>     colab_manga 脚本目录（仅旧引擎 mit 必需；mtui 下仅用于自定义 sakura_dict.txt；校验后写入设置）
          --deploy-retries N      部署失败重试次数 0–2（默认 0）

        stdout 仅输出 NDJSON 事件，最后一行恒为 result；诊断信息走 stderr。
        退出码: 0 成功 / 1 异常 / 2 用法 / 3 环境 / 4 锁占用 / 5 部署失败 /
                6 任务失败 / 7 停机未核实（可能仍在计费）/ 8 孤儿会话 / 9 探测失败 / 130 中断
        """;

    public static CommandResult Help() => new()
    {
        Command = "help",
        ExitCode = ExitCodes.Ok,
        Message = "MangaCli 用法",
        Fields = { ["usage"] = UsageText }
    };

    public static CommandResult Version()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
            ?? "1.4.0";
        var plus = version.IndexOf('+');
        if (plus > 0)
        {
            version = version[..plus];
        }

        return new CommandResult
        {
            Command = "version",
            ExitCode = ExitCodes.Ok,
            Message = $"MangaCli {version}",
            Fields = { ["version"] = version }
        };
    }

    // ---------------------------------------------------------------- doctor

    public static async Task<CommandResult> DoctorAsync(CommandContext c)
    {
        var r = new CommandResult { Command = "doctor" };
        c.Prompts = new CliPrompts(
            c.Writer, allowStart: false, minutes: null, autoStop: true,
            OrphanPolicy.Dismiss, ExpiryPolicy.None, scriptsDir: null, c.Interrupt);
        await c.EnsureHostAsync(offline: true);

        var issues = await c.InvokeAsync(() =>
        {
            c.Vm.RunEnvironmentSelfCheck();
            c.Vm.ShowDiagnosticsCommand.Execute(null);
            var warnings = c.Vm.HasEnvironmentWarning
                ? c.Vm.EnvironmentWarning.Split('；', StringSplitOptions.RemoveEmptyEntries)
                : [];
            return Task.FromResult(warnings);
        });

        var snapshot = c.Prompts.Diagnostics;
        if (snapshot is not null)
        {
            r.Fields["diagnostics"] = new Dictionary<string, object?>
            {
                ["app_version"] = snapshot.AppVersion,
                ["dotnet_version"] = snapshot.DotNetVersion,
                ["colab_cli_status"] = snapshot.ColabCliStatus,
                ["colab_launch_mode"] = snapshot.ColabLaunchMode,
                ["scripts_directory"] = snapshot.ScriptsDirectory,
                ["scripts_source"] = snapshot.ScriptsSource,
                ["bundled_assets_directory"] = snapshot.BundledAssetsDirectory,
                ["session_store_path"] = snapshot.SessionStorePath,
                ["asset_checksums"] = snapshot.AssetChecksums,
                ["is_elevated"] = snapshot.IsElevated,
                ["crash_log_path"] = snapshot.CrashLogPath
            };
        }

        r.Fields["engine"] = c.Store.LoadSettings().TranslationEngine is { } e
            ? TranslationEngines.Normalize(e)
            : TranslationEngines.Mtui;
        r.Fields["issues"] = issues;
        r.Fields["lock_held"] = ProcessLock.IsHeld(c.Env.MutexName);
        AddCloudCallsBlocked(r, c);

        r.ExitCode = issues.Length > 0 ? ExitCodes.Environment : ExitCodes.Ok;
        r.Message = issues.Length > 0 ? $"环境自检发现 {issues.Length} 个问题" : "环境自检通过";
        return r;
    }

    // ---------------------------------------------------------------- precheck

    public static async Task<CommandResult> PrecheckAsync(CommandContext c)
    {
        var r = new CommandResult { Command = "precheck" };
        if (c.Options.Positionals.Count == 0)
        {
            return r.Fail(ExitCodes.Usage, "precheck 需要至少一个漫画 zip/cbz 路径。");
        }

        if (c.Options.Engine is not null)
        {
            r.Warnings.Add("--engine 对 precheck 不生效：预检始终按当前 settings.json 配置的引擎判断。");
        }

        c.Prompts = new CliPrompts(
            c.Writer, allowStart: false, minutes: null, autoStop: true,
            OrphanPolicy.Dismiss, ExpiryPolicy.None, scriptsDir: null, c.Interrupt);
        await c.EnsureHostAsync(offline: true);

        var (accepted, rejected) = await c.InvokeAsync(() =>
            EnqueueInputsAsync(c, outputDirectory: c.Store.LoadSettings().OutputDir));

        r.Fields["accepted"] = accepted.Select(ToAccepted).ToArray();
        r.Fields["rejected"] = rejected.ToArray();
        AddCloudCallsBlocked(r, c);

        if (accepted.Count == 0)
        {
            return r.Fail(ExitCodes.Usage, "所有输入均未通过离线预检。");
        }

        r.ExitCode = rejected.Count > 0 ? ExitCodes.TaskFailed : ExitCodes.Ok;
        r.Message = $"预检通过 {accepted.Count} 个，拒收 {rejected.Count} 个";
        return r;
    }

    // ---------------------------------------------------------------- settings

    public static async Task<CommandResult> SettingsAsync(CommandContext c)
    {
        var r = new CommandResult { Command = "settings" };
        var args = c.Options.Positionals;
        if (args.Count == 0)
        {
            return r.Fail(ExitCodes.Usage, "settings 需要子命令：get | set。");
        }

        if (args[0] == "get")
        {
            var store = new SessionStore(c.Options.DataDir);
            var scripts = new VmScripts(store, c.Env.AppBaseDirectory);
            var settings = store.LoadSettings();
            r.Fields["settings"] = new Dictionary<string, object?>
            {
                ["scripts_dir"] = settings.ScriptsDir,
                ["theme"] = settings.Theme,
                ["output_dir"] = settings.OutputDir,
                ["colab_exe_path"] = settings.ColabExePath,
                ["translation_engine"] = TranslationEngines.Normalize(settings.TranslationEngine),
                ["preferred_gpu_tier"] = GpuTiers.Normalize(settings.PreferredGpuTier),
                ["high_quality_inpaint_mode"] = InpaintModes.Normalize(settings.HighQualityInpaintMode),
                ["g4_sub_mode"] = G4SubModes.Normalize(settings.G4SubMode),
                ["account_selection_mode"] = AccountSelectionModes.Normalize(settings.AccountSelectionMode),
                ["selected_account_id"] = settings.SelectedAccountId,
                ["quota_auto_poll_enabled"] = settings.QuotaAutoPollEnabled ?? true,
                ["quota_poll_interval_minutes"] = settings.QuotaPollIntervalMinutes ?? AccountManager.DefaultPollIntervalMinutes,
                ["account_min_balance_cu"] = settings.AccountMinBalanceCu ?? AccountManager.DefaultMinBalanceCu
            };
            r.Fields["resolved"] = new Dictionary<string, object?>
            {
                ["scripts_dir"] = scripts.ExistingScriptsDirectory,
                ["scripts_source"] = scripts.ExistingScriptsSource,
                ["assets_dir"] = scripts.BundledAssetsDirectory
            };
            r.Fields["lock_held"] = ProcessLock.IsHeld(c.Env.MutexName);
            AddCloudCallsBlocked(r, c);
            r.ExitCode = ExitCodes.Ok;
            r.Message = "当前设置";
            return r;
        }

        if (args[0] != "set" || args.Count < 3)
        {
            return r.Fail(ExitCodes.Usage, "用法：settings set engine|gpu|inpaint|g4-mode|account|output-dir|scripts-dir <value>");
        }

        if (!c.TryAcquireLock())
        {
            r.Fields["lock_held"] = true;
            return r.Fail(ExitCodes.Locked, "互斥锁被占用（GUI 或另一个 MangaCli 正在运行），settings set 已中止。");
        }

        var key = args[1];
        var value = args[2];
        switch (key)
        {
            case "engine":
            {
                if (value is not ("mtui" or "mit"))
                {
                    return r.Fail(ExitCodes.Usage, "engine 只接受 mtui 或 mit。");
                }

                c.Prompts = new CliPrompts(
                    c.Writer, allowStart: false, minutes: null, autoStop: true,
                    OrphanPolicy.Dismiss, ExpiryPolicy.None, scriptsDir: null, c.Interrupt);
                await c.EnsureHostAsync(offline: true);
                var choice = TranslationEngines.ToChoice(value);
                await c.InvokeAsync(() =>
                {
                    c.Vm.SetEngineCommand.Execute(choice);
                    return Task.CompletedTask;
                });
                if (c.Store.LoadSessionWithStatus().Record is not null)
                {
                    r.Warnings.Add("本地存在会话记录：引擎变更将在下次部署时生效，当前会话不受影响。");
                }

                r.ExitCode = ExitCodes.Ok;
                r.Message = $"翻译引擎已设为 {TranslationEngines.Normalize(value)}";
                break;
            }
            case "output-dir":
            {
                var normalized = value == "default" ? null : value;
                if (normalized is not null && !Directory.Exists(normalized))
                {
                    return r.Fail(ExitCodes.Usage, $"目录不存在：{normalized}");
                }

                var store = new SessionStore(c.Options.DataDir);
                var settings = store.LoadSettings();
                settings.OutputDir = normalized;
                store.SaveSettings(settings);
                r.ExitCode = ExitCodes.Ok;
                r.Message = normalized is null ? "成品输出已恢复默认（源文件同目录）" : $"成品输出目录已设为 {normalized}";
                break;
            }
            case "scripts-dir":
            {
                var store = new SessionStore(c.Options.DataDir);
                var scripts = new VmScripts(store, c.Env.AppBaseDirectory);
                if (!scripts.TrySetExistingScriptsDirectory(value))
                {
                    return r.Fail(ExitCodes.Usage, $"所选目录缺少 manga.json：{value}");
                }

                r.ExitCode = ExitCodes.Ok;
                r.Message = $"脚本目录已设为 {value}";
                break;
            }
            case "gpu":
            {
                if (!GpuTiers.TryParseStrict(value, out var tier))
                {
                    return r.Fail(ExitCodes.Usage, "gpu 只接受 l4、a100、a100-hm 或 g4。");
                }

                var store = new SessionStore(c.Options.DataDir);
                var settings = store.LoadSettings();
                settings.PreferredGpuTier = tier;
                store.SaveSettings(settings);
                var spec = GpuTiers.GetSpec(tier);
                r.ExitCode = ExitCodes.Ok;
                r.Message = $"默认 GPU 档位已设为 {tier}（{spec.ShortName} · 参考 {spec.RateDisplay}）";
                break;
            }
            case "inpaint":
            {
                if (value is not ("lama_large" or "flux2-klein"))
                {
                    return r.Fail(ExitCodes.Usage, "inpaint 只接受 lama_large 或 flux2-klein。");
                }

                var store = new SessionStore(c.Options.DataDir);
                var settings = store.LoadSettings();
                settings.HighQualityInpaintMode = value;
                store.SaveSettings(settings);
                r.ExitCode = ExitCodes.Ok;
                r.Message = $"高显存重绘模式已设为 {value}";
                break;
            }
            case "g4-mode":
            {
                if (value is not ("flagship" or "balanced"))
                {
                    return r.Fail(ExitCodes.Usage, "g4-mode 只接受 flagship 或 balanced。");
                }

                var store = new SessionStore(c.Options.DataDir);
                var settings = store.LoadSettings();
                settings.G4SubMode = value;
                store.SaveSettings(settings);
                r.ExitCode = ExitCodes.Ok;
                r.Message = $"G4 96G 子模式已设为 {value}";
                break;
            }
            case "account":
            {
                c.Prompts = new CliPrompts(
                    c.Writer, allowStart: false, minutes: null, autoStop: true,
                    OrphanPolicy.Dismiss, ExpiryPolicy.None, scriptsDir: null, c.Interrupt);
                await c.EnsureHostAsync(offline: true);
                if (string.Equals(value, AccountSelectionModes.Auto, StringComparison.OrdinalIgnoreCase))
                {
                    await c.InvokeAsync(() =>
                    {
                        c.Accounts.SetSelectionPolicy(AccountSelectionMode.AutoHighestBalance);
                        return Task.CompletedTask;
                    });
                    r.ExitCode = ExitCodes.Ok;
                    r.Message = "账号选号模式已设为 auto（按剩余额度自动轮询）";
                }
                else
                {
                    var found = c.Accounts.GetAccountById(value);
                    if (found is null)
                    {
                        return r.Fail(ExitCodes.Usage, $"未找到账号 ID：{value}（请先运行 MangaCli accounts list 查看）");
                    }

                    await c.InvokeAsync(() =>
                    {
                        c.Accounts.SetSelectionPolicy(AccountSelectionMode.Manual, found.Id);
                        return Task.CompletedTask;
                    });
                    r.ExitCode = ExitCodes.Ok;
                    r.Message = $"账号已切换为手动模式：{found.Name} ({found.Id})";
                }
                break;
            }
            default:
                return r.Fail(ExitCodes.Usage, $"未知设置项：{key}");
        }

        AddCloudCallsBlocked(r, c);
        return r;
    }

    // ---------------------------------------------------------------- accounts

    public static async Task<CommandResult> AccountsAsync(CommandContext c)
    {
        var r = new CommandResult { Command = "accounts" };
        var sub = c.Options.Positionals.FirstOrDefault() ?? "list";
        if (!string.Equals(sub, "list", StringComparison.OrdinalIgnoreCase))
        {
            return r.Fail(ExitCodes.Usage, "用法：accounts list [--refresh]");
        }

        c.Prompts = new CliPrompts(
            c.Writer, allowStart: false, minutes: null, autoStop: true,
            OrphanPolicy.Dismiss, ExpiryPolicy.None, scriptsDir: null, c.Interrupt);
        await c.EnsureHostAsync(offline: !c.Options.Refresh);

        if (c.Options.Refresh)
        {
            await c.InvokeAsync(async () =>
            {
                await c.Accounts.RefreshAllQuotasAsync(force: true, c.Interrupt);
            });
        }

        var profiles = c.Accounts.GetAccounts();
        var quotas = c.Accounts.GetAllQuotas();
        var plan = c.Accounts.ResolveCandidatesForStart();
        r.Fields["selection_mode"] = AccountSelectionModes.ToConfigValue(c.Accounts.SelectionMode);
        r.Fields["selected_account_id"] = c.Accounts.SelectedAccountId;
        r.Fields["recommended_account_id"] = plan.Candidates.FirstOrDefault()?.Id;
        r.Fields["recommendation_reason"] = plan.Reason;
        r.Fields["accounts"] = profiles.Select(p =>
        {
            var q = quotas.TryGetValue(p.Id, out var snap) ? snap : AccountQuotaSnapshot.Unknown(p.Id);
            return new Dictionary<string, object?>
            {
                ["id"] = p.Id,
                ["name"] = p.Name,
                ["masked_email"] = p.MaskedEmail,
                ["enabled"] = p.Enabled,
                ["status"] = CliJson.ToSnakeCase(q.Status.ToString()),
                ["balance_cu"] = q.CurrentBalanceCu,
                ["usage_rate_hourly"] = q.UsageRateHourly,
                ["active_assignments"] = q.ActiveAssignments,
                ["updated_at"] = q.UpdatedAt?.ToString("o"),
                ["error"] = q.ErrorMessage
            };
        }).ToArray();
        AddCloudCallsBlocked(r, c);
        r.ExitCode = ExitCodes.Ok;
        r.Message = c.Options.Refresh
            ? $"已刷新 {profiles.Count} 个账号额度（{plan.Reason}）"
            : $"已列出 {profiles.Count} 个账号（未联网查询额度；加 --refresh 可查最新 CU）";
        return r;
    }

    // ---------------------------------------------------------------- status

    public static async Task<CommandResult> StatusAsync(CommandContext c)
    {
        var r = new CommandResult { Command = "status" };
        if (!c.Options.Probe)
        {
            var store = new SessionStore(c.Options.DataDir);
            var load = store.LoadSessionWithStatus();
            r.Fields["store_status"] = CliJson.ToSnakeCase(load.Status.ToString());
            r.Fields["lock_held"] = ProcessLock.IsHeld(c.Env.MutexName);
            if (load.Status is StoreLoadStatus.Corrupted or StoreLoadStatus.AccessDenied)
            {
                r.Warnings.Add(load.ErrorMessage ?? "本地会话记录异常。");
            }

            if (load.Record is { } record)
            {
                var effectiveRate = record.BillingCuPerHour is > 0
                    ? record.BillingCuPerHour.Value
                    : !string.IsNullOrWhiteSpace(record.DetectedGpuProfile)
                        ? GpuProfiles.DefaultCuPerHourForProfile(record.DetectedGpuProfile)
                        : GpuTiers.GetSpec(record.RequestedGpu).DefaultCuPerHour;
                r.Fields["record"] = new Dictionary<string, object?>
                {
                    ["session_name"] = record.SessionName,
                    ["started_at"] = record.StartedAt.ToString("o"),
                    ["planned_minutes"] = record.PlannedMinutes,
                    ["auto_stop"] = record.AutoStop,
                    ["start_time_unknown"] = record.StartTimeUnknown,
                    ["requested_gpu"] = record.RequestedGpu,
                    ["detected_gpu_profile"] = record.DetectedGpuProfile,
                    ["detected_gpu_name"] = record.DetectedGpuName,
                    ["billing_cu_per_hour"] = effectiveRate,
                    ["inpaint_mode"] = record.InpaintMode
                };
                var billing = new BillingTimer(c.Env.UtcNow);
                billing.Start(record.StartedAt, record.PlannedMinutes, record.AutoStop, record.StartTimeUnknown, effectiveRate);
                r.Fields["billing"] = EventWriter.BillingFields(billing.Evaluate(fireEvents: false));
                r.Fields["session"] = new Dictionary<string, object?>
                {
                    ["has_record"] = true,
                    ["billing_active"] = (bool?)null
                };
            }
            else
            {
                r.Fields["session"] = new Dictionary<string, object?>
                {
                    ["has_record"] = false,
                    ["billing_active"] = false
                };
            }

            AddCloudCallsBlocked(r, c);
            r.ExitCode = ExitCodes.Ok;
            r.Message = load.Record is null ? "本地无会话记录（未联网核实）" : "本地存在会话记录（未联网核实）";
            return r;
        }

        // --probe: hold the lock and run the real startup probe.
        if (!c.TryAcquireLock())
        {
            r.Fields["lock_held"] = true;
            return r.Fail(ExitCodes.Locked, "互斥锁被占用（GUI 或另一个 MangaCli 正在运行），status --probe 已中止。");
        }

        c.Prompts = new CliPrompts(
            c.Writer, allowStart: false, minutes: null, autoStop: true,
            OrphanPolicy.Dismiss, ExpiryPolicy.None, scriptsDir: null, c.Interrupt);
        await c.EnsureHostAsync(offline: false);

        return await c.InvokeAsync(async () =>
        {
            var probe = await c.Session.ProbeOnStartupAsync();
            var session = c.Session;
            var orphan = false;
            if (probe.Outcome == StartupProbeOutcome.OrphanSessionDetected)
            {
                c.Prompts!.AnswerOrphan();
                orphan = true;
            }

            var status = session.LatestStatus;
            // 探测成功时按 has_active_vm 给出确定的 true/false；只有 ProbeFailed 才是 null。
            var billingActive = probe.Outcome == StartupProbeOutcome.ProbeFailed
                ? (bool?)null
                : session.HasActiveVm;
            r.Fields["session"] = SessionInfo(c, stopVerified: null, billingActive: billingActive);
            r.Fields["probe"] = new Dictionary<string, object?>
            {
                ["outcome"] = CliJson.ToSnakeCase(probe.Outcome.ToString()),
                ["message"] = probe.Message,
                ["orphan"] = orphan,
                ["health"] = new Dictionary<string, object?>
                {
                    ["llama_ok"] = status.LlamaOk,
                    ["fallback_llama_ok"] = status.FallbackLlamaOk,
                    ["mit_ok"] = status.MitOk,
                    ["engine_ready"] = status.EngineReady,
                    ["is_disconnected"] = status.IsDisconnected
                },
                ["gpu"] = string.IsNullOrWhiteSpace(status.GpuInfo) ? null : status.GpuInfo,
                ["gpu_profile"] = status.GpuProfile,
                ["gpu_vram_mb"] = status.GpuVramMb,
                ["inpaint_mode"] = status.InpaintMode,
                ["engine_cloud"] = status.DeployedEngine
            };
            r.Fields["billing"] = EventWriter.BillingFields(session.Billing.Evaluate(fireEvents: false));
            AddCloudCallsBlocked(r, c);

            if (probe.Outcome == StartupProbeOutcome.ProbeFailed)
            {
                return r.Fail(ExitCodes.ProbeFailed, $"云端会话状态探测失败：{probe.Message}");
            }

            r.ExitCode = ExitCodes.Ok;
            r.Message = orphan ? "检测到云端孤儿会话 manga（未接管未停止）" : probe.Message;
            return r;
        });
    }

    // ---------------------------------------------------------------- stop

    public static async Task<CommandResult> StopAsync(CommandContext c)
    {
        var r = new CommandResult { Command = "stop" };
        if (!c.TryAcquireLock())
        {
            r.Fields["lock_held"] = true;
            return r.Fail(ExitCodes.Locked, "互斥锁被占用（GUI 或另一个 MangaCli 正在运行），stop 已中止。");
        }

        c.Prompts = new CliPrompts(
            c.Writer, allowStart: false, minutes: null, autoStop: true,
            OrphanPolicy.Stop, ExpiryPolicy.Stop, scriptsDir: null, c.Interrupt);
        await c.EnsureHostAsync(offline: false);

        return await c.InvokeAsync(async () =>
        {
            var session = c.Session;

            // Explicit --account <id>: stop that specific account directly (useful for secondary orphans).
            if (!string.IsNullOrWhiteSpace(c.Options.Account)
                && !string.Equals(c.Options.Account, AccountSelectionModes.Auto, StringComparison.OrdinalIgnoreCase))
            {
                var target = c.Accounts.GetAccountById(c.Options.Account);
                if (target is null)
                {
                    return r.Fail(ExitCodes.Usage, $"未找到账号 ID：{c.Options.Account}");
                }

                var directStop = await session.StopOtherAccountSessionAsync(target.Id, c.Interrupt);
                var savedRec = c.Store.LoadSession();
                if (directStop.VerifiedStopped
                    && savedRec is not null
                    && string.Equals(savedRec.AccountId, target.Id, StringComparison.OrdinalIgnoreCase))
                {
                    c.Store.TryDeleteSession();
                }

                r.Fields["session"] = SessionInfo(c, directStop.VerifiedStopped, billingActive: !directStop.VerifiedStopped);
                if (!directStop.VerifiedStopped)
                {
                    return r.Fail(ExitCodes.StopUnverified, directStop.Message);
                }

                r.ExitCode = ExitCodes.Ok;
                r.Message = directStop.Message;
                return r;
            }

            var probe = await session.ProbeOnStartupAsync();
            bool? stopVerified = null;

            if (probe.Outcome == StartupProbeOutcome.ProbeFailed && !c.Interrupt.IsCancellationRequested)
            {
                r.Fields["session"] = SessionInfo(c, stopVerified: null, billingActive: null);
                return r.Fail(ExitCodes.ProbeFailed, $"云端会话状态探测失败：{probe.Message}");
            }

            if (probe.Outcome == StartupProbeOutcome.OrphanSessionDetected)
            {
                var action = c.Prompts!.AnswerOrphan();
                if (action == OrphanSessionAction.StopNow && session.HasActiveVm)
                {
                    var orphanStop = await session.StopSessionAsync(isAutoStop: false, tryKillTranslate: false);
                    stopVerified = orphanStop.VerifiedStopped;
                }
            }

            if (!session.HasActiveVm)
            {
                stopVerified = true;
                r.Fields["session"] = SessionInfo(c, stopVerified, billingActive: false);
                r.Fields["billing"] = EventWriter.BillingFields(session.Billing.Evaluate(fireEvents: false));
                r.ExitCode = ExitCodes.Ok;
                r.Message = probe.Outcome == StartupProbeOutcome.OrphanSessionDetected
                    ? "孤儿会话已停止并核实。"
                    : "云端无活动会话，无需停止。";
                return r;
            }

            var res = await c.Vm.ExecuteStopAsync(isAutoStop: false);
            stopVerified = res.VerifiedStopped;
            if (!stopVerified.Value && session.HasActiveVm)
            {
                stopVerified = await VerifyStopWithTimeoutAsync(c);
            }

            var finalVerified = stopVerified == true || !session.HasActiveVm;
            r.Fields["session"] = SessionInfo(c, finalVerified, billingActive: finalVerified ? false : true);
            r.Fields["billing"] = EventWriter.BillingFields(session.Billing.Evaluate(fireEvents: false));

            if (!finalVerified)
            {
                return r.Fail(ExitCodes.StopUnverified,
                    "停止未能核实，云端可能仍在计费！请立即重试 MangaCli stop 或在终端执行 colab stop -s manga。");
            }

            r.ExitCode = ExitCodes.Ok;
            r.Message = "会话已停止并核实计费结束。";
            return r;
        });
    }

    // ---------------------------------------------------------------- logs

    public static Task<CommandResult> LogsAsync(CommandContext c)
    {
        var r = new CommandResult { Command = "logs" };
        var path = c.Options.Crash
            ? App.CrashLogPath
            : LatestLogFile(c.Options.DataDir);

        if (path is null || !File.Exists(path))
        {
            r.Fields["file"] = path;
            r.Fields["lines"] = Array.Empty<string>();
            AddCloudCallsBlocked(r, c);
            r.ExitCode = ExitCodes.Ok;
            r.Message = c.Options.Crash ? "没有 crash.log" : "没有日志文件";
            return Task.FromResult(r);
        }

        string[] lines;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(fs))
        {
            lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        r.Fields["file"] = path;
        r.Fields["lines"] = lines.Length > c.Options.Tail ? lines[^c.Options.Tail..] : lines;
        AddCloudCallsBlocked(r, c);
        r.ExitCode = ExitCodes.Ok;
        r.Message = $"{path}（末尾 {Math.Min(c.Options.Tail, lines.Length)} 行）";
        return Task.FromResult(r);
    }

    private static string? LatestLogFile(string? dataDir)
    {
        var dir = Path.Combine(
            dataDir ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "manga_gui"),
            "logs");
        if (!Directory.Exists(dir))
        {
            return null;
        }

        return Directory.GetFiles(dir, "*.log")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    // ---------------------------------------------------------------- shared helpers

    /// <summary>Enqueues inputs one at a time so every input yields accepted items or a reason.</summary>
    internal static async Task<(List<TranslationTaskItem> Accepted, List<Dictionary<string, object?>> Rejected)>
        EnqueueInputsAsync(CommandContext c, string? outputDirectory)
    {
        var accepted = new List<TranslationTaskItem>();
        var rejected = new List<Dictionary<string, object?>>();
        var queue = c.Queue;
        foreach (var input in c.Options.Positionals)
        {
            var before = queue.Items.Count;
            var (added, reason) = await queue.EnqueueFilesAsync([input], outputDirectory);
            for (var i = before; i < queue.Items.Count; i++)
            {
                accepted.Add(queue.Items[i]);
            }

            if (added == 0 || reason is not null)
            {
                rejected.Add(new Dictionary<string, object?>
                {
                    ["input"] = input,
                    ["reason"] = reason ?? "未通过离线预检"
                });
            }
        }

        return (accepted, rejected);
    }

    internal static Dictionary<string, object?> ToAccepted(TranslationTaskItem item) => new()
    {
        ["file"] = item.SourceZipPath,
        ["total_pages"] = item.TotalPages,
        ["precheck_summary"] = item.PrecheckSummary,
        ["output"] = ZipPreprocessor.ResolveUniqueOutputPath(item.SourceZipPath, item.TargetOutputDirectory, "_translated")
    };

    /// <summary>Builds the session{} block shared by session-related commands.</summary>
    internal static Dictionary<string, object?> SessionInfo(CommandContext c, bool? stopVerified, bool? billingActive)
    {
        var session = c.Session;
        var status = session.LatestStatus;
        var configured = session.ConfiguredEngine;
        var cloud = status.DeployedEngine;
        return new Dictionary<string, object?>
        {
            ["state"] = session.State,
            ["has_active_vm"] = session.HasActiveVm,
            ["account_id"] = session.ActiveSessionAccountId,
            ["account_name"] = session.ActiveSessionAccountName,
            ["gpu_requested"] = session.ActiveRequestedGpuTier,
            ["gpu_profile"] = session.ActiveDetectedGpuProfile ?? status.GpuProfile,
            ["gpu_name"] = session.ActiveDetectedGpuName,
            ["inpaint_mode"] = session.ActiveInpaintMode,
            ["billing_cu_per_hour"] = Math.Round(session.Billing.CurrentCuPerHour, 4),
            ["engine_configured"] = configured,
            ["engine_cloud"] = cloud,
            ["engine_mismatch"] = session.HasActiveVm && cloud is not null && cloud != configured,
            ["stop_verified"] = stopVerified,
            ["billing_active"] = billingActive,
            ["unnamed_assignment_warning"] = session.UnnamedAssignmentWarning,
            ["persistence_warning"] = session.PersistenceWarning,
            ["other_account_warnings"] = session.OtherAccountWarnings.Select(w => w.Message).ToArray()
        };
    }

    /// <summary>Runs the auto-stop verification loop bounded by Env.StopVerifyTimeout.</summary>
    internal static async Task<bool> VerifyStopWithTimeoutAsync(CommandContext c)
    {
        try
        {
            using var cts = new CancellationTokenSource(c.Env.StopVerifyTimeout);
            var res = await c.Session.AutoStopUntilVerifiedAsync(tryKillTranslate: false, cts.Token);
            return res.VerifiedStopped;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    internal static void AddCloudCallsBlocked(CommandResult r, CommandContext c)
    {
        var blocked = c.Guard?.BlockedCalls ?? 0;
        r.Fields["cloud_calls_blocked"] = blocked;
        if (blocked > 0)
        {
            r.Warnings.Add($"离线命令拦截了 {blocked} 次意外的云端调用。");
        }
    }
}
