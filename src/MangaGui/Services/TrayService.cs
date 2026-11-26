using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace MangaGui.Services;

/// <summary>
/// Manages the Windows system tray icon for minimize-to-tray continuity and billing notifications.
/// Loads the embedded multi-resolution KomaFlow app icon (falling back to an in-memory vector icon),
/// properly releases native icon handles, and auto-hides transient balloon icons when the main window is visible.
/// </summary>
public sealed class TrayService : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly Icon _trayIcon;
    private readonly System.Threading.Timer _transientHideTimer;
    private bool _explicitlyShown;
    private bool _disposed;

    public event Action? RestoreRequested;
    public event Action? StopBillingRequested;
    public event Action? ExitRequested;

    public TrayService()
    {
        _trayIcon = LoadAppIcon();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => RestoreRequested?.Invoke());
        menu.Items.Add("停止并结束计费", null, (_, _) => StopBillingRequested?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "漫画翻译 · Colab",
            Visible = false,
            ContextMenuStrip = menu
        };

        _notifyIcon.DoubleClick += (_, _) => RestoreRequested?.Invoke();
        _notifyIcon.BalloonTipClosed += (_, _) => HideIfTransient();
        _notifyIcon.BalloonTipClicked += (_, _) => HideIfTransient();

        _transientHideTimer = new System.Threading.Timer(_ => HideIfTransient(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void ShowTrayIcon(string? tooltip = null)
    {
        if (_disposed) return;
        _explicitlyShown = true;
        _transientHideTimer.Change(Timeout.Infinite, Timeout.Infinite);
        if (!string.IsNullOrWhiteSpace(tooltip))
        {
            _notifyIcon.Text = TruncateTooltip(tooltip);
        }
        _notifyIcon.Visible = true;
    }

    public void HideTrayIcon()
    {
        if (_disposed) return;
        _explicitlyShown = false;
        _transientHideTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _notifyIcon.Visible = false;
    }

    public void UpdateTooltip(string tooltip)
    {
        if (_disposed) return;
        _notifyIcon.Text = TruncateTooltip(tooltip);
    }

    public void ShowBalloon(string title, string text, WinForms.ToolTipIcon icon = WinForms.ToolTipIcon.Info)
    {
        if (_disposed) return;
        if (!_notifyIcon.Visible)
        {
            _notifyIcon.Visible = true;
        }

        _notifyIcon.ShowBalloonTip(5000, title, text, icon);

        // If the tray icon was only shown transiently for this balloon (main window is open),
        // hide it again after the balloon dismisses instead of leaving a ghost icon.
        if (!_explicitlyShown)
        {
            _transientHideTimer.Change(TimeSpan.FromSeconds(6), Timeout.InfiniteTimeSpan);
        }
    }

    private void HideIfTransient()
    {
        if (_disposed || _explicitlyShown) return;
        try
        {
            var app = System.Windows.Application.Current;
            if (app is not null && !app.Dispatcher.HasShutdownStarted)
            {
                app.Dispatcher.BeginInvoke(() =>
                {
                    if (!_disposed && !_explicitlyShown)
                    {
                        _notifyIcon.Visible = false;
                    }
                });
            }
        }
        catch
        {
            // Best-effort cleanup during shutdown.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transientHideTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayIcon.Dispose();
    }

    private static string TruncateTooltip(string text)
        => text.Length <= 63 ? text : text[..60] + "...";

    private static Icon LoadAppIcon()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MangaGui.Assets.app.ico");
            if (stream is not null)
            {
                return new Icon(stream, new Size(32, 32));
            }
        }
        catch
        {
            // Fall back to synthesized vector icon if embedded resource cannot be decoded.
        }

        return CreateWarmClayIcon();
    }

    private static Icon CreateWarmClayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Warm clay circle (#D97757) with cream ink accent (#FAF9F5)
            using var bgBrush = new SolidBrush(Color.FromArgb(217, 119, 87));
            g.FillEllipse(bgBrush, 2, 2, 28, 28);

            using var pen = new Pen(Color.FromArgb(250, 249, 245), 2.5f);
            g.DrawRectangle(pen, 9, 8, 14, 16);
            g.DrawLine(pen, 13, 13, 19, 13);
            g.DrawLine(pen, 13, 18, 19, 18);
        }

        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
