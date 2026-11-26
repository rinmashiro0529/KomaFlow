using System.Windows;
using MangaGui.ViewModels;

namespace MangaGui.Views;

public partial class ConfirmStopDialog : AppContentDialog
{
    public ConfirmStopDialog(ConfirmStopSummary summary)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(summary.AccountName))
        {
            AccountLabelBlock.Visibility = Visibility.Visible;
            AccountTextBlock.Visibility = Visibility.Visible;
            AccountTextBlock.Text = summary.AccountName;
        }
        ElapsedTextBlock.Text = summary.ElapsedText;
        CuTextBlock.Text = summary.EstimatedCuText;
        PendingTextBlock.Text = summary.UnfinishedTaskCount > 0
            ? $"{summary.UnfinishedTaskCount} 个"
            : summary.IsDeploying
                ? "0 个（当前正在部署中）"
                : "0 个";
        PendingWarningText.Visibility = summary.UnfinishedTaskCount > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        DeployingWarningText.Visibility = summary.IsDeploying
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
