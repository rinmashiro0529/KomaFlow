using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MangaGui.Services;
using MangaGui.ViewModels;
using MangaGui.Views;

namespace MangaGui;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private TrayService? _trayService;
    private ThemeService? _themeService;
    private const int MaxHandledUiExceptions = 20;
    private MainViewModel? _mainViewModel;
    private bool _crashDialogShown;
    private int _handledUiExceptions;

    public static string CrashLogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "manga_gui",
        "crash.log");

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    protected override void OnStartup(StartupEventArgs e)
    {
        // Enable legacy code pages (Shift-JIS CP932 / GBK CP936) for Japanese/Chinese manga ZIP archives.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, AppServices.SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            TryActivateExistingWindow();
            MessageBox.Show(
                "「漫画翻译 · Colab L4」已在运行中，请查看当前窗口或系统托盘图标；也可能是 MangaCli 命令行工具正在执行任务。\n（为避免计费状态冲突和 SSH 429 限流，同一时间仅允许运行一个实例。）",
                "应用已运行",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        var services = AppServices.Create();
        var store = services.Store;
        var scripts = services.Scripts;
        IColabCli cli = services.Cli;
        var sessionManager = services.Session;

        _themeService = new ThemeService(store);
        _themeService.InitializeFromSettings();

        Action<Action> safeDispatch = action => SafeDispatchToUi(Dispatcher, action);
        var taskQueue = new TaskQueue(cli, sessionManager, safeDispatch);

        _mainViewModel = new MainViewModel(
            cli,
            scripts,
            sessionManager,
            taskQueue,
            _themeService,
            safeDispatch,
            store,
            interval => new Views.DispatcherUiTimer(interval));
        _trayService = new TrayService();

        var mainWindow = new MainWindow(_mainViewModel, _trayService);
        MainWindow = mainWindow;
        _themeService.Attach(mainWindow);
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        _trayService?.Dispose();
        _mainViewModel?.Dispose();
        _themeService?.Dispose();
        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch
            {
                // Ignore if mutex was not owned.
            }
            _singleInstanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    public static void SafeDispatchToUi(Dispatcher dispatcher, Action action)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            dispatcher.Invoke(action);
        }
        catch (TaskCanceledException)
        {
            // Dispatcher shut down before action executed.
        }
        catch (OperationCanceledException)
        {
            // Dispatcher shut down before action executed.
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Logging off or shutting down with a runtime still billing: hold the session end and let the
        // close guard ask what to do, instead of silently leaving the runtime running.
        if (_mainViewModel?.HasActiveVm == true && MainWindow is { } window)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(() =>
            {
                window.Show();
                window.Activate();
                window.Close();
            });
        }

        base.OnSessionEnding(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog("DispatcherUnhandledException", e.Exception);

        // While a runtime is billing, keep the process alive so the expiry auto-stop and the close
        // guard keep working. The cap stops a repeating render failure from looping forever.
        bool keepRunning = _mainViewModel?.HasActiveVm == true
                           && Interlocked.Increment(ref _handledUiExceptions) <= MaxHandledUiExceptions;
        if (keepRunning)
        {
            e.Handled = true;
        }

        if (!_crashDialogShown)
        {
            _crashDialogShown = true;
            try
            {
                var billingNotice = keepRunning
                    ? "\n\n⚠️ 当前有正在计费的 Colab L4 会话。程序会尝试继续运行以保留到时自动停止；界面若已异常，请尽快点击「停止并结束计费」或在终端执行 colab stop -s manga。"
                    : _mainViewModel?.HasActiveVm == true
                    ? "\n\n⚠️ 重要提醒：当前存在正在计费的 Colab L4 云端会话！请重新打开本程序恢复接管并停止，或在终端执行 colab stop 避免继续扣费。"
                    : string.Empty;

                MessageBox.Show(
                    $"程序遇到未预期异常：{e.Exception.Message}\n\n" +
                    $"详细诊断堆栈已写入：\n{CrashLogPath}{billingNotice}",
                    "漫画翻译 · 异常保护",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // Avoid secondary crash inside crash handler.
            }
        }
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception
            ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown non-CLS exception");
        WriteCrashLog($"AppDomainUnhandledException (IsTerminating={e.IsTerminating})", ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog("TaskScheduler.UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private void WriteCrashLog(string source, Exception ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(CrashLogPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var hasActiveVm = _mainViewModel?.HasActiveVm ?? false;
            var sessionState = _mainViewModel?.SessionState.ToString() ?? "Unknown";
            var redacted = LogSanitizer.Redact(ex.ToString());

            var entry = new StringBuilder();
            entry.AppendLine("============================================================");
            entry.AppendLine($"Time         : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            entry.AppendLine($"Source       : {source}");
            entry.AppendLine($"SessionState : {sessionState} (HasActiveVm={hasActiveVm})");
            entry.AppendLine($"Framework    : {RuntimeInformation.FrameworkDescription}");
            entry.AppendLine(redacted);
            entry.AppendLine();

            File.AppendAllText(CrashLogPath, entry.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Never throw from crash logger.
        }
    }

    private static void TryActivateExistingWindow()
    {
        try
        {
            var hwnd = FindWindow(null, "漫画翻译 · Colab L4");
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, 9); // SW_RESTORE
                SetForegroundWindow(hwnd);
            }
        }
        catch
        {
            // Best-effort foreground activation.
        }
    }
}
