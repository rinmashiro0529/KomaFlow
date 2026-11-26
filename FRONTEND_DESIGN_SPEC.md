# MangaGui 前端设计规格：Windows 原生风格重写

- 日期：2026-11-22（基于 v1.2.0 源码）
- 依据：`FRONTEND_NATIVE_AUDIT_20261122.md`（评估）与用户指示「按 Windows 原生应用的效果重写前端」
- 配套计划书：`..\.claude\plans\colab_manga_gui_20261122_1632.md`
- 本文是视图层的唯一设计依据。它取代 `..\GUI_REQUIREMENTS.md` 的第 6 节布局图和 6.1 节配色规范；该文档第 5 节的行为要求（时长承诺、停止流程、关闭保护、孤儿会话等）全部继续有效。

## 1. 目标

界面看起来、用起来像 Windows 11「设置」这类系统应用：系统控件外观、Mica 背景、跟随系统深浅色与强调色、标准对话框和提示条、键盘可达。

三条不能退让的约束：

1. **计费保护不退化。** 任何状态下，「现在是否在计费」和「怎么停」都必须一眼可见；对话框被关闭、被打断、无人应答时，结果一律是「不做任何改变计费的事」，自动动作只由倒计时触发。
2. **状态不只靠颜色表达。** 每个状态都有图标加文字。
3. **不新增依赖，不升级 WPF-UI。** 只用 .NET 8 WPF、WPF-UI 3.0.5、CommunityToolkit.Mvvm 8.3.2 已有的能力。

## 2. 已定决策

| 项 | 决定 | 理由 |
|---|---|---|
| 技术栈 | WPF + WPF-UI 3.0.5 标准控件 | 保住单文件绿色 EXE；服务层和测试原样保留 |
| 强调色 | 跟随系统强调色，不保留陶土色 | 用户要的是原生效果；固定品牌色是最显眼的「非原生」信号 |
| 深浅色 | 默认跟随系统；保留「跟随系统 / 浅色 / 深色」三选一（`settings.json` 的 `theme` 字段已存在） | 系统应用的通行做法 |
| 布局 | 单窗口单页，纵向三段：会话卡片、任务列表、可折叠日志 | 功能只有一条流程，不需要导航 |
| 对话框 | 全部改为窗口内 `ContentDialog` | 原生做法；与主窗口主题一致 |
| 持久提示 | `InfoBar`，按固定优先级纵向堆叠 | 审计稿写的是「一次一条」，这里改为堆叠：这些提示多与计费有关，隐藏低优先级的一条有风险；实际同时出现通常不超过两条 |
| 瞬时提示 | 窗口在前台用 `Snackbar`；窗口不在前台或已隐藏时用托盘气泡（Win10/11 上显示为系统通知） | 不引入通知库 |
| 托盘 | 沿用 WinForms `NotifyIcon` 和现有菜单 | 换 Fluent 菜单需要新依赖或不稳定的手工实现，本轮不做 |
| 应用图标 | 托盘图标保持现状 | 应用自带图标颜色不影响原生观感 |

## 3. 设计令牌

所有外观只从下面取值。视图里不写十六进制颜色、不写小数字号、不设 `FontFamily`（日志等宽字体除外）、不覆盖 `FocusVisualStyle`。

### 3.1 字阶

用 `ui:TextBlock` 的 `FontTypography`：

| 用途 | FontTypography |
|---|---|
| 会话状态标题、对话框内小标题 | `BodyStrong` |
| 计时主数字（如「42 min / 60 min」） | `Subtitle` |
| 正文、列表主文字、按钮 | `Body` |
| 说明、次要信息、列表副文字 | `Caption` |

日志与原始输出：`FontFamily="Cascadia Mono, Consolas"`，`FontSize="12"`。这是唯一允许硬编码字体和字号的地方。

### 3.2 颜色（全部用 `DynamicResource`）

| 语义 | 资源键 |
|---|---|
| 主文字 / 次要文字 / 更弱的文字 | `TextFillColorPrimaryBrush` / `TextFillColorSecondaryBrush` / `TextFillColorTertiaryBrush` |
| 卡片底 / 卡片边 | `CardBackgroundFillColorDefaultBrush` / `CardStrokeColorDefaultBrush` |
| 成功 / 警告 / 错误 | `SystemFillColorSuccessBrush` / `SystemFillColorCautionBrush` / `SystemFillColorCriticalBrush` |
| 强调（进行中状态图标） | `SystemAccentColorPrimaryBrush` |
| 拖放遮罩 | `SmokeFillColorDefaultBrush` |

这些键已确认存在于本机 WPF-UI 3.0.5 的程序集中。新增其他键之前先确认它能解析（见第 11 节的测试要求）。

### 3.3 间距与圆角

- 间距只用 4 / 8 / 12 / 16 / 24。页面左右边距 24，区块之间 16，卡片内边距 16，同组控件之间 8。
- 圆角用控件默认值，不手写 `CornerRadius`。唯一例外是拖放遮罩里的提示卡，用 8。
- 内容区最大宽度 960，窗口更宽时居中。

### 3.4 图标

用 `ui:SymbolIcon`（`SymbolRegular`）。下列名称已确认存在于 3.0.5：

`Add24` `Stop24` `Play24` `Clock24` `CheckmarkCircle24` `ErrorCircle24` `DismissCircle24` `Warning24` `Circle24` `PlugDisconnected24` `ArrowSync24` `ArrowClockwise24` `HourglassHalf24` `Folder24` `FolderOpen24` `FolderZip24` `Delete24` `Copy24` `Open24` `DocumentText24` `Info24` `Broom24` `MoreHorizontal24` `ArrowCounterclockwise24` `Wrench24` `DarkTheme24`

## 4. 窗口与页面结构

