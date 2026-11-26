using MangaGui.Models;
using MangaGui.Services;
using MangaGui.ViewModels;

namespace MangaCli;

/// <summary>
/// <c>run</c>：持锁 → 预检入队 → 启动探测（镜像 MainViewModel.RunStartupProbeAsync 的各 outcome
/// 处理）→ 部署或恢复 → 等任务完成 → 停机核实。所有 VM/Session 调用都发生在 headless
/// dispatcher 线程上。
/// </summary>
internal static class RunCommand
{
    public static async Task<CommandResult> ExecuteAsync(CommandContext c)
    {
        var r = new CommandResult { Command = "run" };
        var o = c.Options;

        if (o.Minutes is not { } minutes || minutes is < 1 or > 720)
        {
            return r.Fail(ExitCodes.Usage, "run 需要 --minutes N（1–720 的整数）。");
        }

        if (!c.TryAcquireLock())
        {
            r.Fields["lock_held"] = true;
            return r.Fail(ExitCodes.Locked, "互斥锁被占用（GUI 或另一个 MangaCli 正在运行），run 已中止。");
        }

        var orphanPolicy = o.OnOrphan switch
        {
            "adopt" => OrphanPolicy.Adopt,
            "stop" => OrphanPolicy.Stop,
            _ => OrphanPolicy.Fail
        };
        var expiryPolicy = o.OnExpiry switch
        {
            "drain" => ExpiryPolicy.Drain,
            "stop" => ExpiryPolicy.Stop,
            _ => ExpiryPolicy.Auto
        };
        c.Prompts = new CliPrompts(
            c.Writer, allowStart: true, minutes: minutes, autoStop: !o.NoAutoStop,
            orphanPolicy, expiryPolicy, o.ScriptsDir, c.Interrupt);
        await c.EnsureHostAsync(offline: false);

        return await c.InvokeAsync(() => RunOnDispatcherAsync(c, r));
    }

