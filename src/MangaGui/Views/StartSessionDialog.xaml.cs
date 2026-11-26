using System.Windows;
using System.Windows.Controls;
using MangaGui.ViewModels;

namespace MangaGui.Views;

public partial class StartSessionDialog : AppContentDialog
{
    public StartSessionDialogViewModel ViewModel { get; }

    public StartSessionDialog() : this(new StartSessionDialogViewModel())
    {
    }

    public StartSessionDialog(StartSessionDialogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = ViewModel;
    }

    private void OnDurationChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag)
        {
            return;
        }

        ViewModel.SelectedChoice = tag switch
        {
            "30" => DurationChoice.Minutes30,
            "60" => DurationChoice.Minutes60,
            "120" => DurationChoice.Minutes120,
            "0" => DurationChoice.Unlimited,
            _ => DurationChoice.Minutes60
        };
    }
}
