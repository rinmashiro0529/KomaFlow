# MangaGui 项目优化与演进计划书

- **制定日期**：2026-11-20
- **输入依据**：
  - [功能、计费与安全审计报告](Z:/控制台/colab_manga/gui/AUDIT_REPORT.md)（A-01 ~ A-15，共 15 项）
  - [可拓展性与可适用性审计报告](Z:/控制台/colab_manga/gui/EXTENSIBILITY_AUDIT.md)（E-01 ~ E-06 及衍生方向）
  - [原需求文档](Z:/控制台/colab_manga/GUI_REQUIREMENTS.md)
  - 本机环境实测核验（`colab.cmd` 批处理封装结构及 `colab sessions --help` 参数支持情况）

---

## 一、核心诊断与演进原则

### 1.1 当前最核心的风险
项目已具备清晰的模块化雏形（会话管理、墙钟计费、CLI 串行与 429 退避、队列与双主题 UI），但两份审计揭示了两类必须正视的问题：
1. **核心承诺存在缺口（必须优先修复）**：在网络超时、空输出、本地落盘失败、并发状态回写等异常路径下，界面可能把「未知」说成「已停止计费」，或让活动 VM 失去关闭保护；在任务侧，仅凭文件数和非零字节判断成功，可能把残缺/截断包误报为成品。
2. **局部边界过早耦合（适合顺手收口）**：文件输入、输出命名、运行脚本协议与任务调度分散在多处；本机 `colab.cmd` 实测包含 `echo %* | findstr /C:"--auth"`，导致下载路径等任意参数都暴露在 `cmd.exe` 解析风险下；`colab sessions` 不支持 `--json`，必须依赖严格三态解析。

### 1.2 演进原则与取舍底线
1. **先守住原承诺，再做拓展**：P1 计费可信与产物完整是下一版发布硬门槛，未修完前不把旧逻辑复制到新入口。
2. **先内聚，后抽象**：只有真实第二运行环境成立时才做多后端适配器；现阶段先在 `gui/` 内把「本地文件包处理」「Colab 运行协议」「生命周期协调」三个边界收干净。
3. **非侵入优先**：尽量把新脚本协议放在 GUI 自有目录 [assets/](Z:/控制台/colab_manga/gui/assets)，保持上级目录的 `run_translate.sh` / `wait_translate.sh` / `deploy.py` / `submit.py` 不受破坏；确实涉及加固上级 `vm_setup.sh` 的改动单独列为需确认项。
4. **明确不做清单（当前阶段）**：不做插件平台、关系型数据库、Web/移动端、多用户并发共享、GPU 自动降级、PDF/CBR 解码器或内嵌看图预览器。

---

## 二、分阶段路线图与全量追踪矩阵

| 阶段 | 目标与发布门槛 | 覆盖审计项 | 核心产出 |
|---|---|---|---|
| **Phase 1（P1 · 必做）** | **计费真相与生命周期原子协调**（下一次发布的硬门槛） | A-01, A-02, A-03 | 三态会话解析、悲观计费保护、容错落盘、单一生命周期协调器 + 会话代次取消机制、4 项计费竞态自动化测试 |
| **Phase 2（P1 · 必做）** | **产物完整性、任务隔离与输入预算**（下一次发布的硬门槛） | A-04, A-05, A-06, A-08 | 任务级云端隔离脚本 `vm_job.sh`、退出码 + 摘要/结构校验、本地预算与统一校验、自然页序修复、全路径清理 |
| **Phase 3（P2 · 稳健性）** | **监控准确、到时显式、安全边界与局部恢复** | A-07, A-09, A-10, A-11, A-12, A-13 | 健康去重与失联态、显式到时策略、脚本来源优先级与启动阻断、`colab.exe` 直连/安全下载、`mit API` 门槛解耦、任务重试/移除 |
| **Phase 4（P3 + 首期拓展）** | **可追溯发布、模块收口与高性价比能力拓展** | A-14, A-15, E-01, E-03(收口) | 版本与资产清单诊断页、单点 CU 常量、桌面人工验收清单、`.cbz` + 可选输出目录 + 离线预检（「先验货、再付费」） |
| **Phase 5（按需择一）** | **按高频痛点做第二批拓展** | E-02 / E-04（二选一）；E-05 / E-06（条件触发） | 路线 A：本地任务持久化与中断续查/续下载；路线 B：任务级预设快照与小样试译 |

