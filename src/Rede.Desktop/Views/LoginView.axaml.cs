using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Rede.Desktop.Controls;
using Rede.Desktop.ViewModels;

namespace Rede.Desktop.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
        // Enter on any of the passphrase fields submits the current mode.
        this.AttachedToVisualTree += (_, _) =>
        {
            if (this.FindControl<SecureTextBox>("QuickPassphraseBox") is { } q)
                q.EnterPressed += (_, _) => SubmitCurrentMode();
            if (this.FindControl<SecureTextBox>("LoginPassphraseBox") is { } l)
                l.EnterPressed += (_, _) => SubmitCurrentMode();
            if (this.FindControl<SecureTextBox>("RegisterPassphraseBox") is { } r)
                r.EnterPressed += (_, _) => SubmitCurrentMode();
            if (this.FindControl<SecureTextBox>("RegisterPassphraseConfirmBox") is { } rc)
                rc.EnterPressed += (_, _) => SubmitCurrentMode();
        };
    }

    private void SubmitCurrentMode()
    {
        if (DataContext is not LoginViewModel vm) return;
        if (vm.IsLoading) return;
        if (vm.HasQuickLogin) DoQuickLogin(vm);
        else if (vm.IsRegisterMode) DoRegister(vm);
        else DoLogin(vm);
    }

    private void OnQuickLoginClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm) DoQuickLogin(vm);
    }

    private void OnLoginClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm) DoLogin(vm);
    }

    private void OnRegisterClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm) DoRegister(vm);
    }

    private void DoQuickLogin(LoginViewModel vm)
    {
        var box = this.FindControl<SecureTextBox>("QuickPassphraseBox");
        if (box is null) return;
        var bytes = box.ExtractPassphrase();
        vm.SubmitQuickLogin(bytes);
    }

    private void DoLogin(LoginViewModel vm)
    {
        var box = this.FindControl<SecureTextBox>("LoginPassphraseBox");
        if (box is null) return;
        var bytes = box.ExtractPassphrase();
        vm.SubmitLogin(bytes);
    }

    private void DoRegister(LoginViewModel vm)
    {
        var pass = this.FindControl<SecureTextBox>("RegisterPassphraseBox");
        var conf = this.FindControl<SecureTextBox>("RegisterPassphraseConfirmBox");
        if (pass is null || conf is null) return;

        // PEEK the primary passphrase first so we can compare against confirm
        // before transferring ownership. If validation fails we zero our copies
        // and leave the user's input in place; on success we extract (which
        // clears the control), clear the confirm box, and zero the confirm copy.
        var passBytes = pass.PeekPassphrase();
        var confBytes = conf.PeekPassphrase();
        var charCount = pass.CharCount;

        var ok = vm.SubmitRegister(passBytes, confBytes, charCount);

        // passBytes ownership transferred only on success — in that case the
        // VM invoked the event handler which now owns it. We still need to
        // clear the SecureTextBox backing buffer, since PeekPassphrase leaves
        // it intact.
        if (ok)
        {
            pass.Clear();
            conf.Clear();
        }
        // confBytes is our local copy — always zero it.
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(confBytes);
        // On failure, passBytes is also a local copy that never reached a
        // subscriber — zero it too.
        if (!ok)
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(passBytes);
    }
}
