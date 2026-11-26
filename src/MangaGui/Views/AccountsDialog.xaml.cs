using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MangaGui.ViewModels;
using Microsoft.Win32;

namespace MangaGui.Views;

public partial class AccountsDialog : AppContentDialog
{
    public AccountsDialogViewModel ViewModel { get; }
    private bool _loaded;

    public AccountsDialog(AccountsDialogViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;

        ViewModel.RequestBrowseAdcJson += BrowseAdcJson;
        ViewModel.RequestCopyToClipboard += text =>
        {
            try { Clipboard.SetText(text); } catch { }
        };

        Loaded += (_, _) => _loaded = true;
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    private string? BrowseAdcJson()
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 Google ADC 凭据 JSON 文件",
            Filter = "JSON 凭据文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            Multiselect = false
        };
        var owner = Window.GetWindow(this);
        return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
    }

    private void OnAccountNameLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is AccountBadgeItem item)
        {
            ViewModel.CommitRename(item, tb.Text);
        }
    }

    private void OnAccountNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb && tb.DataContext is AccountBadgeItem item)
        {
            ViewModel.CommitRename(item, tb.Text);
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void OnPollingOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loaded) ViewModel.SavePollingPreferences();
    }

    private void OnPollingSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded) ViewModel.SavePollingPreferences();
    }

    private void OnMinBalanceLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loaded) ViewModel.SavePollingPreferences();
    }
}
