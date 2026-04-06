using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Rede.Desktop.ViewModels;

// C1: Safe color parsing — validates hex format before Brush.Parse to prevent UI crashes
internal static partial class ColorHelper
{
    private static readonly IBrush DefaultBrush = Brush.Parse("#8b5cf6");

    [GeneratedRegex(@"^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColorRegex();

    public static IBrush SafeParse(string color, string fallback = "#8b5cf6")
    {
        try
        {
            if (HexColorRegex().IsMatch(color))
                return Brush.Parse(color);
            return fallback == "#8b5cf6" ? DefaultBrush : Brush.Parse(fallback);
        }
        catch { return DefaultBrush; }
    }

    public static string Validate(string color, string fallback = "#8b5cf6")
        => HexColorRegex().IsMatch(color) ? color : fallback;

    // H2: Max avatar size on receive (256KB decoded)
    public const int MaxAvatarBytes = 256 * 1024;
    public const int MaxIconBytes = 256 * 1024;
}

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty] private ViewModelBase _currentView;
    [ObservableProperty] private string _connectionStatus = "Disconnected";
    [ObservableProperty] private bool _isConnected;

    // User profile (bottom-left panel)
    [ObservableProperty] private string _ownDisplayName = "";
    [ObservableProperty] private string _ownUserId = "";
    [ObservableProperty] private string _ownAccentColor = "#8b5cf6";
    [ObservableProperty] private string _ownStatus = "offline";
    [ObservableProperty] private string? _ownCustomStatus;
    [ObservableProperty] private Bitmap? _ownAvatarImage;
    [ObservableProperty] private bool _hasOwnAvatar;

    public string OwnInitial => string.IsNullOrEmpty(OwnDisplayName) ? "?" : OwnDisplayName[..1].ToUpperInvariant();
    public IBrush OwnAccentBrush => ColorHelper.SafeParse(OwnAccentColor);
    public IBrush OwnStatusBrush => OwnStatus switch
    {
        "online" => Brush.Parse("#22c55e"),
        "away" => Brush.Parse("#eab308"),
        "dnd" => Brush.Parse("#ef4444"),
        _ => Brush.Parse("#6b7280"),
    };
    public string OwnStatusText => OwnCustomStatus ?? OwnStatus switch
    {
        "online" => "Online",
        "away" => "Away",
        "dnd" => "Do Not Disturb",
        _ => "Offline",
    };

