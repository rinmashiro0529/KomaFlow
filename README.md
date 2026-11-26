# KomaFlow 漫画翻译工作台 · v1.7.0

**Windows x64：先安装 .NET 8 Desktop Runtime，再完整解压发行包。** 保持 `MangaGui.exe`、`MangaCli.exe` 和 `assets/` 同级，不要只复制 exe。

### 5 分钟完成本地环境检查

1. 打开「更多 → 环境检查与修复」，确认内置资产完整。检查本身不申请 GPU；五分钟不含下载安装工具、授权和首次云端模型下载。
2. 定位 `colab.exe`，检查 Windows OpenSSH 与 SSH 密钥。已有密钥不要覆盖。
3. 打开「账号管理」，登录或导入 ADC，核对账号及额度。网络异常不等于授权失效；只启用愿意承担计费的账号。
4. 默认 mtui 无需 `manga.json`，可独立选择术语表。mit 请将 `assets/manga.example.json` 复制为配置目录内的 `manga.json` 后按需编辑。
5. 用以下命令预检，不创建计费实例：

```powershell
.\MangaCli.exe doctor
.\MangaCli.exe precheck .\book.cbz
.\MangaCli.exe settings get
.\MangaCli.exe status
```

| GPU 档位 | 显存与用途 | 参考 CU/h（以实际分配为准） |
|---|---|---:|
| L4 | 22.5G，经济档 | 2.33 |
| A100 标准 | 40G 或 80G，自适应 | 8.16 / 10.99 |
| A100 High-RAM | 优先 80G，不保证分配 | 10.99（80G） |
| G4 | 96G，旗舰或均衡模式 | 10.00 |

| 症状 | 下一步 |
|---|---|
| 缺少 assets | 重新解压完整包，不要选择 mit 目录来修复 |
| 授权失效 / 网络异常 | 前者重新授权；后者检查代理后刷新 |
| 503 且已核实未分配 | 改选 A100 标准、L4 或另一个账号 |
| STAGE2 / STAGE5 等待 | 查看最近部署日志；下载期间仍计费 |
| 停机后仍有 `[?]` | 用提示中的账号打开 Colab 网页 → 管理会话，核对并手动停止 |


基于 **WPF + C# + .NET 8 + WPF-UI + CommunityToolkit.Mvvm** 打造的原生 Windows 桌面客户端，自 v1.3.0 起采用 **Windows 11 Fluent 视觉体系**（Mica 窗口、系统强调色、浅/深/跟随系统主题、窗内 `ContentDialog`），围绕「多账号额度轮询与自动选号」、「防忘关持续扣费」、「严格成品校验」与「零侵入全自动串行漫画翻译」设计。

## v1.5.0 变更（多账号切换、额度轮询与跨账号计费保护）

- **账号与凭据隔离**：每个 Google 账号使用独立的 ADC 凭据（`%APPDATA%\manga_gui\accounts\<id>.json`）和独立 `colab-cli` 会话文件（`--config <id>_sessions.json`），通过 `GOOGLE_APPLICATION_CREDENTIALS` 与 `--auth=adc` 按进程隔离，零污染系统默认 `%APPDATA%\gcloud` 与 Keyring。首次升级时自动发现 `%APPDATA%\manga_gui\accounts\*.json`。
- **主界面额度展示与快速切换**：会话卡片常驻「账号与额度栏」，一眼查看各启用账号剩余 CU、当前选用/运行中状态及低余额/活跃实例提示；空闲时点击账号胶囊或标题栏「…→ Colab 账号」即可切换，运行中自动锁定归属账号防错停。
- **按剩余额度自动轮询与安全 Failover**：默认开启「自动按剩余额度轮询」——启动应用、打开「启动服务」弹窗（缓存超 60s）、停机核实后、以及后台每 10 分钟（5–60 分钟可调/可关闭）并发刷新各账号额度；开机时优先挑选空闲（`ActiveAssignments == 0`）且余额最高的账号。若 `colab new` 失败且严格核实 `ExplicitlyAbsent` 且无新增 `[?]` 未命名实例，自动顺延尝试下一候选账号；状态存疑时严禁顺延以防双重计费。
- **跨账号孤儿会话告警与一键停机**：启动探测并发扫描所有启用账号，若非当前会话的其他账号存在残留计费会话，主界面顶部弹出持久 `InfoBar` 告警并支持一键停止；所有计费弹窗、停机失败兜底命令与托盘提示均显式标明归属账号名；停机后自动刷新该账号余额并在日志报告真实扣费差额。
- **应用内账号管理（`AccountsDialog`）**：支持隔离 `gcloud` 浏览器一键登录添加账号（带可复制授权链接，随时取消并清理临时目录与端口）、导入已有 ADC JSON、通过 `whoami` 校验 `colaboratory` 权限并按邮箱短哈希防重复添加、重命名、启用/停用、移除及轮询参数配置。
- **MangaCli 同步支持**：新增 `MangaCli accounts list [--refresh]`、`MangaCli settings set account auto|<id>`、`MangaCli run --account auto|<id>`、`MangaCli stop --account <id>`；离线命令仍由 `OfflineGuardColabCli` 拦截零触网。

