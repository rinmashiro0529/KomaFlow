你要把一个 Windows 桌面应用的前端按 Windows 11 原生风格重写。计划和设计已经做完并由用户批准，你负责按文档实现、验证并汇报。

## 背景

项目在 `Z:\控制台\colab_manga\gui`，叫 MangaGui，是 C# / WPF / .NET 8 应用。它替用户管理一台按时计费的 Google Colab L4 GPU 运行环境：启动、部署翻译服务、排队翻译漫画压缩包、下载成品、停止计费。这个应用最重要的职责是防止用户忘记停止计费，所以关闭保护、到时处理、孤儿会话这几条路径出错的代价是真金白银。

业务逻辑层刚做完一轮加固，56 项测试全过，本次不动。现在的界面是在 WPF 上手绘的一套品牌风格，用户想要系统应用那样的效果。路线已定：继续用 WPF，把视图换成项目已依赖的 WPF-UI 3.0.5 的 Fluent 标准控件，跟随系统强调色和深浅色。

## 先读这些文件

按顺序读完再动手：

1. `Z:\控制台\colab_manga\.claude\plans\colab_manga_gui_20261122_1632.md`：计划书。完成标准、分阶段步骤 S0–S4、必须新增的测试、授权删除的 5 个文件都在里面。
2. `Z:\控制台\colab_manga\gui\FRONTEND_DESIGN_SPEC.md`：设计规格。布局、每个控件用什么、每个状态怎么显示、每个对话框的文案和按钮、已核实的 WPF-UI 3.0.5 事实，都以它为准。
3. `Z:\控制台\colab_manga\GUI_REQUIREMENTS.md` 第 5 节：行为需求，仍然有效。它的第 6 节和 6.1 节已被设计规格取代，不要照着做。
4. 现有代码：`gui\src\MangaGui\ViewModels\`、`Views\`、`App.xaml(.cs)`、`Services\ThemeService.cs`、`Services\TrayService.cs`，以及 `gui\tests\MangaGui.Tests\UiRenderTests.cs` 和 `HardeningTests.cs`。

用户把这份任务交给你，就表示计划书已获批准，包括其中列明的 5 个文件的删除。不需要重新写计划或再次请求批准，直接从 S0 开始。

## 要做的事

按计划书的 S0 → S4 顺序实现。每个阶段：

1. 开始前，把 `gui\src` 和 `gui\tests`（不含 `bin`、`obj`）复制到 `%TEMP%\manga_gui_frontend_rewrite\backup_S{n}\`。项目不是 Git 仓库，这是唯一的回退手段。
2. 实现该阶段。
3. 构建并跑全量测试，0 警告、0 失败才算过：

   ```powershell
   & "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build MangaGui.sln -c Release
   & "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test MangaGui.sln -c Release
   ```

   PATH 里的 `dotnet` 没有 SDK，必须用上面这个路径。
4. 从 S1 起，设置环境变量 `MANGA_GUI_SHOT_DIR` 让渲染测试输出离屏截图，逐张打开，对照设计规格检查。离屏渲染没有 Mica，背景是纯色，属正常。

如果做不完，停在阶段边界上：宁可完整交付到 S2，也不要留一个编译不过或测试不过的 S3。

## 这件事难在哪

- **同步对话框变异步。** 原来 `ShowDialog()` 立刻返回结果，现在要 `await`。等待期间会话状态可能已经变了，返回后必须重新读状态再动作，不能用等待之前的判断。
- **WPF-UI 3.0.5 的 `ContentDialog` 一次只能显示一个。** 叠开会互相破坏，所以需要一个统一的 `DialogService` 排队，并让到时处理和关闭保护能打断普通对话框。细节在设计规格 9.1。
- **对话框的安全默认。** 任何对话框被 Esc、被打断、被取消时，结果必须是不改变计费的那个选项。到时对话框的选项多于三个，内置按钮不够用，而且内置关闭按钮和 Esc 结果相同，所以它要用自定义按钮，见设计规格 9.2。
- **到时倒计时。** 截止时刻要在请求产生时确定，放进可测试的 ViewModel。如果按对话框显示出来的时刻算，排队会让自动停止推迟，用户会多付钱。
- **日志列表有一个修过的崩溃。** 在集合变更回调里同步滚动或布局，会让 WPF 的 `ItemContainerGenerator` 失步而崩溃。`MainWindow.xaml.cs` 里有 `P0 fix` 注释说明现有做法，重写时保留这套做法和对应的测试。
- **`InfoBar` 不能放按钮。** 3.0.5 的模板不呈现内容，操作按钮要放在它旁边。
- 设计规格第 12 节末尾列了四项我没能核实的行为（如 `ContentDialog` 是否处理 Esc）。先写个小验证再依赖它们；规格里对每一项都给了不成立时的做法。

## 边界

可以改：`gui\src\MangaGui\` 下的 `ViewModels\`、`Views\`、`Themes\`、`Converters\`、`App.xaml`、`App.xaml.cs`、`MangaGui.csproj`（仅版本号和必要的文件项）、`Services\ThemeService.cs`、`Services\TrayService.cs`；`gui\tests\MangaGui.Tests\`；`gui\README.md`；在 `gui\FRONTEND_DESIGN_SPEC.md` 末尾追加实施记录。

不可以：

- 改 `Services\` 下其他文件、`Models\`、`gui\assets\`，或 `gui\` 以外的任何文件。如果你认为必须改，停下来汇报原因，不要自己动。
- 新增或升级 NuGet 依赖。
- 执行任何 `colab` 命令，或启动连接真实 CLI 的 `MangaGui.exe`。应用一启动就会调用 `colab sessions`；需要看界面就用测试里的 `FakeColabCli` 做离屏渲染。真实运行环境是计费的，联调由用户另行安排。
- 删除计划书列明的 5 个文件之外的任何文件；动 `publish`、`publish_v2`、`bin`、`obj`；初始化 Git；重新发布 EXE。
- 删除或跳过现有测试来让构建通过。两项界面渲染测试要按新视图重写并保留原意图，其余 54 项应当原样通过；如果其中某项因为 S0 的事件签名变化需要适配，只改订阅写法，不改断言。
- 在视图里写十六进制颜色、小数字号、手写按钮模板，或覆盖焦点样式。

遇到规格没覆盖的小问题（措辞、间距取哪一档、图标选哪个），自己按规格的原则决定，在汇报里列出来。遇到会改变行为或范围的问题，停下来问。同一个问题用同一种办法失败两次就换思路，再不行就汇报现象和已试过的办法。

## 完成后汇报

用中文，按下面的结构：

1. **结果**：做到了哪个阶段；每个阶段是完成、部分完成还是未开始。
2. **验证**：最后一次构建和测试的实际输出摘要（警告数、通过 / 失败 / 跳过数）；截图目录路径和张数。只写你实际运行并看到的结果，没运行的写「未运行」和原因。
3. **改动清单**：新增、修改、删除的文件列表，每个文件一句话说明。
4. **与设计规格不一致的地方**：每处写明规格怎么说、实际怎么做、为什么。
5. **规格第 12 节四项未核实行为的验证结果**。
6. **自行决定的事项**，以及**需要用户决定或实机验证的事项**。
7. **备份位置**。