---

## 三、分阶段详细实施方案

### Phase 1：计费状态可信与生命周期统一协调（P1）

> **目标**：无论发生 CLI 超时、stdout 空白、磁盘写/删失败、还是部署与停机交错，UI 与关闭保护永远不会把「仍在计费或未知」误判为「已安全停机」。

#### 1.1 A-01：三态会话存在性解析
- **现状核实**：本机 `colab sessions --help` 仅支持 `-h/--help`，无 `--json` 结构化输出。当前 [VmScripts.HasActiveSession](Z:/控制台/colab_manga/gui/src/MangaGui/Services/VmScripts.cs:156) 返回 `bool`，将空 stdout 和无法识别文本均视作 `false`。
- **改造方案**：
  - 新增 `enum SessionPresence { Present, ExplicitlyAbsent, Unknown }` 与 `SessionQueryResult(SessionPresence Presence, string Reason, string RawOutput)`。
  - 解析规则：
    1. 退出码非 0 或超时 → `Unknown`。
    2. 匹配已知空列表文案（如 `No active sessions`）→ `ExplicitlyAbsent`。
    3. 识别到有效表格/列表结构：含目标会话名 `manga` → `Present`；明确列出其他会话且不含 `manga` → `ExplicitlyAbsent`。
    4. 空 stdout、仅含杂项警告/异常文本或格式不可识别 → `Unknown`。
  - 消费规则：
    - **停止核实**：仅 `ExplicitlyAbsent` 允许返回 `VerifiedStopped = true`、停止计时并清除本地记录；`Unknown` 保留 `HasActiveVm = true`、保留 `session.json`、进入 `SessionState.Unknown`（或带警示的 `Error`），展示「状态未知，可能仍在计费」及「立即复查 / 手动停止命令」。
    - **启动探测**：`Unknown` 时若本地有记录则沿用计时并保持关闭保护；若本地无记录也提示「会话状态未知」并提供「重新探测」按钮，不静默放行。

#### 1.2 A-02：创建不确定态与本地 I/O 容错解耦
- **现状问题**：`new` 超时/异常未复查即置 `HasActiveVm = false`；[SessionStore](Z:/控制台/colab_manga/gui/src/MangaGui/Services/SessionStore.cs:31) 写失败抛异常阻断内存保护；停机后先删盘再更新内存，删盘失败卡死停机态。
- **改造方案**：
  - **状态细化**：新增 `Creating` / `BillingUnknown` 语义标志；发起 `colab new` 前即启用关闭保护（防止请求发出后用户直接关窗）。
  - **超时自动复查**：`new` 超时或返回非确定性错误时，在总时限（如 25 秒、最多 2 次）内调用 `sessions` 复查；若为 `Present` 则无缝转入已创建保护，若为 `Unknown` 则进入「创建结果未知，可能已计费」并保留停机与复查入口。
  - **内存优先 + 落盘降级**：远端确认存在后**先**置 `HasActiveVm = true` 并启动 `BillingTimer`，再 `TrySaveSession()`；写盘失败只弹出独立持久化告警横幅，不回滚内存保护与停机按钮。
  - **停机与删盘解耦**：远端确认为 `ExplicitlyAbsent` 后**先**停止计时并置 `Idle / HasActiveVm = false`，再 `TryDeleteSession()`；删盘失败单独提示手动清理路径，不阻断停机成功态。
  - **结构化读盘**：`SessionStore.LoadSessionResult()` 区分 `Found` / `Missing` / `Corrupted` / `AccessDenied`，损坏或无权限时在日志与横幅明确提示，不静默当无记录。

