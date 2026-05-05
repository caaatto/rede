using System;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Rede.Desktop.Controls;
using Rede.Desktop.ViewModels;

namespace Rede.Desktop.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
    }

    private NotifyCollectionChangedEventHandler? _scrollHandler;
    private bool _isAtBottom = true;
    // Auto-scroll snaps when the viewport is within ~50px of the end. The
    // "Newest Messages" button uses a much larger threshold so it doesn't
    // flash on send (extent grows for one frame before auto-scroll lands)
    // or on tiny scroll nudges — only appears once you've scrolled roughly
    // 15 messages back.
    private const double AutoScrollEpsilonPx = 50;
    private const double NewestButtonThresholdPx = 600;

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // Drag-drop attachments onto the chat area.
        AddHandler(DragDrop.DragOverEvent, OnChatDragOver);
        AddHandler(DragDrop.DropEvent, OnChatDrop);

        // Intercept Enter on the message input during the *tunneling* phase. The
        // TextBox's built-in class handler inserts a newline (because AcceptsReturn
        // is true, which we still want for Shift+Enter) — that handler runs before
        // bubbling KeyDown handlers can prevent it. By tunneling we see the key
        // first and can mark Enter handled before the TextBox ever sees it.
        InputBox.AddHandler(KeyDownEvent, InputBox_TunnelKeyDown,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Track scroll position for "Newest Messages" button
        MessageScroller.ScrollChanged += OnMessageScrollChanged;

        // Auto-scroll when new messages arrive (only if already at bottom)
        if (DataContext is MainViewModel vm)
        {
            // M4: Store handler reference for cleanup in OnUnloaded
            _scrollHandler = (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Add && _isAtBottom)
                {
                    // Post to dispatcher so layout completes before we scroll
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        MessageScroller.ScrollToEnd();
                    }, Avalonia.Threading.DispatcherPriority.Loaded);
                }
            };
            vm.Messages.CollectionChanged += _scrollHandler;
        }
    }

    protected override void OnUnloaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        MessageScroller.ScrollChanged -= OnMessageScrollChanged;
        RemoveHandler(DragDrop.DragOverEvent, OnChatDragOver);
        RemoveHandler(DragDrop.DropEvent, OnChatDrop);
        InputBox.RemoveHandler(KeyDownEvent, InputBox_TunnelKeyDown);
        // M4: Unsubscribe to prevent memory leak
        if (DataContext is MainViewModel vm && _scrollHandler is not null)
        {
            vm.Messages.CollectionChanged -= _scrollHandler;
            _scrollHandler = null;
        }
    }

    private void OnChatDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnChatDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var files = e.Data.GetFiles();
        if (files is null) return;

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Cast<string>()
            .ToArray();
        if (paths.Length > 0) vm.RequestAttach(paths);
        e.Handled = true;
    }

    private void OnMessageScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var sv = MessageScroller;
        var distanceFromBottom = sv.Extent.Height - sv.Viewport.Height - sv.Offset.Y;
        _isAtBottom = distanceFromBottom <= AutoScrollEpsilonPx;
        NewestMessagesBtn.IsVisible = distanceFromBottom > NewestButtonThresholdPx;
    }

    private void NewestMessagesBtn_Click(object? sender, RoutedEventArgs e)
    {
        MessageScroller.ScrollToEnd();
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

    // Drop the saved Double Ratchet state for this contact so the next outgoing
    // message re-runs X3DH. Needed after deleting + re-adding a contact, or when
    // the peer reinstalled, otherwise both sides hold mismatched ratchet keys
    // and decryption silently fails.
    private void ResyncContact_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (vm.SelectedConversation is not ContactItemViewModel contact) return;
        vm.ExecuteCommand("resync", new[] { contact.UserId });
    }

    // Tunneling Enter handler — runs *before* TextBox's class handler can insert a
    // newline. Plain Enter sends; Shift+Enter falls through and the TextBox does its
    // normal newline insertion.
    private void InputBox_TunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        if (DataContext is MainViewModel vm)
            vm.SendMessageCommand.Execute(null);
        e.Handled = true;
    }

    private async void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (DataContext is MainViewModel vm)
            {
                // Cancel reply/edit first; only fall back to sidebar toggle when nothing's queued.
                if (vm.IsReplying) { vm.CancelReplyCommand.Execute(null); e.Handled = true; return; }
                if (vm.IsEditing)  { vm.CancelEditCommand.Execute(null);  e.Handled = true; return; }
                vm.ToggleSidebarCommand.Execute(null);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Intercept paste only when the clipboard holds image bytes — text paste
            // continues to flow through the TextBox's default handler.
            if (await TryPasteImageAsync())
                e.Handled = true;
        }
    }

    // Ctrl+F bar — Esc closes, Enter does nothing special (filter is live as you type).
    private void MessageSearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainViewModel vm)
        {
            vm.CloseMessageSearchCommand.Execute(null);
            InputBox.Focus();
            e.Handled = true;
        }
    }

    // Open the in-chat search bar from MainWindow's global Ctrl+F handler.
    public void FocusMessageSearch()
    {
        if (DataContext is not MainViewModel vm) return;
        vm.OpenMessageSearch();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            this.FindControl<TextBox>("MessageSearchBox")?.Focus();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    // Quick switcher — keyboard navigation inside the popup textbox.
    private void QuickSwitcherBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        switch (e.Key)
        {
            case Key.Escape:
                vm.CloseQuickSwitcherCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter:
                vm.QuickSwitcherActivate();
                e.Handled = true;
                break;
            case Key.Down:
                vm.QuickSwitcherMove(1);
                e.Handled = true;
                break;
            case Key.Up:
                vm.QuickSwitcherMove(-1);
                e.Handled = true;
                break;
        }
    }

    // Click outside the popup body dismisses the switcher.
    private void QuickSwitcherDim_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.CloseQuickSwitcherCommand.Execute(null);
    }

    // Eat clicks inside the popup so the dim handler doesn't dismiss when interacting.
    private void QuickSwitcherPopup_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    public void FocusQuickSwitcher()
    {
        if (DataContext is not MainViewModel vm) return;
        vm.OpenQuickSwitcher();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            this.FindControl<TextBox>("QuickSwitcherBox")?.Focus();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private async System.Threading.Tasks.Task<bool> TryPasteImageAsync()
    {
        if (DataContext is not MainViewModel vm) return false;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return false;

        // Avalonia exposes raw bitmap data under "image/png" on Linux/X11, "PNG" on
        // Windows, and "public.png" on macOS. Try all common identifiers.
        string[] candidates = { "image/png", "PNG", "public.png", "image/jpeg", "image/bmp" };
        byte[]? data = null;
        string ext = ".png";
        foreach (var fmt in candidates)
        {
            try
            {
                if (await clipboard.GetDataAsync(fmt) is byte[] bytes && bytes.Length > 0)
                {
                    data = bytes;
                    if (fmt.Contains("jpeg", StringComparison.OrdinalIgnoreCase)) ext = ".jpg";
                    else if (fmt.Contains("bmp", StringComparison.OrdinalIgnoreCase)) ext = ".bmp";
                    break;
                }
            }
            catch { /* format not present, try next */ }
        }
        if (data is null) return false;

        try
        {
            var tmp = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"rede-paste-{DateTime.UtcNow:yyyyMMddHHmmssfff}{ext}");
            await System.IO.File.WriteAllBytesAsync(tmp, data);
            vm.RequestAttach(new[] { tmp });
            return true;
        }
        catch { return false; }
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
        menu.Closed += (_, _) => btn.ContextMenu = null;
        menu.Open(btn);
        e.Handled = true;
    }

    private void PlaceHeader_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not PlaceItemViewModel place) return;
        if (DataContext is not MainViewModel vm) return;

        // Single-active-place: clicking another place deactivates the current one.
        // Clicking the same place again deselects (returns to Home).
        var wasActive = place.IsExpanded;
        foreach (var p in vm.Places) p.IsExpanded = false;
        place.IsExpanded = !wasActive;
        vm.ActivePlace = place.IsExpanded ? place : null;
    }

    private void Home_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        foreach (var p in vm.Places) p.IsExpanded = false;
        vm.ActivePlace = null;
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

        // Admin/Creator actions
        if (place.IsCreator || place.IsAdmin)
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

            // Ban member
            var banItem = new MenuItem
            {
                Header = "Ban member...",
                Foreground = Brush.Parse("#f87171"),
            };
            banItem.Click += (_, _) =>
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
                var reasonInput = new TextBox
                {
                    Watermark = "Reason (optional)",
                    Width = 200,
                    MaxLength = 200,
                    Background = Brush.Parse("#12121a"),
                    Foreground = Brush.Parse("#e0e0e8"),
                    BorderBrush = Brush.Parse("#1e1e2e"),
                };
                var banBtn = new Button
                {
                    Content = "Ban",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 8, 0, 0),
                };
                var panel = new StackPanel { Spacing = 4, Width = 210, Children = { input, reasonInput, banBtn } };
                var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
                banBtn.Click += (_, _) =>
                {
                    var uid = input.Text?.Trim();
                    if (!string.IsNullOrEmpty(uid))
                    {
                        var reason = reasonInput.Text?.Trim();
                        var cmdArgs = string.IsNullOrEmpty(reason)
                            ? new[] { place.PlaceId, uid }
                            : new[] { place.PlaceId, uid, reason };
                        vm.ExecuteCommand("pban", cmdArgs);
                        flyout.Hide();
                    }
                };
                flyout.ShowAt(btn);
            };
            menu.Items.Add(banItem);

            // Add category
            var categoryItem = new MenuItem
            {
                Header = "Add category...",
                Foreground = Brush.Parse("#e0e0e8"),
            };
            categoryItem.Click += (_, _) =>
            {
                var input = new TextBox
                {
                    Watermark = "Category name",
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
                        vm.ExecuteCommand("pcategory", new[] { place.PlaceId, name });
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
                            vm.ExecuteCommand("pcategory", new[] { place.PlaceId, name });
                            flyout.Hide();
                        }
                        ke.Handled = true;
                    }
                };
                flyout.ShowAt(btn);
            };
            menu.Items.Add(categoryItem);
        }

        // Admin/Creator: edit profile & colors
        if (place.IsCreator || place.IsAdmin)
        {
            // Edit place profile (accent color)
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

            // Role colors
            var roleColorsItem = new MenuItem
            {
                Header = "Role colors...",
                Foreground = Brush.Parse("#e0e0e8"),
            };
            roleColorsItem.Click += (_, _) =>
            {
                var ownerColor = place.OwnerColor;
                var adminColor = place.AdminColor;
                var memberColor = place.MemberColor;

                WrapPanel BuildRoleSwatches(string label, string current, Action<string> onSelect)
                {
                    var lbl = new TextBlock
                    {
                        Text = label,
                        Foreground = Brush.Parse("#e0e0e8"),
                        FontSize = 11,
                        Margin = new Thickness(0, 4, 0, 2),
                    };
                    var palette = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };

                    void Build()
                    {
                        palette.Children.Clear();
                        foreach (var hex in ViewModels.SettingsViewModel.PresetColors)
                        {
                            var c = hex;
                            var swatch = new Border
                            {
                                Width = 20, Height = 20,
                                CornerRadius = new CornerRadius(10),
                                Background = Brush.Parse(c),
                                Margin = new Thickness(1),
                                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                                BorderThickness = new Thickness(2),
                                BorderBrush = current == c ? Brush.Parse("#e0e0e8") : Avalonia.Media.Brushes.Transparent,
                            };
                            swatch.PointerPressed += (_, _) =>
                            {
                                current = c;
                                onSelect(c);
                                Build();
                            };
                            palette.Children.Add(swatch);
                        }
                    }
                    Build();
                    var stack = new WrapPanel();
                    // Return as a single panel
                    return palette;
                }

                var ownerLbl = new TextBlock { Text = "Owner", Foreground = Brush.Parse("#e0e0e8"), FontSize = 11, Margin = new Thickness(0, 4, 0, 2) };
                var ownerPalette = BuildRoleSwatches("Owner", ownerColor, c => ownerColor = c);
                var adminLbl = new TextBlock { Text = "Admin", Foreground = Brush.Parse("#e0e0e8"), FontSize = 11, Margin = new Thickness(0, 4, 0, 2) };
                var adminPalette = BuildRoleSwatches("Admin", adminColor, c => adminColor = c);
                var memberLbl = new TextBlock { Text = "Member", Foreground = Brush.Parse("#e0e0e8"), FontSize = 11, Margin = new Thickness(0, 4, 0, 2) };
                var memberPalette = BuildRoleSwatches("Member", memberColor, c => memberColor = c);

                var applyBtn = new Button
                {
                    Content = "Apply",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 8, 0, 0),
                };
                var panel = new StackPanel
                {
                    Spacing = 2, Width = 230,
                    Children =
                    {
                        new TextBlock { Text = "Role Colors", Foreground = Brush.Parse("#e0e0e8"), FontWeight = FontWeight.SemiBold, FontSize = 13 },
                        ownerLbl, ownerPalette,
                        adminLbl, adminPalette,
                        memberLbl, memberPalette,
                        applyBtn,
                    }
                };
                var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
                applyBtn.Click += (_, _) =>
                {
                    vm.ExecuteCommand("prolecolors", new[] { place.PlaceId, ownerColor, adminColor, memberColor });
                    flyout.Hide();
                };
                flyout.ShowAt(btn);
            };
            menu.Items.Add(roleColorsItem);
        }

        // Place settings (admin/creator)
        if (place.IsCreator || place.IsAdmin)
        {
            menu.Items.Add(new Separator());

            var settingsItem = new MenuItem
            {
                Header = "Place settings",
                Foreground = Brush.Parse("#2dd4bf"),
                FontWeight = FontWeight.SemiBold,
            };
            settingsItem.Click += (_, _) => vm.ExecuteCommand("placesettings", new[] { place.PlaceId });
            menu.Items.Add(settingsItem);
        }

        // Creator-only actions
        if (place.IsCreator)
        {
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
        menu.Closed += (_, _) => btn.ContextMenu = null;
        menu.Open(btn);
        e.Handled = true;
    }

    private void Channel_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        if (sender is not Button btn || btn.DataContext is not ChannelItemViewModel channel) return;
        if (DataContext is not MainViewModel vm) return;

        var placeVm = vm.Places.FirstOrDefault(p => p.PlaceId == channel.PlaceId);

        var menu = new ContextMenu();

        // Show topic
        if (!string.IsNullOrEmpty(channel.Topic))
        {
            var topicItem = new MenuItem
            {
                Header = $"Topic: {(channel.Topic.Length > 40 ? channel.Topic[..40] + "..." : channel.Topic)}",
                Foreground = Brush.Parse("#9ca3af"),
                IsEnabled = false,
            };
            menu.Items.Add(topicItem);
            menu.Items.Add(new Separator());
        }

        // Admin actions
        if (placeVm?.IsCreator == true || placeVm?.IsAdmin == true)
        {
            // Set topic
            var topicEditItem = new MenuItem
            {
                Header = "Set topic...",
                Foreground = Brush.Parse("#e0e0e8"),
            };
            topicEditItem.Click += (_, _) =>
            {
                var input = new TextBox
                {
                    Watermark = "Channel topic",
                    Text = channel.Topic,
                    Width = 250,
                    MaxLength = 200,
                    Background = Brush.Parse("#12121a"),
                    Foreground = Brush.Parse("#e0e0e8"),
                    BorderBrush = Brush.Parse("#1e1e2e"),
                };
                var setBtn = new Button
                {
                    Content = "Set",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 8, 0, 0),
                };
                var panel = new StackPanel { Spacing = 4, Width = 260, Children = { input, setBtn } };
                var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
                setBtn.Click += (_, _) =>
                {
                    vm.ExecuteCommand("ptopic", new[] { channel.PlaceId, channel.ChannelId, input.Text ?? "" });
                    flyout.Hide();
                };
                flyout.ShowAt(btn);
            };
            menu.Items.Add(topicEditItem);

            // Delete channel
            var deleteItem = new MenuItem
            {
                Header = "Delete channel",
                Foreground = Brush.Parse("#f87171"),
            };
            deleteItem.Click += (_, _) => vm.ExecuteCommand("pchannelrm", new[] { channel.PlaceId, channel.ChannelId });
            menu.Items.Add(deleteItem);
        }
        else if (channel.IsCreator)
        {
            var deleteItem = new MenuItem
            {
                Header = "Delete channel",
                Foreground = Brush.Parse("#f87171"),
            };
            deleteItem.Click += (_, _) => vm.ExecuteCommand("pchannelrm", new[] { channel.PlaceId, channel.ChannelId });
            menu.Items.Add(deleteItem);
        }

        if (menu.Items.Count == 0) return;

        btn.ContextMenu = menu;
        menu.Closed += (_, _) => btn.ContextMenu = null;
        menu.Open(btn);
        e.Handled = true;
    }

    private void ToggleMemberList_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.IsMemberListVisible = !vm.IsMemberListVisible;
    }

    private void Member_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        if (sender is not Border border || border.DataContext is not PlaceMemberViewModel member) return;
        if (DataContext is not MainViewModel vm) return;

        // Find current place
        if (vm.SelectedConversation is not ChannelItemViewModel channel) return;
        var placeVm = vm.Places.FirstOrDefault(p => p.PlaceId == channel.PlaceId);
        if (placeVm is null) return;

        var menu = new ContextMenu();

        // View fingerprint
        var fpItem = new MenuItem { Header = "View fingerprint", Foreground = Brush.Parse("#e0e0e8") };
        fpItem.Click += (_, _) => vm.ExecuteCommand("fingerprint", new[] { member.UserId });
        menu.Items.Add(fpItem);

        // Admin actions
        if (placeVm.IsCreator || placeVm.IsAdmin)
        {
            menu.Items.Add(new Separator());

            // Set role (owner only)
            if (placeVm.IsCreator && member.Role != "Owner")
            {
                if (member.Role != "Admin")
                {
                    var promoteItem = new MenuItem { Header = "Promote to Admin", Foreground = Brush.Parse("#8b5cf6") };
                    promoteItem.Click += (_, _) => vm.ExecuteCommand("prole", new[] { placeVm.PlaceId, member.UserId, "admin" });
                    menu.Items.Add(promoteItem);
                }
                else
                {
                    var demoteItem = new MenuItem { Header = "Demote to Member", Foreground = Brush.Parse("#e0e0e8") };
                    demoteItem.Click += (_, _) => vm.ExecuteCommand("prole", new[] { placeVm.PlaceId, member.UserId, "member" });
                    menu.Items.Add(demoteItem);
                }
            }

            // Kick (can't kick owner)
            if (member.Role != "Owner")
            {
                var kickItem = new MenuItem { Header = "Kick", Foreground = Brush.Parse("#f87171") };
                kickItem.Click += (_, _) => vm.ExecuteCommand("pkick", new[] { placeVm.PlaceId, member.UserId });
                menu.Items.Add(kickItem);
            }

            // Ban (can't ban owner)
            if (member.Role != "Owner")
            {
                var banItem = new MenuItem { Header = "Ban", Foreground = Brush.Parse("#f87171") };
                banItem.Click += (_, _) => vm.ExecuteCommand("pban", new[] { placeVm.PlaceId, member.UserId });
                menu.Items.Add(banItem);
            }
        }

        border.ContextMenu = menu;
        menu.Closed += (_, _) => border.ContextMenu = null;
        menu.Open(border);
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

        menu.Items.Add(new Separator());

        var deleteItem = new MenuItem
        {
            Header = "Delete contact",
            Foreground = Brush.Parse("#ef4444"),
        };
        deleteItem.Click += (_, _) => vm.ExecuteCommand("remove", new[] { contact.UserId });
        menu.Items.Add(deleteItem);

        btn.ContextMenu = menu;
        menu.Closed += (_, _) => btn.ContextMenu = null;
        menu.Open(btn);
        e.Handled = true;
    }

    private async void AttachButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach files",
            AllowMultiple = true,
        });

        if (files.Count > 0)
        {
            var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).ToArray();
            if (paths.Length > 0) vm.RequestAttach(paths!);
        }
    }

    private void AttachmentItem_Click(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (sender is not Border border || border.Tag is not AttachmentViewModel att) return;
        // Download will be handled by MainWindow via event
    }

    private void MessageItem_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if (sender is not Border border || border.Tag is not ChatMessageViewModel msg) return;
        if (DataContext is not MainViewModel vm) return;
        if (msg.IsSystem || msg.IsSecurityAlert || msg.IsDeleted) return;

        var menu = new ContextMenu();
        var fg = Brush.Parse("#e0e0e8");
        var dim = Brush.Parse("#6b7280");

        // — Reactions at the top: horizontal quick bar + expandable categories —
        if (msg.MsgId is not null)
        {
            var quickEmojis = new[] { "👍", "❤️", "😂", "🔥", "👀" };
            var emojiBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
            };
            foreach (var emoji in quickEmojis)
            {
                var capturedEmoji = emoji;
                var existing = msg.Reactions.FirstOrDefault(r => r.Emoji == capturedEmoji);
                var isOwn = existing is not null && existing.IsOwn;
                var tb = new TextBlock
                {
                    Text = emoji,
                    FontSize = 22,
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Opacity = isOwn ? 0.45 : 1.0,
                    Padding = new Thickness(6, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                tb.PointerPressed += (_, pe) =>
                {
                    pe.Handled = true;
                    vm.RequestReaction(msg.MsgId!, capturedEmoji, !isOwn);
                    menu.Close();
                };
                emojiBar.Children.Add(tb);
            }

            var barItem = new MenuItem { Header = emojiBar, Padding = new Thickness(4, 2) };
            barItem.Click += (_, ce) => ce.Handled = true;
            menu.Items.Add(barItem);

            var categories = new (string Name, string[] Emojis)[]
            {
                ("😀 Smileys", new[] { "😀", "😁", "😅", "🤣", "😊", "😇", "😍", "🥰", "😘", "😎", "🤩", "🤔", "🤨", "😏", "🙄", "😬", "😢", "😭", "😤", "😡", "🥺", "😱", "🤯", "😴", "🤮", "🥳", "😈", "🤡" }),
                ("👋 Gestures", new[] { "👍", "👎", "👏", "🙌", "🤝", "🙏", "💪", "✌️", "🤞", "👌", "✋", "👋", "🤙", "✊", "👊", "☝️", "🫡", "🫶" }),
                ("❤️ Hearts", new[] { "❤️", "🧡", "💛", "💚", "💙", "💜", "🖤", "🤍", "💔", "❣️", "💕", "💖", "💝" }),
                ("⭐ Symbols", new[] { "✨", "⭐", "💯", "💥", "💫", "💤", "💬", "👀", "✅", "❌", "⚠️", "🚀", "🎉", "🏆", "💎", "🔔", "📌", "🎯", "🔥", "💀", "☠️", "🤖", "👽", "👻", "💩" }),
                ("🐾 Animals", new[] { "🐶", "🐱", "🐭", "🐰", "🦊", "🐻", "🐼", "🐸", "🐵", "🙈", "🙉", "🙊", "🐧", "🦄", "🐍", "🦋", "🐝", "🐢" }),
                ("🍕 Food", new[] { "🍎", "🍕", "🍔", "🌮", "🍟", "🍿", "🍩", "🍪", "🎂", "🍰", "☕", "🍺", "🍷", "🧃", "🍫" }),
            };

            var moreItem = new MenuItem { Header = "More reactions ▸", Foreground = dim };
            foreach (var (catName, catEmojis) in categories)
            {
                var catItem = new MenuItem { Header = catName, Foreground = fg };
                foreach (var emoji in catEmojis)
                {
                    var capturedEmoji = emoji;
                    var existing = msg.Reactions.FirstOrDefault(r => r.Emoji == capturedEmoji);
                    var isOwn = existing is not null && existing.IsOwn;
                    var emojiItem = new MenuItem
                    {
                        Header = isOwn ? $"{emoji} ✕" : emoji,
                        Foreground = fg,
                        FontSize = 18,
                    };
                    emojiItem.Click += (_, _) => vm.RequestReaction(msg.MsgId!, capturedEmoji, !isOwn);
                    catItem.Items.Add(emojiItem);
                }
                moreItem.Items.Add(catItem);
            }
            menu.Items.Add(moreItem);
            menu.Items.Add(new Separator());
        }

        // — Edit Message (own only) —
        if (msg.IsOwn && msg.MsgId is not null)
        {
            var editItem = new MenuItem { Header = "Edit Message", Foreground = fg };
            editItem.Click += (_, _) => vm.StartEdit(msg);
            menu.Items.Add(editItem);
        }

        // — Reply —
        if (msg.MsgId is not null)
        {
            var replyItem = new MenuItem { Header = "Reply", Foreground = fg };
            replyItem.Click += (_, _) => vm.SetReplyTarget(msg);
            menu.Items.Add(replyItem);
        }

        // — Forward (submenu with contacts + groups) —
        if (!string.IsNullOrEmpty(msg.Text))
        {
            var forwardItem = new MenuItem { Header = "Forward", Foreground = fg };
            foreach (var contact in vm.Contacts)
            {
                var c = contact;
                var sub = new MenuItem { Header = c.DisplayName, Foreground = fg };
                sub.Click += (_, _) => vm.RequestForward(c.UserId, msg.Text, false);
                forwardItem.Items.Add(sub);
            }
            if (vm.Contacts.Count > 0 && vm.Groups.Count > 0)
                forwardItem.Items.Add(new Separator());
            foreach (var group in vm.Groups)
            {
                var g = group;
                var sub = new MenuItem { Header = $"# {g.Name}", Foreground = fg };
                sub.Click += (_, _) => vm.RequestForward(g.GroupId, msg.Text, true);
                forwardItem.Items.Add(sub);
            }
            if (forwardItem.Items.Count > 0)
                menu.Items.Add(forwardItem);
        }

        // — Copy Text —
        if (!string.IsNullOrEmpty(msg.Text))
        {
            string? selectedText = null;
            foreach (var desc in border.GetVisualDescendants())
            {
                if (desc is MarkdownTextBlock mtb && !string.IsNullOrEmpty(mtb.SelectedText))
                {
                    selectedText = mtb.SelectedText;
                    break;
                }
            }
            var textToCopy = selectedText ?? msg.Text;
            var copyItem = new MenuItem
            {
                Header = selectedText is not null ? "Copy Selection" : "Copy Text",
                Foreground = fg
            };
            copyItem.Click += async (_, _) =>
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is not null)
                    await clipboard.SetTextAsync(textToCopy);
            };
            menu.Items.Add(copyItem);
        }

        // — Pin Message (Places only) —
        if (msg.MsgId is not null && vm.SelectedConversation is ChannelItemViewModel)
        {
            var pinItem = new MenuItem { Header = "Pin Message", Foreground = fg };
            pinItem.Click += (_, _) => vm.RequestPin(msg.MsgId, msg.Text, msg.From);
            menu.Items.Add(pinItem);
        }

        // — Mark Unread —
        {
            var markItem = new MenuItem { Header = "Mark Unread", Foreground = fg };
            markItem.Click += (_, _) =>
            {
                if (vm.SelectedConversation is ContactItemViewModel c) c.HasUnread = true;
                else if (vm.SelectedConversation is GroupItemViewModel g) g.HasUnread = true;
                else if (vm.SelectedConversation is ChannelItemViewModel ch) ch.HasUnread = true;
            };
            menu.Items.Add(markItem);
        }

        // — Speak Message —
        if (!string.IsNullOrEmpty(msg.Text))
        {
            var speakItem = new MenuItem { Header = "Speak Message", Foreground = fg };
            speakItem.Click += (_, _) =>
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo { CreateNoWindow = true, UseShellExecute = false };
                    if (OperatingSystem.IsLinux())
                    {
                        psi.FileName = "spd-say";
                        psi.ArgumentList.Add(msg.Text);
                    }
                    else if (OperatingSystem.IsMacOS())
                    {
                        psi.FileName = "say";
                        psi.ArgumentList.Add(msg.Text);
                    }
                    else if (OperatingSystem.IsWindows())
                    {
                        psi.FileName = "powershell";
                        psi.ArgumentList.Add("-Command");
                        psi.ArgumentList.Add($"Add-Type -AssemblyName System.Speech; (New-Object System.Speech.Synthesis.SpeechSynthesizer).Speak([System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(msg.Text))}')))");
                    }
                    if (psi.FileName is not null)
                        System.Diagnostics.Process.Start(psi);
                }
                catch { /* TTS not available */ }
            };
            menu.Items.Add(speakItem);
        }

        menu.Items.Add(new Separator());

        // — Delete Message (red) —
        if (msg.MsgId is not null)
        {
            var canDelete = msg.IsOwn;
            if (!canDelete && vm.SelectedConversation is ChannelItemViewModel)
                canDelete = true; // Places: server-side validates permissions
            if (canDelete)
            {
                var deleteItem = new MenuItem { Header = "Delete Message", Foreground = Brush.Parse("#f87171") };
                deleteItem.Click += (_, _) =>
                {
                    msg.IsDeleted = true;
                    msg.Text = "";
                    vm.RequestDelete(msg.MsgId);
                };
                menu.Items.Add(deleteItem);
            }
        }

        // — Copy Message ID —
        if (msg.MsgId is not null)
        {
            var idItem = new MenuItem { Header = "Copy Message ID", Foreground = dim };
            idItem.Click += async (_, _) =>
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is not null)
                    await clipboard.SetTextAsync(msg.MsgId);
            };
            menu.Items.Add(idItem);
        }

        if (menu.Items.Count == 0) return;

        border.ContextMenu = menu;
        menu.Closed += (_, _) => border.ContextMenu = null;
        menu.Open(border);
        e.Handled = true;
    }
}
