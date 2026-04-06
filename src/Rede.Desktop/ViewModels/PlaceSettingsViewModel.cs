using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rede.Core.Storage;

namespace Rede.Desktop.ViewModels;

public partial class PlaceSettingsViewModel : ViewModelBase
{
    [ObservableProperty] private string _placeId = "";
    [ObservableProperty] private string _placeName = "";
    [ObservableProperty] private string _accentColor = "#8b5cf6";
    [ObservableProperty] private bool _isCreator;
    [ObservableProperty] private bool _isAdmin;

    // Category navigation
    [ObservableProperty] private int _selectedCategoryIndex;

    public bool IsOverviewCategory => SelectedCategoryIndex == 0;
    public bool IsRolesCategory => SelectedCategoryIndex == 1;
    public bool IsMembersCategory => SelectedCategoryIndex == 2;
    public bool IsChannelsCategory => SelectedCategoryIndex == 3;
    public bool IsBansCategory => SelectedCategoryIndex == 4;
    public bool IsEmotesCategory => SelectedCategoryIndex == 5;

    partial void OnSelectedCategoryIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsOverviewCategory));
        OnPropertyChanged(nameof(IsRolesCategory));
        OnPropertyChanged(nameof(IsMembersCategory));
        OnPropertyChanged(nameof(IsChannelsCategory));
        OnPropertyChanged(nameof(IsBansCategory));
        OnPropertyChanged(nameof(IsEmotesCategory));
    }

    // Overview
    [ObservableProperty] private string _ownerColor = "#eab308";
    [ObservableProperty] private string _adminColor = "#8b5cf6";
    [ObservableProperty] private string _memberColor = "#6b7280";
    [ObservableProperty] private int _memberCount;
    [ObservableProperty] private int _channelCount;
    [ObservableProperty] private int _roleCount;
    [ObservableProperty] private int _banCount;
    [ObservableProperty] private int _emoteCount;

    // Roles
    [ObservableProperty] private ObservableCollection<RoleViewModel> _roles = new();
    [ObservableProperty] private RoleViewModel? _selectedRole;

    // New role form
    [ObservableProperty] private string _newRoleName = "";
    [ObservableProperty] private string _newRoleColor = "#6b7280";

    // Members
    [ObservableProperty] private ObservableCollection<MemberRoleViewModel> _memberRoles = new();

    // Channels
    [ObservableProperty] private ObservableCollection<ChannelSettingsViewModel> _channelSettings = new();

    // Bans
    [ObservableProperty] private ObservableCollection<BanViewModel> _bans = new();

    // Emotes
    [ObservableProperty] private ObservableCollection<EmoteViewModel> _emotes = new();

    public string PlaceInitial => string.IsNullOrEmpty(PlaceName) ? "?" : PlaceName[..1].ToUpperInvariant();
    public IBrush AccentBrush => ColorHelper.SafeParse(AccentColor);

    public event Action? OnBackRequested;
    public event Action<string, string, string, string>? OnRoleColorsChanged; // placeId, owner, admin, member
    public event Action<string, string, string, long>? OnCreateRole; // placeId, name, color, permissions
    public event Action<string, string>? OnDeleteRole; // placeId, roleId
    public event Action<string, string, string>? OnAssignRole; // placeId, userId, roleId
    public event Action<string, string, string>? OnRemoveRole; // placeId, userId, roleId
    public event Action<string, string>? OnKickMember; // placeId, userId
    public event Action<string, string, string?>? OnBanMember; // placeId, userId, reason
    public event Action<string, string>? OnUnbanMember; // placeId, userId
    public event Action<string, string>? OnDeleteChannel; // placeId, channelId
    public event Action<string, string, string>? OnSetTopic; // placeId, channelId, topic
    public event Action<string, string>? OnCreateChannel; // placeId, name
    public event Action<string, string>? OnCreateCategory; // placeId, name
    public event Action<string, string>? OnDeleteCategory; // placeId, name
    public event Action<string, string, string, long, long>? OnSetChannelPerms; // placeId, channelId, roleId, allow, deny
    public event Action<string, string>? OnProfileChanged; // placeId, accentColor
    public event Action<string>? OnInitRoles; // placeId

    [RelayCommand]
    private void Back() => OnBackRequested?.Invoke();

    // Public invoke methods for code-behind (events can only be raised from declaring class)
    public void RequestCreateRole(string pid, string name, string color, long perms) => OnCreateRole?.Invoke(pid, name, color, perms);
    public void RequestDeleteRole(string pid, string roleId) => OnDeleteRole?.Invoke(pid, roleId);
    public void RequestAssignRole(string pid, string userId, string roleId) => OnAssignRole?.Invoke(pid, userId, roleId);
    public void RequestRemoveRole(string pid, string userId, string roleId) => OnRemoveRole?.Invoke(pid, userId, roleId);
    public void RequestKickMember(string pid, string userId) => OnKickMember?.Invoke(pid, userId);
    public void RequestBanMember(string pid, string userId, string? reason) => OnBanMember?.Invoke(pid, userId, reason);
    public void RequestUnbanMember(string pid, string userId) => OnUnbanMember?.Invoke(pid, userId);
    public void RequestCreateChannel(string pid, string name) => OnCreateChannel?.Invoke(pid, name);
    public void RequestCreateCategory(string pid, string name) => OnCreateCategory?.Invoke(pid, name);
    public void RequestDeleteChannel(string pid, string chId) => OnDeleteChannel?.Invoke(pid, chId);

    [RelayCommand]
    private void CreateRole()
    {
        if (string.IsNullOrWhiteSpace(NewRoleName)) return;
        OnCreateRole?.Invoke(PlaceId, NewRoleName.Trim(), NewRoleColor, (long)PlacePermission.SendMessages);
        NewRoleName = "";
    }

    [RelayCommand]
    private void DeleteRole(RoleViewModel? role)
    {
        if (role is null) return;
        OnDeleteRole?.Invoke(PlaceId, role.RoleId);
    }

    [RelayCommand]
    private void InitializeRoles()
    {
        OnInitRoles?.Invoke(PlaceId);
    }

    [RelayCommand]
    private void SaveRoleColors()
    {
        OnRoleColorsChanged?.Invoke(PlaceId, OwnerColor, AdminColor, MemberColor);
    }

    [RelayCommand]
    private void SaveProfile()
    {
        OnProfileChanged?.Invoke(PlaceId, AccentColor);
    }

    public void LoadFromPlace(Place place, string placeId, string ownUserId)
    {
        PlaceId = placeId;
        PlaceName = place.Name;
        AccentColor = place.AccentColor ?? "#8b5cf6";
        IsCreator = place.CreatorId == ownUserId;
        IsAdmin = place.Roles.TryGetValue(ownUserId, out var role) && role >= PlaceRole.Admin;
        OwnerColor = place.OwnerColor;
        AdminColor = place.AdminColor;
        MemberColor = place.MemberColor;
        MemberCount = place.Members.Count;
        ChannelCount = place.Channels.Count;
        BanCount = place.Bans.Count;
        EmoteCount = place.Emotes.Count;

        // Load roles
        Roles.Clear();
        foreach (var (id, cr) in place.CustomRoles.OrderByDescending(r => r.Value.Position))
        {
            Roles.Add(new RoleViewModel
            {
                RoleId = id,
                Name = cr.Name,
                Color = cr.Color,
                Position = cr.Position,
                Permissions = cr.Permissions,
                IsBuiltIn = cr.Name is "@everyone" or "Admin" or "Owner",
            });
        }
        RoleCount = Roles.Count;

        // Load members with roles
        MemberRoles.Clear();
        foreach (var memberId in place.Members.OrderBy(m => m))
        {
            var legacyRole = place.Roles.TryGetValue(memberId, out var lr) ? lr : PlaceRole.Member;
            var assignedRoles = place.MemberRoles.TryGetValue(memberId, out var rids) ? rids : new();
            var highestRole = Rede.Core.Services.PlaceService.GetHighestRole(place, memberId);

            MemberRoles.Add(new MemberRoleViewModel
            {
                UserId = memberId,
                LegacyRole = legacyRole,
                RoleName = highestRole.Name,
                RoleColor = highestRole.Color,
                AssignedRoleIds = new ObservableCollection<string>(assignedRoles),
                IsOwner = place.CreatorId == memberId,
            });
        }

        // Load channels
        ChannelSettings.Clear();
        foreach (var (chId, ch) in place.Channels.OrderBy(c => c.Value.Category ?? "").ThenBy(c => c.Value.Position))
        {
            ChannelSettings.Add(new ChannelSettingsViewModel
            {
                ChannelId = chId,
                Name = ch.Name,
                Topic = ch.Topic ?? "",
                Category = ch.Category ?? "",
                Position = ch.Position,
                PermOverrideCount = ch.PermissionOverrides?.Count ?? 0,
            });
        }

        // Load bans
        Bans.Clear();
        foreach (var (uid, ban) in place.Bans)
        {
            Bans.Add(new BanViewModel
            {
                UserId = ban.UserId,
                BannedBy = ban.BannedBy,
                Reason = ban.Reason ?? "",
                BannedAt = DateTimeOffset.FromUnixTimeMilliseconds(ban.BannedAt).LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
            });
        }

        // Load emotes
        Emotes.Clear();
        foreach (var (eid, emote) in place.Emotes)
        {
            Emotes.Add(new EmoteViewModel
            {
                EmoteId = eid,
                Name = emote.Name,
                UploadedBy = emote.UploadedBy,
            });
        }
    }
}

