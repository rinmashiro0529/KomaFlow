# 外部拖入 ZIP 崩溃深度审计报告

- **审计对象**：`MangaGui` v1.1.0（`Z:\控制台\colab_manga\gui\src\MangaGui\`）
- **审计日期**：2026-11-20
- **审计结论摘要**：本次崩溃**不是简单的拖放代码空引用或文件读取报错**，而是由 **WPF 集合事件订阅顺序倒置 + `CollectionChanged` 内同步强制布局重入 + OLE COM `IDropTarget::Drop` 边界静默吞异常** 三层机制叠加引爆的**系统性架构缺陷**；同时沿拖放 → 预检 → 入队 → UI 渲染 → 异常守护全链路审出 **8 类深层隐患**（其中多个问题会直接击穿进程或在 GPU 运行期间造成云端孤儿计费）。

---

## 一、实证取证：Windows 事件查看器 + 最小复现

### 1.1 真实崩溃记录（Windows Application Event Log）

在本地事件日志中共提取到 3 条 `MangaGui.exe` 的 `.NET Runtime (ID=1026)` 崩溃记录：

| 时间 | 版本 | 异常类型 | 核心信息 |
|---|---|---|---|
| `2026-11-20 14:47:39` | `v1.1.0.0` | `System.InvalidOperationException` | `LogListBox`（`Items.Count: 6`）与项源不一致：**累积计数 5 与实际计数 6 不相同** |
| `2026-11-20 03:50:41` | `v1.0.0.0` | `System.InvalidOperationException` | `LogListBox`（`Items.Count: 5`）与项源不一致：**累积计数 4 与实际计数 5 不相同** |
| `2026-11-20 03:50:19` | `v1.0.0.0` | `System.Windows.Markup.XamlParseException` | 只读属性 `ProgressPercent` 不能 `TwoWay` 绑定（已在 `v1.1.0` 改为 `Mode=OneWay`，本次已复查全部 XAML 确认无残余） |

最新一次崩溃（`14:47:39`，`v1.1.0.0`）的顶部与底部关键帧：

```text
System.InvalidOperationException: 某个 ItemsControl 与它的项源不一致。
 ---> System.Exception:
引发此异常的原因是名为“LogListBox”的控件“System.Windows.Controls.ListBox Items.Count:6”的生成器
已接收到一个 CollectionChanged 事件序列，这些事件与 Items 集合的当前状态不符。检测到以下差异:
  累积计数 5 与实际计数 6 不相同。
  * System.Collections.ObjectModel.ObservableCollection`1[[MangaGui.Models.LogEntry, ...]]

   at System.Windows.Controls.ItemContainerGenerator.Verify()
   at System.Windows.Controls.VirtualizingStackPanel.MeasureChild(...)
   at System.Windows.Controls.VirtualizingStackPanel.MeasureOverrideImpl(...)
   ...
   at System.Windows.Window.MeasureOverride(Size availableSize)
   at System.Windows.ContextLayoutManager.UpdateLayoutCallback(Object arg)
   at System.Windows.Media.MediaContext.FireInvokeOnRenderCallbacks()
   at System.Windows.Media.MediaContext.RenderMessageHandler(Object resizedCompositionTarget)
```

> **极具迷惑性的表象**：崩溃堆栈落点在 `LogListBox` 的渲染布局期（`MediaContext.RenderMessageHandler`），堆栈中**完全没有出现 `OnWindowDrop`、`EnqueueDroppedFiles` 或 `ZipPreprocessor`**。

### 1.2 实测复现与委托链取证

通过独立复现程序在真实 `MainWindow` + `Dispatcher` 上反射打印 `MainViewModel.Logs.CollectionChanged` 的多播委托注册顺序，并调用 `EnqueueDroppedFiles`，直接捕获到**完整的一、二阶段双栈证据**：

