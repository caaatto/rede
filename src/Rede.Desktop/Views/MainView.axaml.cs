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

    private NotifyCollectionChangedEventHandler? _scrollHandler;

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // Auto-scroll when new messages arrive
        if (DataContext is MainViewModel vm)
        {
            // M4: Store handler reference for cleanup in OnUnloaded
            _scrollHandler = (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Add)
                    MessageScroller.ScrollToEnd();
            };
            vm.Messages.CollectionChanged += _scrollHandler;
        }
    }

    protected override void OnUnloaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        // M4: Unsubscribe to prevent memory leak
        if (DataContext is MainViewModel vm && _scrollHandler is not null)
        {
            vm.Messages.CollectionChanged -= _scrollHandler;
            _scrollHandler = null;
        }
    }

    public event Action? OnRetryConnection;
    public event Action<string>? OnCallContact;
    public event Action? OnSettingsRequested;

    private void RetryConnection_Click(object? sender, RoutedEventArgs e)
    {
        OnRetryConnection?.Invoke();
    }

    private void Settings_Click(object? sender, RoutedEventArgs e)
    {
        OnSettingsRequested?.Invoke();
    }

    private void CallContact_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.SelectedConversation is ContactItemViewModel contact)
        {
            OnCallContact?.Invoke(contact.UserId);
        }
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
            MaxLength = 255,
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
            MaxLength = 64,
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

    private void CreatePlace_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || DataContext is not MainViewModel vm) return;

        var input = new TextBox
        {
            Watermark = "Place name",
            Width = 200,
            MaxLength = 64,
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
                    Text = "Create Place",
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
                vm.ExecuteCommand("place", new[] { name });
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
                    vm.ExecuteCommand("place", new[] { name });
                    flyout.Hide();
                }
                ke.Handled = true;
            }
        };

        flyout.ShowAt(btn);
    }

    private void Group_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        if (sender is not Button btn || btn.DataContext is not GroupItemViewModel group) return;
        if (DataContext is not MainViewModel vm) return;

        var menu = new ContextMenu();

        var rekeyItem = new MenuItem
        {
            Header = "Rotate key",
            Foreground = Brush.Parse("#e0e0e8"),
        };
        rekeyItem.Click += (_, _) => vm.ExecuteCommand("rekey", new[] { group.GroupId });
        menu.Items.Add(rekeyItem);

        var inviteItem = new MenuItem
        {
            Header = "Invite member...",
            Foreground = Brush.Parse("#e0e0e8"),
        };
        inviteItem.Click += (_, _) =>
        {
            // Show inline input for user ID
            var input = new TextBox
            {
                Watermark = "user#id",
                Width = 200,
                MaxLength = 255,
                Background = Brush.Parse("#12121a"),
                Foreground = Brush.Parse("#e0e0e8"),
                BorderBrush = Brush.Parse("#1e1e2e"),
            };
            var addBtn = new Button
            {
                Content = "Invite",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
            };
            var panel = new StackPanel { Spacing = 4, Width = 210, Children = { input, addBtn } };
            var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
            addBtn.Click += (_, _) =>
            {
                var uid = input.Text?.Trim();
                if (!string.IsNullOrEmpty(uid))
                {
                    vm.ExecuteCommand("ginvite", new[] { group.GroupId, uid });
                    flyout.Hide();
                }
            };
            input.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Enter)
                {
                    var uid = input.Text?.Trim();
                    if (!string.IsNullOrEmpty(uid))
                    {
                        vm.ExecuteCommand("ginvite", new[] { group.GroupId, uid });
                        flyout.Hide();
                    }
                    ke.Handled = true;
                }
            };
            flyout.ShowAt(btn);
        };
        menu.Items.Add(inviteItem);

        var kickItem = new MenuItem
        {
            Header = "Kick member...",
            Foreground = Brush.Parse("#e0e0e8"),
        };
        kickItem.Click += (_, _) =>
        {
            var input = new TextBox
            {
                Watermark = "user#id",
                Width = 200,
                MaxLength = 255,
                Background = Brush.Parse("#12121a"),
                Foreground = Brush.Parse("#e0e0e8"),
                BorderBrush = Brush.Parse("#1e1e2e"),
            };
            var kickBtn = new Button
            {
                Content = "Kick",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
            };
            var panel = new StackPanel { Spacing = 4, Width = 210, Children = { input, kickBtn } };
            var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
            kickBtn.Click += (_, _) =>
            {
                var uid = input.Text?.Trim();
                if (!string.IsNullOrEmpty(uid))
                {
                    vm.ExecuteCommand("kick", new[] { group.GroupId, uid });
                    flyout.Hide();
                }
            };
            input.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Enter)
                {
                    var uid = input.Text?.Trim();
                    if (!string.IsNullOrEmpty(uid))
                    {
                        vm.ExecuteCommand("kick", new[] { group.GroupId, uid });
                        flyout.Hide();
                    }
                    ke.Handled = true;
                }
            };
            flyout.ShowAt(btn);
        };
        menu.Items.Add(kickItem);

        btn.ContextMenu = menu;
        menu.Open(btn);
        e.Handled = true;
    }

    private void PlaceHeader_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is PlaceItemViewModel place)
        {
            place.IsExpanded = !place.IsExpanded;
        }
    }

    private void Channel_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is ChannelItemViewModel channel
            && DataContext is MainViewModel vm)
        {
            vm.SelectConversationCommand.Execute(channel);
        }
    }

    private void Place_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        if (sender is not Button btn || btn.DataContext is not PlaceItemViewModel place) return;
        if (DataContext is not MainViewModel vm) return;

        var menu = new ContextMenu();

        // Invite member
        var inviteItem = new MenuItem
        {
            Header = "Invite member...",
            Foreground = Brush.Parse("#e0e0e8"),
        };
        inviteItem.Click += (_, _) =>
        {
            var input = new TextBox
            {
                Watermark = "user#id",
                Width = 200,
                MaxLength = 255,
                Background = Brush.Parse("#12121a"),
                Foreground = Brush.Parse("#e0e0e8"),
                BorderBrush = Brush.Parse("#1e1e2e"),
            };
            var addBtn = new Button
            {
                Content = "Invite",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
            };
            var panel = new StackPanel { Spacing = 4, Width = 210, Children = { input, addBtn } };
            var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
            addBtn.Click += (_, _) =>
            {
                var uid = input.Text?.Trim();
                if (!string.IsNullOrEmpty(uid))
                {
                    vm.ExecuteCommand("pinvite", new[] { place.PlaceId, uid });
                    flyout.Hide();
                }
            };
            input.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Enter)
                {
                    var uid = input.Text?.Trim();
                    if (!string.IsNullOrEmpty(uid))
                    {
                        vm.ExecuteCommand("pinvite", new[] { place.PlaceId, uid });
                        flyout.Hide();
                    }
                    ke.Handled = true;
                }
            };
            flyout.ShowAt(btn);
        };
        menu.Items.Add(inviteItem);

        // Create channel
        var channelItem = new MenuItem
        {
            Header = "Create channel...",
            Foreground = Brush.Parse("#e0e0e8"),
        };
        channelItem.Click += (_, _) =>
        {
            var input = new TextBox
            {
                Watermark = "Channel name",
                Width = 200,
                MaxLength = 64,
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
            var panel = new StackPanel { Spacing = 4, Width = 210, Children = { input, createBtn } };
            var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
            createBtn.Click += (_, _) =>
            {
                var name = input.Text?.Trim();
                if (!string.IsNullOrEmpty(name))
                {
                    vm.ExecuteCommand("pchannel", new[] { place.PlaceId, name });
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
                        vm.ExecuteCommand("pchannel", new[] { place.PlaceId, name });
                        flyout.Hide();
                    }
                    ke.Handled = true;
                }
            };
            flyout.ShowAt(btn);
        };
        menu.Items.Add(channelItem);

        // Creator-only actions
        if (place.IsCreator)
        {
            menu.Items.Add(new Separator());

            // Kick member
            var kickItem = new MenuItem
            {
                Header = "Kick member...",
                Foreground = Brush.Parse("#e0e0e8"),
            };
            kickItem.Click += (_, _) =>
            {
                var input = new TextBox
                {
                    Watermark = "user#id",
                    Width = 200,
                    MaxLength = 255,
                    Background = Brush.Parse("#12121a"),
                    Foreground = Brush.Parse("#e0e0e8"),
                    BorderBrush = Brush.Parse("#1e1e2e"),
                };
                var kickBtn = new Button
                {
                    Content = "Kick",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 8, 0, 0),
                };
                var panel = new StackPanel { Spacing = 4, Width = 210, Children = { input, kickBtn } };
                var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
                kickBtn.Click += (_, _) =>
                {
                    var uid = input.Text?.Trim();
                    if (!string.IsNullOrEmpty(uid))
                    {
                        vm.ExecuteCommand("pkick", new[] { place.PlaceId, uid });
                        flyout.Hide();
                    }
                };
                input.KeyDown += (_, ke) =>
                {
                    if (ke.Key == Key.Enter)
                    {
                        var uid = input.Text?.Trim();
                        if (!string.IsNullOrEmpty(uid))
                        {
                            vm.ExecuteCommand("pkick", new[] { place.PlaceId, uid });
                            flyout.Hide();
                        }
                        ke.Handled = true;
                    }
                };
                flyout.ShowAt(btn);
            };
            menu.Items.Add(kickItem);

            // Edit place profile
            var profileItem = new MenuItem
            {
                Header = "Edit profile...",
                Foreground = Brush.Parse("#e0e0e8"),
            };
            profileItem.Click += (_, _) =>
            {
                var colorLabel = new TextBlock
                {
                    Text = "Accent Color",
                    Foreground = Brush.Parse("#e0e0e8"),
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 13,
                };
                var colorPalette = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
                var selectedColor = place.AccentColor;

                void BuildSwatches()
                {
                    colorPalette.Children.Clear();
                    foreach (var hex in ViewModels.SettingsViewModel.PresetColors)
                    {
                        var c = hex;
                        var swatch = new Border
                        {
                            Width = 24, Height = 24,
                            CornerRadius = new CornerRadius(12),
                            Background = Brush.Parse(c),
                            Margin = new Thickness(2),
                            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                            BorderThickness = new Thickness(2),
                            BorderBrush = selectedColor == c ? Brush.Parse("#e0e0e8") : Avalonia.Media.Brushes.Transparent,
                        };
                        swatch.PointerPressed += (_, _) =>
                        {
                            selectedColor = c;
                            BuildSwatches();
                        };
                        colorPalette.Children.Add(swatch);
                    }
                }
                BuildSwatches();

                var applyBtn = new Button
                {
                    Content = "Apply",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 8, 0, 0),
                };
                var panel = new StackPanel { Spacing = 4, Width = 230, Children = { colorLabel, colorPalette, applyBtn } };
                var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
                applyBtn.Click += (_, _) =>
                {
                    vm.ExecuteCommand("pprofile", new[] { place.PlaceId, selectedColor });
                    flyout.Hide();
                };
                flyout.ShowAt(btn);
            };
            menu.Items.Add(profileItem);

            // Rotate key
            var rekeyItem = new MenuItem
            {
                Header = "Rotate key",
                Foreground = Brush.Parse("#e0e0e8"),
            };
            rekeyItem.Click += (_, _) => vm.ExecuteCommand("prekey", new[] { place.PlaceId });
            menu.Items.Add(rekeyItem);
        }

        menu.Items.Add(new Separator());

        // Leave place
        var leaveItem = new MenuItem
        {
            Header = "Leave place",
            Foreground = Brush.Parse("#f87171"),
        };
        leaveItem.Click += (_, _) => vm.ExecuteCommand("pleave", new[] { place.PlaceId });
        menu.Items.Add(leaveItem);

        btn.ContextMenu = menu;
        menu.Open(btn);
        e.Handled = true;
    }

    private void Channel_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        if (sender is not Button btn || btn.DataContext is not ChannelItemViewModel channel) return;
        if (DataContext is not MainViewModel vm) return;

        var menu = new ContextMenu();

        if (channel.IsCreator)
        {
            var deleteItem = new MenuItem
            {
                Header = "Delete channel",
                Foreground = Brush.Parse("#f87171"),
            };
            deleteItem.Click += (_, _) => vm.ExecuteCommand("pchannelrm", new[] { channel.PlaceId, channel.ChannelId });
            menu.Items.Add(deleteItem);
        }

        // Only show menu if it has items
        if (menu.Items.Count == 0) return;

        btn.ContextMenu = menu;
        menu.Open(btn);
        e.Handled = true;
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
                var displayName = group.Name.Length > 32 ? group.Name[..32] + "..." : group.Name;
                var item = new MenuItem
                {
                    Header = $"Invite to #{displayName}",
                    Foreground = Brush.Parse("#e0e0e8"),
                };
                item.Click += (_, _) => vm.InviteContactToGroup(groupId, contact.UserId);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
        }

        // Add "Invite to place" items for each place
        if (vm.Places.Count > 0)
        {
            foreach (var place in vm.Places)
            {
                var placeId = place.PlaceId;
                var displayName = place.Name.Length > 32 ? place.Name[..32] + "..." : place.Name;
                var item = new MenuItem
                {
                    Header = $"Invite to {displayName}",
                    Foreground = Brush.Parse("#e0e0e8"),
                };
                item.Click += (_, _) => vm.ExecuteCommand("pinvite", new[] { placeId, contact.UserId });
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