#### 1.3 A-03：单一生命周期协调器与会话代次（Generation）
- **现状问题**：启动探测、部署、手动停机、队列自动停机与健康检查均可并发改写 `SessionManager`；停机前的 `kill_translate` 可能排在 30 分钟 `vm_setup.sh` 的 SSH 锁后面。
- **改造方案**：
  - **操作代次与取消令牌**：为每次会话生命周期维护递增的 `long _operationEpoch` 和当前活动操作的 `CancellationTokenSource`（探测/部署/健康检查均绑定该 CTS）。
  - **停机最高优先级**：触发停机时，先递增 `_operationEpoch` 并立即 `Cancel()` 正在进行的部署/探测/健康检查子进程（利用 `ColabCli.TryKillTree` 立刻释放 SSH 锁），再执行停机；旧操作即便滞后返回，因其 `epoch` 过期，其状态回写一律被忽略。
  - **并发停机合并（Single-Flight）**：同一会话已有停机任务在执行时，后续手动/自动停机复用同一 `Task<StopResult>`，避免重复打 `colab stop`。
  - **移除停机前不必要阻塞**：整台 VM 即将被 `colab stop` 销毁，原则上无需等待 SSH `kill_translate`；如保留优雅终止，仅在「当前确有运行中翻译任务且未在部署」时执行，且把等待 SSH 锁 + 执行 + 重试合并进硬性总时限（≤ 5 秒），超时立即跳过并执行 `colab stop`。
  - **启动探测门禁**：应用启动探测完成前，禁用「启动服务」等冲突指令，仅允许取消/退出（探测中无活动会话）。

#### 1.4 Phase 1 验收测试
- 在 [MangaGui.Tests](Z:/控制台/colab_manga/gui/tests/MangaGui.Tests) 中新增并固化 4 类确定性回归测试（不调用真实 Colab）：
  1. `StopSession` 遇到 `stop` 失败 + `sessions` 返回空 stdout / 杂项文本 / 非零退出 → 必须 `VerifiedStopped == false`、`HasActiveVm == true`、保留 `session.json`。
  2. `StartOrDeploy` 遇到 `new` 超时但远端实际已建（复查 `sessions` 返回 `Present` 或 `Unknown`）→ 必须保持关闭保护与停机入口；本地目录不可写（`IOException`）→ 内存计时与关闭保护仍生效；停机后删盘失败 → 状态正确归 `Idle` 且返回告警。
  3. 并发闸门测试：探测与启动重叠、部署未返回即触发停机（旧部署随后失败返回不得覆盖 `Idle`）、队列收尾自动停机与手动停机同时触发合并为一次停机、部署占用 SSH 锁时停机在短时间内抢占不被 30 分钟阻塞。

---

### Phase 2：产物完整性、任务隔离与输入预算（P1）

> **目标**：杜绝「残缺/空/写到一半的 ZIP 被标记完成」「乱序页」「大包/畸形包撑爆磁盘」「失败或取消残留云端/本地垃圾或污染下一任务」。

#### 2.1 任务级云端工作目录与协议契约（A-04, A-05 云端侧, A-06）
- **设计策略（不修改上级 `run_translate.sh`）**：
  - 在 [gui/assets/](Z:/控制台/colab_manga/gui/assets) 新增 `vm_job.sh`（并同步升级 `vm_status.sh` 与 `vm_pack.sh`），GUI 队列统一使用任务专属目录 `/content/jobs/<job_id>/`：
    - `input.zip`、`src/`、`out/`、`translate.log`、`pid`、`exit_code`、`out.zip`
  - **`vm_job.sh start <job_dir> <expected_count> [config_path]`**：
    - 启用 `set -euo pipefail`，在隔离目录 `$JOB_DIR/src` 中执行 `unzip -q -o`；解压失败立即非零退出。
    - 统计解压后的有效图片文件数（仅常见图片扩展名，排除隐藏文件），与 `<expected_count>` 不一致时立即报错退出，绝不启动翻译。
    - 用子 shell 封装后台进程，把**真实进程 PID** 写入 `$JOB_DIR/pid`，进程退出时原子写入 `$JOB_DIR/exit_code`（先写 `.tmp` 再 `mv`），输出仅写入 `$JOB_DIR/translate.log`。
  - **`vm_status.sh [job_dir]`**：
    - 携带 `job_dir` 时，输出带协议版本号 `PROTO 2`：
      - `DONE_COUNT`：仅统计 `$JOB_DIR/out` 下的常规图片文件（排除隐藏文件与临时后缀）。
      - `JOB_ALIVE`：按 `$JOB_DIR/pid` 精确检测该任务进程（`kill -0 "$PID"`），不再全局 `pgrep -fc`。
      - `JOB_EXIT_CODE`：若 `$JOB_DIR/exit_code` 存在则输出具体退出码（否则 `RUNNING`）。
      - `LAST_SAVING`：仅从 `$JOB_DIR/translate.log` 读取。
  - **`vm_pack.sh pack <job_dir>`**：
    - 打包前检查 `$JOB_DIR/exit_code` 必须为 `0` 且无存活翻译进程，杜绝边写边打包。
    - 仅打包有效图片文件，完成后输出 `PACK_OK <bytes> <sha256> <image_count>`。
  - **`vm_pack.sh stop_job <job_dir>` & `clean <job_dir>`**：
    - 仅终止指定 `$JOB_DIR/pid` 进程树并确认已退出；清理严格白名单校验 `[[ "$JOB_DIR" == /content/jobs/job_* ]]`。

