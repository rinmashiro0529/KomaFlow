using MangaGui.ViewModels;
using Wpf.Ui.Controls;

namespace MangaGui.Views;

public partial class OrphanSessionDialog : AppContentDialog
{
    /// <summary>Mapped from the clicked footer button; <see cref="OrphanSessionAction.Dismiss"/> is the safe default (Esc / interrupted).</summary>
    public OrphanSessionAction SelectedAction { get; private set; } = OrphanSessionAction.Dismiss;

    public OrphanSessionDialog(string? accountName = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(accountName))
        {
            BodyTextBlock.Text = $"云端账号「{accountName}」存在运行中的 manga 会话，但本地未找到 session.json（可能是之前通过命令行启动或异常退出遗留），该会话当前正在持续计费，已运行时长未知。";
        }
    }

    protected override void OnButtonClick(ContentDialogButton button)
    {
        SelectedAction = button switch
        {
            ContentDialogButton.Primary => OrphanSessionAction.Adopt,
            ContentDialogButton.Secondary => OrphanSessionAction.StopNow,
            _ => OrphanSessionAction.Dismiss
        };
        base.OnButtonClick(button);
    }
}
