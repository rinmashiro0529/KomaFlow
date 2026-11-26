# MangaGui 源码审计报告

- 审计日期：2026-11-20
- 范围：Windows GUI（C#、WPF/XAML）、`IColabCli` 调用边界、会话与计费、任务队列、ZIP 输入输出、GUI 资产与实际依赖的上级云端脚本、现有测试和发布说明。未审计 Google Colab 服务端、第三方库内部源码或发布 EXE 与源码的一致性。
- 方法：源码追踪、从源码构建并运行测试、Bash 语法检查、使用当前编译产物及模拟 CLI 进行离线专项复核。不改应用源码，不调用真实 Colab、ADC 或 GPU。
- 基线：本目录及祖先不是 Git 工作树（`git rev-parse` 退出 128）；报告按审计当天的磁盘源码定位，不对应已知提交。

## 结论

**建议先修计费状态，再修成品完整性和 ZIP 边界，最后改善交互与发布。** 项目已经把会话、计时、脚本调用和队列拆开，具备用假 CLI 回归的基础；正常路径测试全部通过。但三个可复现的异常会让界面说错最重要的两件事：云端是否还在计费，以及停止命令是否真的完成。当前「到时自动停止」只能视作客户端的尽力而为，不能当作费用上限。

| 优先级 | 数量 | 决策依据 |
|---|---:|---|
| P1 | 5 | 可能隐瞒活动会话、延误停机、误报成品或让输入耗尽本地/云端资源；建议作为下一次发布的修复门槛 |
| P2 | 8 | 影响日常成功率、恢复体验、安全边界或部署稳定性；按风险分批处理 |
| P3 | 2 | 降低长期维护与验收成本；可在前两批完成后处理 |

「已离线复现」指下方列出实际观察结果；「静态确认」指可由源码确定分支或缺失的约束，但未在云端触发；「待验证」指实际表现取决于平台、CLI 或外部部署条件。优先级按影响与易达性判断，不等同于 CVSS。

## P1 · 优先修复

### A-01｜停机确认把「缺证据」当成「已停止」（功能 / 计费）

**已离线复现。** `stop` 返回失败，随后 `sessions` 退出码为 0、标准输出为空，结果仍是 `VerifiedStopped=True`、状态 `Idle`。解析器对空文本、未知格式和明确无会话都返回 `false`；启动探测也用这个值，并可能删除本地记录。CLI 输出格式变化、异常仅写到 stderr 时，不能据此判定云端不存在。

- **定位：** [StopSessionAsync](Z:/控制台/colab_manga/gui/src/MangaGui/Services/SessionManager.cs:339)、[启动探测](Z:/控制台/colab_manga/gui/src/MangaGui/Services/SessionManager.cs:74)、[HasActiveSession](Z:/控制台/colab_manga/gui/src/MangaGui/Services/VmScripts.cs:156)。
- **优化：** 统一解析为「存在 / 明确不存在 / 无法确认」。优先使用已安装 CLI 经核实支持的结构化输出；否则只接受识别出的完整列表与可靠的空列表提示。`Unknown` 时保留记录和关闭保护，显示「可能仍在计费」并提供复查；不要显示停止成功。
- **验收：** `stop` 失败后，空 stdout、未知格式和查询失败均不能返回 `VerifiedStopped`；若可识别的列表明确无目标会话，才允许清除记录。

### A-02｜创建结果不确定或本地落盘失败时，活动会话失去保护（功能 / 计费）

**已离线复现两条路径。** ① 模拟云端已创建，但 `new` 超时：仅调用了创建前的那次 `sessions`，最终 `HasActiveVm=False`。② 模拟云端创建成功，但会话文件的父路径不可用：抛出 `IOException`，`HasActiveVm=False`，计时未启动。关闭保护只看 `HasActiveVm`，所以这两条路径都可能让用户在未停机时正常关窗。停止成功后反过来也会被本地 I/O 阻塞：先删本地记录，再停止计时和更新状态；删除失败会抛出，是同类设计问题（静态确认）。

