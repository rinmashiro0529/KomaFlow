using System.Text;
using System.Windows;
using System.Windows.Media;
using MangaGui.Services;
using MangaGui.ViewModels;

namespace MangaGui.Views;

public partial class DiagnosticsDialog : AppContentDialog
{
    private readonly string _reportText;

    public DiagnosticsDialog(DiagnosticsSnapshot snapshot)
    {
        InitializeComponent();
        SubtitleTextBlock.Text = $"MangaGui {snapshot.AppVersion} · {snapshot.DotNetVersion} · VM PROTO 2";

        var sb = new StringBuilder();
        sb.AppendLine($"应用版本     : {snapshot.AppVersion}");
        sb.AppendLine($"运行框架     : {snapshot.DotNetVersion}");
        sb.AppendLine($"权限级别     : {(snapshot.IsElevated ? "管理员权限（注意：Windows UIPI 会阻止普通资源管理器向管理员窗口拖入文件，请以普通权限启动或点击「+ 添加漫画」）" : "标准用户权限（支持资源管理器直接拖入）")}");
        sb.AppendLine($"计费单价     : {MangaGui.Models.BillingConstants.L4CuPerHourDisplay}");
        sb.AppendLine($"Colab CLI    : {snapshot.ColabCliStatus}");
        sb.AppendLine($"CLI 启动模式 : {snapshot.ColabLaunchMode}");
        sb.AppendLine($"现有脚本目录 : {snapshot.ScriptsDirectory}");
        sb.AppendLine($"脚本来源     : {snapshot.ScriptsSource}");
        sb.AppendLine($"内置资产目录 : {snapshot.BundledAssetsDirectory}");
        sb.AppendLine($"会话存储路径 : {snapshot.SessionStorePath}");
        if (!string.IsNullOrWhiteSpace(snapshot.CrashLogPath))
        {
            sb.AppendLine($"崩溃日志路径 : {snapshot.CrashLogPath}");
        }
        if (snapshot.AccountSummaries is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Colab 账号与额度状态（已脱敏）:");
            foreach (var line in snapshot.AccountSummaries)
            {
                sb.AppendLine($"  - {line}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("内置脚本 SHA256 (前 16 位):");
        if (snapshot.AssetChecksums.Count == 0)
        {
            sb.AppendLine("  (未找到内置资产脚本)");
        }
        else
        {
            foreach (var kv in snapshot.AssetChecksums.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"  - {kv.Key,-14}: {kv.Value}");
            }
        }

        _reportText = sb.ToString().TrimEnd();
        DiagnosticsTextBox.Text = _reportText;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LogSanitizer.Redact(_reportText));
            StatusTextBlock.Foreground =
                TryFindResource("SystemFillColorSuccessBrush") as Brush ?? Brushes.ForestGreen;
            StatusTextBlock.Text = "已复制脱敏诊断信息到剪贴板";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Foreground =
                TryFindResource("SystemFillColorCriticalBrush") as Brush ?? Brushes.IndianRed;
            StatusTextBlock.Text = $"复制失败：{ex.Message}";
        }
    }
}
