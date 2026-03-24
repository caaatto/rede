using System;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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

    private void Contact_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        if (sender is not Button btn || btn.DataContext is not ContactItemViewModel contact) return;
        if (DataContext is not MainViewModel vm) return;

        var menu = new ContextMenu();

        // Add "Invite to group" submenu items for each group
        if (vm.Groups.Count > 0)
        {
            foreach (var group in vm.Groups)
            {
                var groupId = group.GroupId;
                var item = new MenuItem
                {
                    Header = $"Invite to #{group.Name}",
                    Foreground = Brush.Parse("#e0e0e8"),
                };
                item.Click += (_, _) => vm.InviteContactToGroup(groupId, contact.UserId);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
        }

        var fpItem = new MenuItem
        {
            Header = "View fingerprint",
            Foreground = Brush.Parse("#e0e0e8"),
        };
        fpItem.Click += (_, _) => vm.ExecuteCommand("fingerprint", new[] { contact.UserId });
        menu.Items.Add(fpItem);

        btn.ContextMenu = menu;
        menu.Open(btn);
        e.Handled = true;
    }
}
