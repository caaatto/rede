using System;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Rede.Desktop.ViewModels;

namespace Rede.Desktop.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // Auto-scroll when new messages arrive
        if (DataContext is MainViewModel vm)
        {
            vm.Messages.CollectionChanged += (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Add)
                    MessageScroller.ScrollToEnd();
            };
        }
    }

    public event Action? OnRetryConnection;

    private void RetryConnection_Click(object? sender, RoutedEventArgs e)
    {
        OnRetryConnection?.Invoke();
    }

    private void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (DataContext is MainViewModel vm)
                vm.SendMessageCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (DataContext is MainViewModel vm)
                vm.ToggleSidebarCommand.Execute(null);
            e.Handled = true;
        }
    }
}