## v1.4.0 变更（双引擎）

- **新引擎 mtui（默认）**：云端部署 `manga-translator-ui`（固定提交 `8cd6fee`），OCR 用 manga-ocr、检测用 YOLO OBB、HanLP 语义断行、Lama Large 擦字，气泡内嵌字质量更好。实测（真实 L4）：**约 16 秒/页**（30 页 ≈ 8 分钟；7 页样本 ≈ 20 秒/页），冷部署 ≈ **5 分 40 秒**（STAGE1 75s + STAGE2 2m44s + STAGE5 35s）。`batch_size` 保持 1：实测 batch 3 仅快约 4% 且 VRAM 峰值同为 ~17.4 GB。
- **旧引擎 mit（回退）**：原 `manga-image-translator` 完整保留，在标题栏「…（更多）→ 翻译引擎」菜单里切换；部署中或任务进行中该菜单不可用，设置与云端实际引擎不一致时状态区会提示「需要重试部署」。实测 mit 约 16 秒/页；会话内切换 mtui→mit（重试部署）≈ 6 分钟。
- **仅日文**：mtui 流水线只处理日文原文。文件名（不含扩展名）含 `english` / `eng` / `thai` / `ไทย` 单词的压缩包在新引擎模式下入队即拒收并提示切换旧引擎；`Engage` / `Length` 这类仅含字母序列的文件名不受影响。
- **字体**：默认 **Microsoft YaHei**。改用 Noto Sans CJK SC：编辑 `assets\mtui_config.json` 中 `render.font_family` 为 `"Noto Sans CJK SC"`（改后重新部署生效；需要 VM 已装该字体——默认部署未安装 Noto，可在 `vm_setup.sh` 的 mtui apt 依赖里追加 `fonts-noto-cjk` 后再改）。
- **术语表**：格式为每行一条 `原文->译文 #备注`（`#` 后注释可选；旧 mit 引擎也兼容 TAB 分隔）。两引擎共用 `SAKURA_DICT_PATH=/content/sakura_dict.txt`；如果 `colab_manga\` 目录下存在你自己的 `sakura_dict.txt`，会替代随附空文件上传。随附的 `assets\sakura_dict.txt` 是**空文件**：空术语表会让旧引擎字典加载递归报错，因此云端文件不含任何 `->`/TAB 条目时，两个引擎都改为指向不存在的 `sakura_dict.disabled`（即不挂术语表）。⚠️ 附带空术语表意味着旧引擎不再使用其内置《路人女主》示例人名，属刻意行为改动。
- **许可证提示**：`manga-translator-ui` 以 **GPL-3.0** 发布，部署即在云端引入该许可证下的代码；本 GUI 自身许可证不变。
- **已知副作用**：只含 `…` / `！` / `？` / `♥` 等非日文字符的气泡在日文过滤器下保留原文不译（属接受范围内的取舍）。
- **就绪语义**：`vm_status.sh` 新增 `ENGINE <name> <ready|missing>` 行；GUI 判定就绪要求 `llama :6006` 健康 **且** 引擎标记存在，缺 ENGINE 行一律视为未就绪（mit :8001 依然为可选项）。

## v1.3.0 变更（前端重写）

- **Fluent 化**：窗口改为 Mica `FluentWindow` + `TitleBar`，所有对话框改为窗内 `ContentDialog`，提示条统一为 `InfoBar`，瞬时通知用 `Snackbar`（窗口隐藏/最小化时回落到托盘气泡）。
- **对话框队列与计费安全**：统一的 `DialogService` 保证一次只显示一个对话框；到时处理与关闭保护属于紧急对话框，可打断普通对话框；任何被打断/取消的对话框都回落到不增加计费的默认值。到时对话框的无人值守截止时刻以「请求产生时」为准，排队不会推迟自动停止。
- **系统集成**：任务栏进度（部署中/翻译中显示进度、队列暂停、异常红色指示）；快捷键 Ctrl+O 添加漫画、F5 复查云端会话、Delete 移除选中任务、Enter/双击打开位置或日志、日志列表 Ctrl+C 复制选中行；任务行右键菜单（打开所在位置/重试/查看失败日志/移除）。
- **无障碍**：任务行与所有无文字按钮带自动化名称，进度条具名，提示条按级别使用 `Assertive`/`Polite` LiveSetting。
- 业务逻辑（计费保护、会话管理、任务队列、停机与到时处理语义）与 1.2.0 一致，未改动 `Services\`（除 `ThemeService` 内部迁移到 WPF-UI 主题 API）与 `Models\`。

## 核心能力（v1.2.0）

- **计费真值与原子停机**：`colab sessions` 三态解析（拒绝把空输出/报错当成已停机）；`colab new` 前悲观武装计费保护并在超时/异常后自动复查；停机立即抢占取消进行中的部署（epoch 隔离 + single-flight 防重入）。
- **任务隔离与成品校验（PROTO 2）**：每次任务独立运行于 `/content/jobs/<job_id>/`，必须等待进程退出且 `exit_code == 0` 才打包；下载后校验字节数、SHA256、ZIP 结构、页数与图片魔数，未通过绝不落盘。
- **离线预检与自然页序**：支持 `.zip` 与 `.cbz`，入队即离线检查损坏包/体积/页数预算（先验货再花 GPU），自动按自然页序重排（`1, 2, 10` 不错乱），支持自定义输出目录与单任务移除/重试。
- **CLI 安全与诊断**：自动解包 `colab.cmd` 垫片直连 `colab.exe`（绕过 `cmd.exe` 元字符注入风险），日志查看支持一键脱敏复制；右上角「诊断」窗口集中展示版本、CLI 启动模式、脚本来源与内置脚本 SHA256。

## v1.2.0 变更（2026-11-22 审计修复）

- **会话识别对齐真实 CLI**：按 `[name] endpoint | Hardware: ...` 逐行解析 `colab sessions`，孤儿会话探测与崩溃恢复在 google-colab-cli 0.8.0 下可用；`[?]` 未命名实例会单独告警。
- **无人值守也会停**：到时对话框带倒计时，无人应答时执行安全默认动作（翻译中 5 分钟后转收尾，空闲/异常 2 分钟后停止）；自动停止核实失败按 1/2/5 分钟重试；仅提醒模式每 10 分钟再提醒；收尾模式下队列被暂停会直接停止计费。
- **不再中途打断 `colab new`**：停止会等待创建请求返回后再按名称停止，避免留下无法按名称终止的实例。
- **大文件分片传输**：超过 48 MiB 的压缩包分片上传、云端校验合并；成品同样分片下载后本地校验。任务按停滞时间（20 分钟无新页）判超时。
- **部分成品与页名还原**：任务中途失败时，已完成的页面另存为 `*_translated_partial`（任务仍记为失败）；重打包过的压缩包在成品中还原原目录与文件名。
- **其他**：申请 GPU 前检查 SSH 前置条件；日志脱敏覆盖 Google 凭据、邮箱与正斜杠路径；有活动会话时界面异常不再直接退出进程。

以上改动均由替身 CLI 的自动化测试覆盖，**尚未在真实 Colab 上联调**；`assets\vm_setup.sh` 与分片传输需要一次真实部署验证。

## 前置依赖（首次运行自检）

1. **Windows 10 / 11 (x64)**，已安装 `.NET 8 Desktop Runtime`。
2. **google-colab-cli**（自动优先定位 `colab.exe`，或从 `%USERPROFILE%\.local\bin\colab.cmd` 解包真实 `colab.exe`），已完成 ADC 认证。
3. **本机 SSH**：PATH 中有 `ssh.exe`，且 `~/.ssh` 下有 `id_ed25519.pub` 或 `id_ecdsa.pub`（Colab 不接受 RSA 密钥）。缺失时启动自检会阻止申请 GPU。
4. **翻译配置目录**（默认自动向上定位到 含 `manga.json` 的上级目录，或在界面中手动指定，用户指定具有最高优先级），只读取：
   - `manga.json`（仅旧引擎 mit 需要；新引擎 mtui 使用随附的 `assets\mtui_config.json`）
   - `sakura_dict.txt`（可选：存在时替代随附空术语表，两引擎共用）
   （部署、翻译执行、进度轮询、打包与清理均由 GUI 内置的 `assets\vm_setup.sh` / `vm_job.sh` / `vm_status.sh` / `vm_pack.sh` 完成。`assets\vm_setup.sh` 是上级 `vm_setup.sh` 的加固版，上级原脚本不改动，命令行流程不受影响。）

## 构建、测试与发布命令

> 已在 `global.json` 中固定 `.NET 8` SDK。若系统 PATH 未包含 .NET 8 SDK，请使用 `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`。

```powershell
# 进入开发目录
# 在仓库根目录运行

