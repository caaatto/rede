using Avalonia.Controls;
using Avalonia.Interactivity;
using Rede.Desktop.ViewModels;

namespace Rede.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private async void CopyFingerprint_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(vm.Fingerprint);
        }
    }
}