- `ui:FluentWindow`，`WindowBackdropType="Mica"`，`ExtendsContentIntoTitleBar="True"`，居中启动。
- 默认 900×680，最小 640×520。
- 窗口 `Title` 保持「漫画翻译 · Colab L4」不变（`App.TryActivateExistingWindow` 按这个标题查找已运行的窗口）。

```text
┌──────────────────────────────────────────────────────────────┐
│ 漫画翻译 · Colab L4                         …   ─  □  ✕      │ ① 标题栏
├──────────────────────────────────────────────────────────────┤
│ ⚠ 队列已暂停  连续两个任务失败…                  [恢复队列]  │ ② 提示条区（无提示时不占位）
│                                                              │
│ ┌──────────────────────────────────────────────────────────┐ │
│ │ ◔ 翻译中                        [+30 分钟] [停止并结束计费]│ │ ③ 会话卡片
│ │   正在翻译 第02卷.cbz                                     │ │
│ │ 42 min / 60 min   ≈ 1.36 CU   到期空闲自动停 · 预计 15:00 │ │
│ │ ▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔ │ │
│ │ ▸ 服务详情                              已更新 14:42:07   │ │
│ └──────────────────────────────────────────────────────────┘ │
│                                                              │
│ 任务  4 个                   [＋ 添加] [输出位置 ▾] [清理 ▾] │ ④ 任务区
│ 成品默认保存至原文件目录                                     │
│ ┌──────────────────────────────────────────────────────────┐ │
│ │ ✓ Sejyo.zip                                   [打开位置] │ │
│ │   已完成                                                 │ │
│ │ ◔ 第02卷.cbz                                             │ │
│ │   翻译中 34/87 页  ▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔                   │ │
│ │ ✕ damaged.zip                         [查看日志] [重试]  │ │
│ │   失败：下载结果缺页                                     │ │
│ │ ○ next_volume.cbz                               [移除]   │ │
│ │   等待中 (32 页)                                         │ │
│ └──────────────────────────────────────────────────────────┘ │
│ ▸ 运行日志      [14:42:07] page 34/87 ok                     │ ⑤ 日志区（默认折叠）
└──────────────────────────────────────────────────────────────┘
```

纵向分配：①②③④的标题行和⑤都按内容高度，任务列表占满剩余高度。窗口高度不足时，任务列表自己滚动，页面整体不滚动。

Tab 顺序自上而下、自左而右：标题栏菜单 → 提示条按钮 → 会话卡片按钮 → 服务详情 → 任务命令 → 任务列表 → 日志。

### ① 标题栏

`ui:TitleBar`，显示标题。右侧放一个「更多」按钮（图标 `MoreHorizontal24`，透明外观，自动化名称「更多」），点开是菜单：

- 主题 ▸ 跟随系统 / 浅色 / 深色（单选，当前项打勾）
- 选择脚本目录…（`BrowseScriptsFolderCommand`）
- 复查云端会话　F5（`ReprobeSessionCommand`）
- 诊断信息…（`ShowDiagnosticsCommand`）

「更多」按钮先尝试放进 `TitleBar.Header`，并设 `WindowChrome.IsHitTestVisibleInChrome="True"`。如果在 3.0.5 上点不中或位置不对，改放到会话卡片第一行最右侧，不要为此改标题栏模板。

主题菜单需要「直接设为某一项」的命令；现有的 `CycleThemeCommand`（循环切换）不再被界面使用，可以删除。

## 5. 会话卡片

卡片是一个带卡片底色和边线的容器，内部四行。

### 第一行：状态与主要操作

左侧是状态图标（20 px）加状态标题（`RuntimeBadgeText`，`BodyStrong`），其下一行是 `StatusDetailText`（`Caption`，次要文字色，最多两行，超出省略并用工具提示显示全文）。右侧是按钮。

| 条件 | 图标 | 图标颜色 | 右侧按钮 |
|---|---|---|---|
| 正在探测（`IsProbingStartup`） | `ProgressRing`（不确定） | 默认 | 启动服务（禁用） |
| `Idle` | `Circle24` | 次要文字色 | **启动服务**（强调色） |
| `Deploying` | `ProgressRing`（不确定） | 默认 | 停止并结束计费 |
| `Ready` | `CheckmarkCircle24` | 成功 | +30 分钟、停止并结束计费 |
| `Ready` 且 `IsHealthDisconnected` | `PlugDisconnected24` | 警告 | 同上 |
| `Busy` | `ArrowSync24` | 强调 | 同上 |
| `Draining` | `HourglassHalf24` | 警告 | 同上 |
| `Stopping` / `AutoStopping` | `ProgressRing`（不确定） | 默认 | 停止并结束计费（禁用） |
| `Error` 且 `HasActiveVm` | `ErrorCircle24` | 错误 | **重试部署**（强调色）、停止并结束计费 |
| `Error` 且无活动会话 | `ErrorCircle24` | 错误 | **启动服务**（强调色） |

按钮规则：

- 可见性和可用性继续绑定现有属性：`CanStartService`、`CanRetryDeploy`、`CanStopService`、`CanExtendTime`。上表只是这些属性在各状态下的结果，不要在视图里另写一套状态判断。
- 「启动服务」「重试部署」用 `Appearance="Primary"`，其余用默认外观。整个窗口同一时刻最多一个强调色按钮。
- 「停止并结束计费」只要 `HasActiveVm` 为真就必须可见，带图标 `Stop24`。它不做成红色：在这里停止是安全动作，原生应用也不用红色按钮。
- 状态标题所在的文本设 `AutomationProperties.LiveSetting="Polite"`。

### 第二行：计费

仅在 `HasActiveVm` 时显示：`ElapsedDisplay`（`Subtitle`）、`BillingCuDisplay`（`Body`）、`BillingPolicyDisplay`（`Caption`，次要文字色）依次横排，窗口变窄时换行。

