using System;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Rede.Desktop.Controls;
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
            var btn = new Button
            {
                Width = 36,
                Height = 36,
                MinWidth = 0,
                MinHeight = 0,
                CornerRadius = new CornerRadius(18),
                Background = Brush.Parse(color),
                Margin = new Thickness(4),
                Cursor = new Cursor(StandardCursorType.Hand),
                BorderThickness = new Thickness(3),
                BorderBrush = vm.AccentColor == color
                    ? Brush.Parse("#e0e0e8")
                    : Brushes.Transparent,
                Padding = new Thickness(0),
                Tag = color,
            };

            btn.Click += (s, _) =>
            {
                if (DataContext is SettingsViewModel v && s is Button b && b.Tag is string c)
                    v.AccentColor = c;
            };

            ColorPalette.Children.Add(btn);
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
            if (child is Button b && b.Tag is string hex)
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

    private async void InstallRnnoise_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && vm.OnInstallRnnoise is not null)
            await vm.OnInstallRnnoise.Invoke();
    }

    private async void ChangePassphrase_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;

        var currentBox = this.FindControl<SecureTextBox>("CurrentPassphraseBox");
        var newBox = this.FindControl<SecureTextBox>("NewPassphraseBox");
        var confirmBox = this.FindControl<SecureTextBox>("ConfirmPassphraseBox");
        if (currentBox is null || newBox is null || confirmBox is null) return;

        if (currentBox.ByteLength == 0 || newBox.ByteLength == 0)
        {
            vm.PassphraseChangeStatus = "Please fill in all fields.";
            return;
        }

        // Compare new and confirm
        var newBytes = newBox.PeekPassphrase();
        var confirmBytes = confirmBox.PeekPassphrase();
        bool match = newBytes.Length == confirmBytes.Length
                     && CryptographicOperations.FixedTimeEquals(newBytes, confirmBytes);
        CryptographicOperations.ZeroMemory(confirmBytes);

        if (!match)
        {
            CryptographicOperations.ZeroMemory(newBytes);
            vm.PassphraseChangeStatus = "New passphrases don't match.";
            return;
        }
        CryptographicOperations.ZeroMemory(newBytes);

        // Extract (zeros internal buffers)
        var currentPass = currentBox.ExtractPassphrase();
        var newPass = newBox.ExtractPassphrase();
        confirmBox.Clear();

        try
        {
            await vm.ChangePassphraseAsync(currentPass, newPass);
            // newPass ownership transferred to MainWindow handler if successful
        }
        finally
        {
            CryptographicOperations.ZeroMemory(currentPass);
            // newPass is zeroed by the handler after mlock replacement
        }
    }

    private async void EnrollSecurityKey_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var name = this.FindControl<TextBox>("FidoKeyNameBox")?.Text?.Trim();
        var pin = this.FindControl<TextBox>("FidoPinBox")?.Text;
        await vm.EnrollKeyAsync(string.IsNullOrWhiteSpace(name) ? "Security key" : name,
            string.IsNullOrEmpty(pin) ? null : pin);
        // Clear the PIN box after use.
        var pinBox = this.FindControl<TextBox>("FidoPinBox");
        if (pinBox is not null) pinBox.Text = "";
    }

    private async void GenerateRecovery_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            await vm.GenerateRecoveryAsync();
    }
}