```text
--- CollectionChanged delegates [After new MainWindow()] ---
  [0] MangaGui.Views.MainWindow.OnLogsCollectionChanged
--- CollectionChanged delegates [Inside Loaded] ---
  [0] MangaGui.Views.MainWindow.OnLogsCollectionChanged
  [1] System.Windows.Data.ListCollectionView.OnCollectionChanged

[Before Enqueue] Logs.Count=3, Items.Count=3, IsMeasureValid=True
=== 第一阶段：EnqueueDroppedFiles 内部同步抛出（OLE Drop 会吞掉此异常） ===
System.InvalidOperationException: 某个 ItemsControl 与它的项源不一致。
   at System.Windows.Controls.ItemContainerGenerator.Verify()
   at System.Windows.Controls.VirtualizingStackPanel.MeasureChild(...)
   at System.Windows.ContextLayoutManager.UpdateLayout()
   at System.Windows.Controls.ItemsControl.OnBringItemIntoView(ItemInfo info)
   at System.Windows.Controls.ListBox.ScrollIntoView(Object item)
   at MangaGui.Views.MainWindow.OnLogsCollectionChanged(...)  // MainWindow.xaml.cs:288
   at System.Collections.ObjectModel.ObservableCollection`1.OnCollectionChanged(...)
   at MangaGui.ViewModels.MainViewModel.<AppendLog>b__0()     // MainViewModel.cs:886
   at MangaGui.ViewModels.MainViewModel.EnqueueDroppedFiles(...) // MainViewModel.cs:418

=== 第二阶段：下一帧渲染（与 Windows 事件日志 100% 字节级一致） ===
System.InvalidOperationException: 某个 ItemsControl 与它的项源不一致。
  累积计数 4 与实际计数 5 不相同。
   at System.Windows.Controls.ItemContainerGenerator.Verify()
   ...
   at System.Windows.Window.MeasureOverride(...)
   at System.Windows.Media.MediaContext.RenderMessageHandler(...)