没有活动会话时，这一行换成一句 `Caption`：「L4 按 {BillingRateDisplay} 计费，运行环境存在即计费。」

`BillingSeverity` 的表现：`Normal` 不变；`Warning` 时计时数字前加 `Warning24` 图标，数字用警告色；`Danger` 时图标与数字用错误色。

### 第三行：进度条

- `IsTimedMode` 时显示计时进度条，值为 `BillingProgressPercent`。`Warning` / `Danger` 时前景分别换成警告色 / 错误色。
- `IsDeploying` 时再显示一行：`DeployStageText`（`Caption`）加部署进度条（`DeployProgressPercent`）。
- 进度条都要有自动化名称（「计费时长进度」「部署进度」）。

### 第四行：服务详情（默认折叠）

标准 `Expander`。标题「服务详情」，标题行右侧显示 `HealthStatusDisplay`（`Caption`）。展开后三行，每行是「名称 · 图标 · 文字」：

| 名称 | 状态来源 | 文字 |
|---|---|---|
| llama-server（核心）:6006 | `LlamaHealthy`、`IsHealthDisconnected` | 正常 / 未就绪 / 失联 |
| mit API（可选）:8001 | `MitHealthy` | 正常 / 未启用 |
| GPU 显存 | — | `GpuInfoDisplay` |

末尾一个默认外观的「复查会话」按钮（`ReprobeSessionCommand`，工具提示「立即调用 colab sessions 核实云端会话状态（F5）」）。

## 6. 提示条区

每条提示是一行两列：左列 `ui:InfoBar`（占满），右列一个默认外观的操作按钮（垂直居中，左边距 8）。WPF-UI 3.0.5 的 `InfoBar` 模板只显示标题、消息和关闭按钮，不显示内容，所以操作按钮只能放在它旁边。

自上而下的固定顺序：

| 顺序 | 显示条件 | 级别 | 标题 | 消息 | 操作 | 可关闭 |
|---|---|---|---|---|---|---|
| 1 | `HasErrorBanner` | Error | 会话错误 | `ErrorBannerText` | 查看原始输出 | 否 |
| 2 | `HasPersistenceWarning` | Warning | 计费保护提示 | `PersistenceWarningText` | 立即复查 | 否 |
| 3 | 5 分钟预警生效 | Warning | 剩余不足 5 分钟 | 预警文本 | 延长 30 分钟 | 是 |
| 4 | `IsQueuePaused` | Warning | 队列已暂停 | `QueuePauseReason` | 恢复队列 | 否 |
| 5 | `HasEnvironmentWarning` | Warning | 环境检查 | `EnvironmentWarning` | 选择脚本目录… | 否 |

- 第 3 条需要 ViewModel 新增两个属性（是否显示、文本）。在 5 分钟预警事件里置位；延长时长、会话结束、进入到时处理、用户点关闭时清除。
- 提示条设 `AutomationProperties.LiveSetting="Assertive"`（第 1、2 条）或 `"Polite"`（其余）。
- 消息文字自动换行，不截断。

瞬时提示（`RequestSystemNotice`，如「翻译完成」「会话已恢复」）：窗口可见且处于前台时在窗口底部用 `Snackbar` 显示 5 秒；否则用托盘气泡。两者不同时出现。

## 7. 任务区

### 7.1 标题行与命令

左：「任务」（`BodyStrong`）加数量（`Caption`，如「4 个」，无任务时不显示）。右：三个默认外观按钮。

| 按钮 | 图标 | 行为 |
|---|---|---|
| 添加 | `Add24` | `AddFilesCommand`；`IsDropZoneActive` 为假时禁用。工具提示「添加漫画压缩包（Ctrl+O）」 |
| 输出位置 ▾ | `Folder24` | 下拉菜单：更改输出目录…（`SelectOutputDirectoryCommand`）；恢复为原文件目录（`ResetOutputDirectoryCommand`，仅 `HasCustomOutputDirectory` 时可用） |
| 清理 ▾ | `Broom24` | 下拉菜单：清空已完成（`ClearCompletedTasksCommand`）；清空失败与已取消（`ClearFailedTasksCommand`） |

标题行下面一行 `Caption`（次要文字色，超长省略加工具提示）：

- 正在预检（`IsPrechecking`）或处于收尾模式（`IsDropZoneActive` 为假）时：显示 `DropZonePromptText`，预检时前面加一个小 `ProgressRing`。
- 其他时候：显示 `OutputDirectoryDisplay`。

ViewModel 里现有文案提到「左侧」的地方（`StatusDetailText` 初始值、`UpdateDropZonePromptText`）要按新布局改掉。

### 7.2 任务列表

`ui:ListView`，`SelectionMode="Extended"`，保持虚拟化，`ItemsSource` 绑定 `Tasks`。使用控件自带的选中、悬停、焦点样式，不要去掉。

每行三列，行内边距 12（左右）/ 8（上下）：

- **图标列**（20 px）：

  | 状态 | 图标 | 颜色 |
  |---|---|---|
  | `Pending` | `Clock24` | 次要文字色 |
  | 进行中（`IsActive`） | 小 `ProgressRing`（不确定） | 默认 |
  | `Completed` | `CheckmarkCircle24` | 成功 |
  | `Failed` | `ErrorCircle24` | 错误 |
  | `Cancelled` | `DismissCircle24` | 次要文字色 |

- **文字列**：第一行 `FileName`（`Body`，超长省略，工具提示为 `SourceZipPath`）。第二行 `StatusDisplay`（`Caption`；失败时用错误色，其余用次要文字色），`DetailHint` 非空时接在后面，用「 · 」分隔。`IsActive` 时第三行是进度条（`ProgressPercent`）。
- **操作列**：行内按钮，透明外观、带文字，每个都在 Tab 顺序里，自动化名称带文件名（如「重试 damaged.zip」）。

  | 条件 | 按钮 |
  |---|---|
  | `CanRemove` | 移除 |
  | `CanRetry` | 重试 |
  | `CanViewLog` | 查看日志 |
  | `IsCompleted` | 打开位置 |