    partial void OnOwnDisplayNameChanged(string value) => OnPropertyChanged(nameof(OwnInitial));
    partial void OnOwnAccentColorChanged(string value) => OnPropertyChanged(nameof(OwnAccentBrush));
    partial void OnOwnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(OwnStatusBrush));
        OnPropertyChanged(nameof(OwnStatusText));
    }
    partial void OnOwnCustomStatusChanged(string? value) => OnPropertyChanged(nameof(OwnStatusText));

    // Sidebar state
    [ObservableProperty] private bool _isSidebarCollapsed;
    [ObservableProperty] private ObservableCollection<ContactItemViewModel> _contacts = new();
    [ObservableProperty] private ObservableCollection<GroupItemViewModel> _groups = new();
    [ObservableProperty] private ObservableCollection<PlaceItemViewModel> _places = new();
    [ObservableProperty] private object? _selectedConversation;
    [ObservableProperty] private string _searchText = "";

    partial void OnSearchTextChanged(string value) => ApplySearchFilter();

    private void ApplySearchFilter()
    {
        var q = (SearchText ?? "").Trim();
        var hasQuery = q.Length > 0;
        var qLower = q.ToLowerInvariant();

        foreach (var c in Contacts)
            c.IsMatch = !hasQuery || (c.DisplayName ?? "").ToLowerInvariant().Contains(qLower);
        foreach (var g in Groups)
            g.IsMatch = !hasQuery || (g.Name ?? "").ToLowerInvariant().Contains(qLower);
        foreach (var p in Places)
            p.IsMatch = !hasQuery || (p.Name ?? "").ToLowerInvariant().Contains(qLower);
    }

    // Chat state
    [ObservableProperty] private ObservableCollection<ChatMessageViewModel> _messages = new();
    [ObservableProperty] private string _chatTitle = "";
    [ObservableProperty] private string _inputText = "";
    [ObservableProperty] private int _ttlSeconds;
    [ObservableProperty] private bool _isContactSelected;
    [ObservableProperty] private bool _isPlaceSelected;
    [ObservableProperty] private string _channelTopic = "";
    [ObservableProperty] private bool _isMemberListVisible;
    [ObservableProperty] private ObservableCollection<PlaceMemberViewModel> _memberList = new();

    public MainViewModel()
    {
        _currentView = this;
    }

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
    }

    [RelayCommand]
    private void SelectConversation(object? item)
    {
        SelectedConversation = item;
        IsPlaceSelected = false;
        ChannelTopic = "";
        if (item is ContactItemViewModel contact)
        {
            ChatTitle = contact.DisplayName;
            IsContactSelected = true;
            LoadChatHistory(contact.UserId);
        }
        else if (item is GroupItemViewModel group)
        {
            ChatTitle = $"# {group.Name}";
            IsContactSelected = false;
            LoadChatHistory(group.GroupId);
        }
        else if (item is ChannelItemViewModel channel)
        {
            ChatTitle = $"{channel.PlaceName} > #{channel.Name}";
            IsContactSelected = false;
            IsPlaceSelected = true;
            ChannelTopic = channel.Topic;
            LoadChatHistory($"place:{channel.PlaceId}:{channel.ChannelId}");
            OnMemberListRequested?.Invoke(channel.PlaceId);
            return;
        }
    }

    public void DeselectConversation()
    {
        SelectedConversation = null;
        ChatTitle = "";
        IsContactSelected = false;
        IsPlaceSelected = false;
        ChannelTopic = "";
        IsMemberListVisible = false;
        Messages.Clear();
        MemberList.Clear();
    }

    [RelayCommand]
    private void SendMessage()
    {
        var text = InputText?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        if (text.Length > 4096) text = text[..4096];

        InputText = "";

        if (text.StartsWith('/'))
        {
            HandleCommand(text);
            return;
        }

        // Handle edit mode
        if (IsEditing && EditingMsgId is not null)
        {
            var editMsgId = EditingMsgId;
            CancelEdit();
            // Update the message in UI
            var existing = Messages.FirstOrDefault(m => m.MsgId == editMsgId);
            if (existing is not null)
            {
                existing.Text = text;
                existing.IsEdited = true;
            }
            OnMessageEdit?.Invoke(editMsgId, text);
            return;
        }

        // Capture reply state before clearing
        var replyMsgId = ReplyToMsgId;
        var replyPreview = ReplyToPreview;
        var replyAuthor = ReplyToAuthor;
        CancelReply();

        // Add message to UI immediately (optimistic)
        Messages.Add(new ChatMessageViewModel
        {
            Text = text,
            IsOwn = true,
            Timestamp = DateTime.Now,
            ReplyToPreview = replyPreview,
            ReplyToAuthor = replyAuthor,
        });

        // Actual send logic will be wired via service layer
        OnMessageSend?.Invoke(text, replyMsgId, replyPreview, replyAuthor);
    }

    private void HandleCommand(string text)
    {
        var parts = text[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        OnCommandExecuted?.Invoke(parts[0], parts.Length > 1 ? parts[1..] : Array.Empty<string>());
    }

    private void LoadChatHistory(string chatId)
    {
        Messages.Clear();
        OnChatHistoryRequested?.Invoke(chatId);
    }

    public void AddIncomingMessage(string from, string text, DateTime timestamp, bool isSystem = false,
        string? senderRole = null, string? roleBadgeColor = null,
        string? msgId = null, string? replyToPreview = null, string? replyToAuthor = null)
    {
        // M10: Truncate incoming message text before rendering
        if (text.Length > 8192) text = text[..8192] + "…";

        // Look up sender's profile customization from contacts
        var contact = Contacts.FirstOrDefault(c => c.UserId == from);
        var accentColor = contact?.AccentColor ?? "#8b5cf6";
        var initial = string.IsNullOrEmpty(from) ? "?" : from[..1].ToUpperInvariant();
        if (contact is not null)
            initial = contact.Initial;

        var msg = new ChatMessageViewModel
        {
            From = from,
            Text = text,
            IsOwn = false,
            IsSystem = isSystem,
            Timestamp = timestamp,
            SenderAccentColor = accentColor,
            SenderInitial = initial,
            SenderAvatar = contact?.AvatarImage,
            HasSenderAvatar = contact?.HasAvatar ?? false,
            SenderRole = senderRole,
            RoleBadgeColor = roleBadgeColor ?? "#8b5cf6",
            MsgId = msgId,
            ReplyToPreview = replyToPreview,
            ReplyToAuthor = replyToAuthor,
        };
        // M5: Cap in-memory message display to prevent OOM
        while (Messages.Count >= 1000)
            Messages.RemoveAt(0);
        Messages.Add(msg);
    }

    public void AddSystemMessage(string text)
    {
        // L1: Truncate very long system messages
        if (text.Length > 1000) text = text[..1000] + "...";
        var isAlert = text.Contains("[SECURITY]") || text.Contains("[WARNING]");
        Messages.Add(new ChatMessageViewModel
        {
            Text = text,
            IsSystem = !isAlert,
            IsSecurityAlert = isAlert,
            Timestamp = DateTime.Now,
        });
    }

    // Reply state
    [ObservableProperty] private string? _replyToMsgId;
    [ObservableProperty] private string? _replyToPreview;
    [ObservableProperty] private string? _replyToAuthor;
    [ObservableProperty] private bool _isReplying;

    [RelayCommand]
    private void CancelReply()
    {
        ReplyToMsgId = null;
        ReplyToPreview = null;
        ReplyToAuthor = null;
        IsReplying = false;
    }

    public void SetReplyTarget(ChatMessageViewModel msg)
    {
        CancelEdit(); // Can't reply while editing
        ReplyToMsgId = msg.MsgId;
        ReplyToPreview = msg.Text.Length > 100 ? msg.Text[..100] : msg.Text;
        ReplyToAuthor = msg.From;
        IsReplying = true;
    }

    // Edit state
    [ObservableProperty] private string? _editingMsgId;
    [ObservableProperty] private bool _isEditing;

    public void StartEdit(ChatMessageViewModel msg)
    {
        CancelReply(); // Can't edit while replying
        EditingMsgId = msg.MsgId;
        IsEditing = true;
        InputText = msg.Text; // Load original text into input
    }

    [RelayCommand]
    private void CancelEdit()
    {
        EditingMsgId = null;
        IsEditing = false;
        // Don't clear InputText — user may have typed something
    }

    public event Action<string, string?, string?, string?>? OnMessageSend; // text, replyToMsgId, replyToPreview, replyToAuthor
    public event Action<string, string>? OnMessageEdit; // msgId, newText
    public event Action<string>? OnMessageDelete; // msgId
    public event Action<string[]>? OnAttachFiles; // file paths

    public void RequestDelete(string msgId) => OnMessageDelete?.Invoke(msgId);
    public void RequestAttach(string[] paths) => OnAttachFiles?.Invoke(paths);

    public event Action<string, string, string>? OnPinMessage; // msgId, preview, author
    public void RequestPin(string msgId, string preview, string author) => OnPinMessage?.Invoke(msgId, preview, author);
    public event Action<string, string[]>? OnCommandExecuted;
    public event Action<string>? OnChatHistoryRequested;
    public event Action<string>? OnMemberListRequested;

    public void InviteContactToGroup(string groupId, string userId)
    {
        OnCommandExecuted?.Invoke("ginvite", new[] { groupId, userId });
    }

    public void ExecuteCommand(string cmd, string[] args)
    {
        OnCommandExecuted?.Invoke(cmd, args);
    }
}