#### 2.2 本地输入预算、统一校验与自然页序（A-05, A-08, E-01 底座）
- **统一模块**：将 [ZipPreprocessor.cs](Z:/控制台/colab_manga/gui/src/MangaGui/Services/ZipPreprocessor.cs) 演进为统一的 `MangaPackageService`（为 Phase 4 `.cbz` / 可选输出目录 / 离线预检直接复用）。
- **强制资源预算（默认阈值可通过构造参数/配置覆写，便于小阈值单测）**：
  - 源包体积上限（如 500 MB）、最大页数（如 1000 页）、解压后总字节上限（如 1.5 GB，防 ZIP 炸弹）、单页解压上限（如 30 MB）、最大路径深度与非法路径（`..` / 绝对路径）拦截。
  - 魔数/头部轻量校验（JPEG `FF D8 FF`、PNG `89 50 4E 47`、WebP `RIFF..WEBP`、BMP `BM`、TIFF），避免把伪装成 `.jpg` 的损坏/非图片文件传上云端。
- **双路径一致校验**：无论是否平铺，都执行相同的条目扫描、重名检测、隐藏文件/非普通条目过滤和魔数校验；重打包流式拷贝使用 `CopyToAsync` 支持 `CancellationToken` 及时中断并清理半成品临时 ZIP。
- **自然排序修复（A-08）**：
  - 实现 `NaturalPathComparer`：按 `/` 拆目录段，每段内将文本与连续数字拆块，数字块按数值比较（`1 < 2 < 10`），文本块不区分大小写比较，原始完整路径作稳定次排序键。

#### 2.3 本地成品校验与全路径清理（A-04, A-06）
- **下载原子性与完整性校验**：
  - 使用独占临时文件名（避免两处竞争同一 `.downloading`），且放到 `try ... finally` 统一清理。
  - 若走 `.cmd` 回退或目标目录含批处理敏感字符，先下载到 `%TEMP%\manga_gui_dl\<job_id>.zip`（纯 ASCII 路径），校验通过后再原子移动（跨卷则复制+改名+删临时文件）到最终唯一路径（保持不覆盖已有成品）。
  - **强校验清单**：
    1. 文件大小严格匹配云端 `PACK_OK <bytes>`；
    2. 本地计算 SHA256 与云端 `PACK_OK <sha256>` 一致；
    3. `ZipFile.OpenRead` 可正常打开且非空；
    4. ZIP 内有效图片数与预期页数（及 `PACK_OK <image_count>`）一致且每条图片非零字节。
  - 任一项不通过 → 删除临时文件，标记任务失败并保留错误详情与日志。
- **云端清理与队列隔离（A-06）**：
  - 任务成功、失败、取消均在 `finally` 中调用 `vm_pack.sh clean <job_dir>`（检查退出码并记日志，失败不掩盖主结果但记录告警）。
  - 超时或取消后先调用 `vm_pack.sh stop_job <job_dir>` 确认旧任务进程已退出；若无法确认旧进程退出，**暂停队列并提示用户**，绝不让下一任务在同一 GPU 上并发争抢显存或串日志。

#### 2.4 Phase 2 验收测试
- 升级 [TaskQueueTests.cs](Z:/控制台/colab_manga/gui/tests/MangaGui.Tests/TaskQueueTests.cs) 夹具，使用真实多页图片 ZIP 与模拟 SHA256/退出码，并新增负向用例：
  - 写中未退出（`exit_code` 缺失或 `JOB_ALIVE=1`）禁止打包；
  - 缺页、多余非图片文件、截断 ZIP、SHA256 不匹配、22 字节空 ZIP 均不得进入 `Completed`；
  - 小阈值预算触发（超页数、超单页、超总解压字节、坏图片头、重名/隐藏条目、`CancellationToken` 取消）零上传且临时文件已清；
  - 多话子目录 `1.jpg / 2.jpg / 10.jpg` 拍平后断言字节内容严格按 `1 → 2 → 10` 顺序；
  - 上传后失败、下载取消、旧任务停不掉时暂停队列。