列表项的自动化名称是「文件名，状态文字」。

交互：

- 右键菜单：打开所在位置 / 重试 / 查看失败日志 / 移除。不适用的项不显示。
- `Delete`：移除所有选中且 `CanRemove` 的任务。
- `Enter` 或双击：已完成的任务打开所在位置；有日志的失败任务打开日志。

### 7.3 空状态

`HasTasks` 为假时，列表区域居中显示：`FolderZip24` 图标（40 px，更弱的文字色）、一行 `Body`「把漫画 .zip / .cbz 或文件夹拖到这里」、一行 `Caption`「也可以按 Ctrl+O 选择文件。添加时先做离线预检，不会产生计费。」、一个默认外观的「选择文件…」按钮。

### 7.4 拖放

整个窗口都是放置目标，沿用现有的 `OnWindowDragEnter/Over/Leave/Drop` 逻辑（包括「先同步拷贝文件列表、再异步入队」）。`IsDragOver` 为真时，在内容区（不含标题栏）盖一层遮罩，中间一张提示卡：`Add24` 图标加「松开以添加到队列」。`IsDropZoneActive` 为假时不显示遮罩，鼠标显示禁止图标（现有逻辑已是如此）。

## 8. 日志区

标准 `Expander`，默认折叠，展开状态不持久化。

- 标题行：左「运行日志」，中间是最新一条日志的预览（`Caption`，次要文字色，超长省略；需要 ViewModel 新增一个「最新日志行」属性），展开时右侧显示「复制全部」按钮。
- 展开内容：高度 200 的列表，`SelectionMode="Extended"`，`Ctrl+C` 复制选中行。每行是级别图标（`Warning` 用 `Warning24`、`Error` 用 `ErrorCircle24`、`Success` 用 `CheckmarkCircle24`、`Info` 无图标）加 `FormattedLine`，等宽字体 12；文字颜色按级别取主文字 / 成功 / 警告 / 错误。背景跟随主题，不再固定深色。
- 自动滚动沿用现有做法：只有当前已在底部附近时才滚到底。

**必须保留现有的防崩溃约束**（见 `MainWindow.xaml.cs` 里 `P0 fix` 注释和 `DRAG_DROP_AUDIT.md`）：对 `Logs.CollectionChanged` 的订阅放在窗口 `Loaded` 之后；滚动放在 `Dispatcher.BeginInvoke(DispatcherPriority.Background, …)` 里合并执行，不在集合变更回调里同步滚动或布局。

## 9. 对话框

### 9.1 通用规则