public partial class ContactItemViewModel : ViewModelBase
{
    [ObservableProperty] private string _userId = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private bool _hasUnread;
    [ObservableProperty] private string _lastMessage = "";
    [ObservableProperty] private string _lastMessageTime = "";
    [ObservableProperty] private string _accentColor = "#8b5cf6";
    [ObservableProperty] private Bitmap? _avatarImage;
    [ObservableProperty] private bool _hasAvatar;
    [ObservableProperty] private string _status = "offline"; // online, away, dnd, offline
    [ObservableProperty] private string? _customStatus;
    [ObservableProperty] private bool _isMatch = true;

    public string Initial => string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName[..1].ToUpperInvariant();
    public IBrush AccentBrush => ColorHelper.SafeParse(AccentColor);
    public IBrush StatusBrush => Status switch
    {
        "online" => ColorHelper.SafeParse("#22c55e"),
        "away" => ColorHelper.SafeParse("#eab308"),
        "dnd" => ColorHelper.SafeParse("#ef4444"),
        _ => ColorHelper.SafeParse("#6b7280"),
    };
    public string StatusTooltip => Status switch
    {
        "online" => CustomStatus ?? "Online",
        "away" => CustomStatus ?? "Away",
        "dnd" => CustomStatus ?? "Do Not Disturb",
        _ => "Offline",
    };

