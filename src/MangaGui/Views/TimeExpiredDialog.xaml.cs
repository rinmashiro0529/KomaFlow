using Wpf.Ui.Controls;
using MangaGui.ViewModels;

namespace MangaGui.Views;

/// <summary>
/// Time-expired dialog: three to four stacked equal-width buttons instead of the footer, so it is
/// shown with <c>IsFooterVisible="False"</c>. Each button records its action on the view model and
/// hides with <see cref="ContentDialogResult.None"/>; the accent button is always the billing-safe
/// action and gets the default focus (and the Enter key via <see cref="OnEnterPressed"/>).
/// </summary>
public partial class TimeExpiredDialog : AppContentDialog
{
    public TimeExpiredDialogViewModel ViewModel { get; }

    public TimeExpiredDialog(TimeExpiredDialogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        DrainButton.Appearance = viewModel.IsDrainVisible
            ? ControlAppearance.Primary
            : ControlAppearance.Secondary;
        StopNowButton.Appearance = viewModel.IsDrainVisible
            ? ControlAppearance.Secondary
            : ControlAppearance.Primary;

        Loaded += (_, _) =>
        {
            var accent = viewModel.IsDrainVisible ? (System.Windows.UIElement)DrainButton : StopNowButton;
            accent.Focus();
        };
    }

    private void OnDrainClick(object sender, System.Windows.RoutedEventArgs e)
    {
        ViewModel.Choose(TimeExpiredAction.DrainThenStop);
        Hide(ContentDialogResult.None);
    }

    private void OnExtendClick(object sender, System.Windows.RoutedEventArgs e)
    {
        ViewModel.Choose(TimeExpiredAction.Extend30Minutes);
        Hide(ContentDialogResult.None);
    }

    private void OnStopNowClick(object sender, System.Windows.RoutedEventArgs e)
    {
        ViewModel.Choose(TimeExpiredAction.StopImmediately);
        Hide(ContentDialogResult.None);
    }

    private void OnRemindLaterClick(object sender, System.Windows.RoutedEventArgs e)
    {
        // Same as Esc: no extension, no stop — the reminder is scheduled again.
        ViewModel.Choose(TimeExpiredAction.None);
        Hide(ContentDialogResult.None);
    }

    protected override void OnEnterPressed()
    {
        ViewModel.Choose(ViewModel.AccentAction);
        Hide(ContentDialogResult.None);
    }
}