# 1. 构建解决方案（0 警告 0 错误）
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build MangaGui.sln -c Release

# 2. 运行全部单元测试（Mock IColabCli，零 Colab 计费）
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test MangaGui.sln -c Release

# 3. 发布启动程序及配套资产（输出到 publish/）
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" publish src\MangaGui\MangaGui.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

完整发布前门禁清单见 [`RELEASE_CHECKLIST.md`](RELEASE_CHECKLIST.md)。

## 命令行 MangaCli

`src\MangaCli` 是与 GUI 共用同一套编排对象（`SessionStore → VmScripts → IColabCli → BillingTimer → SessionManager → TaskQueue → MainViewModel`）的无窗口命令行前端，面向脚本化与 agent 自动化。它复用 GUI 的会话、部署、任务队列、计费、脚本与 ZIP 预检逻辑，行为与 GUI 一致。

### 命令表

| 命令 | 说明 | 是否持锁 | 是否联网 |
|---|---|---|---|
| `doctor` | 环境自检 + 诊断快照（colab 定位、脚本/资产目录、SSH 前置） | 否 | 否 |
| `precheck <路径...>` | 离线预检漫画 `.zip`/`.cbz`（体积/页数/损坏/mtui 非日文文件名拒收），不部署不计费 | 否 | 否 |
| `accounts list [--refresh]` | 列出已配置 Colab 账号与选号策略；加 `--refresh` 并发联网查各号剩余 CU | 否 | 仅 `--refresh` |
| `settings get` | 查看设置与解析后的脚本/资产目录 | 否 | 否 |
| `settings set engine mtui\|mit` / `account auto\|<id>` / `output-dir <dir\|default>` / `scripts-dir <dir>` | 修改设置 | 是 | 否 |
| `status [--probe]` | 会话与计费状态；无 `--probe` 仅读本地记录，`--probe` 联网探测（不启动不停止） | 仅 `--probe` | 仅 `--probe` |
| `run [路径...] --minutes N` | 部署 → 排队翻译 → 停机核实 全流程；无路径 = 仅部署冒烟（支持 `--account auto\|<id>`） | 是 | 是 |
| `stop [--account <id>]` | 停止会话并核实计费结束（含孤儿会话处理；可显式指定某账号兜底停机） | 是 | 是 |
| `logs [--tail N] [--crash]` | 查看本地持久化日志 / crash.log | 否 | 否 |
| `help` / `--help`、`--version` | 帮助与版本（以 result 事件输出） | 否 | 否 |