---

### Phase 3：稳定性、安全边界与交互细节打磨（P2）

#### 3.1 A-07：健康检查去重、失联态与真实墙钟超时
- [MainViewModel.cs](Z:/控制台/colab_manga/gui/src/MangaGui/ViewModels/MainViewModel.cs:107) 空闲 15 秒轮询加入 `Interlocked` in-flight 标志与专属 CTS；切入忙碌、部署或停机时立即取消挂起中的空闲查询。
- `VmStatusSnapshot` 区分「成功获取」与「查询失联」；连续失败（如 2 次）将服务指示灯切换为灰色/琥珀「失联 · 上次检查 HH:mm:ss」，不再保留陈旧绿灯；连续失联达阈值（如 3 次）触发一次轻量 `sessions` 复查以发现外部停机。
- 任务轮询改用真实墙钟时长上限（如 `Stopwatch` / `UtcNow`，默认 90 分钟）+ 连续轮询失败阈值（如连续 5 次 SSH 失败），替代固定 `480` 次计数。

#### 3.2 A-09：到时策略显式化与防静默续费
- [TimeExpiredDialog.xaml.cs](Z:/控制台/colab_manga/gui/src/MangaGui/Views/TimeExpiredDialog.xaml.cs:8) 默认 `SelectedAction` 改为 `None`（禁止默认延长），检查 `ShowDialog() == true`；用户点右上角 `✕` 关闭弹窗时需明确处理（如弹窗确认或保持倒计时警示/按预设超时策略执行，绝不静默 `+30 分钟`）。
- 细分到时场景：
  - `Ready` 且队列空 + 开启自动停止 → 直接停机；
  - `Deploying` / `Error` → 弹窗明确告知「部署超时 / 服务异常仍在计费」，给出「立即停止计费（推荐）」与「继续等待并延长 30 分钟」；
  - `Busy` / 队列非空 → 给出「翻完当前队列再停」「立即停止」「延长 30 分钟」。
- 左侧计费卡片明确展示：自动停机状态标签（如 `到时自动停 · 预计 05:30` 或 `仅提醒 · 不自动停`）。

#### 3.3 A-10：脚本来源优先级与启动前强阻断
- 修复 [VmScripts.FindExistingScriptsDirectory](Z:/控制台/colab_manga/gui/src/MangaGui/Services/VmScripts.cs:289) 优先级：
  1. 用户显式在 `settings.json` 中选定且校验通过的目录（最高优先级，重选立即生效且重启不回退）；
  2. 应用目录内置或相邻目录（受限深度，记录来源类型 `UserSelected` / `AutoDiscovered`）。
- 界面诊断/提示区显示当前实际使用的脚本目录来源。
- **启动门禁**：点击「启动服务」前验证全部必需脚本与 `assets` 可读，任一缺失直接阻断（零 `colab new` 调用），但仍保留「复查云端会话 / 停止现有计费」入口。

#### 3.4 A-11：云端部署稳定性与 `mit API :8001` 解耦
- **GUI 侧（本阶段直接完成）**：
  - 核心翻译命令 `manga_translator local` 仅依赖 `llama-server :6006`，不走 `mit API :8001`。
  - 将就绪门槛调整为 `LlamaHealthy == true`（`mit API` 作为可选附属状态展示，不再阻断部署完成与队列执行）。
- **上级脚本 `vm_setup.sh` 侧（需用户确认是否允许改动上级脚本）**：
  - 锁定 `manga-image-translator` Git commit SHA、Python 依赖版本与模型/字体文件校验和（SHA256）；
  - 所有下载改用 `curl -fsSL` 下载到 `.tmp` 验校后 `mv` 替换，脚本头部启用 `set -euo pipefail`；
  - 确认无外部脚本依赖 `:8001` 后，停用 `mit API` 启动或将监听收窄至 `127.0.0.1:8001`。

#### 3.5 A-12：Windows CLI 执行边界、重试边界与日志脱敏
- **绕开 `.cmd` 批处理注入风险**：
  - 基于本机实测，`colab.cmd` 只是调用 `C:\Users\Mashiro\.venvs\colab\Scripts\colab.exe --auth=adc %*`，且 `echo %*` 存在批处理元字符风险。
  - [ColabCli.cs](Z:/控制台/colab_manga/gui/src/MangaGui/Services/ColabCli.cs:44) 优先定位真实 `colab.exe`（支持用户在 `settings.json` 显式配置、自动解析 `colab.cmd` 内 `COLAB_EXE` 或探测 `%USERPROFILE%\.venvs\colab\Scripts\colab.exe`），直接通过 `ArgumentList` 传入 `--auth=adc` + 参数，彻底脱离 `cmd.exe` 解析。
  - 若环境仅存在 `.cmd/.bat` 回退，强制上传与下载两端都经由纯 ASCII 安全临时目录中转。