public partial class RoleViewModel : ViewModelBase
{
    [ObservableProperty] private string _roleId = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _color = "#6b7280";
    [ObservableProperty] private int _position;
    [ObservableProperty] private long _permissions;
    [ObservableProperty] private bool _isBuiltIn;

    public IBrush ColorBrush => ColorHelper.SafeParse(Color);

    // Permission toggles (bound to UI checkboxes)
    public bool CanSendMessages { get => HasPerm(PlacePermission.SendMessages); set => TogglePerm(PlacePermission.SendMessages, value); }
    public bool CanManageMessages { get => HasPerm(PlacePermission.ManageMessages); set => TogglePerm(PlacePermission.ManageMessages, value); }
    public bool CanManageChannels { get => HasPerm(PlacePermission.ManageChannels); set => TogglePerm(PlacePermission.ManageChannels, value); }
    public bool CanManageRoles { get => HasPerm(PlacePermission.ManageRoles); set => TogglePerm(PlacePermission.ManageRoles, value); }
    public bool CanKickMembers { get => HasPerm(PlacePermission.KickMembers); set => TogglePerm(PlacePermission.KickMembers, value); }
    public bool CanBanMembers { get => HasPerm(PlacePermission.BanMembers); set => TogglePerm(PlacePermission.BanMembers, value); }
    public bool CanManageEmotes { get => HasPerm(PlacePermission.ManageEmotes); set => TogglePerm(PlacePermission.ManageEmotes, value); }
    public bool CanManagePlace { get => HasPerm(PlacePermission.ManagePlace); set => TogglePerm(PlacePermission.ManagePlace, value); }
    public bool IsAdministrator { get => HasPerm(PlacePermission.Administrator); set => TogglePerm(PlacePermission.Administrator, value); }