- **定位：** [创建与状态持久化](Z:/控制台/colab_manga/gui/src/MangaGui/Services/SessionManager.cs:205)、[停止成功后的本地删除](Z:/控制台/colab_manga/gui/src/MangaGui/Services/SessionManager.cs:355)、[SessionStore](Z:/控制台/colab_manga/gui/src/MangaGui/Services/SessionStore.cs:31)、[窗口关闭保护](Z:/控制台/colab_manga/gui/src/MangaGui/Views/MainWindow.xaml.cs:149)。
- **优化：** 请求创建前进入「创建中／可能计费」状态，不把通信失败直接解释为“没创建”。超时或异常后进行有总时限的复查。确认远端存在后先建立内存保护，再尝试写入；本地保存失败显示独立警告但保留停机入口。远端已明确释放时，本地删除失败也应单独提示，不应卡在 `Stopping`。读盘需区分记录缺失、损坏和无权限。
- **验收：** 模拟创建已发生但应答丢失、磁盘写失败、删除失败；每条路径的计费标签、关闭保护、复查与停机入口均与远端事实或未知状态一致。

### A-03｜启动、部署、停止与队列没有统一的操作边界（功能 / 计费）

**已离线复现状态覆盖。** 部署尚未返回时停机已复查成功；部署旧请求稍后返回失败，又把界面改成 `Error / HasActiveVm=True`。启动探测、部署、手动停机和队列收尾也可分别修改同一状态；字段锁只保护单次写入，不防旧操作回写。**静态确认延误风险：** 停机根据「有活跃或排队任务」决定先尝试 SSH 终止进程；有排队任务但尚在部署时，终止调用会先排在 SSH 锁后面，所谓 10 秒只约束子进程，不包含排队，可能被 30 分钟部署调用阻塞。

- **定位：** [部署回调与结果](Z:/控制台/colab_manga/gui/src/MangaGui/Services/SessionManager.cs:264)、[停机前 SSH](Z:/控制台/colab_manga/gui/src/MangaGui/Services/SessionManager.cs:312)、[SSH 锁及超时](Z:/控制台/colab_manga/gui/src/MangaGui/Services/ColabCli.cs:112)、[停机入口](Z:/控制台/colab_manga/gui/src/MangaGui/ViewModels/MainViewModel.cs:274)、[队列自动停机](Z:/控制台/colab_manga/gui/src/MangaGui/Services/TaskQueue.cs:207)。
- **优化：** 用单一协调器承接生命周期操作。停机优先于部署和健康查询；用取消令牌和会话代次让旧回调失效，同一会话并发停机合并成一次。为了释放整台 VM，不等待无关的 SSH 清理；如保留预清理，将排队、重试和执行一起纳入总时限，只对已确认启动的当前任务执行。应用启动探测完成前不开放相冲突的指令。
- **验收：** 使用测试闸门安排「探测与启动重叠」「部署未完成即停止」「收尾与手动停机同时触发」；停止请求不会被占用的 SSH 锁无限拖延，已核实停止的状态不会被旧结果覆盖。

### A-04｜文件数量和非零字节不足以证明翻译成品完整（功能 / 数据完整性）

**静态确认，现有测试也未约束此行为。** 云端进度统计所有顶层常规文件；只要数量到达目标，甚至仍有进程运行，GUI 就开始打包。没有检查这些文件是否对应预期页面或已写完。下载后仅检查非零字节，不校验 ZIP 能否打开、页数和内容是否完整，也不使用 `PACK_OK` 后跟随的大小。现有队列测试用一个没有任何成员的 22 字节 ZIP 就被标记为完成，测试用例本身没有检验实际输出。

- **定位：** [vm_status.sh](Z:/控制台/colab_manga/gui/assets/vm_status.sh:22)、[进度结束条件](Z:/控制台/colab_manga/gui/src/MangaGui/Services/TaskQueue.cs:313)、[打包与下载验证](Z:/控制台/colab_manga/gui/src/MangaGui/Services/TaskQueue.cs:362)、[现有测试的下载夹具](Z:/控制台/colab_manga/gui/tests/MangaGui.Tests/TaskQueueTests.cs:71)。
- **优化：** 给任务自己的进程和完成记录，以明确退出状态或结果清单收尾，避免仅凭数量就打包。打包完成后返回字节数，必要时加摘要；本地至少核验 ZIP 结构、期望图片清单和必要完整性，再原子改名。保留目前不覆盖已有成品的设计。
- **验收：** 写入中的页面、额外非图片文件、缺页、截断 ZIP 和能打开但没有图片的 ZIP 都不能进入 `Completed`；正常成品才可见最终文件名。

### A-05｜ZIP 输入处理没有资源预算，且平铺包绕过归一化（安全 / 可用性）

