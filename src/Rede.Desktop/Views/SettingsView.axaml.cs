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
        DataContextChanged += (_, _) => PopulateColorPalette();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        PopulateColorPalette();
    }

    private void PopulateColorPalette()
    {
        if (ColorPalette is null) return;
        if (DataContext is not SettingsViewModel vm) return;

        // Unsubscribe from any prior VM to avoid leaks on re-open
        vm.PropertyChanged -= OnVmPropertyChanged;
        vm.PropertyChanged += OnVmPropertyChanged;

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
                Tag = color,
            };

            swatch.PointerPressed += (s, _) =>
            {
                if (DataContext is SettingsViewModel v && s is Border b && b.Tag is string c)
                    v.AccentColor = c;
            };

            ColorPalette.Children.Add(swatch);
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.AccentColor))
            RefreshSelectionBorder();
    }

    private void RefreshSelectionBorder()
    {
        if (ColorPalette is null || DataContext is not SettingsViewModel vm) return;
        foreach (var child in ColorPalette.Children)
        {
            if (child is Border b && b.Tag is string hex)
                b.BorderBrush = vm.AccentColor == hex ? Brush.Parse("#e0e0e8") : Brushes.Transparent;
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