    private bool HasPerm(PlacePermission p) => (Permissions & (long)p) != 0;
    private void TogglePerm(PlacePermission p, bool on)
    {
        if (on) Permissions |= (long)p;
        else Permissions &= ~(long)p;
        OnPropertyChanged(nameof(PermissionsSummary));
    }

    public string PermissionsSummary
    {
        get
        {
            if ((Permissions & (long)PlacePermission.Administrator) != 0) return "Administrator";
            var parts = new System.Collections.Generic.List<string>();
            if (HasPerm(PlacePermission.SendMessages)) parts.Add("Send");
            if (HasPerm(PlacePermission.ManageMessages)) parts.Add("ManageMsgs");
            if (HasPerm(PlacePermission.ManageChannels)) parts.Add("ManageCh");
            if (HasPerm(PlacePermission.ManageRoles)) parts.Add("Roles");
            if (HasPerm(PlacePermission.KickMembers)) parts.Add("Kick");
            if (HasPerm(PlacePermission.BanMembers)) parts.Add("Ban");
            if (HasPerm(PlacePermission.ManageEmotes)) parts.Add("Emotes");
            if (HasPerm(PlacePermission.ManagePlace)) parts.Add("Place");
            return parts.Count == 0 ? "None" : string.Join(", ", parts);
        }
    }

    partial void OnPermissionsChanged(long value)
    {
        OnPropertyChanged(nameof(PermissionsSummary));
        OnPropertyChanged(nameof(CanSendMessages));
        OnPropertyChanged(nameof(CanManageMessages));
        OnPropertyChanged(nameof(CanManageChannels));
        OnPropertyChanged(nameof(CanManageRoles));
        OnPropertyChanged(nameof(CanKickMembers));
        OnPropertyChanged(nameof(CanBanMembers));
        OnPropertyChanged(nameof(CanManageEmotes));
        OnPropertyChanged(nameof(CanManagePlace));
        OnPropertyChanged(nameof(IsAdministrator));
    }
}

public partial class MemberRoleViewModel : ViewModelBase
{
    [ObservableProperty] private string _userId = "";
    [ObservableProperty] private PlaceRole _legacyRole;
    [ObservableProperty] private string _roleName = "";
    [ObservableProperty] private string _roleColor = "#6b7280";
    [ObservableProperty] private ObservableCollection<string> _assignedRoleIds = new();
    [ObservableProperty] private bool _isOwner;

    public IBrush RoleColorBrush => ColorHelper.SafeParse(RoleColor);
    public string DisplayId => UserId.Length > 12 ? UserId[..12] + "..." : UserId;
}

public partial class ChannelSettingsViewModel : ViewModelBase
{
    [ObservableProperty] private string _channelId = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _topic = "";
    [ObservableProperty] private string _category = "";
    [ObservableProperty] private int _position;
    [ObservableProperty] private int _permOverrideCount;
}

public partial class BanViewModel : ViewModelBase
{
    [ObservableProperty] private string _userId = "";
    [ObservableProperty] private string _bannedBy = "";
    [ObservableProperty] private string _reason = "";
    [ObservableProperty] private string _bannedAt = "";

    public string DisplayId => UserId.Length > 16 ? UserId[..16] + "..." : UserId;
}

public partial class EmoteViewModel : ViewModelBase
{
    [ObservableProperty] private string _emoteId = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _uploadedBy = "";
}