**静态确认。** 本地只根据扩展名和目录形态挑选图片，不限制压缩包大小、条目数、解压总量或单页大小；需拍平时直接 `CopyTo`，取消不能打断已开始的拷贝。满足平铺分支的档案原样上传，未统一检查重复名称、隐藏名、非普通条目或实际图片有效性。远端直接 `unzip -o`；提取失败不阻止后续计数和启动翻译。这给破损或不可信 ZIP 带来本机临时盘占满、云端耗时计费及处理半成品的风险。没有制作恶意档案，也未验证具体 `unzip` 版本的安全行为。

- **定位：** [枚举、原样上传和重打包](Z:/控制台/colab_manga/gui/src/MangaGui/Services/ZipPreprocessor.cs:35)、[云端提取](Z:/控制台/colab_manga/run_translate.sh:3)。
- **优化：** 在上传前统一执行资源限制和可取消的校验；界面明确报告拒绝原因。将格式、归一化路径、重名和条目类型检查应用于原样上传与重打包两条路径；只在每任务隔离目录内解压，并在提取失败或实际图片统计不符时立即退出。评估图片尺寸和解码内存上限，不依赖扩展名判断内容。
- **验收：** 用小阈值配合普通测试档案覆盖超限、无有效图片、重复文件名、取消与提取失败，确认不上传或不中途启动翻译，且临时文件能清理。

## P2 · 提升成功率、交互与安全边界

### A-06｜失败与取消路径留下临时文件，上一任务未退出仍可推进队列

**静态确认。** 云端 `clean` 只在本地下载并改名成功后调用，而且不检查返回的退出码；失败或取消没有对应的云端清理。下载取消或最终移动失败时，`.downloading` 也没有统一 `finally`。轮询超时后队列会继续下一个任务，上一任务不一定已退出；所有任务使用同一个日志路径及全局进程名进行存活检查，可能把上一任务的活跃和日志归到下一本。

- **定位：** [队列循环与结果处理](Z:/控制台/colab_manga/gui/src/MangaGui/Services/TaskQueue.cs:185)、[超时、下载与清理](Z:/控制台/colab_manga/gui/src/MangaGui/Services/TaskQueue.cs:356)、[全局进程与日志](Z:/控制台/colab_manga/gui/assets/vm_status.sh:27)、[云端清理](Z:/控制台/colab_manga/gui/assets/vm_pack.sh:24)。
- **优化 / 验收：** 将文件、进程标识、日志和状态放入任务专属目录。确认当前任务停止后再继续；不能确认时暂停队列并提示，不误判下一任务。用独占的本地临时名，在 `finally` 中只清理本任务创建的文件；云端清理失败可重试或记录。覆盖上传后失败、下载取消、目标名发生竞争和清理返回非零。

### A-07｜健康检查会叠加，失联仍可能显示上次的绿灯

**静态确认。** 15 秒 `DispatcherTimer` 回调异步查询，每次只检查 `Ready`，没有 in-flight 标记；单次 SSH 允许更长时间，后续 tick 可以继续排队，任务开始也不会取消已有检查。SSH 锁防并发执行，不防请求积压。状态查询失败直接返回 `null`，不清除或标识旧状态；任务以 480 次轮询作为“约 96 分钟”上限，仅 12 秒间隔的合计是 96 分钟，实际还包括 SSH 排队、执行和重试。

- **定位：** [健康定时器](Z:/控制台/colab_manga/gui/src/MangaGui/ViewModels/MainViewModel.cs:107)、[空闲查询](Z:/控制台/colab_manga/gui/src/MangaGui/ViewModels/MainViewModel.cs:377)、[失败状态保留](Z:/控制台/colab_manga/gui/src/MangaGui/Services/SessionManager.cs:388)、[任务轮询次数限制](Z:/控制台/colab_manga/gui/src/MangaGui/Services/TaskQueue.cs:294)。
- **优化 / 验收：** 空闲健康查询只保留一个最新请求，切换任务时取消或忽略旧请求。显示最近成功检查的时间，传输失败时标「失联／状态未知」，必要时复查会话存在性。用整段实际耗时上限与连续失败阈值，覆盖慢响应、失联、切入忙碌及外部停机。

### A-08｜拍平压缩包可能打乱正常页序

**已离线复现。** 有子目录的 ZIP 中，`1.jpg`、`2.jpg`、`10.jpg` 拍平重命名后，内容顺序是 **1、10、2**。实现用不区分大小写的字典序排序，不是注释暗示的自然顺序；重写成 `0001` 等名称后，阅读器无法还原原文件名排序。