- **收窄 429 自动重试契约**：
  - `RunSshAsync` 增加 `bool allowRateLimitRetry`（默认对读/幂等命令开启，对启动翻译等有副作用命令关闭）；
  - 匹配更精准的限流特征（优先检查 stderr 或 HTTP 429 / `RESOURCE_EXHAUSTED` / `Too Many Requests` 传输错误，避免误把远端日志正文里的数字 `429` 当限流）。
- **输出缓冲上限与日志脱敏**：
  - `ReadStreamLinesAsync` 限制单次子进程保留上限（如头部 + 尾部环形缓冲 ≤ 512 KB），防异常刷屏导致 OOM；
  - [LogViewerDialog](Z:/控制台/colab_manga/gui/src/MangaGui/Views/LogViewerDialog.xaml.cs:15) 复制日志时提供脱敏选项/默认脱敏常见敏感串（Bearer/Token、Windows 用户名路径片段），并对剪贴板占用异常给出明确提示。

#### 3.6 A-13：任务级局部恢复与交互细节
- 任务行新增操作：
  - `Pending` 状态：提供「移除」按钮；
  - `Failed` / `Cancelled` 状态：提供「重试」按钮（生成新 `job_id` 重新入队）；
  - 顶部清理拆分为「清空已完成」与「清空失败/已取消」（或下拉菜单）。
- 退出提示：若 `HasActiveVm == false` 但队列仍有未开始任务，关闭窗口时提示「队列中仍有 N 项未开始任务，退出将清空」。
- 拖放区控件改为可键盘聚焦（`Focusable="True"` / `Button` 语义），禁用时（如 `Draining`）切换视觉禁用态并显示原因文案。
- 日志列表仅当滚动条位于底部附近时自动 `ScrollIntoView`，用户向上回翻排查错误时不再被强制拉回底部。

---

### Phase 4：工程可追溯性、模块收口与首期高性价比拓展（P3 + E-01）

#### 4.1 A-14：可追溯构建、协议版本与单点常量
- 添加 `global.json` 锁定 `.NET 8` SDK 主版本；在 [MangaGui.csproj](Z:/控制台/colab_manga/gui/src/MangaGui/MangaGui.csproj) 引入语义化版本（如 `1.1.0`）、构建时间戳与 Git/源码摘要信息；发布脚本同时输出 `build_manifest.json`（含版本、内置脚本 SHA256、兼容说明）。
- 在标题栏或左侧栏底部新增轻量「诊断 / 关于」弹窗，集中展示：应用版本、`.NET` 运行时、解析到的 `colab` 可执行路径、当前生效的脚本目录与来源、`assets` 脚本哈希。
- 移除对已不使用的 `wait_translate.sh` 的强制存在校验（避免旧脚本阻碍启动）；
- 将 `1.94 CU/h` 费率收敛为单一配置常量（`BillingConstants.L4CuPerHour`），XAML、弹窗文案与计算逻辑全部绑定同一来源。

#### 4.2 A-15：补齐测试金字塔与桌面人工验收清单
- **新增自动化契约测试**：
  - 真实本地假 CLI 子进程测试（用极简 `.exe`/脚本验证含中文、空格、`&`、`%`、`()` 的本地路径参数原样往返）。
  - `ColabRuntimeAdapter` 协议解析契约测试（覆盖 `PROTO 2`、`sessions` 三态、`PACK_OK` 摘要解析）。
- **固化 Windows 桌面人工验收清单**（写入 `gui/RELEASE_CHECKLIST.md`）：
  - 125% / 150% DPI 缩放与 900px 最小宽度无裁切；
  - Windows 高对比度 + 浅色/深色/跟随系统切换；
  - 纯键盘 Tab / Enter / Esc 遍历添加任务、重试、查看日志、弹窗关闭；
  - 系统托盘最小化、双击恢复、托盘右键停机、断网弹窗表现；
  - 真实 Colab 冒烟单次授权清单（明确操作人、时长预算、结束 `colab sessions` 截图/输出核验）。