1. 每个对话框是 `ui:ContentDialog` 的派生类，沿用现有文件名（根元素由 `Window` 换成 `ui:ContentDialog`）。派生类必须在自己的资源里加一条 `Style`，`TargetType` 为该派生类、`BasedOn="{StaticResource {x:Type ui:ContentDialog}}"`，否则没有外观。
2. 主窗口里放一个 `ContentPresenter` 作为对话框宿主，覆盖标题栏以下的整个内容区。所有对话框通过一个 `DialogService`（放在 `Views\`）显示，不直接调用 `ShowAsync`。
3. **一次只显示一个。** 3.0.5 的 `ShowAsync` 直接替换宿主内容，叠开两个会互相破坏。`DialogService` 负责排队。
4. **紧急对话框可以打断普通对话框。** 到时处理和关闭保护是紧急的：它们到来时，正在显示的普通对话框被取消（通过 `ShowAsync` 的 `CancellationToken`，取消会抛 `OperationCanceledException`，由 `DialogService` 接住），然后显示紧急对话框。两个紧急对话框之间按先后排队。
5. **安全默认。** 对话框以 `ContentDialogResult.None` 结束（按 Esc、被打断、被取消）时，映射到不改变计费的那个结果：

   | 对话框 | `None` 对应的结果 |
   |---|---|
   | 启动时长 | 未确认 |
   | 停止确认 | 未确认 |
   | 到时处理 | `TimeExpiredAction.None`（稍后再提醒） |
   | 孤儿会话 | `OrphanSessionAction.Dismiss` |
   | 关闭保护 | `CloseGuardAction.Cancel` |

6. 对话框显示期间，页面内容整体 `IsEnabled="False"`，关闭后恢复，并把焦点还给之前的控件。
7. Esc 以 `None` 关闭；Enter 触发默认按钮。3.0.5 的 `ContentDialog` 源码里没有看到按键处理，先验证，缺的在一个公共基类里补上。
8. 普通对话框 `DialogMaxWidth` 480；日志与诊断 720。正文用 `Body`，自动换行。
9. 崩溃提示和「应用已运行」提示保持系统 `MessageBox`：它们必须在没有主窗口时也能显示。

### 9.2 各对话框

**启动时长（`StartSessionDialog`）**，也用于接管孤儿会话后的时长设置。

- 标题：启动 Colab L4 运行环境
- 内容：一句说明「Colab L4 按 1.94 CU/小时计费（含约 10 分钟部署阶段），运行环境存在即计费。」；四个单选项：30 分钟（约 0.97 CU）/ 1 小时（约 1.94 CU，推荐，默认选中）/ 2 小时（约 3.88 CU）/ 不限时长（需手动停止）；一个复选框「到时且队列空闲时自动停止计费」（选不限时长时禁用）。选不限时长时，再显示一条 Warning 级 `InfoBar` 和一个复选框「我已了解需要手动停止计费」。
- 按钮：主按钮「启动并开始计费」（`CanConfirm` 为假时禁用）；关闭按钮「取消」。
- 继续用现有的 `StartSessionDialogViewModel`。

**停止确认（`ConfirmStopDialog`）**

- 标题：停止并结束计费？
- 内容：「将执行 colab stop -s manga 并复查活动会话，确认释放 GPU 后结束计费。」加三行键值：已运行时长、估算计费、队列未完成任务。未完成任务数大于 0 时加一行错误色 `Caption`「未完成的任务会被取消」；`IsDeploying` 时加一行「部署尚未完成，停止后需要重新部署」。
- 按钮：主按钮「停止并结束计费」；关闭按钮「取消」。

**到时处理（`TimeExpiredDialog`）**

这个对话框有三到四个选项，内置的三个按钮不够用，而且内置关闭按钮与 Esc 的结果相同（都是 `None`）。所以隐藏内置按钮区（`IsFooterVisible="False"`），在内容里纵向排列等宽按钮，每个按钮先记下所选动作再调用 `Hide()`。

- 标题与正文：沿用现有四种场景的文案（见现有 `TimeExpiredDialog.xaml.cs`）。
- 按钮（自上而下）：

  | 场景 | 按钮 |
  |---|---|
  | 队列仍有任务 | **翻完当前队列再停**（强调色）、延长 30 分钟、立即停止（丢弃未完成任务）、稍后提醒 |
  | 其余三种场景 | **立即停止并结束计费**（强调色）、延长 30 分钟、稍后提醒 |

  强调色按钮永远是对计费安全的那个，默认焦点也在它上面。「稍后提醒」等同 Esc。
- 倒计时行（按钮下方，`Caption`）：「无人操作时将在 m:ss 后自动{动作}。关闭此对话框不会延长时长，也不会停止计费，稍后会再次提醒。」没有自动动作时只显示后半句。
- 倒计时逻辑从视图移到一个新的 `TimeExpiredDialogViewModel`：截止时刻在**请求产生时**确定（不是对话框显示出来时），到点后以自动动作结束；只有「收尾后停止」和「立即停止」可以自动执行，「延长」永远不会。这样即使对话框因排队晚显示，自动动作也不会推迟。
- 窗口隐藏在托盘或最小化时，先恢复并激活窗口再显示（现有 `RestoreFromTrayIfHidden`）。

**孤儿会话（`OrphanSessionDialog`）**

- 标题：检测到云端已有运行中的 manga 会话
- 内容：沿用现有说明文字。
- 按钮：主按钮「接管并设置运行时长」；次按钮「立即停止计费」；关闭按钮「暂不处理」。

**关闭保护（`CloseGuardDialog`）**

- 标题：运行环境仍在计费
- 内容：「已运行 {时长}，{估算}。直接关闭窗口不会释放 Colab L4 实例。」
- 按钮：主按钮「停止计费并退出」；次按钮「最小化到托盘」；关闭按钮「取消」。
- 关闭窗口时的规则：
  - 没有活动会话且没有待处理任务：直接关闭，不管是否有对话框开着。
  - 没有活动会话但有待处理任务：显示简单确认（标题「确认退出？」，主按钮「退出」，关闭按钮「取消」），取代现在的 `MessageBox`。
  - 有活动会话：取消本次关闭，以紧急对话框显示关闭保护。「停止计费并退出」失败时，用一个只有「知道了」的对话框显示失败信息，窗口不关闭。
  - 系统注销或关机（`OnSessionEnding`）走同一条路径，行为不变。

**日志详情（`LogViewerDialog`）与诊断信息（`DiagnosticsDialog`）**

- 内容：一句 `Caption` 说明；一个只读、可选中、等宽 12 号、带滚动条的文本框（高约 360）；文本框下一行按钮：日志是「复制脱敏日志」「复制原文」，诊断是「复制脱敏诊断」；旁边一行 `Caption` 显示「已复制」。复制按钮放在内容里（内置主按钮点了会关闭对话框）。
- 按钮：只有关闭按钮「关闭」。
- 脱敏逻辑沿用现有实现。

## 10. 系统集成

### 10.1 通知

| 事件 | 窗口在前台 | 窗口不在前台或已隐藏 |
|---|---|---|
| 5 分钟预警 | 第 6 节的提示条 | 提示条 + 托盘气泡 |
| 瞬时提示（完成、恢复、重试等） | `Snackbar` | 托盘气泡 |
| 到时处理 | 对话框 | 恢复并激活窗口，再显示对话框 |

现有的置顶小窗 `WarningToastWindow` 删除。

### 10.2 任务栏进度

用 `Wpf.Ui.TaskBar.TaskBarProgress`，在视图层根据 ViewModel 属性变化更新：

| 状态 | 任务栏 |
|---|---|
| `Deploying` | Normal，值为 `DeployProgressPercent` |
| `Busy` / `Draining` 且有进行中的任务 | Normal，值为该任务的 `ProgressPercent` |
| `IsQueuePaused` 且有活动会话 | Paused |
| `Error` 且 `HasActiveVm` | Error |
| 其余 | None |

### 10.3 快捷键

| 键 | 动作 |
|---|---|
| Ctrl+O | 添加漫画 |
| F5 | 复查云端会话 |
| Delete | 移除选中的待处理任务（焦点在任务列表时） |
| Enter | 打开所在位置 / 查看日志（焦点在任务列表时） |
| Ctrl+C | 复制选中的日志行（焦点在日志列表时） |
| Esc | 关闭当前对话框 |

对话框显示期间，前四个快捷键不生效。

### 10.4 主题

`ThemeService` 保留类名和 ViewModel 用到的公开成员（`Preference`、`IsDarkMode`、`ThemeChanged`、`Apply`、`FormatPreferenceLabel`），内部改为：

- 跟随系统：`SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, true)`。
- 指定浅色或深色：`SystemThemeWatcher.UnWatch(window)` 后调用 `ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, true)`。
- 强调色：`ApplicationAccentColorManager.ApplySystemAccent()`。

原来切换自定义调色板、同步标题栏按钮颜色的代码全部去掉。三个主题文件 `Themes\LightPalette.xaml`、`Themes\DarkPalette.xaml`、`Themes\WarmPaperTheme.xaml` 删除；如确实需要共用样式，新建一个小的 `Themes\AppStyles.xaml`。

`Converters\ValueConverters.cs` 里返回固定颜色画刷的三个转换器不再适用（颜色要随主题变化），改用 `DataTrigger` 加 `DynamicResource`，或让转换器返回资源键。

### 10.5 托盘

行为不变：有活动会话时可最小化到托盘继续运行，双击恢复，菜单有「显示主窗口 / 停止并结束计费 / 退出」。

## 11. 无障碍与测试要求

- 所有只有图标的按钮都有 `AutomationProperties.Name`。
- 进度条有名称；`ProgressRing` 所在的状态同时有文字。
- 不覆盖控件的焦点样式；所有可点的东西都能用 Tab 到达、用 Enter 或空格触发。
- 界面渲染测试（`UiRenderTests`）随视图重写，保留原有两项测试的意图：
  1. 主窗口和每个对话框在浅色、深色下都能渲染，无异常、无绑定错误。
  2. 实时 Dispatcher 下拖放加日志突发不触发 `ItemContainerGenerator` 失步。
- 新增一项测试：视图 XAML 中用到的每个 `DynamicResource` 键都能在应用资源里解析到。
- 渲染测试在设置环境变量 `MANGA_GUI_SHOT_DIR` 时，把各状态的离屏截图存成 PNG，供人工查看。离屏渲染看不到 Mica，背景是纯色，这是正常的。

需要截图的状态：未运行（空队列）、部署中、翻译中（含完成 / 进行中 / 失败 / 等待四种任务行，日志展开）、异常且仍在计费（两条提示条堆叠），各有浅色和深色；另加每个对话框一张。

## 12. WPF-UI 3.0.5 已核实的事实

以下来自本机 NuGet 包和 3.0.5 标签的源码，写代码时可以直接依赖：

- `ContentDialog` 有带 `ContentPresenter` 参数的构造函数；`ShowAsync(CancellationToken)` 把自己设为宿主的 `Content`，结束后置空；取消令牌触发时任务以取消结束（抛异常）。
- `ContentDialog.Hide(result)` 结束对话框；内置按钮经 `OnButtonClick` 调用 `Hide`，关闭按钮的结果是 `None`。
- `ContentDialogService` 的方法是 `SetDialogHost` / `GetDialogHost` / `ShowAsync`（不是 4.x 的其他名字）。
- `InfoBar` 只有 `Title`、`Message`、`Severity`、`IsOpen`、`IsClosable`，模板不呈现 `Content`。
- `TitleBar` 有 `Header` 属性，没有 4.x 的 `TrailingContent`。
- 存在：`Card`、`CardExpander`、`CardControl`、`ProgressRing`、`DropDownButton`、`SplitButton`、`Snackbar` / `SnackbarPresenter` / `SnackbarService`、`ui:ListView`、`ui:TextBlock`（`FontTypography`）、`TaskBarProgress`、`SystemThemeWatcher`、`ApplicationThemeManager`、`ApplicationAccentColorManager`。
- 没有 `ContentDialogHost` 控件（那是 4.x 的）。

没有核实、需要在实现时先试的：

- `ContentDialog` 是否处理 Esc / Enter。
- `TitleBar.Header` 里的按钮能否正常点击。
- `ApplicationThemeManager` 对系统高对比度的处理。
- `Snackbar` 在本项目窗口结构下的位置与层级。

## 13. 本轮不做

- 带按钮的系统通知（需要新依赖）。
- Fluent 风格的托盘菜单。
- 设置页、侧边导航、日志导出、多语言。
- 升级 WPF-UI、迁移 WinUI 3。
- 重新设计应用图标。
- 高对比度的完整适配（只做检查并记录结果）。

## 14. 实施记录（v1.3.0，S0–S4）

### 各阶段完成内容

- **S0（ViewModel 与 WPF 解耦）**：四个带返回值的对话框事件改为返回 `Task`；两个 `DispatcherTimer` 换成可注入的 `IUiTimer` 工厂（`ViewModels\UiTimer.cs`：接口 + `ManualUiTimer`/`NullUiTimer`）；`DefaultDispatchToUi` 改为可选委托、未注入时同步执行；ViewModels 下不再引用 `System.Windows.Threading`/`Application`；每个 await 之后动作之前重读 `_session.State`/`HasActiveVm`。
- **S1（主题与主窗口）**：`FluentWindow`（Mica + `ExtendsContentIntoTitleBar`）+ `TitleBar`（「…」菜单放 `Header`）；五条 `InfoBar` 定序堆叠、操作按钮旁置；会话卡片、任务区 `ui:ListView` + 空状态 + 拖放遮罩、日志 `CardExpander`；`ThemeService` 内部迁移到 `SystemThemeWatcher`/`ApplicationThemeManager`/`ApplicationAccentColorManager`，公开成员不变；ViewModel 新增 5 分钟预警显示状态、`LatestLogLine`、直接设定主题的 `SetThemeCommand`。
- **S2（对话框迁移）**：八个旧对话框中七个改为 `AppContentDialog`（派生自 `ui:ContentDialog`），`WarningToastWindow` 删除；新增 `Views\DialogService.cs`（普通/紧急双队列、紧急打断、externalCancel、安全默认值）与 `Views\AppContentDialog.cs`（补 Esc/无页脚 Enter）；新增 `ViewModels\TimeExpiredDialogViewModel.cs`，倒计时逻辑移出 code-behind、截止时刻锚定在请求产生时；`OnWindowClosing` 按 9.2 重写（`e.Cancel` 在首个 await 前同步置位）；删除 `LightPalette/DarkPalette/WarmPaperTheme.xaml`，App.xaml 不再合并旧主题。
- **S3（系统集成）**：`RequestSystemNotice` 前台走 `Snackbar`、后台走托盘气泡；`RequestFiveMinuteWarningToast` 仅窗口不在前台时补托盘气泡（前台 InfoBar 已覆盖）；`Views\TaskbarProgressMapper.cs` 纯函数 + 视图接线 `TaskBarProgress`；快捷键（Ctrl+O/F5/Delete/Enter/Ctrl+C）经 `PreviewKeyDown` 单点实现；任务行右键菜单补齐 7.2 四项。
- **S4（无障碍/收尾）**：行内任务进度条补 `AutomationProperties.Name`；新增资源键解析测试（扫描 `Views\` XAML 的 `DynamicResource`/`StaticResource` 键，浅/深两主题均可解析）；版本号升 1.3.0；跨线程通知与任务栏进度入口加 `Dispatcher.CheckAccess` 封送（视图层，VM 不改）；Enter/双击收敛为单任务激活。
- **S4 返工（局部 Style 缺 BasedOn）**：验收发现部署中状态图标缺失——`MainWindow.xaml` 里 4 个局部 `<Style>`（`ui:ProgressRing` ×2、`ProgressBar`、`TextBlock`）替换了 WPF-UI 隐式样式导致模板丢失，已全部补 `BasedOn="{StaticResource {x:Type T}}"`；`ItemContainerStyle` 改目标类型为 `ui:ListViewItem` 并 `BasedOn` 其隐式样式（容器实为 `Wpf.Ui.Controls.ListViewItem`，命名键 `ListViewItemStyle` 也指向它）。`ui:SymbolIcon`、`ui:TextBlock`、`StackPanel` 经 `TryFindResource` 实测无隐式样式，不加。新增守卫测试：扫描 `Views\` 全部局部 Style，TargetType 有隐式样式而缺 `BasedOn` 即失败；渲染测试断言 Deploying 状态 ProgressRing 有模板且实际尺寸 >0、行容器 Style.BasedOn 即隐式样式对象。
- **S4 返工 2（对话框空白 / Snackbar 层级 / 间距令牌）**：验收再发现三处——①对话框按钮下方 ~110px 空白：视觉树探针定位到 `ContentDialog.MeasureOverride` 的高度回馈（`DialogHeight = desired - DialogMargin + Padding` 绑卡片 `MaxHeight`）与 `ResizeWidth` 钳制路径（把含卡片外边距的 `DesiredSize.Height` 写进 `DialogHeight`）。最终方案：无页脚 `TimeExpiredDialog` 用 `Padding="0"` 收敛到自然高；有页脚对话框保默认 `Padding`（`Padding=0` 会把页脚按钮压到贴边，`DialogMargin` 上下归 0 会让 DesiredSize 回馈坍缩裁切内容——两条公开属性路线都试过并回退），接受 ~70 DIP 残留空白并在测试容差中注明是 3.0.5 限制；孤儿会话 `DialogMaxWidth=560` 避开钳制（按钮文案实测完整）。`Dialogs_RenderWithoutExcessWhitespace` 断言：无页脚对话框高差 ≤4，有页脚 ≤80，且全部页脚按钮距卡片左/右/下边缘 ≥16 DIP。②对话框截图混入上一条 Snackbar：截图前统一 `SnackbarPresenter` `HideCurrent()`/`Content=null` 清理，新增 `dialog_over_snackbar.png` 像素证据——Snackbar 在对话框遮罩之下被压暗（逐通道变暗断言），非 Popup 实现，`Panel.ZIndex` 无需调整；③规格 3.3 令牌违规全量归一：`Margin`/`Padding` 只允许 0/4/8/12/16/24，新增守卫测试扫描全部 `Views\*.xaml`（`DialogMargin` 等框架 DP 不在扫描范围）；`CornerRadius` 全删只留拖放提示卡一处 `8`。

- **S5（对话框空白消除 / 行内按钮间距 / Esc 兜底）**：①有页脚对话框 ~70 DIP 底部空白根治（此前记为"3.0.5 不可修复限制"）：`AppContentDialog.MeasureOverride` 前置把 `Content.MaxWidth` 钳到 `DialogMaxWidth − Padding − 模板横向余量`（内层 Grid 左右各 24 + PassiveScrollViewer 右 12 + BorderThickness），内容首轮测量即换行，`DialogWidth` 不越界、`ResizeWidth` 裕度路径不触发；随后把 `DialogHeight` 归一到卡片 Border 的 `DesiredSize`（**注意 DesiredSize 含外边距**，需减 `DialogMargin` 上下——否则重犯上游同款 bug）消除 `GetNewDialogSize` 把 Padding 计入高度的残余空白。实测 8 个对话框 slack ≤0.8、页脚按钮未被裁切；`Dialogs_RenderWithoutExcessWhitespace` 容差由 80 收紧到 24。②任务行操作按钮间距：原 `StackPanel Margin="8,0,0,0"` + 按钮零间距（「重试」贴「查看日志」），改为面板 `Margin=0`、四个 `ui:Button` 各自 `Margin="8,0,0,0"`，Collapsed 按钮不产生间隙。③Esc 兜底：`AppContentDialog.HideAsCancelled()` 公开 None 关闭语义；新增 `Views\DialogKeyboardGuard.TryHandleEscape(ContentPresenter)`——`DialogHost.Content` 是 `AppContentDialog` 且键盘焦点不在对话框内时转发 Esc→`None`（焦点在内交给对话框自身 `OnKeyDown`，避免重复 Hide）；`MainWindow` 对话框显示期间 `PreviewKeyDown` 先走该守卫，`Activated` 时把焦点交还打开的对话框。新增测试：Esc 焦点在外转发为 `ContentDialogResult.None`、焦点在内守卫不介入；`CloseGuardDialog` 断言 `DialogWidth<=DialogMaxWidth` 且卡片内网格高差 ≤4。

### 与规格不一致的地方

| 规格 | 实际 | 原因 |
|---|---|---|
| 会话卡片副标题「最多两行」 | `TextWrapping=Wrap` + `MaxHeight=32` 近似（WPF `TextBlock` 无 `MaxLines`），配全文 `ToolTip` | 框架不支持精确两行截断 |
| 日志/诊断文本框「高约 360」 | `Height="320"` | 默认 680 高窗口内 360 会把内容内复制按钮挤出对话框可视区 |
| 孤儿会话主按钮「接管并设置运行时长」 | 内容 `MinWidth="440"` 保证三按钮完整显示 | 缩文案违反规格 |
| 未规定 Snackbar 时长 | `Timeout=5s` | 3.0.5 默认 2 秒对两行中文提示偏短 |
| 日志行字阶未单列 | `ui:TextBlock FontTypography="Caption"` + `Cascadia Mono, Consolas` | 等宽日志需求 + 禁用小数字号 |
| Enter/双击未限定单选 | Enter 只作用于键盘焦点行（取不到用 `SelectedItem`），双击只作用于被双击行 | 多选批量「打开位置」会一次弹出多个资源管理器窗口 |
| 截图窗口 | 测试用 `Opacity=0` 的屏幕内窗口，busy 状态高 1000 DIP | 幽灵显示器 DPI 上下文不收敛导致截图变形 |
| 未规定对话框高度 | 无页脚的 `TimeExpiredDialog` 设 `Padding="0"` 收敛到内容自然高；**有页脚对话框保持默认 `Padding`**，`MeasureOverride` 回馈（`DialogHeight = desired - DialogMargin + Padding` 绑卡片 `MaxHeight`）残留 ~70 DIP 底部空白。曾试 `Padding=0`（页脚按钮被压到贴边，退化）与 `DialogMargin` 上下归 0（空白消除但在 start/orphan 上 DesiredSize 回馈坍缩、内容被滚动裁切），均回退；孤儿会话 `DialogMaxWidth=560`（自然宽 ~552 必触发 `ResizeWidth` 钳制，钳制路径额外叠加 ~70px，放宽后走正常路径） | 该缺陷是 WPF-UI 3.0.5 `MeasureOverride`/`ResizeWidth` 的既有行为，不改模板则无法用公开属性同时保住页脚内边距与零空白；取页脚观感，接受残留空白，测试容差按实测设 80 DIP。**已于 S5 根治**（`AppContentDialog.MeasureOverride` 前置内容限宽 + `DialogHeight` 归一，见上） |
| 普通对话框 `DialogMaxWidth` 480 | 孤儿会话对话框放宽到 `DialogMaxWidth="560"`（实测自然宽 ~552，页脚三个按钮自身就需要 ~500，实测按钮文案完整） | 该对话框内容自然宽超过 480 必触发 `ResizeWidth` 钳制，钳制路径会叠加额外高度空白；放宽走正常测量路径，与其他对话框一致 |

### 第 12 节四项未核实行为的验证结果

1. **`ContentDialog` 是否处理 Esc/Enter** —— 部分成立：3.0.5 自身无按键处理，但模板页脚按钮带 `IsDefault`/`IsCancel`，页脚可见时 Enter/Esc 原生生效；无页脚或需强制语义时不够，已在 `AppContentDialog` 补齐（Esc→`None`、无页脚 Enter→`OnEnterPressed`，到时对话框映射到计费安全动作）。
2. **`TitleBar.Header` 里的按钮能否点击** —— 成立：「…」DropDownButton 放 `Header` 后可正常弹出菜单；注意 `Header` 内容不进窗口逻辑树，`DataContext` 需在按钮上显式 `RelativeSource AncestorType=FluentWindow` 绑定。
3. **`ApplicationThemeManager` 对系统高对比度的处理** —— 存在 `ApplicationTheme.HighContrast` 枚举值与 `IsSystemHighContrast()` 检测；`ApplySystemTheme()` 会在系统 HC 开启时自动选 HC 主题，`Apply(HighContrast)` 按真实系统主题映射到 HC1/HC2/HCBlack/HCWhite 四套主题字典（默认 HCWhite）。本应用走 `SystemThemeWatcher`（跟随系统）与显式 `Apply(Light|Dark)`，**未在真实高对比度环境下验证观感，需要实机验证**。
4. **`Snackbar` 在本项目窗口结构下的位置与层级** —— `ui:SnackbarPresenter` 放根 Grid `Grid.Row=1`、声明于页面内容之后、`DialogHost` 之前、`VerticalAlignment=Bottom`：Snackbar 在内容区底部居中弹出，层级在页面内容之上、窗内对话框之下。双重证据：根面板 `Children` 顺序断言（`PageContent < SnackbarPresenter < DialogHost`）+ 像素证据（对话框打开前后 Snackbar 区域同位置像素被遮罩压暗，`shots_S4\dialog_over_snackbar.png`）；视觉祖先链实测 `Snackbar→SnackbarPresenter→Grid→ContentPresenter→ClientAreaBorder→AdornerDecorator→MainWindow`，不在独立 `Popup`/`AdornerLayer` 中，无需 `Panel.ZIndex` 调整。

### 需要实机验证的项目

- 键盘走查（Tab 顺序、焦点可见性）与读屏（Narrator/读屏软件读自动化名称）。
- 125%/150% DPI 缩放下的实际显示（测试截图基于 125%）。
- 系统高对比度模式下的观感（见上，wpf-ui 支持但未实测）。
- 托盘最小化/恢复/气泡、真实拖拽入队、Mica 实际观感、任务栏进度实际显示。
- 真实 Colab 联调（计费、部署、翻译全流程）。