- **定位：** [排序与编号](Z:/控制台/colab_manga/gui/src/MangaGui/Services/ZipPreprocessor.cs:98)。
- **优化 / 验收：** 明确以目录分段与数字自然排序为默认，原名作为稳定的次排序键。有多话、多目录或重名时用夹具断言具体输出页序，不只断言页数。

### A-09｜到时对话框可能静默延长，部署失败也会显示「仍有任务」

**静态确认。** 设置为自动停止时，仅在 `Ready` 且队列空闲时直接停机；仍在部署、错误或恰好刚处理完的情形会进入等待用户选择分支，甚至显示 0 个任务的「队列仍有任务」。弹窗默认结果是「延长 30 分钟」，调用方不检查 `ShowDialog()` 结果，点窗口关闭会得到延长，而非显式确认。长时间不回应则保持等待；退出应用、系统休眠或断网期间，客户端也不能保证届时停机。

- **定位：** [到时分支](Z:/控制台/colab_manga/gui/src/MangaGui/ViewModels/MainViewModel.cs:412)、[弹窗调用](Z:/控制台/colab_manga/gui/src/MangaGui/Views/MainWindow.xaml.cs:56)、[默认结果](Z:/控制台/colab_manga/gui/src/MangaGui/Views/TimeExpiredDialog.xaml.cs:8)。
- **优化 / 验收：** 延长必须显式选择，关闭和无响应有明确、可预先配置的含义；区分「部署超时」「服务错误」「仍在翻译」「无任务」场景。计费卡同时显示自动停止是否启用、确切截止时刻和超时策略。用假时钟测试忙碌、失败、0 任务、关闭弹窗及跨过截止时间；若将来引入云端硬上限，应先确认平台支持并另行授权验证。

### A-10｜手动选择脚本会被覆盖，资产缺失仍可先申请 GPU

**已离线复现目录选择问题。** 重新选目录后保存成功，但 `RefreshLocations()` 先沿 EXE 父目录搜索，再读用户保存值，立即回到原目录。另一路径**静态确认**：环境自检会提示缺 `assets`，启动命令却不把它作为阻断条件；上传阶段才会因缺脚本失败，此时可能已经创建计费 VM。自动找任意祖先目录还会扩大脚本信任范围：只按文件名存在判断，之后会实际上传和执行。

- **定位：** [选择与发现](Z:/控制台/colab_manga/gui/src/MangaGui/Services/VmScripts.cs:47)、[自动查找优先级](Z:/控制台/colab_manga/gui/src/MangaGui/Services/VmScripts.cs:289)、[启动命令预检](Z:/控制台/colab_manga/gui/src/MangaGui/ViewModels/MainViewModel.cs:174)、[创建后才上传](Z:/控制台/colab_manga/gui/src/MangaGui/Services/SessionManager.cs:247)。
- **优化 / 验收：** 以明确选定的、经过验证的脚本目录为准，回退自动发现时显示实际来源；对随版本发布的脚本优先使用应用目录下的清单。所有脚本可读且本次使用的来源已确定，才允许创建；资产缺失时仍保留复查和停止现有会话的入口。测试目录切换、重启后生效、缺资产时零 `new` 调用。

### A-11｜云端部署不能稳定重建，且启用了 GUI 流程不需要的监听

**静态确认。** `manga-image-translator` 取默认分支，模型和字体取 `main`，多项 Python 依赖未锁定；下载没有校验摘要，`curl -sL` 不把 HTTP 错误码当失败，也不先下载到临时文件完成校验后再替换。阶段里的多条命令未逐一判断，末条命令成功可能掩盖先前失败；重试依赖少数文件是否存在，不能确认缓存内容可用。`mit API` 绑定 `0.0.0.0:8001`，部署和恢复都要求 API 健康，但实际队列用 `manga_translator local`，不走该端口。**没有证据表明端口可从公网访问。**

- **定位：** [安装与下载](Z:/控制台/colab_manga/vm_setup.sh:7)、[API 监听](Z:/控制台/colab_manga/vm_setup.sh:55)、[实际翻译命令](Z:/控制台/colab_manga/run_translate.sh:10)、[GUI 双服务门槛](Z:/控制台/colab_manga/gui/src/MangaGui/Services/SessionManager.cs:295)。
- **优化 / 验收：** 固定经验证的提交、依赖和模型版本，校验下载；采用临时文件和明确的失败退出，凭完整版本清单与实际健康结果决定是否跳过步骤。先确认是否有外部工作流依赖 `mit API`：若无，GUI 的就绪判据不应依赖它；若需保留，按消费者范围选择监听地址及访问控制。用隔离环境验证全新部署、部分下载后的重试及 API 不可用时的 GUI 行为。

