using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using MangaGui.ViewModels;

namespace MangaGui.Views;

public partial class EnvironmentCheckDialog : AppContentDialog
{
    public EnvironmentCheckDialog() => InitializeComponent();
    private EnvironmentCheckDialogViewModel Vm => (EnvironmentCheckDialogViewModel)DataContext;
    private void OnRefresh(object sender, RoutedEventArgs e) => Vm.Refresh();
    private void OnChoose(object sender, RoutedEventArgs e)
    {
        var kind = (string)((FrameworkElement)sender).Tag;
        try
        {
            if (kind == "mit") { var d = new OpenFolderDialog(); if (d.ShowDialog() == true) Vm.SelectPath(kind, d.FolderName); }
            else { var d = new OpenFileDialog { Filter = kind == "colab" ? "colab.exe|colab.exe" : "术语表|*.txt" }; if (d.ShowDialog() == true) Vm.SelectPath(kind, d.FileName); }
        }
        catch (Exception ex) { Feedback.Text = ex.Message; }
    }
    private async void OnGenerate(object sender, RoutedEventArgs e)
    {
        try { await Vm.GenerateAsync(); Feedback.Text = "已生成密钥。"; }
        catch (Exception ex) { Feedback.Text = ex.Message; }
    }
    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText((string)((FrameworkElement)sender).Tag); Feedback.Text = "已复制命令。"; }
        catch (System.Runtime.InteropServices.ExternalException) { Feedback.Text = "剪贴板不可用，请稍后重试。"; }
    }
    private void OnFeatures(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:optionalfeatures") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { Feedback.Text = "请打开 Windows 设置 → 可选功能 → OpenSSH 客户端。"; }
    }
}
