using MangaGui.ViewModels;
using Wpf.Ui.Controls;

namespace MangaGui.Views;

public partial class CloseGuardDialog : AppContentDialog
{
    /// <summary>Mapped from the clicked footer button; <see cref="CloseGuardAction.Cancel"/> is the safe default (Esc / interrupted).</summary>
    public CloseGuardAction SelectedAction { get; private set; } = CloseGuardAction.Cancel;

    public CloseGuardDialog(ConfirmStopSummary summary)
    {
        InitializeComponent();
        var acctPrefix = !string.IsNullOrWhiteSpace(summary.AccountName)
            ? $"账号「{summary.AccountName}」"
            : string.Empty;
        BodyTextBlock.Text = $"{acctPrefix}已运行 {summary.ElapsedText}，{summary.EstimatedCuText}。直接关闭窗口不会释放 Colab 实例。";
    }

    protected override void OnButtonClick(ContentDialogButton button)
    {
        SelectedAction = button switch
        {
            ContentDialogButton.Primary => CloseGuardAction.StopAndExit,
            ContentDialogButton.Secondary => CloseGuardAction.MinimizeToTray,
            _ => CloseGuardAction.Cancel
        };
        base.OnButtonClick(button);
    }
}