### A-12｜Windows CLI 边界、输出解析和日志缺少防护契约

**静态确认设计缺口；未证实命令注入或日志泄密。** CLI 路径接受 `PATH` 里的 `.cmd`/`.bat`，`ArgumentList` 不能替代对批处理 shim 参数行为的实际验证。预处理只针对上传路径的一组字符做规避，下载传入原目录衍生路径，没有同等的边界验证。另有两个问题：SSH 将任意失败正文中的 `429` 视为限流并重放，对启动任务等有副作用的调用不安全；子进程全文读入无长度上限，日志窗口又可原样复制，并未作脱敏。

- **定位：** [CLI 查找、重试、进程参数](Z:/控制台/colab_manga/gui/src/MangaGui/Services/ColabCli.cs:44)、[限流识别](Z:/控制台/colab_manga/gui/src/MangaGui/Services/ColabCli.cs:139)、[日志缓冲](Z:/控制台/colab_manga/gui/src/MangaGui/Services/ColabCli.cs:201)、[下载路径](Z:/控制台/colab_manga/gui/src/MangaGui/Services/TaskQueue.cs:377)、[日志复制](Z:/控制台/colab_manga/gui/src/MangaGui/Views/LogViewerDialog.xaml.cs:15)。
- **优化 / 验收：** 固定可识别的官方 CLI 路径与版本，优先用不经批处理解释器的调用方式；用假的本地 CLI 验证含 Unicode、空格及合法特殊字符的路径参数能原样往返。仅对明确为传输限流且可安全重试的命令重试；不确定是否已产生副作用时先查远端。限制输出字节/行数，导出前提供脱敏及失败反馈。不将凭据写入测试夹具或日志。

### A-13｜失败后缺少局部恢复入口，部分交互反馈不反映真实操作

**静态确认。** 任务行只提供完成时「打开位置」与失败时「查看日志」，不能直接重试失败任务或移除尚未运行的单项；「清空已完成」也不移除失败项。队列仅在内存里，正常退出时如果尚未申请 VM，没有针对排队任务的关闭提示。收尾时底部拖放区依旧显示可点击样式，处理函数却会忽略点击；日志每追加一条就滚动到底部，妨碍查看较早的错误。

- **定位：** [队列存储、入队和清理](Z:/控制台/colab_manga/gui/src/MangaGui/Services/TaskQueue.cs:20)、[任务行与拖放区](Z:/控制台/colab_manga/gui/src/MangaGui/Views/MainWindow.xaml:507)、[退出、点击与自动滚动](Z:/控制台/colab_manga/gui/src/MangaGui/Views/MainWindow.xaml.cs:149)。
- **优化 / 验收：** 先加「移除待处理」「重试失败」和按状态清理的明确操作；退出时有未启动任务要说明会丢失，若做恢复，先保持为本地待处理清单，不擅自恢复云端执行。把拖放区做成可聚焦且有禁用理由的控件，仅在用户停在日志末尾时跟随滚动。用键盘覆盖添加、重试、查看错误及退出。

## P3 · 可持续维护与验收

### A-14｜构建、发布及脚本/CLI 协议缺少可追溯边界

**静态确认。** 两份发布 EXE 文件版本均为 `1.0.0.0`，仅凭目录名和时间不能确定由哪份源码、哪组脚本和哪个工具链构建。程序用多处文本标识拼接 GUI 与 CLI/Bash 契约；等待脚本 `wait_translate.sh` 仍被强制要求和上传，但实际轮询走 `vm_status.sh`。CU 单价也在代码、按钮文案和 XAML 各自维护，改一处可能不一致。恢复部署部分路径只补传状态脚本，不能证明其他任务脚本是本版本。

- **定位：** [项目版本与资产复制](Z:/控制台/colab_manga/gui/src/MangaGui/MangaGui.csproj:12)、[构建与发布说明](Z:/控制台/colab_manga/gui/README.md:17)、[脚本清单及协议解析](Z:/控制台/colab_manga/gui/src/MangaGui/Services/VmScripts.cs:14)、[恢复兜底](Z:/控制台/colab_manga/gui/src/MangaGui/Services/SessionManager.cs:509)、[计费常量](Z:/控制台/colab_manga/gui/src/MangaGui/Services/BillingTimer.cs:25)。
- **优化 / 验收：** 固定 SDK 与依赖还原基线；每个发布附来源标识、构建版本、兼容 CLI 版本和脚本版本清单，并在诊断页可见。GUI 与脚本用带版本的结果格式，启动或恢复校验配套资产；将不用的强制依赖与计费数字重复来源逐步收敛。验证从干净目录可构建，并能说明任一 EXE 配套的脚本及运行时要求。

