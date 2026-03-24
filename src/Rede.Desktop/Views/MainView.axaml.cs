using System;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
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

    private void AddContact_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || DataContext is not MainViewModel vm) return;

        var input = new TextBox
        {
            Watermark = "alice#a3f1",
            Width = 200,
            Background = Brush.Parse("#12121a"),
            Foreground = Brush.Parse("#e0e0e8"),
            BorderBrush = Brush.Parse("#1e1e2e"),
        };

        var addBtn = new Button
        {
            Content = "Add",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var panel = new StackPanel
        {
            Spacing = 4,
            Width = 210,
            Children =
            {
                new TextBlock
                {
                    Text = "Add Contact",
                    Foreground = Brush.Parse("#e0e0e8"),
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 13,
                },
                input,
                addBtn,
            }
        };

        var flyout = new Flyout
        {
            Content = panel,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
        };

        addBtn.Click += (_, _) =>
        {
            var userId = input.Text?.Trim();
            if (!string.IsNullOrEmpty(userId))
            {
                vm.ExecuteCommand("add", new[] { userId });
                flyout.Hide();
            }
        };

        input.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter)
            {
                var userId = input.Text?.Trim();
                if (!string.IsNullOrEmpty(userId))
                {
                    vm.ExecuteCommand("add", new[] { userId });
                    flyout.Hide();
                }
                ke.Handled = true;
            }
        };

        flyout.ShowAt(btn);
    }

    private void CreateGroup_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || DataContext is not MainViewModel vm) return;

        var input = new TextBox
        {
            Watermark = "Group name",
            Width = 200,
            Background = Brush.Parse("#12121a"),
            Foreground = Brush.Parse("#e0e0e8"),
            BorderBrush = Brush.Parse("#1e1e2e"),
        };

        var createBtn = new Button
        {
            Content = "Create",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var panel = new StackPanel
        {
            Spacing = 4,
            Width = 210,
            Children =
            {
                new TextBlock
                {
                    Text = "Create Group",
                    Foreground = Brush.Parse("#e0e0e8"),
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 13,
                },
                input,
                createBtn,
            }
        };

        var flyout = new Flyout
        {
            Content = panel,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
        };

        createBtn.Click += (_, _) =>
        {
            var name = input.Text?.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                vm.ExecuteCommand("group", new[] { name });
                flyout.Hide();
            }
        };

        input.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter)
            {
                var name = input.Text?.Trim();
                if (!string.IsNullOrEmpty(name))
                {
                    vm.ExecuteCommand("group", new[] { name });
                    flyout.Hide();
                }
                ke.Handled = true;
            }
        };

        flyout.ShowAt(btn);
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