    private static async Task<CommandResult> RunOnDispatcherAsync(CommandContext c, CommandResult r)
    {
        var o = c.Options;
        var session = c.Session;
        var vm = c.Vm;
        var queue = c.Queue;
        var prompts = c.Prompts!;
        var settings = c.Store.LoadSettings();

        // --account：校验并写入选号策略（若已有运行中会话则沿用原会话并告警）。
        if (!string.IsNullOrWhiteSpace(o.Account))
        {
            if (string.Equals(o.Account, AccountSelectionModes.Auto, StringComparison.OrdinalIgnoreCase))
            {
                c.Accounts.SetSelectionPolicy(AccountSelectionMode.AutoHighestBalance);
            }
            else
            {
                var found = c.Accounts.GetAccountById(o.Account);
                if (found is null)
                {
                    return r.Fail(ExitCodes.Usage, $"未找到账号 ID：{o.Account}（请先运行 MangaCli accounts list 查看），未申请 GPU。");
                }
                c.Accounts.SetSelectionPolicy(AccountSelectionMode.Manual, found.Id);
            }
        }

        // --engine：先写入设置；与已恢复会话冲突时在恢复后校验并还原（A7）。
        var previousEngine = TranslationEngines.Normalize(settings.TranslationEngine);
        if (o.Engine is not null && o.Engine != previousEngine)
        {
            vm.SetEngineCommand.Execute(TranslationEngines.ToChoice(o.Engine));
            settings = c.Store.LoadSettings();
        }

        // --gpu / --inpaint / --g4-mode：写入设置，新启会话时按所选档位申请并自适应部署。
        if (o.Gpu is not null || o.Inpaint is not null || o.G4Mode is not null)
        {
            if (o.Gpu is not null) settings.PreferredGpuTier = GpuTiers.Normalize(o.Gpu);
            if (o.Inpaint is not null) settings.HighQualityInpaintMode = InpaintModes.Normalize(o.Inpaint);
            if (o.G4Mode is not null) settings.G4SubMode = G4SubModes.Normalize(o.G4Mode);
            c.Store.TrySaveSettings(settings);
        }

        vm.RunEnvironmentSelfCheck();
        var colabOk = c.Cli.IsColabAvailable(out _);
        if (!colabOk)
        {
            return r.Fail(ExitCodes.Environment, "未找到 colab 命令行工具，未申请 GPU。");
        }

        // --scripts-dir：等同 GUI/CLI 的「选择脚本目录」，校验目录含 manga.json 后持久化
        // 到设置；目录不合规直接退出，不产生任何云端调用。
        if (o.ScriptsDir is not null && !c.Scripts.TrySetExistingScriptsDirectory(o.ScriptsDir))
        {
            return r.Fail(ExitCodes.Usage, $"--scripts-dir 目录缺少 manga.json：{o.ScriptsDir}");
        }

        var missingAssets = c.Scripts.GetMissingScriptsReport(
            TranslationEngines.Normalize(settings.TranslationEngine));
        if (missingAssets.Count > 0)
        {
            return r.Fail(ExitCodes.Environment,
                $"缺少部署资产：{string.Join("、", missingAssets)}（assets 目录：{c.Scripts.BundledAssetsDirectory}），未申请 GPU。");
        }

        var effectiveOutputDir = o.OutputDir ?? settings.OutputDir;
        var rejected = new List<Dictionary<string, object?>>();
        if (o.Positionals.Count > 0)
        {
            var (accepted, rej) = await CliCommands.EnqueueInputsAsync(c, effectiveOutputDir);
            rejected = rej;
            if (accepted.Count == 0)
            {
                r.Fields["rejected"] = rejected.ToArray();
                return r.Fail(ExitCodes.Usage, "所有输入均未通过离线预检，未申请 GPU。");
            }
        }

        // A4：中断必须能抢占部署。StartService/RetryDeploy 不接受 CancellationToken，
        // 这里把「停止」投递到 dispatcher（等同 GUI 部署中点「停止」——StopSessionAsync
        // 会 bump epoch 抢占部署）。注册一次，方法结束时 dispose。
        using var interruptReg = c.Interrupt.Register(() =>
        {
            try
            {
                if (o.KeepSession)
                {
                    queue.CancelPendingAndActive();
                }
                else
                {
                    _ = c.Host!.InvokeAsync(async () =>
                    {
                        try { await vm.ExecuteStopAsync(isAutoStop: false); }
                        catch { /* best-effort: the teardown path verifies again */ }
                    });
                }
            }
            catch
            {
                // Host may already be gone during teardown.
            }
        });

        // G3：任务 worker 只有在引擎决定（沿用云端引擎 / 会话内重部署成功）后才启动。
        var recoveredPendingReady = false;

        // ---- 启动探测：逐项镜像 MainViewModel.RunStartupProbeAsync 的 outcome 处理 ----
        var probe = await session.ProbeOnStartupAsync();

        if (probe.Outcome == StartupProbeOutcome.ProbeFailed && !c.Interrupt.IsCancellationRequested)
        {
            r.Fields["rejected"] = rejected.ToArray();
            r.Fields["session"] = CliCommands.SessionInfo(c, stopVerified: null, billingActive: session.HasActiveVm ? true : null);
            return r.Fail(ExitCodes.ProbeFailed, $"云端会话状态探测失败：{probe.Message}");
        }

        switch (probe.Outcome)
        {
            case StartupProbeOutcome.RecoveredReady:
            {
                // A1/G3：worker 启动推迟到引擎决定之后（会话内切换要先重部署）。
                recoveredPendingReady = true;
                var snap = session.Billing.Evaluate(fireEvents: false);
                r.Warnings.Add(
                    $"已恢复原会话，沿用原计划时长 {snap.PlannedMinutes?.ToString() ?? "未知"} 分钟" +
                    (snap.DeadlineUtc is { } dl ? $"，预计到期 {dl.LocalDateTime:yyyy-MM-dd HH:mm:ss}" : string.Empty) +
                    "；本次 --minutes 不生效。");
                break;
            }

            case StartupProbeOutcome.OrphanSessionDetected:
            {
                var action = prompts.AnswerOrphan();
                switch (action)
                {
                    case OrphanSessionAction.Dismiss:
                        r.Fields["rejected"] = rejected.ToArray();
                        r.Fields["session"] = CliCommands.SessionInfo(c, stopVerified: null, billingActive: true);
                        return r.Fail(ExitCodes.OrphanRefused,
                            "检测到云端孤儿会话 manga 且 --on-orphan fail，未做任何改动。可改用 --on-orphan adopt 或 stop，或先执行 MangaCli stop。");
                    case OrphanSessionAction.StopNow:
                        if (session.HasActiveVm)
                        {
                            var orphanStop = await session.StopSessionAsync(isAutoStop: false, tryKillTranslate: false);
                            if (!orphanStop.VerifiedStopped)
                            {
                                var verified = await CliCommands.VerifyStopWithTimeoutAsync(c);
                                if (!verified)
                                {
                                    r.Fields["session"] = CliCommands.SessionInfo(c, verified, billingActive: true);
                                    return r.Fail(ExitCodes.StopUnverified,
                                        "孤儿会话停止未能核实，云端可能仍在计费！请立即执行 MangaCli stop。");
                                }
                            }
                        }
                        break;
                    case OrphanSessionAction.Adopt:
                    {
                        // A2：与 VM 一致——HasActiveVm 即可，问 start 对话框，成功后启动 worker。
                        var (confirmed, planned, autoStop) = prompts.AnswerStart();
                        if (!confirmed)
                        {
                            r.Fields["session"] = CliCommands.SessionInfo(c, stopVerified: null, billingActive: true);
                            return r.Fail(ExitCodes.Usage, "接管孤儿会话被拒绝（内部策略未确认）。");
                        }

                        if (session.HasActiveVm)
                        {
                            var adopted = await session.AdoptOrphanSessionAsync(
                                plannedMinutes: planned, autoStop: autoStop, cancellationToken: c.Interrupt);
                            if (adopted)
                            {
                                recoveredPendingReady = true;
                            }
                            // adopt 失败 → 落入下面的 Error && HasActiveVm 重试部署分支。
                        }
                        break;
                    }
                }
                break;
            }
        }

        // G3：会话内引擎切换——云端已报就绪引擎且与 --engine 不同时，在当前会话重部署；
        // 不带 --engine 时按云端引擎翻译并提示。cloud 为 null（未就绪/Error）时走 A3。
        var cloudEngine = session.LatestStatus.DeployedEngine;
        var engineSwitchNeeded = o.Engine is not null
            && session.HasActiveVm
            && cloudEngine is not null
            && cloudEngine != TranslationEngines.Normalize(o.Engine);

        if (engineSwitchNeeded)
        {
            recoveredPendingReady = false;
            r.Warnings.Add($"云端引擎 {cloudEngine} → 按 --engine {TranslationEngines.Normalize(o.Engine)} 在当前会话重新部署。");
            // RetryDeployAsync 成功后自行 StartWorker + NotifySessionReady（且保留计费开始时间）。
            await vm.RetryDeployCommand.ExecuteAsync(null);
        }
        else if (o.Engine is null && session.HasActiveVm
            && cloudEngine is not null && cloudEngine != session.ConfiguredEngine)
        {
            r.Warnings.Add($"云端引擎为 {cloudEngine}，设置为 {session.ConfiguredEngine}；本次按云端引擎翻译。如需切换请加 --engine {session.ConfiguredEngine}");
        }

        if (recoveredPendingReady && !engineSwitchNeeded)
        {
            queue.StartWorker();
            queue.NotifySessionReady();
        }

        // A3：Error && HasActiveVm（RecoveredNeedsDeploy 或接管失败）→ 等同 GUI「重试部署」一次。
        // 引擎切换本身已是一次部署尝试，跳过 A3；后续只由 --deploy-retries 重试。
        if (!engineSwitchNeeded && !c.Interrupt.IsCancellationRequested
            && session.State == SessionState.Error && session.HasActiveVm)
        {
            await vm.RetryDeployCommand.ExecuteAsync(null);
        }

        // 部署：无会话且未中断 → 走 VM 的 StartServiceCommand（内部会再次询问 start 对话框）。
        if (!c.Interrupt.IsCancellationRequested && !session.HasActiveVm && session.State != SessionState.Deploying)
        {
            // IsProbingStartup 期间 StartServiceAsync 会直接返回，先等探测完全结束。
            var waitProbe = 0;
            while (session.IsProbingStartup && !c.Interrupt.IsCancellationRequested && waitProbe < 60_000)
            {
                await Task.Delay(500);
                waitProbe += 500;
            }

            // 无 VM 时 GUI 只允许「启动服务」（CanStartService 覆盖 Idle 与 Error&&!hasVm）；
            // RetryDeploy 会退回 60 分钟默认时长而忽略 --minutes，这里不能用。
            if (!c.Interrupt.IsCancellationRequested)
            {
                // Auto multi-account selection: refresh balances right before selecting an account.
                if (c.Accounts.HasMultipleAccounts
                    && c.Accounts.SelectionMode == AccountSelectionMode.AutoHighestBalance)
                {
                    try
                    {
                        await c.Accounts.EnsureQuotasFreshAsync(TimeSpan.FromSeconds(60), c.Interrupt);
                    }
                    catch
                    {
                        // Non-fatal: proceed with available/fallback candidates.
                    }
                }

                await vm.StartServiceCommand.ExecuteAsync(null);
            }
        }

        // 部署失败（含 VM 已建立但 setup 未完成）→ 按 --deploy-retries 重试部署。
        for (var retry = 0; session.State == SessionState.Error
            && retry < o.DeployRetries && !c.Interrupt.IsCancellationRequested; retry++)
        {
            await vm.RetryDeployCommand.ExecuteAsync(null);
        }

        // 会话内重部署后必须确实达到目标引擎的就绪态，否则按部署失败处理（exit 5）。
        // 在 --deploy-retries 循环之后重新评估，重试成功后不得再按失败处理。
        var engineSwitchFailed = engineSwitchNeeded
            && (session.State is not (SessionState.Ready or SessionState.Busy)
                || session.LatestStatus.DeployedEngine != TranslationEngines.Normalize(o.Engine));

        var interruptedEarly = c.Interrupt.IsCancellationRequested;
        if (interruptedEarly || engineSwitchFailed || session.State == SessionState.Error || !session.HasActiveVm)
        {
            // 部署失败/被中断 → 尽力停机核实（账单安全优先）。
            var stopVerified = o.KeepSession
                ? session.HasActiveVm is false
                : await ShutdownAndVerifyAsync(c);
            r.Fields["rejected"] = rejected.ToArray();
            r.Fields["session"] = CliCommands.SessionInfo(
                c, stopVerified, billingActive: o.KeepSession ? (session.HasActiveVm ? true : null) : (stopVerified ? false : true));
            if (!o.KeepSession && !stopVerified)
            {
                return r.Fail(ExitCodes.StopUnverified,
                    "部署失败后停止未能核实，云端可能仍在计费！请立即执行 MangaCli stop。");
            }

            if (interruptedEarly)
            {
                return r.Fail(ExitCodes.Interrupted, o.KeepSession
                    ? "部署期间被中断，会话按 --keep-session 保留（仍在计费）。"
                    : "部署期间被中断，已停机核实。");
            }

            var reason = session.LastError;
            if (string.IsNullOrWhiteSpace(reason) && engineSwitchFailed)
            {
                reason = $"按 --engine {TranslationEngines.Normalize(o.Engine)} 重新部署后云端引擎未达标（云端引擎：{session.LatestStatus.DeployedEngine ?? "未就绪"}，会话状态：{session.State}）";
            }
            if (string.IsNullOrWhiteSpace(reason))
            {
                // A6：Idle 且无 VM —— StartServiceAsync 被前置检查拦下，根本没部署（未申请 GPU）。
                var lastLine = vm.LatestLogLine;
                return r.Fail(ExitCodes.DeployFailed,
                    $"未能启动部署（未申请 GPU）{(string.IsNullOrWhiteSpace(lastLine) ? "" : $"：{lastLine}")}");
            }

            return r.Fail(ExitCodes.DeployFailed, $"部署失败：{reason}（已停机核实）");
        }

        // 等待任务完成 / 停机 / 错误 / 中断。
        await WaitForCompletionAsync(c);

        var interrupted = c.Interrupt.IsCancellationRequested;
        if (!o.KeepSession || interrupted)
        {
            queue.CancelPendingAndActive();

            // F7：被取消的活动任务在 worker finally 里还要跑 vm_pack.sh stop_job/clean（SSH）。
            // 中断注册的 CancelPendingAndActive 只负责取消；这里在主流程上等（dispatcher
            // 上 await 不阻塞它）到没有活动状态的任务，否则进程退出会让远端任务残留。
            // 超时只告警——下次接管时 clean_stale 会兜底。
            var cleanupDeadline = DateTimeOffset.UtcNow + c.Env.InterruptCleanupBound;
            while (queue.Items.Any(t => t.Status is TranslationTaskStatus.Preparing
                or TranslationTaskStatus.Uploading or TranslationTaskStatus.Translating
                or TranslationTaskStatus.Packing or TranslationTaskStatus.Downloading))
            {
                if (DateTimeOffset.UtcNow >= cleanupDeadline)
                {
                    r.Warnings.Add($"云端任务清理未在 {c.Env.InterruptCleanupBound.TotalSeconds:0} 秒内完成，下次接管时会自动清理遗留任务。");
                    break;
                }

                await Task.Delay(250);
            }
        }

        // A5：任务统计在 CancelPendingAndActive 之后取；任何非 Completed 都算未完成 → 6。
        var tasks = queue.Items.ToList();
        var incompleteTasks = tasks.Count(t => t.Status != TranslationTaskStatus.Completed);
        var completedTasks = tasks.Count - incompleteTasks;

        // 停机后 BillingTimer 归零；先留存本次会话的用时/CU 估算供 result 汇报。
        var billingBeforeStop = session.Billing.Evaluate(fireEvents: false);
        bool stopVerifiedFinal = true;
        if (!o.KeepSession && session.HasActiveVm)
        {
            var stop = await vm.ExecuteStopAsync(isAutoStop: false);
            stopVerifiedFinal = stop.VerifiedStopped;
            if (!stopVerifiedFinal && session.HasActiveVm)
            {
                stopVerifiedFinal = await CliCommands.VerifyStopWithTimeoutAsync(c);
            }
        }

        // 汇总 result。
        r.Fields["rejected"] = rejected.ToArray();
        r.Fields["keep_session"] = o.KeepSession;
        r.Fields["tasks"] = tasks.Select(t => TaskResult(c, t)).ToArray();
        r.Fields["session"] = CliCommands.SessionInfo(
            c, o.KeepSession ? null : stopVerifiedFinal, billingActive: o.KeepSession ? (session.HasActiveVm ? true : null) : (stopVerifiedFinal ? false : true));
        var billingNow = session.Billing.Evaluate(fireEvents: false);
        r.Fields["billing"] = EventWriter.BillingFields(billingNow.IsRunning ? billingNow : billingBeforeStop);

        if (!o.KeepSession && !stopVerifiedFinal)
        {
            return r.Fail(ExitCodes.StopUnverified,
                "停止未能核实，云端可能仍在计费！请立即执行 MangaCli stop 或在终端执行 colab stop -s manga。");
        }

        if (o.KeepSession)
        {
            r.Warnings.Add("--keep-session：CLI 退出后无人执行到期自动停机。需要 GUI 接管或执行 MangaCli stop，否则将持续计费。");
        }

        if (interrupted)
        {
            return r.Fail(ExitCodes.Interrupted, o.KeepSession
                ? "已中断，任务已取消，会话按 --keep-session 保留（仍在计费）。"
                : "已中断，已停机核实。");
        }

        if (incompleteTasks > 0 || rejected.Count > 0)
        {
            return r.Fail(ExitCodes.TaskFailed,
                $"完成 {completedTasks} 个，未完成 {incompleteTasks} 个，拒收 {rejected.Count} 个。");
        }

        r.ExitCode = ExitCodes.Ok;
        r.Message = (o.Positionals.Count, o.KeepSession) switch
        {
            (0, true) => "部署冒烟成功，会话按 --keep-session 保留（仍在计费）。",
            (0, false) => "部署冒烟成功，已停机核实。",
            (_, true) => $"全部 {completedTasks} 个任务完成，会话按 --keep-session 保留（仍在计费）。",
            _ => $"全部 {completedTasks} 个任务完成，已停机核实。"
        };
        return r;
    }