#### 4.3 E-03（内聚收口）+ E-01（第一批高性价比拓展）
- **运行模块内聚（E-03）**：将分散在 `SessionManager`、`TaskQueue`、`VmScripts` 中的 Colab 参数拼装、远端目录布局与输出解析统一收口到 `ColabRuntimeClient`（`IColabCli` 保留为其内部子进程测试 seam），使 UI 与队列只依赖强类型结果。*不提前引入多后端插件接口。*
- **`.cbz` 原生支持（E-01）**：在 `MangaPackageService` 统一输入白名单（`.zip`、`.cbz`），复用同一份预算、魔数、自然排序和成品校验契约；错误后缀（如把 RAR 改成 `.cbz`）在申请 GPU 前明确拒绝。
- **可选输出目录（E-01）**：允许在队列区顶部或单任务设置输出目录（默认仍写回源目录旁、保持不覆盖已有成品），解决源目录只读或想集中收稿的痛点。
- **离线预检「先验货、再付费」（衍生方向 #1）**：
  - 任务入队即自动（或点击「预检」）在本地执行零计费检查：展示有效页数、自然排序后的首尾页预览名、预计输出路径、是否触发拍平、是否超预算或存在坏页。
  - 在未登录、未申请 GPU 的离线状态下即可完成批量排错，显著减少付费后才发现压缩包损坏的浪费。

---

### Phase 5：按实际使用频率择一推进（第二批拓展）

完成 Phase 1–4 并实际跑过几批漫画后，根据最高频痛点从以下两项中**先选一项**，不同时铺开：

1. **路线 A · 多卷中断可接手（E-04）**：
   - 将队列与任务状态持久化到 `%APPDATA%\manga_gui\tasks.json`（稳定 `job_id`、源文件大小/修改时间指纹、预设版本、阶段、输出路径、远端 `job_dir` 标记）。
   - 在应用管理的远端目录写入唯一 `run_instance_id` 标记，重启后可区分「上次同一台 VM」与「外部新建的同名 `manga` VM」。
   - 核实身份后仅提供显式按钮：「续查进度 / 续下载已打包成品 / 确认重跑」，绝不静默重提或误停同名新会话。
2. **路线 B · 预设快照与小样试译（E-02 + 衍生方向 #2）**：
   - 引入经过实测验证的翻译预设（冻结任务级参数快照随任务上传，区分任务级配置与需重启模型的环境级配置），输出文件名附带预设标识并记录轻量本地回执（不含原文与凭据）。
   - 支持「先翻代表性 3–5 页试译」：输出 `*_sample.zip`，用户用本地看图软件确认效果满意后再排整本。
3. **条件触发项（暂不启动，满足前提再立项）**：
   - **无人值守 CLI（E-05）**：确有定时/脚本批处理需求时，将核心用例抽离至无 WPF 依赖的 `MangaGui.Core`，与 GUI 共享会话所有权与停机策略，不与旧 `submit.py` 并行抢占同一会话。
   - **第二运行环境 / 跨平台 / 团队协作（E-03, E-06）**：仅在明确具体目标环境（如本机/局域网 GPU 或独立服务）及费用/权限归属后另行架构设计。

---

## 四、执行前需确认的决策项

在开始编码落地前，建议确认以下 3 项边界：

1. **上级脚本 [vm_setup.sh](Z:/控制台/colab_manga/vm_setup.sh) 是否允许加固（A-11）**？
   - **方案 A（推荐）**：Phase 3 中 GUI 先解耦 `:8001` 门槛；同时另起一次小改动加固 `vm_setup.sh`（锁定 commit/校验和、`set -euo pipefail`、停用或仅本地监听 `:8001`）。
   - **方案 B（严格零侵入）**：完全不改 `Z:\控制台\colab_manga\` 下的任何旧脚本，仅在 `gui/` 内解耦 `:8001` 就绪判断并通过 `gui/assets/vm_job.sh` 隔离任务执行。
2. **推进节奏**：是否先按 **Phase 1 + Phase 2（全部 P1 修复 + 自动化回归）** 落地并出新版验证，再推进 Phase 3 与 Phase 4？
3. **Phase 5 偏好**：后续更看重 **多卷中断续查/续下载（路线 A）** 还是 **预设快照与小样试译（路线 B）**？