### A-15｜现有测试擅长正常路径，不等同于桌面交互或真实链路验收

**静态确认覆盖边界。** WPF 测试在固定画布上 `Measure/Arrange`，检查了深色标题栏和计费区，但不显示真实交互窗口，不走 Tab、系统托盘、对话框关闭、DPI 或 Windows 高对比度。主窗口最小宽度 900 DIP、固定双栏，自定义按钮和任务模板的真实可访问性没有验证。CLI 单测替换了子进程执行器，不能验证真实 shim 参数传递和输出。

- **定位：** [WPF 渲染测试](Z:/控制台/colab_manga/gui/tests/MangaGui.Tests/UiRenderTests.cs:64)、[窗口尺寸](Z:/控制台/colab_manga/gui/src/MangaGui/Views/MainWindow.xaml:1)、[CLI 重试测试](Z:/控制台/colab_manga/gui/tests/MangaGui.Tests/ColabCliRetryTests.cs:9)。
- **优化 / 验收：** 保留当前快速离线测试，补齐状态竞态、CLI 子进程契约及 ZIP 完整性验证；再在 Windows 上人工检查键盘焦点、屏幕阅读器的任务状态、125%/150% DPI、高对比度、托盘和断网提示。真实 Colab 冒烟需单独授权，约定资源和停机方式，结束必须复查会话，并保留单独的结果证明。

## 验证记录

| 检查 | 实际结果 |
|---|---|
| 本机 SDK 8.0.428：`dotnet test MangaGui.sln -c Release --no-restore --logger "console;verbosity=normal"` | 从当前源码构建 Release；**17 通过，0 失败**，含 1 个离屏 WPF 测试，使用已有依赖 |
| `bash -n`：`vm_setup.sh`、`run_translate.sh`、`wait_translate.sh`、`assets/vm_status.sh`、`assets/vm_pack.sh` | **5 个均通过**。只解析语法，没有执行脚本 |
| 临时 .NET 控制台引用本次构建的 `MangaGui.dll`，用模拟 CLI 和本地临时文件跑专项 | **6 个均复现，退出码 0**。临时控制台构建曾报 `MSB3277`（引用 `WindowsBase` 的版本冲突），但顺利运行；正常解决方案测试命令未报告该警告。专项未合入现有测试项目 |
| 真实 Colab、认证、网络中断、外部暴露、真实 DPI/托盘操作、发布 EXE 运行 | **未运行**；本次为本地源码审计，未访问账号或产生云端费用 |

离线专项的现场观察：

| 对应项 | 模拟场景 | 观察结果 |
|---|---|---|
| A-01 | 停止失败，复查命令退出 0 但 stdout 为空 | `VerifiedStopped=True`，`Idle` |
| A-02 | 模拟会话已创建，但 CLI 返回超时 | `createResult=False`，模拟会话仍存在，`HasActiveVm=False`，`sessions` 仅调用一次 |
| A-02 | 模拟创建成功，但应用数据路径为文件而非目录 | `IOException`，模拟会话仍存在，`HasActiveVm=False`，计时未启动 |
| A-03 | 停止已复查成功，随后旧部署报错 | `VerifiedStopped=True`，模拟会话已不存在，最终 `Error`、`HasActiveVm=True` |
| A-08 | 拍平非补零编号的三页 ZIP | 新 ZIP 中内容顺序为 `1,10,2` |
| A-10 | 手动选另一套目录并刷新 | 配置保存了新目录，实际又使用了原目录 |

## 建议实施顺序

1. **先让计费状态可信（A-01～A-03）。** 列表解析必须区分未知；创建、恢复和停止统一协调；磁盘和网络异常不能让关闭保护消失。把 4 个计费专项转成自动化回归后，再考虑重发 EXE。
2. **再让任务结果可信（A-04～A-08）。** 定义任务级进程和输出契约、输入预算、产物校验与清理，补齐失败与取消路径。先保证不误报成功，再优化轮询频率和显示。
3. **最后把异常说清并做成可重复发布（A-09～A-15）。** 明确到时策略与脚本选择；缩小 CLI 与部署信任边界；补真实桌面验收和版本清单。未授权前不需改云端服务或升级依赖。