    private static async Task WaitForCompletionAsync(CommandContext c)
    {
        var session = c.Session;
        var queue = c.Queue;
        while (!c.Interrupt.IsCancellationRequested)
        {
            var anyActiveOrPending = queue.Items.Any(t =>
                t.Status is TranslationTaskStatus.Pending or TranslationTaskStatus.Preparing
                    or TranslationTaskStatus.Uploading or TranslationTaskStatus.Translating
                    or TranslationTaskStatus.Packing or TranslationTaskStatus.Downloading);
            if (!anyActiveOrPending
                || (queue.IsQueuePaused && !queue.HasActiveTask)
                || !session.HasActiveVm
                || session.State == SessionState.Error)
            {
                return;
            }

            await Task.Delay(1000);
        }
    }

    private static async Task<bool> ShutdownAndVerifyAsync(CommandContext c)
    {
        var session = c.Session;
        if (!session.HasActiveVm)
        {
            return true;
        }

        var res = await c.Vm.ExecuteStopAsync(isAutoStop: false);
        if (res.VerifiedStopped)
        {
            return true;
        }

        if (!session.HasActiveVm)
        {
            return true;
        }

        return await CliCommands.VerifyStopWithTimeoutAsync(c);
    }

    private static Dictionary<string, object?> TaskResult(CommandContext c, TranslationTaskItem t)
    {
        var fields = CliCommands.ToAccepted(t);
        fields["job_id"] = t.JobId;
        fields["source"] = t.SourceZipPath;
        fields["status"] = t.Status;
        fields["completed_pages"] = t.CompletedPages;
        fields["output"] = t.OutputZipPath;
        fields["error"] = t.ErrorSummary;
        if (t.Status == TranslationTaskStatus.Failed)
        {
            fields["log_tail"] = TaskQueue.TailLines(t.RawLog, 40);
        }

        return fields;
    }
}