    partial void OnAccentColorChanged(string value) => OnPropertyChanged(nameof(AccentBrush));
    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(StatusTooltip));
        IsOnline = value != "offline";
    }
    partial void OnCustomStatusChanged(string? value) => OnPropertyChanged(nameof(StatusTooltip));

    public void LoadAvatar(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) { AvatarImage?.Dispose(); AvatarImage = null; HasAvatar = false; return; }
        // M2: Pre-validate base64 length before allocation (256KB decoded ≈ 350KB base64)
        if (base64.Length > 350_000) { AvatarImage?.Dispose(); AvatarImage = null; HasAvatar = false; return; }
        try
        {
            var bytes = Convert.FromBase64String(base64);
            // H2: Reject oversized avatars from network
            if (bytes.Length > ColorHelper.MaxAvatarBytes) { AvatarImage?.Dispose(); AvatarImage = null; HasAvatar = false; return; }
            using var ms = new MemoryStream(bytes);
            var old = AvatarImage;
            AvatarImage = new Bitmap(ms);
            old?.Dispose(); // M9: Dispose old bitmap
            HasAvatar = true;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"LoadAvatar failed: {ex.Message}"); AvatarImage?.Dispose(); AvatarImage = null; HasAvatar = false; }
    }
}

public partial class GroupItemViewModel : ViewModelBase
{
    [ObservableProperty] private string _groupId = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _hasUnread;
    [ObservableProperty] private int _memberCount;
    [ObservableProperty] private bool _isMatch = true;
}

public partial class PlaceItemViewModel : ViewModelBase
{
    [ObservableProperty] private string _placeId = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private ObservableCollection<ChannelItemViewModel> _channels = new();
    [ObservableProperty] private bool _hasUnread;
    [ObservableProperty] private int _memberCount;
    [ObservableProperty] private bool _isCreator;
    [ObservableProperty] private string _accentColor = "#8b5cf6";
    [ObservableProperty] private Bitmap? _iconImage;
    [ObservableProperty] private bool _hasIcon;

    [ObservableProperty] private ObservableCollection<PlaceMemberViewModel> _members = new();
    [ObservableProperty] private bool _isAdmin; // current user is admin or owner
    [ObservableProperty] private string _ownerColor = "#eab308";
    [ObservableProperty] private string _adminColor = "#8b5cf6";
    [ObservableProperty] private string _memberColor = "#6b7280";
    [ObservableProperty] private bool _isMatch = true;

    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();
    public IBrush AccentBrush => ColorHelper.SafeParse(AccentColor);

    partial void OnAccentColorChanged(string value) => OnPropertyChanged(nameof(AccentBrush));

    public void LoadIcon(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) { IconImage?.Dispose(); IconImage = null; HasIcon = false; return; }
        // M2: Pre-validate base64 length before allocation (256KB decoded ≈ 350KB base64)
        if (base64.Length > 350_000) { IconImage?.Dispose(); IconImage = null; HasIcon = false; return; }
        try
        {
            var bytes = Convert.FromBase64String(base64);
            // H2: Reject oversized icons from network
            if (bytes.Length > ColorHelper.MaxIconBytes) { IconImage?.Dispose(); IconImage = null; HasIcon = false; return; }
            using var ms = new MemoryStream(bytes);
            var old = IconImage;
            IconImage = new Bitmap(ms);
            old?.Dispose(); // M9: Dispose old bitmap
            HasIcon = true;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"LoadIcon failed: {ex.Message}"); IconImage?.Dispose(); IconImage = null; HasIcon = false; }
    }
}

