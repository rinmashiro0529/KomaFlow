using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;
using MangaGui.Services;
using MangaGui.ViewModels;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using WinForms = System.Windows.Forms;

namespace MangaGui.Views;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly TrayService _trayService;
    private readonly DialogService _dialogService;
    private bool _allowDirectClose;
    private bool _isShuttingDown;
    private bool _closeGuardActive;
    private bool _logScrollScheduled;
    private bool _logsSubscribed;
    private IInputElement? _focusedBeforeDialog;
    private ScrollViewer? _logScrollViewer;
    private bool _taskbarUpdateScheduled;

    public MainWindow(MainViewModel viewModel, TrayService trayService)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _trayService = trayService;
        _dialogService = new DialogService(OnDialogShowingChanged);
        DataContext = _viewModel;

        WireViewModelEvents();
        WireTrayEvents();

        _viewModel.PropertyChanged += OnTaskbarRelevantPropertyChanged;
        ((INotifyCollectionChanged)_viewModel.Tasks).CollectionChanged += OnTasksCollectionChanged;

        // NOTE (P0 fix): Do NOT subscribe to _viewModel.Logs.CollectionChanged in the constructor!
        // In WPF, ItemsControl attaches ListCollectionView's CollectionChanged listener when the
        // window is shown/loaded. Subscribing earlier places our handler ahead of WPF's internal
        // ItemContainerGenerator listener; any synchronous layout/scroll call would then desync the
        // generator and crash MediaContext on the next render frame.
        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        Activated += OnWindowActivated;
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _logScrollViewer = FindVisualChild<ScrollViewer>(LogListBox);
        if (!_logsSubscribed)
        {
            ((INotifyCollectionChanged)_viewModel.Logs).CollectionChanged += OnLogsCollectionChanged;
            _logsSubscribed = true;
        }

        ScheduleLogAutoScroll();
        UpdateTaskbarProgress();
        await _viewModel.InitializeAsync();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_logsSubscribed)
        {
            ((INotifyCollectionChanged)_viewModel.Logs).CollectionChanged -= OnLogsCollectionChanged;
            _logsSubscribed = false;
        }

        _trayService.RestoreRequested -= OnTrayRestoreRequested;
        _trayService.StopBillingRequested -= OnTrayStopBillingRequested;
        _trayService.ExitRequested -= OnTrayExitRequested;

        _viewModel.PropertyChanged -= OnTaskbarRelevantPropertyChanged;
        ((INotifyCollectionChanged)_viewModel.Tasks).CollectionChanged -= OnTasksCollectionChanged;
        foreach (var item in _viewModel.Tasks)
        {
            item.PropertyChanged -= OnTaskItemPropertyChanged;
        }
    }

    /// <summary>
    /// While a dialog is on screen the whole page (and the title-bar "…" menu) is disabled and the
    /// previously focused element is remembered; on close both are restored (spec 9.1-6).
    /// </summary>
    private void OnDialogShowingChanged(bool showing)
    {
        if (showing)
        {
            _focusedBeforeDialog = Keyboard.FocusedElement;
            PageContent.IsEnabled = false;
            MoreButton.IsEnabled = false;
        }
        else
        {
            PageContent.IsEnabled = true;
            MoreButton.IsEnabled = true;
            if (_focusedBeforeDialog is not null)
            {
                Keyboard.Focus(_focusedBeforeDialog);
                _focusedBeforeDialog = null;
            }
        }
    }

    private void WireViewModelEvents()
    {
        _viewModel.RequestStartDialog += ShowStartDialogAsync;
        _viewModel.RequestStartDialogWithAccount += ShowStartDialogWithAccountAsync;

        _viewModel.RequestConfirmStopDialog += async summary =>
        {
            var dlg = new ConfirmStopDialog(summary);
            return await _dialogService.ShowContentDialogAsync(dlg, DialogHost)
                == ContentDialogResult.Primary;
        };

        _viewModel.RequestTimeExpiredDialog += ShowTimeExpiredDialogAsync;

        _viewModel.RequestOrphanSessionDialog += async () =>
        {
            var dlg = new OrphanSessionDialog();
            var result = await _dialogService.ShowContentDialogAsync(dlg, DialogHost);
            // None (Esc / interrupted) must never change billing: it maps to Dismiss.
            return result == ContentDialogResult.None
                ? OrphanSessionAction.Dismiss
                : dlg.SelectedAction;
        };

        _viewModel.RequestOrphanSessionDialogForAccount += async accountName =>
        {
            var dlg = new OrphanSessionDialog(accountName);
            var result = await _dialogService.ShowContentDialogAsync(dlg, DialogHost);
            return result == ContentDialogResult.None
                ? OrphanSessionAction.Dismiss
                : dlg.SelectedAction;
        };

        _viewModel.RequestShowAccountsDialog += async accountsVm =>
        {
            var dlg = new AccountsDialog(accountsVm);
            await _dialogService.ShowContentDialogAsync(dlg, DialogHost);
        };

        // Transient notices: foreground -> in-window Snackbar; hidden/minimized/inactive -> tray balloon.
        _viewModel.RequestSystemNotice += (title, message) => MarshalToUi(() =>
        {
            if (IsWindowInForeground)
            {
                ShowSnackbarNotice(title, message);
            }
            else
            {
                _trayService.ShowBalloon(title, message, WinForms.ToolTipIcon.Info);
            }
        });

        // Five-minute billing warning: the InfoBar already covers the foreground case;
        // add a tray balloon only while the window is away (spec 10.1).
        _viewModel.RequestFiveMinuteWarningToast += msg => MarshalToUi(() =>
        {
            if (!IsWindowInForeground)
            {
                _trayService.ShowBalloon("计费提醒", msg, WinForms.ToolTipIcon.Warning);
            }
        });

        _viewModel.RequestShowLogDialog += (title, content) =>
        {
            _ = _dialogService.ShowContentDialogAsync(new LogViewerDialog(title, content), DialogHost);
        };

        _viewModel.RequestShowDiagnosticsDialog += snapshot =>
        {
            _ = _dialogService.ShowContentDialogAsync(new DiagnosticsDialog(snapshot), DialogHost);
        };

        _viewModel.RequestBrowseScriptsFolder += () =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "选择 colab_manga 目录（含 manga.json）"
            };
            return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
        };

        _viewModel.RequestBrowseOutputFolder += () =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "选择翻译成品输出目录"
            };
            return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
        };

        _viewModel.RequestBrowseZipFiles += () =>
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择要翻译的漫画压缩包（.zip / .cbz）",
                Filter = "漫画压缩包 (*.zip;*.cbz)|*.zip;*.cbz|所有文件 (*.*)|*.*",
                Multiselect = true
            };
            return dlg.ShowDialog(this) == true ? dlg.FileNames : null;
        };
    }

    private async Task<(bool Confirmed, int? PlannedMinutes, bool AutoStop)> ShowStartDialogAsync()
    {
        var dlg = new StartSessionDialog();
        var result = await _dialogService.ShowContentDialogAsync(dlg, DialogHost);
        return result == ContentDialogResult.Primary
            ? (true, dlg.ViewModel.PlannedMinutes, dlg.ViewModel.EffectiveAutoStop)
            : (false, null, false);
    }

    private async Task<bool> ShowStartDialogWithAccountAsync(StartSessionDialogViewModel viewModel)
    {
        var dlg = new StartSessionDialog(viewModel);
        var result = await _dialogService.ShowContentDialogAsync(dlg, DialogHost);
        return result == ContentDialogResult.Primary;
    }

    private async Task<TimeExpiredAction> ShowTimeExpiredDialogAsync(TimeExpiredRequest request)
    {
        // The deadline is anchored at request time inside the VM, so queueing can never postpone it.
        var dialogVm = new TimeExpiredDialogViewModel(
            request,
            DateTimeOffset.UtcNow,
            () => DateTimeOffset.UtcNow,
            new DispatcherUiTimer(TimeSpan.FromSeconds(1)));
        var dlg = new TimeExpiredDialog(dialogVm);

        // When the VM completes first (unattended auto action), pull the dialog down.
        // No `using`: Completion is RunContinuationsAsynchronously, so this continuation is posted
        // to the dispatcher and can run after this method returns — disposing the CTS here would
        // turn a late Cancel() into an unobserved ObjectDisposedException.
        var completionCancel = new CancellationTokenSource();
        _ = dialogVm.Completion.ContinueWith(
            _ =>
            {
                if (!completionCancel.IsCancellationRequested)
                {
                    completionCancel.Cancel();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.FromCurrentSynchronizationContext());

        RestoreFromTrayIfHidden();
        var result = await _dialogService.ShowContentDialogAsync(
            dlg, DialogHost, urgent: true, externalCancel: completionCancel.Token);

        // Esc / interrupted by another urgent dialog: safe default is None (remind later).
        if (result == ContentDialogResult.None)
        {
            dialogVm.Choose(TimeExpiredAction.None);
        }

        return await dialogVm.Completion;
    }

    private void WireTrayEvents()
    {
        _trayService.RestoreRequested += OnTrayRestoreRequested;
        _trayService.StopBillingRequested += OnTrayStopBillingRequested;
        _trayService.ExitRequested += OnTrayExitRequested;
    }

    private void OnTrayRestoreRequested()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(RestoreFromTrayIfHidden);
    }

    private void OnTrayStopBillingRequested()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(() =>
        {
            RestoreFromTrayIfHidden();
            if (_viewModel.CanStopService)
            {
                _viewModel.StopServiceCommand.Execute(null);
            }
        });
    }

    private void OnTrayExitRequested()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(() =>
        {
            RestoreFromTrayIfHidden();
            Close();
        });
    }

    /// <summary>"In the foreground" per spec 10.1: visible, not minimized, and active.</summary>
    private bool IsWindowInForeground => IsVisible && WindowState != WindowState.Minimized && IsActive;

    /// <summary>
    /// The view model raises its notice events from whichever thread its async continuations
    /// happen to run on (services use ConfigureAwait(false)), and WPF properties like
    /// <see cref="IsWindowInForeground"/> are thread-affine — marshal back to the dispatcher
    /// before touching them.
    /// </summary>
    private void MarshalToUi(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
        {
            Dispatcher.BeginInvoke(action);
        }
    }

    private void ShowSnackbarNotice(string title, string message)
    {
        var snackbar = new Snackbar(SnackbarPresenter)
        {
            Title = title,
            Content = message,
            Appearance = ControlAppearance.Secondary,
            Timeout = TimeSpan.FromSeconds(5)
        };
        snackbar.Show();
    }

    private void RestoreFromTrayIfHidden()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        _trayService.HideTrayIcon();
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowDirectClose)
        {
            return;
        }

        if (!_viewModel.HasActiveVm)
        {
            // No active session: closing with pending offline tasks needs one confirmation.
            var pending = _viewModel.Queue.PendingCount;
            if (pending <= 0)
            {
                return;
            }

            // A-13: e.Cancel is set synchronously before any await; state is re-read afterwards.
            e.Cancel = true;
            var confirm = new AppContentDialog
            {
                Title = "确认退出？",
                DialogMaxWidth = 480,
                PrimaryButtonText = "退出",
                CloseButtonText = "取消",
                Content = new System.Windows.Controls.TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"队列中还有 {pending} 个待翻译的漫画任务尚未启动，关闭窗口将清空待处理队列。\n确认退出吗？"
                }
            };
            var confirmResult = await _dialogService.ShowContentDialogAsync(confirm, DialogHost);
            if (confirmResult == ContentDialogResult.Primary && !_viewModel.HasActiveVm)
            {
                _allowDirectClose = true;
                Close();
            }

            return;
        }

        // A runtime is still billing: never let the window close without an explicit choice.
        e.Cancel = true;
        if (_isShuttingDown || _closeGuardActive)
        {
            return;
        }

        var summary = _viewModel.GetCurrentStopSummary();
        _closeGuardActive = true;
        CloseGuardAction action;
        try
        {
            var guard = new CloseGuardDialog(summary);
            var result = await _dialogService.ShowContentDialogAsync(guard, DialogHost, urgent: true);
            action = result == ContentDialogResult.None ? CloseGuardAction.Cancel : guard.SelectedAction;
        }
        finally
        {
            _closeGuardActive = false;
        }

        // The session may have been stopped by another path while the guard was on screen.
        if (!_viewModel.HasActiveVm)
        {
            if (action == CloseGuardAction.StopAndExit)
            {
                _allowDirectClose = true;
                Close();
            }

            return;
        }

        switch (action)
        {
            case CloseGuardAction.MinimizeToTray:
                Hide();
                var trayAcct = string.IsNullOrWhiteSpace(summary.AccountName) ? string.Empty : $"[{summary.AccountName}] ";
                _trayService.ShowTrayIcon($"漫画翻译 · {trayAcct}{summary.ElapsedText} ({summary.EstimatedCuText})");
                _trayService.ShowBalloon("已最小化到系统托盘", $"{trayAcct}Colab L4 仍在后台运行计费，双击托盘图标可恢复窗口。");
                break;

            case CloseGuardAction.StopAndExit:
                _isShuttingDown = true;
                try
                {
                    var res = await _viewModel.ExecuteStopAsync(isAutoStop: false);
                    if (res.VerifiedStopped)
                    {
                        _allowDirectClose = true;
                        Close();
                    }
                    else
                    {
                        var failure = new AppContentDialog
                        {
                            Title = "停止计费失败",
                            DialogMaxWidth = 480,
                            CloseButtonText = "知道了",
                            Content = new System.Windows.Controls.TextBlock
                            {
                                TextWrapping = TextWrapping.Wrap,
                                Text = res.Message
                            }
                        };
                        await _dialogService.ShowContentDialogAsync(failure, DialogHost);
                    }
                }
                finally
                {
                    _isShuttingDown = false;
                }
                break;

            case CloseGuardAction.Cancel:
            default:
                break;
        }
    }

    // ---------- Taskbar progress (spec 10.2) ----------

    private void OnTaskbarRelevantPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SessionState)
            or nameof(MainViewModel.HasActiveVm)
            or nameof(MainViewModel.IsQueuePaused)
            or nameof(MainViewModel.DeployProgressPercent))
        {
            UpdateTaskbarProgress();
        }
    }

    private void OnTasksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (Models.TranslationTaskItem item in e.OldItems)
            {
                item.PropertyChanged -= OnTaskItemPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (Models.TranslationTaskItem item in e.NewItems)
            {
                item.PropertyChanged += OnTaskItemPropertyChanged;
            }
        }

        UpdateTaskbarProgress();
    }

    private void OnTaskItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Models.TranslationTaskItem.IsActive)
            or nameof(Models.TranslationTaskItem.ProgressPercent))
        {
            UpdateTaskbarProgress();
        }
    }

    private void UpdateTaskbarProgress()
    {
        // Property/collection changes can arrive on pool threads — marshal and coalesce.
        if (!Dispatcher.CheckAccess())
        {
            if (_taskbarUpdateScheduled || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            _taskbarUpdateScheduled = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, UpdateTaskbarProgress);
            return;
        }

        _taskbarUpdateScheduled = false;

        // Skip until the HWND exists — ITaskbarList3 calls would throw in early load and in tests.
        if (new WindowInteropHelper(this).Handle == IntPtr.Zero)
        {
            return;
        }

        var activeTask = _viewModel.Tasks.FirstOrDefault(t => t.IsActive);
        var (state, value) = TaskbarProgressMapper.Map(
            _viewModel.SessionState,
            _viewModel.HasActiveVm,
            _viewModel.IsQueuePaused,
            (int)Math.Round(_viewModel.DeployProgressPercent),
            activeTask is null ? null : (int)Math.Round(activeTask.ProgressPercent));

        try
        {
            if (state == Wpf.Ui.TaskBar.TaskBarProgressState.Normal)
            {
                Wpf.Ui.TaskBar.TaskBarProgress.SetValue(this, Wpf.Ui.TaskBar.TaskBarProgressState.Normal, value);
            }
            else
            {
                Wpf.Ui.TaskBar.TaskBarProgress.SetState(this, state);
            }
        }
        catch
        {
            // Taskbar COM glue is best-effort chrome; a shell without ITaskbarList3 must not crash the app.
        }
    }

    // ---------- Keyboard shortcuts (spec 10.3) ----------

    /// <summary>
    /// While a dialog is on screen the whole page (and the title-bar "…" menu) is disabled, so
    /// keyboard focus can end up on the window itself — reactivation restores focus there too.
    /// Hand it back to the dialog so Esc/Enter keep working inside it.
    /// </summary>
    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (_dialogService.IsShowing
            && DialogHost.Content is AppContentDialog dialog
            && !dialog.IsKeyboardFocusWithin)
        {
            dialog.Focus();
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // While a dialog is up, none of the window-level accelerators apply (spec 10.3);
        // Esc inside the dialog itself is handled by AppContentDialog.
        if (_dialogService.IsShowing)
        {
            // Focus may sit outside the dialog (window chrome); Esc still reaches it through
            // the guard, always as the billing-safe None result.
            if (e.Key == Key.Escape && DialogKeyboardGuard.TryHandleEscape(DialogHost))
            {
                e.Handled = true;
            }

            return;
        }

        // Never steal keystrokes from text input.
        if (Keyboard.FocusedElement is TextBoxBase)
        {
            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (ctrl && e.Key == Key.O)
        {
            TryExecute(_viewModel.AddFilesCommand, null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5)
        {
            TryExecute(_viewModel.ReprobeSessionCommand, null);
            e.Handled = true;
            return;
        }

        if (TaskListView.IsKeyboardFocusWithin)
        {
            if (e.Key == Key.Delete)
            {
                foreach (var item in TaskListView.SelectedItems
                    .Cast<Models.TranslationTaskItem>()
                    .Where(t => t.CanRemove)
                    .ToList())
                {
                    TryExecute(_viewModel.RemoveTaskCommand, item);
                }
                e.Handled = true;
                return;
            }

            // Enter activates one row — the keyboard-focused item, falling back to the single
            // selection; let in-row buttons keep their own Enter.
            if (e.Key == Key.Enter && e.OriginalSource is not ButtonBase)
            {
                var item = (FindAncestorOfType<System.Windows.Controls.ListViewItem>(
                        Keyboard.FocusedElement as DependencyObject)?.DataContext
                    ?? TaskListView.SelectedItem) as Models.TranslationTaskItem;
                if (item is not null)
                {
                    ActivateTaskItem(item);
                }
                e.Handled = true;
                return;
            }
        }

        if (LogListBox.IsKeyboardFocusWithin && ctrl && e.Key == Key.C)
        {
            CopySelectedLogLines();
            e.Handled = true;
        }
    }

    private void ActivateTaskItem(Models.TranslationTaskItem item)
    {
        if (item.IsCompleted)
        {
            TryExecute(_viewModel.OpenOutputFolderCommand, item);
        }
        else if (item.CanViewLog)
        {
            TryExecute(_viewModel.ViewTaskLogCommand, item);
        }
    }

    private static void TryExecute(System.Windows.Input.ICommand command, object? parameter)
    {
        if (command.CanExecute(parameter))
        {
            command.Execute(parameter);
        }
    }

    private void CopySelectedLogLines()
    {
        try
        {
            var text = string.Join(Environment.NewLine,
                LogListBox.SelectedItems.Cast<Models.LogEntry>().Select(l => l.FormattedLine));
            if (!string.IsNullOrEmpty(text))
            {
                Clipboard.SetText(text);
            }
        }
        catch
        {
            // Clipboard can throw when another app holds it open; copying logs is best-effort.
        }
    }

    // ---------- Task row interactions ----------

    private void OnTaskItemPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Standard shell behavior: right-clicking an unselected row selects just that row;
        // right-clicking inside the current selection keeps the multi-selection.
        if (sender is System.Windows.Controls.ListViewItem item && !item.IsSelected)
        {
            TaskListView.SelectedItem = item.DataContext;
        }
    }

    private void OnTaskItemMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // A double-click that began on an in-row button belongs to the button.
        if (e.OriginalSource is DependencyObject source && IsWithin<ButtonBase>(source))
        {
            return;
        }

        if (sender is System.Windows.Controls.ListViewItem item && item.DataContext is Models.TranslationTaskItem task)
        {
            ActivateTaskItem(task);
        }
    }

    private static bool IsWithin<T>(DependencyObject source) where T : DependencyObject
        => FindAncestorOfType<T>(source) is not null;

    private static T? FindAncestorOfType<T>(DependencyObject? source) where T : DependencyObject
    {
        for (DependencyObject? current = source; current is not null;)
        {
            if (current is T match)
            {
                return match;
            }
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private void OnWindowDragEnter(object sender, DragEventArgs e)
    {
        UpdateDragEffectsAndHighlight(e);
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        UpdateDragEffectsAndHighlight(e);
    }

    private void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        _viewModel.IsDragOver = false;
        e.Handled = true;
    }

    private void UpdateDragEffectsAndHighlight(DragEventArgs e)
    {
        try
        {
            bool hasFiles = e.Data is not null && e.Data.GetDataPresent(DataFormats.FileDrop);
            if (hasFiles && _viewModel.IsDropZoneActive)
            {
                e.Effects = DragDropEffects.Copy;
                _viewModel.IsDragOver = true;
            }
            else
            {
                e.Effects = DragDropEffects.None;
                _viewModel.IsDragOver = false;
            }
        }
        catch
        {
            // Malformed or inaccessible COM IDataObject from third-party shell extensions.
            e.Effects = DragDropEffects.None;
            _viewModel.IsDragOver = false;
        }

        e.Handled = true;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        _viewModel.IsDragOver = false;
        e.Handled = true;

        string[]? copiedFiles = null;
        try
        {
            if (e.Data is not null
                && e.Data.GetDataPresent(DataFormats.FileDrop)
                && e.Data.GetData(DataFormats.FileDrop) is string[] files
                && files.Length > 0)
            {
                // Copy payload immediately and return synchronously from OLE IDropTarget::Drop
                // so Explorer never freezes and COM never swallows downstream UI exceptions.
                copiedFiles = files.ToArray();
            }
        }
        catch (Exception ex)
        {
            _viewModel.Logs.Add(new Models.LogEntry(
                DateTimeOffset.Now,
                Models.LogLevel.Warning,
                $"读取拖入文件列表失败（可能来自不支持的虚拟目录或压缩包内直接拖出）：{ex.Message}"));
            return;
        }

        if (copiedFiles is not null && !Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                _ = _viewModel.EnqueueDroppedFilesAsync(copiedFiles);
            });
        }
    }

    private void OnCopyAllLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = string.Join(Environment.NewLine, _viewModel.Logs.Select(l => l.FormattedLine));
            if (!string.IsNullOrEmpty(text))
            {
                Clipboard.SetText(text);
            }
        }
        catch
        {
            // Clipboard can throw when another app holds it open; copying logs is best-effort.
        }
    }

    private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
        {
            return;
        }

        // Coalesced, deferred scroll outside CollectionChanged so WPF's ItemContainerGenerator
        // always finishes synchronizing before any scroll/layout happens.
        ScheduleLogAutoScroll();
    }

    private void ScheduleLogAutoScroll()
    {
        if (_logScrollScheduled || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _logScrollScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _logScrollScheduled = false;
            if (!IsLoaded || LogListBox.Items.Count == 0)
            {
                return;
            }

            // A-13: Smart auto-scroll — only scroll when user is already near the bottom of the log list.
            _logScrollViewer ??= FindVisualChild<ScrollViewer>(LogListBox);
            if (_logScrollViewer is null)
            {
                return;
            }

            if (_logScrollViewer.ScrollableHeight <= 0
                || _logScrollViewer.VerticalOffset >= _logScrollViewer.ScrollableHeight - 24)
            {
                _logScrollViewer.ScrollToEnd();
            }
        });
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                return typed;
            }
            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }
        return null;
    }
}