### 通用参数

- `--data-dir <dir>`：数据目录（默认 `%APPDATA%\manga_gui`，与 GUI 相同）。
- `--events-file <path>`：把 NDJSON 事件流同时追加写入该文件（供后台轮询）。
- `--quiet-logs`：不输出 `log` 类型事件。

`run` 选项：`--minutes N`（必填，1–720）、`--no-auto-stop`、`--keep-session`、`--engine mtui|mit`、`--output-dir`、`--on-orphan fail|adopt|stop`（默认 `fail`）、`--on-expiry auto|drain|stop`（默认 `auto`，与 GUI 无人值守语义一致）、`--scripts-dir`、`--deploy-retries 0..2`。

### 引擎行为（CLI）

- **mtui 只支持日文原文**（`precheck` 会拒收文件名含 `english`/`eng`/`thai`/`ไทย` 的输入）；`mit` 为旧引擎回退。
- `--scripts-dir` / `settings set scripts-dir`：仅旧引擎 **mit** 需要 `manga.json`；mtui 下该目录只用于放置自定义 `sakura_dict.txt` 术语表（`set` 时目录仍需含 `manga.json`）。`run --scripts-dir <dir>` 在部署前校验目录含 `manga.json` 并**写入设置**（与界面选择目录一致）；目录不合规直接退出码 2，不产生任何云端调用。
- **会话内切换引擎**：`run --engine X` 遇到云端引擎与 X 不一致时，会在当前会话上重新部署为 X（不新建 runtime、计费开始时间不变），成功后才跑任务；失败按部署失败处理（停机核实，退出码 5）。不带 `--engine` 且云端引擎与设置不同时，按云端引擎翻译并给出 warning。
- 云端报 `ENGINE none missing`（部署中途/损坏）时按未就绪处理：恢复路径自动按配置引擎重试部署，`--engine` 不会被误拒。