```

---

## 二、P0 直接致死根因：三层连锁反应拆解

### 第一层：构造函数订阅顺序倒置（`MainWindow.xaml.cs:25-33`）

```csharp
// src/MangaGui/Views/MainWindow.xaml.cs:25-33
InitializeComponent();
_viewModel = viewModel;
_trayService = trayService;
DataContext = _viewModel;
...
((INotifyCollectionChanged)_viewModel.Logs).CollectionChanged += OnLogsCollectionChanged;
```

- 在 `MainWindow` 尚未 `Show()` / `Loaded` 前，设置 `DataContext = _viewModel` **并不会立刻激活** `LogListBox` 上 `ItemsSource="{Binding Logs}"` 的集合监听。
- 但第 33 行立即把 `MainWindow.OnLogsCollectionChanged` 挂到了 `_viewModel.Logs.CollectionChanged` 的 **`[0]` 号位**。
- 等到 `MainWindow.Show()` 触发绑定激活时，WPF 内部的 `ListCollectionView.OnCollectionChanged`（驱动 `ItemCollection` → `ItemContainerGenerator`）才挂到 **`[1]` 号位**，永远排在业务代码之后！

### 第二层：`CollectionChanged` 内同步调用 `ScrollIntoView` 强制重入 `UpdateLayout()`（`MainWindow.xaml.cs:275-290`）

```csharp
// src/MangaGui/Views/MainWindow.xaml.cs:275-290
private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
{
    if (e.Action != NotifyCollectionChangedAction.Add || LogListBox.Items.Count == 0)
        return;

    _logScrollViewer ??= FindVisualChild<ScrollViewer>(LogListBox);
    if (_logScrollViewer is null
        || _logScrollViewer.ScrollableHeight <= 0
        || _logScrollViewer.VerticalOffset >= _logScrollViewer.ScrollableHeight - 24)
    {
        LogListBox.ScrollIntoView(LogListBox.Items[^1]); // <-- 致命重入点！
    }
}
```

拖入 1 个 ZIP 文件时，UI 线程同一帧内连续写入两条日志：
1. `TaskQueue.EnqueueFiles`（`TaskQueue.cs:204-205`）添加任务并写入 **Log #5**（`任务入队：...`）：
   - `[0]` 号位先跑时，`ListCollectionView` 还没收到 #5，`LogListBox.Items[^1]` 仍是已实现的旧项，未触发重排。
   - `[1]` 号位随后收到 #5，`ItemContainerGenerator` 计数更新为 **5**，并将 `LogListBox` 布局标脏（`IsMeasureValid = false`）。
2. 紧接着 `MainViewModel.EnqueueDroppedFiles`（`MainViewModel.cs:418`，或预检失败走 `413`）在同一帧内写入 **Log #6**（`已加入 1 个任务...` 或 `rejectReason`）：
   - `ObservableCollection.InsertItem` 先把底层 `Count` 变成 **6**，再按顺序广播 `CollectionChanged`。
   - **`[0]` 号位 `OnLogsCollectionChanged` 抢先执行**，调用 `LogListBox.ScrollIntoView(LogListBox.Items[^1])`。
   - 此时目标项尚未生成容器且布局已脏，`ItemsControl.OnBringItemIntoView` 同步强行调用 **`ContextLayoutManager.UpdateLayout()`** → `VirtualizingStackPanel.MeasureOverride` → `ItemContainerGenerator.Verify()`。
   - `Verify()` 读到底层集合实际数量 **6**，而 `[1]` 号位 `ListCollectionView` 根本还没轮到执行，生成器累积计数仍是 **5** → **立即抛出 `InvalidOperationException`！**

### 第三层：OLE COM `IDropTarget::Drop` 静默吞异常 + 生成器永久损坏

- `[0]` 号位抛出异常导致多播事件中断，**`[1]` 号位 `ListCollectionView.OnCollectionChanged` 彻底被跳过**。`LogListBox` 的 `ItemContainerGenerator` 从此**永久错位（5 != 6）**。
- 由于触发源在 `OnWindowDrop`（运行于 Windows Explorer 发起的 OLE COM `IDropTarget::Drop` 回调内），COM 互操作层截获并吞掉了第一次异常（返回失败 `HRESULT` 给 Explorer），未在 `OnWindowDrop` 处暴露栈帧。
- 下一帧 WPF 渲染管线（`MediaContext.RenderMessageHandler` → `UpdateLayoutCallback`）对主窗口重排，再次调用 `ItemContainerGenerator.Verify()`，由于生成器已不可逆损坏，直接在主 Dispatcher 抛未处理异常杀掉进程（且即使捕获也会在每一帧无限循环抛出！）。

> **影响面远超拖放**：该缺陷并非“拖放专属”，而是**全局定时炸弹**。点击「+ 添加漫画」按钮、服务部署阶段连续打日志、翻译进度更新叠加状态切换、或**在 Colab L4 正在计费翻译时拖入新漫画**，都会触发同一崩溃。若在 GPU 计费中崩溃，本地计费守护与到期自动停机会立刻失效，云端 L4 将沦为孤儿持续扣费（1.94 CU/h）。

---

## 三、全链路深层隐患审计（拖放 → 预检 → 入队 → UI → 守护）

除 P0 主根因外，沿外部拖入全链路共审出 **7 项高/中危系统性缺陷**：

### S-01 [High] 在 OLE `IDropTarget::Drop` 回调内于 UI 线程同步做重型 ZIP 解压 I/O 与 WinForms 弹窗

- **位置**：`MainWindow.xaml.cs:262-273` → `MainViewModel.cs:408-420` → `TaskQueue.cs:157-220` → `ZipPreprocessor.cs:178-224, 685-702`
- **问题机制**：
  1. `PrecheckArchive` 传参 `verifyActualBytes: false`，导致 `ZipPreprocessor.cs:685` 的 `if (!needsRepack || !verifyActualBytes)` **恒为 `true`** —— 每次拖入都会同步打开 ZIP 并对**每一张图片**执行 `entry.Open()` + Deflate 流读取前 12 字节！
  2. 整个过程同步运行在 UI 线程且位于 Windows Explorer `DoDragDrop()` 发起的 **OLE `IDropTarget::Drop` 同步 COM 回调内**。拖入数百页大包、多本漫画、网络映射盘（如 `Z:\`）或 OneDrive 仅云端占位文件时，会**同时冻结 `MangaGui` 与 Windows Explorer**，严重时触发 OLE RPC 超时或资源管理器白屏无响应。
  3. 预检失败时同步在 `Drop` 回调内调用 `RequestSystemNotice` → WinForms `TrayService.ShowBalloon`（`Shell_NotifyIcon`），存在跨 UI 框架 COM 重入风险。
  4. `OnWindowDrop` 未设置 `e.Handled = true`，且未复查 `_viewModel.IsDropZoneActive`。

### S-02 [High] `ZipPreprocessor.PrecheckArchive` 异常边界残缺，多类常见坏包/权限异常可直接击穿 UI 线程

- **位置**：`ZipPreprocessor.cs:192, 205-223`
- **问题机制**：
  1. 第 192 行 `new FileInfo(sourceZipPath).Length` 位于 `try/catch` **外部**，遇到瞬时删除、权限拒绝、路径异常、网络断链直接外泄未捕获异常。
  2. 第 220 行 `catch (Exception ex) when (ex is InvalidDataException or IOException)` **漏捕多个常见异常**：
     - **`NotSupportedException`**：.NET `ZipArchive` 遇到**加密 ZIP** 或 **Deflate64 / LZMA / BZip2 / PPMd** 压缩方式（日漫资源常见 7z/Bandizip 非标准 ZIP）时会抛出 `NotSupportedException`！
     - **`UnauthorizedAccessException`**：文件被杀毒软件/阅读器独占锁或无读权限。
     - **`ArgumentException` / `OverflowException`**：畸形路径或损坏的 ZIP 长度字段。

### S-03 [Medium-High] OLE `IDataObject` 脆弱性与非标准拖放源 / 文件夹静默失效

- **位置**：`MainWindow.xaml.cs:248-273`、`TaskQueue.cs:167-180`
- **问题机制**：
  1. `OnWindowDragOver` / `OnWindowDrop` 调用 `e.Data.GetDataPresent(DataFormats.FileDrop)` 与 `e.Data.GetData(DataFormats.FileDrop)` **无任何 `try/catch`**。从 7-Zip、WinRAR、资源管理器压缩包预览视图、浏览器或虚拟 Shell 文件夹拖入时，COM `IDataObject` 常抛出 `COMException`（`DV_E_FORMATETC` / `E_FAIL`）。
  2. 拖入**文件夹**或 **`.lnk` 快捷方式**时，`TaskQueue.cs:169` 的 `!File.Exists(path)` 直接 `continue`，不写日志也不给 `rejectedReasons`，呈现“拖进去毫无反应”。
  3. **UIPI 提权隔离**：若用户曾“以管理员身份运行”本程序，普通权限资源管理器的拖放会被 Windows UIPI 静默拦截，且界面无任何提示（也不支持在诊断页暴露当前是否提权）。
  4. `OnWindowDragOver` 不区分拖入项是否包含 `.zip/.cbz`，也不提供任何拖放悬停高亮（`IsDragOver`）反馈。
  5. 无重复拖放去重保护（手抖连拖同一文件两次或在异步预检期间重复拖入，会生成重复任务）。

### S-04 [Medium] 日漫压缩包编码（Shift-JIS / CP932 / GBK）与“伪扩展名图片”兼容性隐患

- **位置**：`ZipPreprocessor.cs:207, 257, 611-621, 691-701`
- **问题机制**：
  1. `ZipFile.OpenRead(sourceZipPath)` 在 .NET 8 中默认用 UTF-8 解码未设 UTF-8 标志位（Bit 11）的条目名。日文 Windows 打包的漫画 ZIP 普遍采用 **Shift-JIS (CP932)** 或中文 **GBK (CP936)**，解码会产生 `U+FFFD`（``）替代符；一旦两页文件名或章节目录乱码撞车，第 618 行 `seenNormalizedPaths.Add` 会误报 `压缩包中存在重复图片路径` 并拒收，或打乱 `NaturalPathComparer` 排序。
  2. 网上流传的漫画包常把实际为 PNG / WebP 的图片保存为 `.jpg` 扩展名。当前 `HasValidImageMagicBytes` 严格按扩展名校验魔数，会直接把整本漫画判为损坏并拒收（更稳妥的做法是：只要魔数属于任意受支持图片格式即放行并在重打包时修正扩展名，或明确区分“扩展名与实际格式不符”提示）。

### S-05 [Medium] `LogEntry` 值相等 `record` 在 WPF `ListBox` 中的碰撞隐患

- **位置**：`Models/LogEntry.cs:11`
- **问题机制**：
  - `public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Message)` 采用值相等（`Equals`）。
  - Windows 系统时钟分辨率约 `15.6ms`，同一 tick 内写入两条相同文案/级别的日志时，两对象的 `Equals` 为 `true`。WPF `ItemsControl` / `ItemContainerGenerator` / `ScrollIntoView` 内部以 `Object.Equals` 定位容器，极易定位到错误索引或破坏滚动行为。

### S-06 [High] 全局崩溃防护与并发/退出安全缺口（直接威胁计费守护）

- **位置**：`App.xaml.cs`、`MainViewModel.cs:678`、`TaskQueue.cs:136-148`、`MainWindow.xaml.cs:133-148`
- **问题机制**：
  1. **零全局异常处理与崩溃落盘**：`App.xaml.cs` 未挂载 `DispatcherUnhandledException`、`AppDomain.UnhandledException`、`TaskScheduler.UnobservedTaskException`，也没有 `%APPDATA%\manga_gui\crash.log` 落盘，导致现场零留痕。
  2. **关闭/退出期 `Dispatcher.Invoke` 竞态**：`App.xaml.cs:53,61` 与 `MainWindow.xaml.cs:133-148` 直接 `Dispatcher.Invoke(action)`，未检查 `Dispatcher.HasShutdownStarted` / `HasShutdownFinished`，后台线程在窗口关闭瞬间回调会抛出 `TaskCanceledException`。
  3. **锁序反转死锁隐患**：`TaskQueue.CancelPendingAndActive`（`TaskQueue.cs:138-147`）在持有 `_queueLock` 期间同步调用 `_dispatchToUi`（即 `Dispatcher.Invoke`），而 UI 线程上的 `PendingCount` / `UnfinishedCount` / `EnqueueFiles` 也会申请 `_queueLock`。一旦从非 UI 线程调用取消逻辑即可能 AB-BA 死锁。
  4. **`async void` 风险**：`MainViewModel.cs:678` 将 `async () => { ... }` 传入 `Action` 参数的 `_dispatchToUi`，构成 `async void` 委托；若到期回调内部抛异常将直接杀死整个进程。

### S-07 [Low-Medium] `TrayService` 幽灵托盘图标残留与原生 GDI `HICON` 句柄泄漏

- **位置**：`Services/TrayService.cs:64-73, 105-106`
- **问题机制**：
  - 每次拖放预检失败调用 `ShowBalloon`，都会把 `_notifyIcon.Visible = true` 打开但永不关闭，即使主窗口正常打开且未计费也在托盘留一个幽灵图标。
  - `bmp.GetHicon()` 创建的原生 `HICON` 经 `Icon.FromHandle` 包装后，`Icon.Dispose()` 不会释放该句柄（MSDN 明确要求显式调用 `DestroyIcon`）。

### S-08 [Medium] 自动化测试盲区（为何 35/35 绿灯仍漏过必现崩溃）

- **位置**：`tests/MangaGui.Tests/UiRenderTests.cs`
- **问题机制**：
  - 现有 `UiRenderTests` 仅对未 `Show()` 的 `rootGrid` 调用 `Measure/Arrange/UpdateLayout`，**没有真实 `HwndSource`、`Window.Loaded` 事件和 `Dispatcher` 消息循环**，因此完全无法复现“构造期绑定延迟激活 → 委托顺序倒置 → 连续 `AppendLog` 触发 `ScrollIntoView` 重入 `UpdateLayout`”的真实运行时行为。
  - `ValueConverters.cs` 中静态 `SolidColorBrush` 未 `.Freeze()`（虽然当前仅 UI 线程访问，但在多 STA 测试线程间共享静态未冻结 `Freezable` 属于已知 WPF 隐患）。

---

## 四、推荐整改方案（按优先级）

### 第一阶段（P0：彻底根治崩溃与 OLE 冻结）
1. **彻底重构日志自动滚动机制（根治 `ItemContainerGenerator` 错位）**：
   - **禁止**在 `CollectionChanged` 回调中同步调用 `LogListBox.ScrollIntoView(...)`。
   - 改为在 `MainWindow` 中使用**防抖的延迟滚动**：收到 `CollectionChanged(Add)` 后，仅置位 `_logScrollPending = true` 并通过 `Dispatcher.BeginInvoke(DispatcherPriority.Background, ...)` 在本帧布局/集合通知**全部完成之后**，调用 `_logScrollViewer.ScrollToEnd()`（完全绕开 `ScrollIntoView` 的 `UpdateLayout` 同步重入与 `Equals` 扫描）。
   - 同时在 `Loaded`（而非构造函数）再挂钩 `CollectionChanged`，并在 `Closed` 中取消订阅；`LogEntry` 改用带唯一自增 `Id` 或改为引用相等 `class`。
2. **彻底解耦 OLE `OnWindowDrop` 与重型 ZIP 预检（根治 UI/Explorer 冻结）**：
   - `OnWindowDrop` 中只做**极速、纯内存、带 `try/catch` 的路径拷贝**（设置 `e.Handled = true`，立刻从 `IDataObject` 提取 `string[]` 快照后立即返回，让 Explorer `DoDragDrop` 毫秒级完成释放！）。
   - 通过 `Dispatcher.BeginInvoke` + `Task.Run` 在**后台线程**异步执行 `ZipPreprocessor.PrecheckArchive` 批量预检（带“正在预检 N 个文件...”非阻塞状态反馈与并发防重入），预检完成后再切回 UI 线程一次性入队与弹通知。
3. **加固 `ZipPreprocessor.PrecheckArchive` 异常边界与快速预检策略**：
   - 将 `FileInfo`、路径检查、压缩包打开、条目读取**全部纳入 `try/catch`**，覆盖 `InvalidDataException`、`IOException`、`UnauthorizedAccessException`、`NotSupportedException`、`ArgumentException`、`OverflowException`，一律转为友好可读的 `ArchivePrecheckResult(false, ...)`。
   - 对非直传或超大包优化预检：快速扫描中央目录 + 按需抽检或异步校验，不再阻塞 UI 线程。

### 第二阶段（P1：全链路健壮性、编码兼容与崩溃观测）
4. **增强拖放体验与边界兼容**：
   - `OnWindowDragOver` 增加 `try/catch`、检查拖入路径是否含受支持文件/目录、增加窗口拖放悬停高亮（`DragEnter` / `DragLeave` / `Drop`）。
   - 拖入文件夹时给出明确提示（或自动扫描顶层 `.zip/.cbz`），不再静默吞掉；对 `.lnk` 与非支持扩展名统一走拒绝提示；在「诊断」弹窗中显示当前是否管理员权限（提示 UIPI 拖放限制）。
   - 引入 `System.Text.Encoding.CodePages`（`CodePagesEncodingProvider.Instance`），为非 UTF-8 ZIP 提供 UTF-8 → CP932 / GBK 回退解码；兼容“内容为合法图片但扩展名写错”的漫画页（重打包时自动修正扩展名）。
5. **补齐全局异常防护与线程/退出安全**：
   - 在 `App.xaml.cs` 注册 `DispatcherUnhandledException`、`AppDomain.UnhandledException`、`TaskScheduler.UnobservedTaskException`，将完整异常栈与当前会话计费状态写入 `%APPDATA%\manga_gui\crash.log`；若检测到存在活动 Colab VM，崩溃提示明确提醒用户**立即重开应用或手动停止云端计费**。
   - 封装安全的 `SafeDispatchToUi`（检查 `Dispatcher.HasShutdownStarted/Finished`，优先 `CheckAccess()` 或 `BeginInvoke` 避免关闭期 `TaskCanceledException`）；修复 `TaskQueue.CancelPendingAndActive` 持锁调用 `_dispatchToUi` 与 `MainViewModel.OnTimeLimitReached` 的 `async void`。
   - 修复 `TrayService` 气球提示后恢复图标可见状态并调用 `DestroyIcon` 释放原生句柄；冻结 `ValueConverters.cs` 中的静态画刷。

### 第三阶段（P2：补齐真实消息循环回归测试）
6. **新增端到端 UI 回归测试**：
   - 新增真实 `Window.Show()`（屏外）+ `Dispatcher` 消息循环 + `Loaded` + `EnqueueDroppedFiles`（连续多条日志、损坏包、加密/异常包、快速连拖）的自动化测试，确保 `ItemContainerGenerator` 永不再失同步。
