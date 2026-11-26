using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MangaGui.Models;
using MangaGui.Services;
using MangaGui.ViewModels;
using MangaGui.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Xunit;

namespace MangaGui.Tests;

/// <summary>
/// Hosts a single persistent STA thread + WPF Application + live Dispatcher for all UI tests,
/// because System.Windows.Application is a process-wide singleton tied to its creating STA thread.
/// </summary>
public sealed class WpfStaFixture : IDisposable
{
    private readonly Thread _staThread;
    private readonly ManualResetEventSlim _ready = new();
    private Dispatcher? _dispatcher;

    public WpfStaFixture()
    {
        _staThread = new Thread(() =>
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            if (Application.Current is null)
            {
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            }

            _ready.Set();
            Dispatcher.Run();
        });
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.IsBackground = true;
        _staThread.Start();
        _ready.Wait(TimeSpan.FromSeconds(10));
    }

    public Dispatcher Dispatcher => _dispatcher ?? throw new InvalidOperationException("WPF STA Dispatcher failed to initialize.");

    public void Run(Action action)
    {
        Exception? captured = null;
        Dispatcher.Invoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });

        if (captured is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }

    public Task RunAsync(Func<Task> asyncAction)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await asyncAction();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    public void Dispose()
    {
        if (_dispatcher is not null && !_dispatcher.HasShutdownStarted)
        {
            _dispatcher.InvokeShutdown();
        }
        _staThread.Join(TimeSpan.FromSeconds(5));
        _ready.Dispose();
    }
}

public sealed class UiRenderTests : IClassFixture<WpfStaFixture>
{
    private readonly WpfStaFixture _wpf;

    public UiRenderTests(WpfStaFixture wpf)
    {
        _wpf = wpf;
    }

    private static bool _appResourcesReady;

