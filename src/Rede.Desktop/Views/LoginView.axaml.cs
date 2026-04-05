using Avalonia.Controls;
using Avalonia.Input;
using Rede.Desktop.ViewModels;

namespace Rede.Desktop.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
    }

    // Submit login on Enter from the passphrase TextBox. Picks the right
    // command based on which login mode is active.
    private void OnPassphraseKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return) return;
        if (DataContext is not LoginViewModel vm) return;
        if (vm.IsLoading) return;

        if (vm.HasQuickLogin)
        {
            if (vm.QuickLoginCommand.CanExecute(null))
                vm.QuickLoginCommand.Execute(null);
        }
        else if (vm.IsRegisterMode)
        {
            if (vm.RegisterCommand.CanExecute(null))
                vm.RegisterCommand.Execute(null);
        }
        else
        {
            if (vm.LoginCommand.CanExecute(null))
                vm.LoginCommand.Execute(null);
        }
        e.Handled = true;
    }
}
