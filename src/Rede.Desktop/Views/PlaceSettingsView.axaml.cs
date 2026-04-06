using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Rede.Desktop.ViewModels;

namespace Rede.Desktop.Views;

public partial class PlaceSettingsView : UserControl
{
    public PlaceSettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is PlaceSettingsViewModel vm)
        {
            BuildColorSwatches(AccentSwatches, vm.AccentColor, c => vm.AccentColor = c);
            BuildColorSwatches(OwnerColorSwatches, vm.OwnerColor, c => vm.OwnerColor = c);
            BuildColorSwatches(AdminColorSwatches, vm.AdminColor, c => vm.AdminColor = c);
            BuildColorSwatches(MemberColorSwatches, vm.MemberColor, c => vm.MemberColor = c);
        }
    }

    private static void BuildColorSwatches(WrapPanel? panel, string selected, Action<string> onSelect)
    {
        if (panel is null) return;
        panel.Children.Clear();

        foreach (var hex in SettingsViewModel.PresetColors)
        {
            var c = hex;
            var swatch = new Border
            {
                Width = 22, Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = Brush.Parse(c),
                Margin = new Thickness(2),
                Cursor = new Cursor(StandardCursorType.Hand),
                BorderThickness = new Thickness(2),
                BorderBrush = selected == c ? Brush.Parse("#e0e0e8") : Brushes.Transparent,
            };
            swatch.PointerPressed += (_, _) =>
            {
                onSelect(c);
                selected = c;
                BuildColorSwatches(panel, selected, onSelect);
            };
            panel.Children.Add(swatch);
        }
    }

    private void SaveRolePerms_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is RoleViewModel role && DataContext is PlaceSettingsViewModel vm)
        {
            vm.RequestCreateRole(vm.PlaceId, role.Name, role.Color, role.Permissions);
        }
    }

    private void DeleteRole_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is RoleViewModel role && DataContext is PlaceSettingsViewModel vm)
        {
            vm.RequestDeleteRole(vm.PlaceId, role.RoleId);
        }
    }

    private void MemberItem_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Border border || border.Tag is not MemberRoleViewModel member) return;
        if (DataContext is not PlaceSettingsViewModel vm) return;
        if (!e.GetCurrentPoint(this).Properties.PointerUpdateKind.ToString().Contains("Right")) return;
        if (!vm.IsAdmin && !vm.IsCreator) return;

        var menu = new ContextMenu();

        // Assign role submenu
        if (vm.Roles.Count > 0)
        {
            var assignMenu = new MenuItem
            {
                Header = "Assign role",
                Foreground = Brush.Parse("#e0e0e8"),
            };
            foreach (var role in vm.Roles)
            {
                var r = role;
                var item = new MenuItem
                {
                    Header = r.Name,
                    Foreground = r.ColorBrush,
                };
                item.Click += (_, _) => vm.RequestAssignRole(vm.PlaceId, member.UserId, r.RoleId);
                ((ItemsControl)assignMenu).Items.Add(item);
            }
            menu.Items.Add(assignMenu);

            // Remove role submenu
            if (member.AssignedRoleIds.Count > 0)
            {
                var removeMenu = new MenuItem
                {
                    Header = "Remove role",
                    Foreground = Brush.Parse("#e0e0e8"),
                };
                foreach (var roleId in member.AssignedRoleIds)
                {
                    var rid = roleId;
                    var roleName = vm.Roles.FirstOrDefault(r => r.RoleId == rid)?.Name ?? rid;
                    var item = new MenuItem { Header = roleName };
                    item.Click += (_, _) => vm.RequestRemoveRole(vm.PlaceId, member.UserId, rid);
                    ((ItemsControl)removeMenu).Items.Add(item);
                }
                menu.Items.Add(removeMenu);
            }

            menu.Items.Add(new Separator());
        }

        // Kick
        if (!member.IsOwner)
        {
            var kickItem = new MenuItem
            {
                Header = "Kick",
                Foreground = Brush.Parse("#e0e0e8"),
            };
            kickItem.Click += (_, _) => vm.RequestKickMember(vm.PlaceId, member.UserId);
            menu.Items.Add(kickItem);

            var banItem = new MenuItem
            {
                Header = "Ban",
                Foreground = Brush.Parse("#f87171"),
            };
            banItem.Click += (_, _) => vm.RequestBanMember(vm.PlaceId, member.UserId, null);
            menu.Items.Add(banItem);
        }

        if (menu.Items.Count == 0) return;
        border.ContextMenu = menu;
        menu.Closed += (_, _) => border.ContextMenu = null;
        menu.Open(border);
        e.Handled = true;
    }

    private void CreateChannel_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PlaceSettingsViewModel vm)
        {
            var name = NewChannelNameInput?.Text?.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                vm.RequestCreateChannel(vm.PlaceId, name);
                if (NewChannelNameInput is not null) NewChannelNameInput.Text = "";
            }
        }
    }

    private void CreateCategory_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PlaceSettingsViewModel vm)
        {
            var name = NewCategoryNameInput?.Text?.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                vm.RequestCreateCategory(vm.PlaceId, name);
                if (NewCategoryNameInput is not null) NewCategoryNameInput.Text = "";
            }
        }
    }

    private void DeleteChannel_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ChannelSettingsViewModel ch && DataContext is PlaceSettingsViewModel vm)
        {
            vm.RequestDeleteChannel(vm.PlaceId, ch.ChannelId);
        }
    }

    private void Unban_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is BanViewModel ban && DataContext is PlaceSettingsViewModel vm)
        {
            vm.RequestUnbanMember(vm.PlaceId, ban.UserId);
        }
    }
}