public partial class ChannelItemViewModel : ViewModelBase
{
    [ObservableProperty] private string _placeId = "";
    [ObservableProperty] private string _channelId = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _placeName = "";
    [ObservableProperty] private bool _hasUnread;
    [ObservableProperty] private bool _isCreator;
    [ObservableProperty] private string? _category;
    [ObservableProperty] private string _topic = "";
}

public partial class PlaceMemberViewModel : ViewModelBase
{
    [ObservableProperty] private string _userId = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _role = "Member"; // Owner, Admin, Member
    [ObservableProperty] private string _status = "offline";
    [ObservableProperty] private string _accentColor = "#8b5cf6";
    [ObservableProperty] private string _roleColor = "#6b7280"; // customizable per-place

    public string Initial => string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName[..1].ToUpperInvariant();
    public IBrush AccentBrush => ColorHelper.SafeParse(AccentColor);
    public IBrush RoleBrush => ColorHelper.SafeParse(RoleColor, "#6b7280");
    public IBrush StatusBrush => Status switch
    {
        "online" => ColorHelper.SafeParse("#22c55e"),
        "away" => ColorHelper.SafeParse("#eab308"),
        "dnd" => ColorHelper.SafeParse("#ef4444"),
        _ => ColorHelper.SafeParse("#6b7280"),
    };
}

public partial class ChatMessageViewModel : ViewModelBase
{
    [ObservableProperty] private string _from = "";
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isOwn;
    [ObservableProperty] private bool _isSystem;
    [ObservableProperty] private DateTime _timestamp;
    [ObservableProperty] private int _ttl;
    [ObservableProperty] private bool _isSecurityAlert;
    [ObservableProperty] private string _senderAccentColor = "#8b5cf6";
    [ObservableProperty] private Bitmap? _senderAvatar;
    [ObservableProperty] private bool _hasSenderAvatar;
    [ObservableProperty] private string _senderInitial = "?";
    [ObservableProperty] private string? _senderRole; // "Owner", "Admin", or null
    [ObservableProperty] private string _roleBadgeColor = "#8b5cf6"; // customizable per-place

    // Reply support
    [ObservableProperty] private string? _msgId;
    [ObservableProperty] private string? _replyToPreview;
    [ObservableProperty] private string? _replyToAuthor;

    // Edit + Delete
    [ObservableProperty] private bool _isEdited;
    [ObservableProperty] private bool _isDeleted;

    // Attachments
    [ObservableProperty] private ObservableCollection<AttachmentViewModel> _attachments = new();
    [ObservableProperty] private bool _hasAttachments;

    // Reactions
    [ObservableProperty] private ObservableCollection<ReactionViewModel> _reactions = new();
    [ObservableProperty] private bool _hasReactions;

    public void UpdateReactions(Dictionary<string, List<string>>? rxDict, string? ownUserId)
    {
        Reactions.Clear();
        if (rxDict is null || rxDict.Count == 0) { HasReactions = false; return; }
        foreach (var (emoji, users) in rxDict)
        {
            Reactions.Add(new ReactionViewModel
            {
                Emoji = emoji,
                Count = users.Count,
                IsOwn = ownUserId is not null && users.Contains(ownUserId),
            });
        }
        HasReactions = Reactions.Count > 0;
    }

    public string TimeString => Timestamp.ToString("h:mm tt").ToLowerInvariant();
    public bool HasTtl => Ttl > 0;
    public string TtlDisplay => Ttl > 0 ? $"{Ttl}d" : "";
    public IBrush SenderAccentBrush => ColorHelper.SafeParse(SenderAccentColor);
    public bool HasSenderRole => SenderRole is not null;
    public IBrush RoleBadgeBrush => ColorHelper.SafeParse(RoleBadgeColor);
    public bool IsReply => ReplyToPreview is not null;
}

public partial class ReactionViewModel : ViewModelBase
{
    [ObservableProperty] private string _emoji = "";
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isOwn; // current user reacted with this emoji
}

public partial class AttachmentViewModel : ViewModelBase
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _blobId = "";
    [ObservableProperty] private string _sizeDisplay = "";
    [ObservableProperty] private bool _isImage;
    [ObservableProperty] private Bitmap? _preview;
    [ObservableProperty] private bool _hasPreview;
}
