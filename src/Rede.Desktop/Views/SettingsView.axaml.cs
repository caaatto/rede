using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Rede.Desktop.ViewModels;

namespace Rede.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        PopulateColorPalette();
    }

    private void PopulateColorPalette()
    {
        if (DataContext is not SettingsViewModel vm) return;

        ColorPalette.Children.Clear();
        foreach (var hex in SettingsViewModel.PresetColors)
        {
            var color = hex;
            var swatch = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(16),
                Background = Brush.Parse(color),
                Margin = new Thickness(4),
                Cursor = new Cursor(StandardCursorType.Hand),
                BorderThickness = new Thickness(2),
                BorderBrush = vm.AccentColor == color
                    ? Brush.Parse("#e0e0e8")
                    : Brushes.Transparent,
            };

            swatch.PointerPressed += (_, _) =>
            {
                vm.AccentColor = color;
                PopulateColorPalette();
            };

            ColorPalette.Children.Add(swatch);
        }
    }

    private async void CopyFingerprint_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(vm.Fingerprint);
        }
    }
}