### NDJSON 输出契约

stdout **只** 输出 NDJSON：每行一个 JSON 对象（UTF-8 无 BOM、snake_case），最后一行恒为 `type=result`；诊断信息走 stderr。事件类型：

| `type` | 关键字段 |
|---|---|
| `log` | `level`, `message` |
| `state` | `session_state`, `has_active_vm`, `badge`, `detail`, `deploy_stage`, `deploy_percent`, `health`, `gpu`, `queue_paused`, `error`, `engine_mismatch`（值不变不重复发送） |
| `task` | `job_id`, `file`, `status`, `completed_pages`, `total_pages`, `output`, `error`（去重） |
| `billing` | `running`, `elapsed_seconds`, `estimated_cu`, `planned_minutes`, `deadline`, `severity`, `start_time_unknown` |
| `notice` | `title`, `message`（系统通知/五分钟预警） |
| `prompt` | `kind`, `answer`（启动/孤儿/到期等对话框的策略自动应答，可审计） |
| `result` | `command`, `exit_code`, `ok`, `message`, `warnings[]`，另含 `session{...}`、`billing{...}`、`tasks[]`、`rejected[]` 等 |

`result.session` 字段：`state`, `has_active_vm`, `engine_configured`, `engine_cloud`, `engine_mismatch`, `stop_verified`, `billing_active`(true/false/null=未知）, `unnamed_assignment_warning`, `persistence_warning`。

注意：`run` 的 result 中 `billing` 是**停机前的快照**（表示本次会话的实际用时与 CU 估算，`running=true`、`elapsed_seconds`/`estimated_cu` 非零）；`session.billing_active` 才是**当前**是否仍在计费的最终结论（停机核实后为 `false`）。

### 退出码

| 码 | 含义 |
|---|---|
| 0 | 成功 |
| 1 | 未预期异常（堆栈写 stderr） |
| 2 | 用法/输入错误（未做任何云端调用） |
| 3 | 环境前置不满足（未申请 GPU） |
| 4 | 互斥锁被占用（GUI 或另一 CLI 在运行） |
| 5 | 部署失败（已尽力停机核实） |
| 6 | 任务失败/取消/部分被拒 |
| **7** | **停止未核实——云端可能仍在计费**（最高优先级，覆盖其他码） |
| 8 | 检测到孤儿会话且 `--on-orphan fail` |
| 9 | 云端状态探测失败（状态未知） |
| 130 | 被中断（已停机核实） |

### Agent 使用规范

- 长任务（`run`、`stop`）**必须后台运行并加 `--events-file`**，轮询该文件而不是阻塞等待 stdout。
- 看到退出码 **7** 立即执行 `MangaCli stop` 重试停机核实——它意味着云端可能仍在计费。
- 看到退出码 **8**（孤儿会话 + fail）时按情况改用 `--on-orphan adopt|stop` 或先 `MangaCli stop`。
- `run` 恢复已有会话（本地 `session.json` 或 `--on-orphan adopt`）时**沿用原会话的计划时长**，本次 `--minutes` 不生效，result 的 warnings 会注明原时长与预计到期时间。
- `status --probe`/`stop`/`run` 恢复会话后，若原计划已到期且开启了自动停机，会触发与打开 GUI 时相同的到期自动停机逻辑（收尾或立即停止由 `--on-expiry` 决定）。
- `--keep-session` 保留会话但 **CLI 退出后无人执行到期自动停机**，需要 GUI 接管或稍后 `MangaCli stop`，否则持续计费。典型用法：先 `MangaCli run --minutes 30 --keep-session` 冒烟部署，随后 `MangaCli run book.zip --minutes 60` 复用同一会话直接翻译（不重新部署），最后 `MangaCli stop` 收尾。
- **Ctrl+C / CTRL_C_EVENT**：v1.4 起 MangaCli 启动时会显式恢复 Ctrl+C 处理（非交互启动器可能给子进程继承「忽略 Ctrl+C」属性）。第一次 SIGINT 优雅退出（取消任务并停机核实，或按 `--keep-session` 保留会话）；第二次强制退出并提示 `MangaCli stop`。更稳妥的做法仍是让 `run` 自行跑完，或配合 `--keep-session` + `MangaCli stop` 收尾。
- **会话接管会清理云端遗留任务**：`run`（或 GUI）恢复/接管会话时，在健康检查前自动执行 `vm_pack.sh clean_stale`——杀掉上次前端被强杀后残留在 GPU 上的 `manga_translator local` 任务并清理 job 目录（其结果无法回收），避免与新任务并发。该步骤失败仅记 Warning，不阻断接管。
- **强杀 MangaCli 进程无法停止云端会话**（与强杀 GUI 相同），此时必须以 `MangaCli stop` 收尾。

### 与 GUI 互斥

GUI 与 MangaCli 共用互斥锁 `Global\MangaGui_ColabL4_SingleInstance` 与同一数据目录：同一时间只允许一个前端操作云端会话。持锁命令（`run`、`stop`、`status --probe`、`settings set`）在 GUI 运行中会以退出码 4 拒绝；离线命令不受影响，`doctor`/`status` 会报告 `lock_held`。

## 目录结构

```text
gui\
├─ global.json
├─ MangaGui.sln
├─ README.md
├─ RELEASE_CHECKLIST.md
├─ OPTIMIZATION_PLAN.md
├─ assets\
│  ├─ vm_setup.sh         # PROTO 2 引擎感知部署（--detach <engine>，STAGE1-5 + ENGINE 标记）
│  ├─ vm_job.sh           # PROTO 2 独立任务启动器（/content/jobs/<job_id>/ + pid + 原子 exit_code + out_path）
│  ├─ vm_status.sh        # PROTO 2 合并健康检查 + GPU 显存 + ENGINE 行 + 单任务页数/存活/退出码查询
│  ├─ vm_pack.sh          # PROTO 2 严格打包（输出大小+SHA256+页数）、单任务停止、日志抓取与清理
│  ├─ mtui_patch.py       # mtui 源码补丁（小写假名修复 + MANGA_JA_ONLY 日文过滤）
│  ├─ mtui_config.json    # mtui 翻译配置（sakura/mocr/YOLO OBB/lama_large/Microsoft YaHei）
│  └─ sakura_dict.txt     # 空术语表占位（用户 colab_manga\sakura_dict.txt 优先）
├─ src\MangaGui\
│  ├─ Models\             # SessionState, BillingConstants, SessionRecord, ThemePreference, VmStatusSnapshot, TranslationTaskItem
│  ├─ Services\           # IColabCli, ColabCli, LogSanitizer, SessionManager, TaskQueue, BillingTimer, VmScripts, ZipPreprocessor, SessionStore, TrayService, ThemeService
│  ├─ ViewModels\         # MainViewModel, StartSessionDialogViewModel, TimeExpiredDialogViewModel, IUiTimer
│  ├─ Views\              # MainWindow + DialogService/AppContentDialog + 启动/确认/到时/关闭保护/孤儿会话/脱敏日志/诊断对话框
│  └─ Themes\             # （已清空——主题由 WPF-UI ThemesDictionary 提供）
├─ src\MangaCli\          # 无窗口命令行前端（CliApp 入口、NDJSON 事件、HeadlessHost 复用 GUI 编排对象）
└─ tests\MangaGui.Tests\  # 197 项 xUnit 自动化回归测试（含多账号/CLI/UI 渲染测试；另有 tests\scripts\ 下 T1 Python 资产测试与 T2 Bash 脚本测试）
```

## 主题切换

标题栏右侧「…」（更多）菜单内可单选 **跟随系统（默认，自动跟随 Windows 深浅色切换） / 浅色 / 深色**，偏好持久化保存在 `%APPDATA%\manga_gui\settings.json`；强调色跟随系统。

## 快捷键

| 键 | 动作 |
|---|---|
| Ctrl+O | 添加漫画压缩包 |
| F5 | 复查云端会话 |
| Delete | 移除选中的待处理任务（焦点在任务列表时） |
| Enter / 双击 | 已完成任务打开所在位置；失败任务查看日志（焦点在任务列表时） |
| Ctrl+C | 复制选中的日志行（焦点在日志列表时） |
| Esc | 关闭当前对话框 |

对话框显示期间，前四个快捷键不生效。