    /// <summary>
    /// Mirrors App.xaml's merged dictionaries: the WPF-UI theme + controls dictionaries plus the
    /// converter keys. Legacy palettes were removed in S2 and are gone from every dialog.
    /// </summary>
    private static void InitAppResources(bool dark)
    {
        var app = Application.Current!;
        var dicts = app.Resources.MergedDictionaries;

        if (!_appResourcesReady)
        {
            dicts.Clear();
            dicts.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light });
            dicts.Add(new Wpf.Ui.Markup.ControlsDictionary());
            app.Resources["BoolToVis"] = new Converters.BoolToVisibilityConverter();
            app.Resources["InverseBoolToVis"] = new Converters.BoolToVisibilityConverter { Invert = true };
            app.Resources["NotEmptyToVis"] = new Converters.NullOrEmptyToVisibilityConverter();
            app.Resources["EnumMatchToBool"] = new Converters.EnumMatchToBoolConverter();
            _appResourcesReady = true;
            return;
        }

        // Subsequent calls swap only the WPF-UI theme dictionary in place. Rebuilding the whole
        // merged set removes ControlsDictionary -> every style re-evaluates -> CardExpander's
        // template is torn down while its Border.Tag opacity animation is attached, which throws
        // ArgumentNullException (wpf-ui#1140). A theme-only swap leaves styles untouched.
        var newThemeDict = new Wpf.Ui.Markup.ThemesDictionary { Theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light };
        int idx = -1;
        for (int i = 0; i < dicts.Count; i++)
        {
            var src = dicts[i].Source?.ToString();
            if (src is not null
                && src.IndexOf("wpf.ui;", StringComparison.OrdinalIgnoreCase) >= 0
                && src.IndexOf("theme", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                idx = i;
                break;
            }
        }
        if (idx >= 0)
        {
            dicts[idx] = newThemeDict;
        }
        else
        {
            dicts.Insert(0, newThemeDict);
        }
    }

    [Fact]
    public async Task MainWindowAndLegacyDialogs_RenderCleanlyInBothLightAndDarkThemes()
    {
        await _wpf.RunAsync(async () =>
        {
            var shotDir = Environment.GetEnvironmentVariable("MANGA_GUI_SHOT_DIR");
            if (!string.IsNullOrWhiteSpace(shotDir))
            {
                Directory.CreateDirectory(shotDir);
            }

            var tempDir = Path.Combine(Path.GetTempPath(), $"manga_gui_ui_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            MainWindow? win = null;
            try
            {
                InitAppResources(dark: false);
                var store = new SessionStore(tempDir);
                using var themeService = new ThemeService(store, detectSystemDarkMode: () => false);

                var scripts = new VmScripts(store, tempDir);
                var cli = new FakeColabCli((_, _, _) =>
                    Task.FromResult(new ColabResult(0, "No active sessions found.", string.Empty)));
                var billing = new BillingTimer();
                var mgr = new SessionManager(cli, scripts, store, billing);
                var queue = new TaskQueue(cli, mgr, a => a());
                using var vm = new MainViewModel(cli, scripts, mgr, queue, themeService, a => a(), store);
                using var tray = new TrayService();

                // A real (hidden) window is required: several WPF-UI template behaviors —
                // expander visibility transitions, title-bar header hosting — only run through a
                // live render pass, which never happens for a never-shown window. Opacity keeps
                // it invisible; parking it off-screen puts the HWND on a ghost monitor whose DPI
                // context does not settle, which would skew VisualBrush captures.
                win = new MainWindow(vm, tray)
                {
                    Left = 0,
                    Top = 0,
                    Opacity = 0,
                    ShowInTaskbar = false,
                    ShowActivated = false
                };
                Assert.Equal("漫画翻译 · Colab", win.Title);

                var loadedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                win.Loaded += (_, _) => loadedTcs.TrySetResult();
                win.Show();
                await loadedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

                // OnWindowLoaded runs the environment self-check synchronously; the mock env lacks
                // scripts so it flags a warning. Clear it so scripted states control the InfoBars.
                vm.HasEnvironmentWarning = false;
                vm.EnvironmentWarning = string.Empty;

                var rootGrid = Assert.IsAssignableFrom<UIElement>(win.Content);

                async Task Render(string state, bool dark, int height = 680)
                {
                    win!.Width = 900;
                    win!.Height = height;
                    InitAppResources(dark);
                    themeService.Apply(dark ? ThemePreference.Dark : ThemePreference.Light, persist: false);
                    // The window backdrop stays transparent in the harness; give the root grid an
                    // opaque fill BEFORE the settle delay so the change is actually rasterized
                    // (setting it inside SavePng lands after the last render pass and is lost).
                    ((Panel)rootGrid).Background = new SolidColorBrush(dark
                        ? Color.FromRgb(0x20, 0x20, 0x20)
                        : Colors.White);
                    await Task.Delay(300); // let layout + expander/theme transitions settle
                    if (!string.IsNullOrWhiteSpace(shotDir))
                    {
                        SavePng(rootGrid, Path.Combine(shotDir, $"mainwindow_{state}_{(dark ? "dark" : "light")}.png"), 900, height);
                    }
                }

                // --- State 1: idle, empty queue ---
                await Render("idle", dark: false);
                await Render("idle", dark: true);

                // --- Engine mismatch InfoBar (configured engine != cloud engine) ---
                vm.HasEngineMismatch = true;
                vm.EngineMismatchText = "当前会话使用 旧引擎 mit，切换到 新引擎 mtui 需要重试部署";
                await Render("engine_mismatch", dark: false);
                await Render("engine_mismatch", dark: true);

                // Mismatch alone must not surface the queue-resume button (regression guard:
                // it once rendered inside the mismatch panel without ColumnDefinitions).
                Assert.True(FindVisualChildren<System.Windows.Controls.Button>(rootGrid)
                    .All(b => !Equals(b.Content, "恢复队列") || !b.IsVisible));

                vm.HasEngineMismatch = false;
                vm.EngineMismatchText = string.Empty;

                // Queue-paused panel: the resume button lives inside the Grid bound to IsQueuePaused.
                vm.IsQueuePaused = true;
                vm.QueuePauseReason = "队列已暂停（示例）";
                await Render("queue_paused", dark: false);
                var resumeBtn = FindVisualChildren<System.Windows.Controls.Button>(rootGrid)
                    .FirstOrDefault(b => Equals(b.Content, "恢复队列") && b.IsVisible);
                Assert.NotNull(resumeBtn);
                var pausedGrid = Assert.IsAssignableFrom<Grid>(VisualTreeHelper.GetParent(resumeBtn));
                var visBinding = System.Windows.Data.BindingOperations
                    .GetBindingExpression(pausedGrid, UIElement.VisibilityProperty)?.ParentBinding;
                Assert.Equal("IsQueuePaused", visBinding?.Path?.Path);
                vm.IsQueuePaused = false;
                vm.QueuePauseReason = string.Empty;

                // --- State 2: deploying ---
                vm.HasActiveVm = true;
                vm.SessionState = SessionState.Deploying;
                vm.RuntimeBadgeText = "部署中";
                vm.StatusDetailText = "正在部署翻译服务（约 10 分钟）";
                vm.ElapsedDisplay = "6 min / 60 min";
                vm.BillingCuDisplay = "≈ 0.19 CU";
                vm.BillingPolicyDisplay = "到期空闲自动停 · 预计 15:00";
                vm.BillingProgressPercent = 10;
                vm.IsTimedMode = true;
                vm.IsDeploying = true;
                vm.DeployStageText = "阶段 4/5：启动 llama-server";
                vm.DeployProgressPercent = 85;
                vm.HealthStatusDisplay = "已更新 14:42:07";
                vm.GpuInfoDisplay = "NVIDIA L4, 15420 MiB / 23034 MiB";
                vm.CanStartService = false;
                vm.CanStopService = true;
                await Render("deploying", dark: false);
                await Render("deploying", dark: true);

                // Deploying must show a real indeterminate ProgressRing as the status icon —
                // a local <Style> without BasedOn drops the WPF-UI template and renders nothing.
                var statusRing = FindVisualChildren<Wpf.Ui.Controls.ProgressRing>(rootGrid)
                    .FirstOrDefault(r => r.IsVisible);
                Assert.NotNull(statusRing);
                Assert.NotNull(statusRing!.Template);
                Assert.True(statusRing.ActualWidth > 0 && statusRing.ActualHeight > 0);

                // The billing ProgressBar must keep its WPF-UI template too.
                var billingBar = FindVisualChildren<ProgressBar>(rootGrid)
                    .FirstOrDefault(b => b.IsVisible);
                Assert.NotNull(billingBar);
                Assert.NotNull(billingBar!.Template);

                vm.IsDeploying = false;

                // --- State 3: busy translating, all four task row kinds, log expanded ---
                vm.SessionState = SessionState.Busy;
                vm.RuntimeBadgeText = "翻译中";
                vm.StatusDetailText = "正在翻译 第02卷.cbz";
                vm.ElapsedDisplay = "42 min / 60 min";
                vm.BillingCuDisplay = "≈ 1.36 CU";
                vm.BillingProgressPercent = 70;
                vm.LlamaHealthy = true;
                vm.MitHealthy = false;
                vm.CanExtendTime = true;

                vm.Tasks.Add(new TranslationTaskItem("job_1", @"C:\Books\Sejyo.zip")
                {
                    Status = TranslationTaskStatus.Completed,
                    TotalPages = 48,
                    CompletedPages = 48,
                    OutputZipPath = @"C:\Books\Sejyo_translated.zip",
                    DetailHint = @"C:\Books\Sejyo_translated.zip（已校验 48 页）"
                });
                vm.Tasks.Add(new TranslationTaskItem("job_2", @"C:\Books\第02卷.cbz")
                {
                    Status = TranslationTaskStatus.Translating,
                    TotalPages = 87,
                    CompletedPages = 34,
                    DetailHint = "Saving \"/content/jobs/job_2/out/0034.jpg\""
                });
                vm.Tasks.Add(new TranslationTaskItem("job_3", @"C:\Books\damaged.zip")
                {
                    Status = TranslationTaskStatus.Failed,
                    ErrorSummary = "下载结果缺页",
                    RawLog = "Authorization: Bearer secret_abc\nCUDA OOM sample log line"
                });
                vm.Tasks.Add(new TranslationTaskItem("job_4", @"C:\Books\next_volume.cbz")
                {
                    Status = TranslationTaskStatus.Pending,
                    TotalPages = 32,
                    DetailHint = "32 页 · 18.4 MB · 可直传"
                });
                vm.HasTasks = true;

                vm.Logs.Add(new LogEntry(DateTimeOffset.Now, LogLevel.Success, "部署完成！llama :6006 ✓ | mit :8001 (可选/未启)"));
                vm.Logs.Add(new LogEntry(DateTimeOffset.Now, LogLevel.Info, "第02卷.cbz 翻译进度：34/87 页"));
                vm.Logs.Add(new LogEntry(DateTimeOffset.Now, LogLevel.Warning, "damaged.zip 成品校验失败：下载结果缺页"));
                vm.Logs.Add(new LogEntry(DateTimeOffset.Now, LogLevel.Error, "下载结果缺页：期望 48 页实际 31 页"));
                vm.LatestLogLine = "[14:42:07] page 34/87 ok";

                var logExpander = win.FindName("LogExpander") as Expander;
                Assert.NotNull(logExpander);
                logExpander!.IsExpanded = true;
                await Render("busy", dark: false, height: 1000);
                await Render("busy", dark: true, height: 1000);
                logExpander.IsExpanded = false;

                // Task rows must keep the WPF-UI fluent container style (selection pill, hover,
                // focus visuals) — the local ItemContainerStyle must be BasedOn it, not replace it.
                var taskListView = FindVisualChildren<System.Windows.Controls.ListView>(rootGrid)
                    .FirstOrDefault();
                Assert.NotNull(taskListView);
                var rowItem = FindVisualChildren<System.Windows.Controls.ListViewItem>(taskListView!)
                    .FirstOrDefault();
                Assert.NotNull(rowItem);
                Assert.NotNull(rowItem!.Template);
                var fluentItemStyle = Assert.IsAssignableFrom<Style>(
                    Application.Current!.TryFindResource(typeof(Wpf.Ui.Controls.ListViewItem)));
                Assert.Same(fluentItemStyle, rowItem.Style?.BasedOn);

                // The active row's ProgressRing must also keep its template (same BasedOn issue).
                var rowRing = FindVisualChildren<Wpf.Ui.Controls.ProgressRing>(taskListView!)
                    .FirstOrDefault(r => r.IsVisible);
                Assert.NotNull(rowRing);
                Assert.NotNull(rowRing!.Template);

                // --- State 4: error while still billing (two stacked InfoBars) ---
                // Reset all five InfoBar flags first: the async environment precheck may
                // have set HasEnvironmentWarning by now, which would stack a third bar.
                vm.HasErrorBanner = false;
                vm.HasPersistenceWarning = false;
                vm.IsFiveMinuteWarningVisible = false;
                vm.IsQueuePaused = false;
                vm.HasEnvironmentWarning = false;
                vm.EnvironmentWarning = string.Empty;
                vm.SessionState = SessionState.Error;
                vm.RuntimeBadgeText = "异常（仍在计费）";
                vm.StatusDetailText = "部署阶段执行失败，运行环境仍在计费";
                vm.HasErrorBanner = true;
                vm.ErrorBannerText = "colab new 返回非零退出码：资源配额不足";
                vm.HasPersistenceWarning = true;
                vm.PersistenceWarningText = "session.json 写入失败，崩溃后将无法自动恢复计费保护";
                vm.BillingSeverity = BillingSeverity.Danger;
                vm.CanRetryDeploy = true;
                await Render("error_billing", dark: false);
                await Render("error_billing", dark: true);

                // Exactly two InfoBars are open in the error+billing state (error + persistence).
                int openInfoBars = 0;
                foreach (var bar in FindVisualChildren<InfoBar>(rootGrid))
                {
                    if (bar.IsOpen)
                    {
                        openInfoBars++;
                    }
                }
                Assert.Equal(2, openInfoBars);

                // The theme switch must actually swap the WPF-UI theme dictionary. Read the
                // semantic ink from inside the active theme dict itself: app.Resources[key]
                // walks every merged dict, so a palette-level hit would hide a failed swap.
                static Color ReadThemeInk()
                {
                    var themeDict = Application.Current!.Resources.MergedDictionaries.First(d =>
                    {
                        var src = d.Source?.ToString();
                        return src is not null
                            && src.IndexOf("wpf.ui;", StringComparison.OrdinalIgnoreCase) >= 0
                            && src.IndexOf("theme", StringComparison.OrdinalIgnoreCase) >= 0;
                    });
                    return Assert.IsAssignableFrom<SolidColorBrush>(themeDict["TextFillColorPrimaryBrush"]).Color;
                }

                InitAppResources(dark: false);
                themeService.Apply(ThemePreference.Light, persist: false);
                var lightInk = ReadThemeInk();
                InitAppResources(dark: true);
                themeService.Apply(ThemePreference.Dark, persist: false);
                var darkInk = ReadThemeInk();
                Assert.NotEqual(lightInk, darkInk);

                // Every DropDownButton opens its ContextMenu. The title-bar "更多" button lives in
                // TitleBar.Header (it renders in the non-client chrome layer, so it is not a visual
                // descendant of the content grid — verify it via the logical Header property).
                var dropDowns = FindVisualChildren<DropDownButton>(rootGrid).ToList();
                var tb = Assert.IsAssignableFrom<TitleBar>(FindVisualChildren<TitleBar>(rootGrid).FirstOrDefault());
                var moreButton = Assert.IsAssignableFrom<DropDownButton>(tb.Header);
                Assert.Equal("更多", AutomationProperties.GetName(moreButton));
                dropDowns.Add(moreButton);
                Assert.True(dropDowns.Count >= 3, $"Expected >= 3 drop-down buttons, found {dropDowns.Count}");
                // A ContextMenu popup cannot actually open off-screen (no PresentationSource), so
                // clickability itself needs real-window verification in S4. What we can verify here:
                // the Flyout wiring, the PlacementTarget DataContext bridge and command resolution.
                foreach (var button in dropDowns)
                {
                    var menu = Assert.IsAssignableFrom<ContextMenu>(button.Flyout);
                    // DropDownButton.OnClick sets PlacementTarget + IsOpen on the ContextMenu; the
                    // popup cannot open off-screen, so apply the PlacementTarget effect directly and
                    // verify the PlacementTarget.DataContext.* command paths resolve.
                    menu.PlacementTarget = button;
                    Assert.True(menu.Items.Count > 0, "ContextMenu has no items");
                    var leafItems = menu.Items.OfType<System.Windows.Controls.MenuItem>().Where(i => !i.Items.Cast<object>().Any()).ToList();
                    Assert.All(leafItems, item => Assert.True(item.Command != null,
                        $"{item.Header}: Command did not resolve through PlacementTarget.DataContext"));
                }

                // Theme submenu: checkable items resolve through the same DataContext bridge.
                var moreMenu = Assert.IsAssignableFrom<ContextMenu>(moreButton.Flyout);
                var themeItem = Assert.IsAssignableFrom<Wpf.Ui.Controls.MenuItem>(moreMenu.Items.OfType<Wpf.Ui.Controls.MenuItem>().First());
                var themeSubItems = themeItem.Items.OfType<Wpf.Ui.Controls.MenuItem>().ToList();
                Assert.Equal(3, themeSubItems.Count);
                for (int i = 0; i < themeSubItems.Count; i++)
                {
                    Assert.Equal(vm.ThemePreference == (ThemePreference)i, themeSubItems[i].IsChecked);
                }
                Assert.All(themeSubItems, item => Assert.NotNull(item.Command));

                // Engine submenu: second top-level item, 2 checkable entries, follows CanChangeEngine.
                var engineItem = moreMenu.Items.OfType<Wpf.Ui.Controls.MenuItem>().ElementAt(1);
                Assert.Equal("翻译引擎", engineItem.Header);
                var engineSubItems = engineItem.Items.OfType<Wpf.Ui.Controls.MenuItem>().ToList();
                Assert.Equal(2, engineSubItems.Count);
                Assert.All(engineSubItems, item => Assert.NotNull(item.Command));
                Assert.Equal(vm.EnginePreference == TranslationEngineChoice.Mtui, engineSubItems[0].IsChecked);
                Assert.Equal(vm.EnginePreference == TranslationEngineChoice.Mit, engineSubItems[1].IsChecked);
                vm.CanChangeEngine = false;
                Assert.False(engineItem.IsEnabled);
                vm.CanChangeEngine = true;
                Assert.True(engineItem.IsEnabled);

                // Every ContentDialog renders through the real DialogHost in both themes.
                var dialogHost = Assert.IsAssignableFrom<ContentPresenter>(win.FindName("DialogHost"));

                // --- Spec 10.1: Snackbar placement/layering (spec 12 previously-unverified item) ---
                // The presenter sits in the root grid between the page content and the dialog
                // host, so transient notices render above the page but below in-window dialogs.
                InitAppResources(dark: false);
                themeService.Apply(ThemePreference.Light, persist: false);
                ((Panel)rootGrid).Background = new SolidColorBrush(Colors.White);
                var snackbarPresenter = Assert.IsAssignableFrom<SnackbarPresenter>(win.FindName("SnackbarPresenter"));
                var pageContent = Assert.IsAssignableFrom<UIElement>(win.FindName("PageContent"));
                var rootPanel = Assert.IsAssignableFrom<Panel>(rootGrid);
                Assert.True(rootPanel.Children.IndexOf(pageContent) < rootPanel.Children.IndexOf(snackbarPresenter),
                    "SnackbarPresenter must render above the page content");
                Assert.True(rootPanel.Children.IndexOf(snackbarPresenter) < rootPanel.Children.IndexOf(dialogHost),
                    "SnackbarPresenter must render below the dialog host");

                var snackbar = new Snackbar(snackbarPresenter)
                {
                    Title = "翻译完成",
                    Content = "Sejyo.zip 已通过校验并保存至本地。",
                    Appearance = ControlAppearance.Secondary,
                    Timeout = TimeSpan.FromMinutes(5)
                };
                snackbar.Show();
                Assert.True(snackbar.IsShown);
                Assert.Same(snackbar, snackbarPresenter.Content);
                await Task.Delay(300);
                if (!string.IsNullOrWhiteSpace(shotDir))
                {
                    SavePng(rootGrid, Path.Combine(shotDir, "mainwindow_snackbar_light.png"), 900, 680);
                }

                // 7.2: task rows carry the right-click menu; per-item visibility + resolvable commands.
                var taskList = Assert.IsAssignableFrom<System.Windows.Controls.ListView>(win.FindName("TaskListView"));
                var rowContainer = FindVisualChildren<System.Windows.Controls.ListViewItem>(taskList).FirstOrDefault();
                Assert.NotNull(rowContainer);
                var taskMenu = Assert.IsAssignableFrom<ContextMenu>(rowContainer!.ContextMenu);
                taskMenu.PlacementTarget = rowContainer;
                var taskMenuItems = taskMenu.Items.OfType<Wpf.Ui.Controls.MenuItem>().ToList();
                Assert.Equal(4, taskMenuItems.Count);
                Assert.Equal(new[] { "打开所在位置", "从头重试", "查看失败日志", "移除" },
                    taskMenuItems.Select(i => i.Header).ToArray());
                Assert.All(taskMenuItems, i => Assert.NotNull(i.Command));

                // Routing check: while the window is the foreground window, a system notice must
                // surface as a Snackbar (the tray balloon is the fallback for hidden/inactive).
                win.Activate();
                if (win.IsActive)
                {
                    await snackbarPresenter.HideCurrent();
                    // Retrying a completed task is rejected instantly with a notice — a safe way to
                    // exercise the real foreground-routing path (notice -> Snackbar, not balloon).
                    var completedItem = vm.Tasks.First(t => t.IsCompleted);
                    await vm.RetryTaskCommand.ExecuteAsync(completedItem);
                    Assert.IsAssignableFrom<Snackbar>(snackbarPresenter.Content);

                    // The same notice raised from a pool thread (services' ConfigureAwait(false)
                    // continuations) must marshal to the UI thread, not throw on thread-affine
                    // properties like IsActive/IsVisible. The command itself cannot run off-thread
                    // (CanExecute touches thread-affine button state), so invoke the event's
                    // subscriber list directly — this hits the real MainWindow handler.
                    await snackbarPresenter.HideCurrent();
                    var noticeField = typeof(MainViewModel).GetField("RequestSystemNotice",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    var noticeHandlers = Assert.IsAssignableFrom<Action<string, string>>(noticeField!.GetValue(vm));
                    await Task.Run(() => noticeHandlers("无法重试任务", "pool-thread notice"));
                    await Task.Delay(300); // pump the dispatcher so the BeginInvoke'd handler runs
                    Assert.IsAssignableFrom<Snackbar>(snackbarPresenter.Content);

                    // Z-order evidence (spec: snackbar sits above page content, below dialogs).
                    // The snackbar lives in the window's visual tree as a sibling declared BEFORE
                    // DialogHost (asserted above), so an open dialog's smoke layer must cover it —
                    // sample the snackbar's center pixel before and while a dialog is open and
                    // require it to dim. If the snackbar were in a Popup, this would stay bright.
                    var snackbarElement = Assert.IsAssignableFrom<FrameworkElement>(snackbarPresenter.Content);
                    var dpi = VisualTreeHelper.GetDpi(rootGrid).DpiScaleX;
                    var sampleAt = snackbarElement.TransformToAncestor(rootGrid)
                        .Transform(new Point(snackbarElement.ActualWidth / 2, snackbarElement.ActualHeight / 2));
                    Color SampleSnackbarPixel()
                    {
                        var rtb = new RenderTargetBitmap(
                            (int)Math.Ceiling(rootGrid.RenderSize.Width * dpi),
                            (int)Math.Ceiling(rootGrid.RenderSize.Height * dpi),
                            96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
                        rtb.Render(rootGrid);
                        var buf = new byte[4];
                        rtb.CopyPixels(new Int32Rect(
                            (int)(sampleAt.X * dpi), (int)(sampleAt.Y * dpi), 1, 1), buf, 4, 0);
                        return Color.FromRgb(buf[2], buf[1], buf[0]);
                    }
                    var beforePixel = SampleSnackbarPixel();

                    var zorderDlg = new CloseGuardDialog(new ConfirmStopSummary("42 min", "约 1.36 CU", 0, false));
                    zorderDlg.DialogHost = dialogHost;
                    var zorderShown = zorderDlg.ShowAsync();
                    await Task.Delay(300);
                    if (!string.IsNullOrWhiteSpace(shotDir))
                    {
                        SavePng(rootGrid, Path.Combine(shotDir, "dialog_over_snackbar.png"), 900, 680);
                    }
                    var duringPixel = SampleSnackbarPixel();
                    zorderDlg.Hide();
                    await zorderShown;

                    Assert.True(
                        duringPixel.R < beforePixel.R || duringPixel.G < beforePixel.G || duringPixel.B < beforePixel.B,
                        $"Snackbar pixel did not dim under the dialog overlay: {beforePixel} -> {duringPixel}");

                    // Keep screenshots deterministic: the snackbar must not leak into later shots.
                    await snackbarPresenter.HideCurrent();
                }
                else
                {
                    await snackbarPresenter.HideCurrent();
                }

                var now = new DateTimeOffset(2026, 11, 22, 15, 0, 0, TimeSpan.Zero);
                var expiryTimer = new ManualUiTimer(TimeSpan.FromSeconds(1));
                var diagSnapshot = new DiagnosticsSnapshot(
                    "v1.1.1", ".NET 8.0", @"C:\mock\colab.exe", "直连 exe 模式",
                    @"Z:\colab_manga", "自动发现", @"Z:\colab_manga\gui\assets",
                    @"%APPDATA%\manga_gui\session.json",
                    new Dictionary<string, string> { ["vm_job.sh"] = "0123456789abcdef" },
                    IsElevated: false,
                    CrashLogPath: @"%APPDATA%\manga_gui\crash.log");

                // name -> factory; every dialog is a ContentDialog, never an independent Window.
                var dialogFactories = new (string Name, Func<AppContentDialog> Create)[]
                {
                    ("start_session", () => new StartSessionDialog()),
                    ("confirm_stop", () => new ConfirmStopDialog(new ConfirmStopSummary("42 min", "约 1.36 CU", 2, false))),
                    ("time_expired_busy", () => new TimeExpiredDialog(new TimeExpiredDialogViewModel(
                        new TimeExpiredRequest("60 min", "约 1.94 CU", 3, TimeExpiredScenario.BusyWithTasks,
                            TimeExpiredAction.DrainThenStop, TimeSpan.FromMinutes(3)),
                        now, () => now, expiryTimer))),
                    ("time_expired_deploying", () => new TimeExpiredDialog(new TimeExpiredDialogViewModel(
                        new TimeExpiredRequest("60 min", "约 1.94 CU", 0, TimeExpiredScenario.DeployingTimeout,
                            TimeExpiredAction.StopImmediately, TimeSpan.FromMinutes(2)),
                        now, () => now, expiryTimer))),
                    ("orphan_session", () => new OrphanSessionDialog()),
                    ("close_guard", () => new CloseGuardDialog(new ConfirmStopSummary("42 min", "约 1.36 CU", 0, false))),
                    ("log_viewer", () => new LogViewerDialog("damaged.zip 的日志", "line1\nline2\nAuthorization: Bearer secret")),
                    ("environment", () => new EnvironmentCheckDialog()),
                    ("diagnostics", () => new DiagnosticsDialog(diagSnapshot)),
                };

                foreach (var (name, create) in dialogFactories)
                {
                    foreach (var darkTheme in new[] { false, true })
                    {
                        InitAppResources(darkTheme);
                        themeService.Apply(darkTheme ? ThemePreference.Dark : ThemePreference.Light, persist: false);
                        var dlg = create();
                        // All dialogs are ContentDialogs hosted in DialogHost, never Window instances.
                        Assert.IsAssignableFrom<ContentDialog>(dlg);
                        dlg.DialogHost = dialogHost;
                        var shown = dlg.ShowAsync();
                        Assert.False(shown.IsCompleted, $"{name}: ShowAsync returned before the dialog was hidden");
                        await Task.Delay(250); // let the dialog template render
                        Assert.Same(dlg, dialogHost.Content);
                        if (!string.IsNullOrWhiteSpace(shotDir) && !darkTheme)
                        {
                            SavePng(rootGrid, Path.Combine(shotDir, $"dialog_{name}.png"), 900, 680);
                        }
                        dlg.Hide();
                        var res = await shown;
                        Assert.Equal(ContentDialogResult.None, res);
                        Assert.Null(dialogHost.Content);
                    }
                }
            }
            finally
            {
                // Hide, not Close: Close() fires the production close guard (modal dialog /
                // MessageBox) because the last rendered state still reports an active session.
                win?.Hide();
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        });
    }

    [Fact]
    public void AllResourceKeysInViewsXaml_ResolveUnderLightAndDarkThemes()
    {
        // Collect every {DynamicResource Key}/{StaticResource Key} written in Views\*.xaml and
        // assert each resolves at app level under both themes. Type-keyed lookups
        // (BasedOn="{StaticResource {x:Type ui:ContentDialog}}") are markup extensions, not string
        // keys — they resolve the implicit WPF-UI style and are excluded by the identifier pattern.
        var viewsDir = FindViewsDirectory();
        var keyPattern = new System.Text.RegularExpressions.Regex(@"\{(?:Dynamic|Static)Resource\s+([A-Za-z0-9_.]+)");
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(viewsDir, "*.xaml"))
        {
            foreach (System.Text.RegularExpressions.Match m in keyPattern.Matches(File.ReadAllText(file)))
            {
                keys.Add(m.Groups[1].Value);
            }
        }
        Assert.NotEmpty(keys);

        _wpf.Run(() =>
        {
            foreach (var dark in new[] { false, true })
            {
                InitAppResources(dark);
                foreach (var key in keys)
                {
                    Assert.True(Application.Current!.TryFindResource(key) is not null,
                        $"Resource key '{key}' does not resolve under {(dark ? "dark" : "light")} theme");
                }
            }
        });
    }

    private static string FindViewsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "MangaGui", "Views");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new DirectoryNotFoundException("Cannot locate src\\MangaGui\\Views relative to the test assembly.");
    }

    [Fact]
    public void LocalStyles_InViewsXaml_InheritImplicitStyleWhenOneExists()
    {
        // A local <Style TargetType="T"> with no BasedOn fully replaces T's implicit style —
        // for WPF-UI controls (ui:ProgressRing, ui:SymbolIcon, ...) that throws away the whole
        // control template, so the control renders nothing. Every local style whose TargetType
        // resolves to an implicit style in app resources must say
        // BasedOn="{StaticResource {x:Type T}}".
        var viewsDir = FindViewsDirectory();
        var styleTag = new System.Text.RegularExpressions.Regex(
            @"<Style\s+[^>]*TargetType\s*=\s*""(?:\{x:Type\s+)?([A-Za-z0-9_:.]+?)\}?""[^>]*>");
        var offenders = new List<string>();
        var probed = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        _wpf.Run(() => InitAppResources(dark: false));

        foreach (var file in Directory.GetFiles(viewsDir, "*.xaml"))
        {
            var text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in styleTag.Matches(text))
            {
                var typeName = m.Groups[1].Value;
                var type = ResolveStyleTargetType(typeName);
                if (type is null)
                {
                    continue; // types we cannot map (converters etc.) are not checked here
                }

                var hasImplicit = false;
                _wpf.Run(() => hasImplicit = Application.Current!.TryFindResource(type) is Style);
                probed[$"{typeName} -> {type.FullName}"] = hasImplicit;
                if (hasImplicit && !m.Value.Contains("BasedOn", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: local Style TargetType={typeName} lacks BasedOn");
                }
            }
        }

        Assert.True(probed.Count > 0);
        Assert.True(offenders.Count == 0,
            "Local styles missing BasedOn:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public async Task Dialogs_RenderWithoutExcessWhitespace()
    {
        // WPF-UI 3.0.5 ContentDialog.MeasureOverride feeds DialogHeight back into the card
        // Border's MaxHeight, so any overshoot becomes bottom whitespace. AppContentDialog
        // pre-wraps the content (Content.MaxWidth, keeping the ResizeWidth clamp path from
        // running) and normalizes DialogHeight to the card's margin-stripped DesiredSize —
        // both fixes live in MeasureOverride, so every dialog converges to its natural
        // content height. Bound: 24 DIP slack for all dialogs (measured ~1 after the fix;
        // previously ~70 on the ResizeWidth path and ~48 from the Padding feedback).
        // Independently, footer buttons must stay >= 16 DIP from the card's left/right/bottom
        // edges — a regression guard for the Padding="0" experiment that squashed the footer.
        await _wpf.RunAsync(async () =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"manga_gui_probe_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            MainWindow? win = null;
            try
            {
                InitAppResources(dark: false);
                var store = new SessionStore(tempDir);
                using var themeService = new ThemeService(store, detectSystemDarkMode: () => false);
                var scripts = new VmScripts(store, tempDir);
                var cli = new FakeColabCli((_, _, _) =>
                    Task.FromResult(new ColabResult(0, "No active sessions found.", string.Empty)));
                var billing = new BillingTimer();
                var mgr = new SessionManager(cli, scripts, store, billing);
                var queue = new TaskQueue(cli, mgr, a => a());
                using var vm = new MainViewModel(cli, scripts, mgr, queue, themeService, a => a(), store);
                using var tray = new TrayService();
                win = new MainWindow(vm, tray) { Left = 0, Top = 0, Opacity = 0, ShowInTaskbar = false, ShowActivated = false, Width = 900, Height = 680 };
                win.Show();
                await Task.Delay(600);

                var dialogHost = (ContentPresenter)win.FindName("DialogHost")!;
                var expiryNow = new DateTimeOffset(2026, 11, 22, 15, 0, 0, TimeSpan.Zero);
                var diagSnapshot = new DiagnosticsSnapshot(
                    "v1.1.1", ".NET 8.0", @"C:\mock\colab.exe", "直连 exe 模式",
                    @"Z:\colab_manga", "自动发现", @"Z:\colab_manga\gui\assets",
                    @"%APPDATA%\manga_gui\session.json",
                    new Dictionary<string, string> { ["vm_job.sh"] = "0123456789abcdef" },
                    IsElevated: false, CrashLogPath: @"%APPDATA%\manga_gui\crash.log");
                var dialogs = new (string Name, bool HasFooter, Func<AppContentDialog> Create)[]
                {
                    ("start_session", true, () => new StartSessionDialog()),
                    ("confirm_stop", true, () => new ConfirmStopDialog(new ConfirmStopSummary("42 min", "约 1.36 CU", 2, false))),
                    ("time_expired_busy", false, () => new TimeExpiredDialog(new TimeExpiredDialogViewModel(
                        new TimeExpiredRequest("60 min", "约 1.94 CU", 3, TimeExpiredScenario.BusyWithTasks,
                            TimeExpiredAction.DrainThenStop, TimeSpan.FromMinutes(3)),
                        expiryNow, () => expiryNow, new ManualUiTimer(TimeSpan.FromSeconds(1))))),
                    ("time_expired_deploying", false, () => new TimeExpiredDialog(new TimeExpiredDialogViewModel(
                        new TimeExpiredRequest("60 min", "约 1.94 CU", 0, TimeExpiredScenario.DeployingTimeout,
                            TimeExpiredAction.StopImmediately, TimeSpan.FromMinutes(2)),
                        expiryNow, () => expiryNow, new ManualUiTimer(TimeSpan.FromSeconds(1))))),
                    ("orphan_session", true, () => new OrphanSessionDialog()),
                    ("close_guard", true, () => new CloseGuardDialog(new ConfirmStopSummary("42 min", "约 1.36 CU", 0, false))),
                    ("log_viewer", true, () => new LogViewerDialog("damaged.zip 的日志", "line1\nline2")),
                    ("environment", true, () => new EnvironmentCheckDialog()),
                    ("diagnostics", true, () => new DiagnosticsDialog(diagSnapshot)),
                };

                foreach (var (name, hasFooter, create) in dialogs)
                {
                    var dlg = create();
                    dlg.DialogHost = dialogHost;
                    var shown = dlg.ShowAsync();
                    await Task.Delay(300);

                    // Smoke grid -> card Border -> inner grid.
                    var smoke = Assert.IsAssignableFrom<Grid>(VisualTreeHelper.GetChild(dlg, 0));
                    var card = Assert.IsAssignableFrom<Border>(VisualTreeHelper.GetChild(smoke, 0));
                    var inner = Assert.IsAssignableFrom<Grid>(VisualTreeHelper.GetChild(card, 0));
                    var slack = Math.Abs(inner.ActualHeight - inner.DesiredSize.Height);
                    Assert.True(slack <= 24,
                        $"{name}: dialog bottom whitespace/clipping {slack:F0}px exceeds the tolerated bound");

                    if (hasFooter)
                    {
                        // Footer buttons live in the bottom band of the card; content buttons
                        // (e.g. log copy buttons) sit higher and are ignored.
                        var footerButtons = new List<ButtonBase>();
                        void CollectButtons(DependencyObject node, int depth)
                        {
                            if (depth > 14) return;
                            if (node is ButtonBase b
                                && b.IsVisible
                                && card.ActualHeight - (b.TransformToAncestor(card)
                                        .Transform(new Point(0, b.ActualHeight)).Y) < 60)
                            {
                                footerButtons.Add(b);
                            }
                            int n = VisualTreeHelper.GetChildrenCount(node);
                            for (int i = 0; i < n; i++)
                            {
                                CollectButtons(VisualTreeHelper.GetChild(node, i), depth + 1);
                            }
                        }
                        CollectButtons(card, 0);
                        Assert.NotEmpty(footerButtons);
                        foreach (var b in footerButtons)
                        {
                            var tl = b.TransformToAncestor(card).Transform(new Point(0, 0));
                            var left = tl.X;
                            var right = card.ActualWidth - tl.X - b.ActualWidth;
                            var bottom = card.ActualHeight - tl.Y - b.ActualHeight;
                            Assert.True(left >= 16 && right >= 16 && bottom >= 16,
                                $"{name}: footer button too close to card edge " +
                                $"(left {left:F0}, right {right:F0}, bottom {bottom:F0})");
                        }
                    }

                    dlg.Hide();
                    await shown;
                }
            }
            finally
            {
                win?.Hide();
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        });
    }

    [Fact]
    public async Task EscapeForwarder_HidesHostedDialogAsNone_WhenFocusIsOutside()
    {
        // Defect: while a dialog is up the page content is disabled, so keyboard focus can end
        // up on the window itself — Esc then never reaches AppContentDialog.OnKeyDown.
        // DialogKeyboardGuard forwards Esc to the hosted dialog as the billing-safe None.
        await _wpf.RunAsync(async () =>
        {
            InitAppResources(dark: false);
            var win = new Window
            {
                Width = 800,
                Height = 600,
                Left = 0,
                Top = 0,
                Opacity = 0,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            var host = new ContentPresenter();
            var outsideButton = new System.Windows.Controls.Button { Content = "outside" };
            var root = new Grid();
            root.Children.Add(outsideButton);
            root.Children.Add(host);
            win.Content = root;
            win.Show();
            try
            {
                // Focus outside the dialog: the guard forwards Esc -> ShowAsync resolves None.
                var dlg = new ConfirmStopDialog(new ConfirmStopSummary("42 min", "约 1.36 CU", 0, false));
                dlg.DialogHost = host;
                var shown = dlg.ShowAsync();
                await Task.Delay(300);
                Assert.Same(dlg, host.Content);

                Keyboard.Focus(outsideButton);
                Assert.False(dlg.IsKeyboardFocusWithin);
                Assert.True(DialogKeyboardGuard.TryHandleEscape(host));
                var result = await shown.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(ContentDialogResult.None, result);
                Assert.Null(host.Content);

                // Focus inside the dialog: the guard steps aside (the dialog's own OnKeyDown
                // handles Esc there — forwarding would double-hide).
                var dlg2 = new ConfirmStopDialog(new ConfirmStopSummary("42 min", "约 1.36 CU", 0, false));
                dlg2.DialogHost = host;
                var shown2 = dlg2.ShowAsync();
                await Task.Delay(300);
                Keyboard.Focus(dlg2);
                Assert.True(dlg2.IsKeyboardFocusWithin, "test env could not focus the dialog");
                Assert.False(DialogKeyboardGuard.TryHandleEscape(host));
                Assert.False(shown2.IsCompleted);
                dlg2.Hide();
                Assert.Equal(ContentDialogResult.None, await shown2.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                win.Close();
            }
        });
    }

    [Fact]
    public async Task CloseGuardDialog_WrapsBeforeWidthClamp_NoBottomWhitespace()
    {
        // Defect: the content's natural width exceeded DialogMaxWidth, so WPF-UI's ResizeWidth
        // rewrote DialogHeight from a margin-inclusive DesiredSize (~70 DIP bottom whitespace).
        // AppContentDialog caps Content.MaxWidth so the first measure already wraps; afterwards
        // DialogHeight is normalized to the card's real needed height — the inner grid must end
        // up no taller than its own DesiredSize (margin-free, small tolerance for rounding).
        await _wpf.RunAsync(async () =>
        {
            InitAppResources(dark: false);
            var win = new Window
            {
                Width = 900,
                Height = 680,
                Left = 0,
                Top = 0,
                Opacity = 0,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            var host = new ContentPresenter();
            win.Content = host;
            win.Show();
            try
            {
                var dlg = new CloseGuardDialog(new ConfirmStopSummary("42 min", "约 1.36 CU", 0, false));
                dlg.DialogHost = host;
                var shown = dlg.ShowAsync();
                await Task.Delay(400);
                Assert.Same(dlg, host.Content);

                Assert.True(dlg.DialogWidth <= dlg.DialogMaxWidth,
                    $"DialogWidth {dlg.DialogWidth} exceeded DialogMaxWidth {dlg.DialogMaxWidth} (ResizeWidth path taken)");

                var smoke = Assert.IsAssignableFrom<Grid>(VisualTreeHelper.GetChild(dlg, 0));
                var card = Assert.IsAssignableFrom<Border>(VisualTreeHelper.GetChild(smoke, 0));
                var inner = Assert.IsAssignableFrom<Grid>(VisualTreeHelper.GetChild(card, 0));
                var slack = inner.ActualHeight - inner.DesiredSize.Height;
                Assert.True(slack <= 4,
                    $"dialog card is {slack:F0} DIP taller than its content needs (bottom whitespace)");

                dlg.Hide();
                Assert.Equal(ContentDialogResult.None, await shown.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                win.Close();
            }
        });
    }

    [Fact]
    public void LayoutTokens_InViewsXaml_OnlyUseSpecSpacingScale()
    {
        // Spec 3.3: spacing values come only from {0,4,8,12,16,24}; CornerRadius is allowed
        // exactly once (the drag-hint card, radius 8) — everything else uses control defaults.
        var allowed = new HashSet<string> { "0", "4", "8", "12", "16", "24" };
        // Bare element attributes only — \s excludes compound DPs like DialogMargin
        // (framework chrome tokens, not spec-3.3 layout spacing).
        var spacing = new System.Text.RegularExpressions.Regex(@"\s(?:Margin|Padding)=""([0-9.,]+)""");
        var radius = new System.Text.RegularExpressions.Regex(@"CornerRadius=""([0-9.,]+)""");
        var offenders = new List<string>();
        var radiusHits = new List<string>();

        foreach (var file in Directory.GetFiles(FindViewsDirectory(), "*.xaml"))
        {
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in spacing.Matches(text))
            {
                foreach (var v in m.Groups[1].Value.Split(','))
                {
                    if (!allowed.Contains(v))
                    {
                        offenders.Add($"{name}: {m.Value} contains non-scale value {v}");
                    }
                }
            }
            foreach (System.Text.RegularExpressions.Match m in radius.Matches(text))
            {
                radiusHits.Add($"{name}: {m.Value}");
                if (m.Groups[1].Value.Split(',').Any(v => v != "8"))
                {
                    offenders.Add($"{name}: {m.Value} uses a non-8 radius");
                }
            }
        }

        Assert.True(radiusHits.Count <= 1,
            "CornerRadius must appear at most once (the drag-hint card); found:\n" + string.Join("\n", radiusHits));
        Assert.True(offenders.Count == 0,
            "Layout token violations:\n" + string.Join("\n", offenders));
    }

    private static Type? ResolveStyleTargetType(string typeName)
    {
        var colon = typeName.IndexOf(':');
        var ns = colon >= 0 ? typeName[..colon] : string.Empty;
        var name = colon >= 0 ? typeName[(colon + 1)..] : typeName;
        return ns switch
        {
            "ui" => typeof(Wpf.Ui.Controls.SymbolIcon).Assembly.GetType("Wpf.Ui.Controls." + name),
            "views" => typeof(AppContentDialog).Assembly.GetType("MangaGui.Views." + name),
            "" => typeof(StackPanel).Assembly.GetType("System.Windows.Controls." + name),
            _ => null,
        };
    }

    [Fact]
    public async Task MainWindow_LiveDispatcherDragDropAndLogBurst_NeverDesyncsItemContainerGenerator()
    {
        // P0 + S-01 + S-05 regression test:
        // Boots a real off-screen MainWindow on the live WPF Dispatcher so ListCollectionView and
        // ItemContainerGenerator actually attach, then enqueues valid/invalid/folder/duplicate ZIPs
        // plus a same-timestamp log burst and pumps render/background frames to verify zero crashes.
        Exception? unhandledEx = null;
        void OnUnhandled(object s, DispatcherUnhandledExceptionEventArgs e)
        {
            unhandledEx ??= e.Exception;
            e.Handled = true;
        }

        _wpf.Dispatcher.UnhandledException += OnUnhandled;
        try
        {
            var runTask = _wpf.RunAsync(async () =>
            {
                InitAppResources(dark: false);

                var tempDir = Path.Combine(Path.GetTempPath(), $"manga_gui_live_drop_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);

                try
                {
                    var validZip = Path.Combine(tempDir, "book_a.zip");
                    using (var z = ZipFile.Open(validZip, ZipArchiveMode.Create))
                    {
                        TestImageFixtures.WriteBytesEntry(z, "001.jpg", TestImageFixtures.CreateValidJpeg(1));
                    }

                    var folderDrop = Path.Combine(tempDir, "dropped_folder");
                    Directory.CreateDirectory(folderDrop);
                    var folderCbz = Path.Combine(folderDrop, "book_b.cbz");
                    using (var z = ZipFile.Open(folderCbz, ZipArchiveMode.Create))
                    {
                        TestImageFixtures.WriteBytesEntry(z, "001.png", TestImageFixtures.CreateValidPng(2));
                    }

                    var badZip = Path.Combine(tempDir, "corrupt.zip");
                    File.WriteAllText(badZip, "not a zip");

                    var store = new SessionStore(tempDir);
                    using var themeService = new ThemeService(store, detectSystemDarkMode: () => false);
                    themeService.Apply(ThemePreference.Light, persist: false);

                    var scripts = new VmScripts(store, tempDir);
                    var cli = new FakeColabCli((_, _, _) =>
                        Task.FromResult(new ColabResult(0, "No active sessions found.", string.Empty)));
                    var billing = new BillingTimer();
                    var mgr = new SessionManager(cli, scripts, store, billing);
                    Action<Action> uiDispatch = a => App.SafeDispatchToUi(_wpf.Dispatcher, a);
                    var queue = new TaskQueue(cli, mgr, uiDispatch);
                    using var vm = new MainViewModel(cli, scripts, mgr, queue, themeService, uiDispatch, store);
                    using var tray = new TrayService();

                    var win = new MainWindow(vm, tray)
                    {
                        Left = -20000,
                        Top = -20000,
                        ShowInTaskbar = false,
                        ShowActivated = false
                    };

                    var loadedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    win.Loaded += (_, _) => loadedTcs.TrySetResult();

                    win.Show();
                    try
                    {
                        await loadedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        await Task.Delay(120);

                        // Enqueue valid zip, folder containing cbz, invalid zip, and duplicate of valid zip
                        await vm.EnqueueDroppedFilesAsync([validZip, folderDrop, badZip, validZip]);

                        // Rapid burst of identical-timestamp log entries (S-05 + P0 scroll stress)
                        var fixedTs = DateTimeOffset.Now;
                        for (int i = 0; i < 40; i++)
                        {
                            vm.Logs.Add(new LogEntry(fixedTs, LogLevel.Info, "burst identical line"));
                        }

                        // Pump render + background deferred scroll passes
                        await Task.Delay(150);

                        Assert.Equal(2, vm.Tasks.Count);
                        Assert.True(vm.Logs.Count >= 40);
                        Assert.False(vm.IsPrechecking);
                    }
                    finally
                    {
                        queue.CancelPendingAndActive();
                        win.Close();
                    }
                }
                finally
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            });

            await runTask.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            _wpf.Dispatcher.UnhandledException -= OnUnhandled;
        }

        Assert.Null(unhandledEx);
    }

    [Fact]
    public void MultiAccountDialogs_InstantiateCleanlyInLightAndDarkThemes()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"manga_gui_acct_ui_{Guid.NewGuid():N}");
        var acctDir = Path.Combine(dir, "accounts");
        Directory.CreateDirectory(acctDir);
        File.WriteAllText(Path.Combine(acctDir, "acct1.json"), """{"type":"authorized_user","refresh_token":"r1"}""");
        File.WriteAllText(Path.Combine(acctDir, "acct2.json"), """{"type":"authorized_user","refresh_token":"r2"}""");

        try
        {
            _wpf.Run(() =>
            {
                foreach (var dark in new[] { false, true })
                {
                    InitAppResources(dark);
                    var store = new SessionStore(dir);
                    var cli = new FakeColabCli((_, _, _) => Task.FromResult(new ColabResult(0, "", "")));
                    var accounts = new AccountManager(cli, store);
                    using var acctVm = new AccountsDialogViewModel(accounts, dir, () => false, () => null);
                    var acctDlg = new AccountsDialog(acctVm);
                    Assert.NotNull(acctDlg);

                    var startVm = new StartSessionDialogViewModel();
                    startVm.LoadAccountOptions(
                        [
                            new StartAccountOption("auto", "自动轮询（推荐：账号 2 · 200.00 CU）", 200.0, AccountQuotaStatus.Ok, null),
                            new StartAccountOption("acct1", "账号 1（1.20 CU）", 1.2, AccountQuotaStatus.Ok, null)
                        ],
                        "acct1");
                    Assert.True(startVm.HasAccountBalanceWarning);
                    Assert.NotNull(new StartSessionDialog(startVm));
                    Assert.NotNull(new ConfirmStopDialog(new ConfirmStopSummary("12 min", "约 0.39 CU", 0, false, "账号 2")));
                    Assert.NotNull(new OrphanSessionDialog("账号 2"));
                }
            });
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                yield return typed;
            }
            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static void SavePng(UIElement element, string path, int dipWidth, int dipHeight)
    {
        // The bitmap must be sized in device pixels at the element's real DPI — a 96-dpi
        // bitmap of a 125%-DPI window rasterizes only 1/1.25 of the content.
        var scale = VisualTreeHelper.GetDpi(element).DpiScaleX;
        var rtb = new RenderTargetBitmap(
            (int)Math.Round(dipWidth * scale),
            (int)Math.Round(dipHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(element);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

}
