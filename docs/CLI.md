# MangaCli 命令行参考

`MangaCli.exe` 是与 KomaFlow GUI 共用同一套编排内核（`SessionStore → VmScripts → IColabCli → BillingTimer → AccountManager → SessionManager → TaskQueue`）的无窗口命令行前端，面向批处理脚本与 Agent 自动化。

---

## 1. 命令一览

| 命令 | 说明 | 持锁 | 联网 |
|---|---|---:|---:|
| `doctor` | 环境自检与诊断快照（`colab.exe`、内置脚本/资产、SSH 密钥） | 否 | 否 |
| `precheck <路径...>` | 离线预检 `.zip` / `.cbz`（损坏/体积/页数/非日文拒收） | 否 | 否 |
| `accounts list [--refresh]` | 列出已配置账号与选号策略；`--refresh` 并发联网查余额 | 否 | 仅 `--refresh` |
| `settings get` | 查看当前配置与解析后的目录路径 | 否 | 否 |
| `settings set <key> <value>` | 修改配置（见下表） | 是 | 否 |
| `status [--probe]` | 查看本地会话状态；`--probe` 联网探测云端真实状态 | 仅 `--probe` | 仅 `--probe` |
| `run [路径...] --minutes N` | 启动/接管会话 → 部署 → 排队翻译 → 停机核实（无路径则仅部署冒烟） | 是 | 是 |
| `stop [--account <id>]` | 停止会话并核实计费结束（支持显式指定账号兜底停机） | 是 | 是 |
| `logs [--tail N] [--crash]` | 查看本地持久化日志或 `crash.log` | 否 | 否 |
| `help` / `version` | 输出帮助文本或版本号（以 `result` 事件输出） | 否 | 否 |

### `settings set` 支持的键

- `engine mtui|mit`
- `account auto|<id>`
- `output-dir <dir>|default`
- `scripts-dir <dir>`（仅旧引擎 `mit` 需要含 `manga.json`）
- `sakura-dict <file>|default`（独立自定义术语表路径）

---

## 2. 常用参数

### 通用参数
- `--data-dir <dir>`：覆盖数据目录（默认 `%APPDATA%\manga_gui`，与 GUI 共用）。
- `--events-file <path>`：将 NDJSON 事件流实时追加写入文件（推荐后台任务轮询使用）。
- `--quiet-logs`：静默 `log` 事件，仅输出状态与结果事件。

### `run` 专用参数
- `--minutes N`（必填，`1–720`）：本次会话计划时长。
- `--gpu l4|a100|a100-hm|g4`：指定目标 GPU 档位（开机后仍按实测显存自动适配方案）。
- `--inpaint lama_large|flux2-klein`：指定擦字重绘模型（`flux2-klein` 仅 40G 及以上生效）。
- `--g4-mode flagship|balanced`：G4（96G）模式下选择 `122B MoE` 旗舰或 `32B+14B` 均衡双模型。
- `--account auto|<id>`：自动按余额轮询选号或指定账号。
- `--engine mtui|mit`：指定翻译引擎（若与云端已部署引擎不同，会在同会话内自动重部署）。
- `--output-dir <dir>`：自定义成品输出目录。
- `--no-auto-stop` / `--keep-session`：翻译完毕后保留云端会话不自动停机（⚠️ 退出后无人计时，需手动 `MangaCli stop` 或由 GUI 接管）。
- `--on-orphan fail|adopt|stop`：发现孤儿会话时的处理策略（默认 `fail`）。
- `--on-expiry auto|drain|stop`：到时无人值守策略（默认 `auto`）。
- `--deploy-retries 0..2`：部署失败重试次数。

---

## 3. NDJSON 输出契约

`stdout` **仅**输出 UTF-8（无 BOM）NDJSON，每行一个 `snake_case` JSON 对象，最后一行恒为 `type: "result"`；诊断堆栈输出至 `stderr`。

| `type` | 关键字段 |
|---|---|
| `log` | `level`, `message` |
| `state` | `session_state`, `has_active_vm`, `badge`, `detail`, `deploy_stage`, `deploy_percent`, `health`, `gpu`, `queue_paused`, `error`, `engine_mismatch` |
| `task` | `job_id`, `file`, `status`, `completed_pages`, `total_pages`, `output`, `error` |
| `billing` | `running`, `elapsed_seconds`, `estimated_cu`, `planned_minutes`, `deadline`, `severity`, `start_time_unknown` |
| `notice` | `title`, `message` |
| `prompt` | `kind`, `answer`（策略自动应答审计记录） |
| `result` | `command`, `exit_code`, `ok`, `message`, `warnings[]`, `session{}`, `billing{}`, `tasks[]`, `rejected[]` |

> **注意**：`run` 结束时 `result.billing` 是停机前快照（记录实际用时与 CU 估算）；是否仍在计费以 `result.session.billing_active`（`false` = 已核实停止，`null` = 未知）为准。

---

## 4. 退出码

| 退出码 | 含义 |
|---:|---|
| `0` | 成功 |
| `1` | 未预期异常（堆栈见 `stderr`） |
| `2` | 用法或输入错误（未触网、未计费） |
| `3` | 环境前置不满足（未申请 GPU） |
| `4` | 互斥锁被占用（GUI 或另一 CLI 实例运行中） |
| `5` | 部署失败（已尽力停机核实） |
| `6` | 任务失败 / 取消 / 部分被拒 |
| **`7`** | **停止未核实——云端可能仍在计费**（最高优先级，请立即执行 `MangaCli stop`） |
| `8` | 检测到孤儿会话且 `--on-orphan fail` |
| `9` | 云端状态探测失败（状态未知） |
| `130` | 被 `Ctrl+C` 中断（已停机核实） |

---

## 5. 与 GUI 互斥及自动化建议

- GUI 与 `MangaCli` 共用全局互斥锁 `Global\MangaGui_ColabL4_SingleInstance`：同一时刻只允许一个前端操作云端会话；只读离线命令不受影响。
- 长耗时任务建议后台执行并加 `--events-file`，轮询事件文件获取进度。
- 强杀 `MangaCli` 进程**不会**停止云端实例，务必通过 `MangaCli stop` 收尾。
- 接管已有会话时会自动执行 `vm_pack.sh clean_stale` 清理上次强杀遗留的云端任务目录，避免并发抢占显存。
