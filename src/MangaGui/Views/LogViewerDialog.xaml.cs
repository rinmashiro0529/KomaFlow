using System.Windows;
using System.Windows.Media;
using MangaGui.Services;

namespace MangaGui.Views;

public partial class LogViewerDialog : AppContentDialog
{
    public LogViewerDialog(string title, string content)
    {
        InitializeComponent();
        Title = title;
        LogTextBox.Text = content;
    }

    private void OnCopyRedactedClick(object sender, RoutedEventArgs e)
    {
        CopyText(LogSanitizer.Redact(LogTextBox.Text), "已复制脱敏日志到剪贴板（敏感 token 与用户路径已隐藏）");
    }

    private void OnCopyRawClick(object sender, RoutedEventArgs e)
    {
        CopyText(LogTextBox.Text, "已复制原始日志到剪贴板");
    }

    private void CopyText(string text, string successMessage)
    {
        try
        {
            Clipboard.SetText(text);
            CopyStatusTextBlock.Foreground =
                TryFindResource("SystemFillColorSuccessBrush") as Brush ?? Brushes.ForestGreen;
            CopyStatusTextBlock.Text = successMessage;
        }
        catch (Exception ex)
        {
            CopyStatusTextBlock.Foreground =
                TryFindResource("SystemFillColorCriticalBrush") as Brush ?? Brushes.IndianRed;
            CopyStatusTextBlock.Text = $"剪贴板占用，复制失败：{ex.Message}";
        }
    }
}
