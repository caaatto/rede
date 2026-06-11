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

    // Currently-active place (selected from the top rail). Null = "Home" view, where
    // the sidebar shows contacts + groups. When set, the sidebar fills with that
    // place's channels and contacts/groups disappear entirely until the user clicks
    // the @ button in the top rail.
    [ObservableProperty] private PlaceItemViewModel? _activePlace;
    public bool HasActivePlace => ActivePlace is not null;

    // The sidebar shows either Direct-Messages (contacts + groups) or the active
    // place's channels — never both. Either disappears when the sidebar is collapsed
    // so nothing overflows past the 48px collapsed width.
    public bool ShowDirectMessages => ActivePlace is null && !IsSidebarCollapsed;
    public bool ShowPlaceChannels  => ActivePlace is not null && !IsSidebarCollapsed;

    partial void OnActivePlaceChanged(PlaceItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasActivePlace));
        OnPropertyChanged(nameof(ShowDirectMessages));
        OnPropertyChanged(nameof(ShowPlaceChannels));
    }

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDirectMessages));
        OnPropertyChanged(nameof(ShowPlaceChannels));
    }

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
    // True only when a group (not a DM, not a place channel) is selected — drives
    // the group-call button in the chat header.
    [ObservableProperty] private bool _isGroupSelected;
    [ObservableProperty] private string _channelTopic = "";
    [ObservableProperty] private bool _isMemberListVisible;
    [ObservableProperty] private ObservableCollection<PlaceMemberViewModel> _memberList = new();

    // Image lightbox — opens a full-screen centered preview when an image
    // attachment is clicked. Bitmap is borrowed from the AttachmentViewModel,
    // so we hold a reference until the lightbox closes.
    [ObservableProperty] private bool _isLightboxOpen;
    [ObservableProperty] private Bitmap? _lightboxImage;

    public void OpenLightbox(Bitmap? bmp)
    {
        if (bmp is null) return;
        LightboxImage = bmp;
        IsLightboxOpen = true;
    }

    [RelayCommand]
    private void CloseLightbox()
    {
        IsLightboxOpen = false;
        LightboxImage = null;
    }

    // Quick switcher (Ctrl+K) — fuzzy-style overlay across all conversations.
    [ObservableProperty] private bool _isQuickSwitcherOpen;
    [ObservableProperty] private string _quickSwitcherQuery = "";
    [ObservableProperty] private ObservableCollection<QuickSwitchResultViewModel> _quickSwitcherResults = new();
    [ObservableProperty] private int _quickSwitcherSelectedIndex;

    partial void OnQuickSwitcherQueryChanged(string value) => RebuildQuickSwitcherResults();

    public void OpenQuickSwitcher()
    {
        QuickSwitcherQuery = "";
        QuickSwitcherSelectedIndex = 0;
        RebuildQuickSwitcherResults();
        IsQuickSwitcherOpen = true;
    }

    [RelayCommand]
    private void CloseQuickSwitcher() => IsQuickSwitcherOpen = false;

    public void QuickSwitcherActivate()
    {
        if (!IsQuickSwitcherOpen || QuickSwitcherResults.Count == 0) { IsQuickSwitcherOpen = false; return; }
        var idx = Math.Clamp(QuickSwitcherSelectedIndex, 0, QuickSwitcherResults.Count - 1);
        var pick = QuickSwitcherResults[idx];
        IsQuickSwitcherOpen = false;
        SelectConversationCommand.Execute(pick.Item);
    }

    public void QuickSwitcherMove(int delta)
    {
        if (QuickSwitcherResults.Count == 0) return;
        var n = QuickSwitcherResults.Count;
        QuickSwitcherSelectedIndex = ((QuickSwitcherSelectedIndex + delta) % n + n) % n;
    }

    [RelayCommand]
    private void QuickSwitcherPick(QuickSwitchResultViewModel? result)
    {
        if (result is null) return;
        IsQuickSwitcherOpen = false;
        SelectConversationCommand.Execute(result.Item);
    }

    private void RebuildQuickSwitcherResults()
    {
        var q = (QuickSwitcherQuery ?? "").Trim().ToLowerInvariant();
        QuickSwitcherResults.Clear();

        bool Match(string s) => q.Length == 0 || (s ?? "").ToLowerInvariant().Contains(q);

        foreach (var c in Contacts)
        {
            if (!Match(c.DisplayName)) continue;
            QuickSwitcherResults.Add(new QuickSwitchResultViewModel
            {
                Item = c,
                Title = c.DisplayName,
                Subtitle = "Direct message",
                IconText = string.IsNullOrEmpty(c.DisplayName) ? "?" : c.DisplayName[..1].ToUpperInvariant(),
                AccentColor = c.AccentColor,
            });
        }
        foreach (var g in Groups)
        {
            if (!Match(g.Name)) continue;
            QuickSwitcherResults.Add(new QuickSwitchResultViewModel
            {
                Item = g,
                Title = g.Name,
                Subtitle = "Group",
                IconText = "#",
                AccentColor = "#2dd4bf",
            });
        }
        foreach (var p in Places)
        {
            foreach (var ch in p.Channels)
            {
                if (!Match(ch.Name) && !Match(p.Name)) continue;
                QuickSwitcherResults.Add(new QuickSwitchResultViewModel
                {
                    Item = ch,
                    Title = $"#{ch.Name}",
                    Subtitle = p.Name,
                    IconText = "#",
                    AccentColor = p.AccentColor,
                });
            }
        }

        if (QuickSwitcherSelectedIndex >= QuickSwitcherResults.Count)
            QuickSwitcherSelectedIndex = Math.Max(0, QuickSwitcherResults.Count - 1);
    }

    // In-chat message search (Ctrl+F) — filters currently loaded messages by substring.
    [ObservableProperty] private bool _isMessageSearchVisible;
    [ObservableProperty] private string _messageSearchQuery = "";

    partial void OnMessageSearchQueryChanged(string value) => ApplyMessageSearch();

    public void OpenMessageSearch()
    {
        IsMessageSearchVisible = true;
    }

    [RelayCommand]
    private void CloseMessageSearch()
    {
        MessageSearchQuery = "";
        IsMessageSearchVisible = false;
        ApplyMessageSearch();
    }

    private void ApplyMessageSearch()
    {
        var q = (MessageSearchQuery ?? "").Trim().ToLowerInvariant();
        var hasQuery = q.Length > 0 && IsMessageSearchVisible;
        foreach (var m in Messages)
            m.IsSearchHidden = hasQuery && !((m.Text ?? "").ToLowerInvariant().Contains(q));
    }

    public MainViewModel()
    {
        _currentView = this;
        Messages.CollectionChanged += (_, _) => { RecomputeMessageGrouping(); ApplyMessageSearch(); };
    }

    /// <summary>
    /// Stamp ShowHeader / ShowDateSeparator on every message based on its
    /// neighbor. Cheap (single pass) and runs whenever the collection mutates.
    /// </summary>
    private void RecomputeMessageGrouping()
    {
        const int groupingWindowSec = 300; // 5 minutes
        ChatMessageViewModel? prev = null;
        foreach (var msg in Messages)
        {
            // Date separator: first message ever, or different calendar day.
            var newDay = prev is null || prev.Timestamp.Date != msg.Timestamp.Date;
            msg.ShowDateSeparator = newDay && !msg.IsSystem && !msg.IsSecurityAlert;
            if (msg.ShowDateSeparator)
                msg.DateSeparatorText = FormatDaySeparator(msg.Timestamp);

            // Compact mode: same sender, same day, within window — no header.
            var sameSender =
                prev is not null
                && !msg.IsSystem && !msg.IsSecurityAlert
                && !prev.IsSystem && !prev.IsSecurityAlert
                && prev.IsOwn == msg.IsOwn
                && prev.From == msg.From
                && !newDay
                && (msg.Timestamp - prev.Timestamp).TotalSeconds <= groupingWindowSec;
            msg.ShowHeader = !sameSender;

            prev = msg;
        }
    }

    private static string FormatDaySeparator(DateTime ts)
    {
        var today = DateTime.Now.Date;
        var d = ts.Date;
        if (d == today) return "Today";
        if (d == today.AddDays(-1)) return "Yesterday";
        if ((today - d).TotalDays < 7) return ts.ToString("dddd"); // weekday name
        return ts.ToString("d MMM yyyy");
    }

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
    }

    [RelayCommand]
    private void SelectConversation(object? item)
    {
        // Reset in-chat search whenever the conversation changes — a stale filter
        // from the previous conversation would silently hide messages here.
        if (IsMessageSearchVisible || !string.IsNullOrEmpty(MessageSearchQuery))
        {
            MessageSearchQuery = "";
            IsMessageSearchVisible = false;
        }

        // Clear messages *before* swapping SelectedConversation/ChatTitle, otherwise
        // there's a render frame where the new header is shown above the previous
        // conversation's messages — looks like the wrong chat briefly flashes in.
        Messages.Clear();
        PendingAckVms.Clear();

        SelectedConversation = item;
        SyncSidebarSelection(item);
        IsPlaceSelected = false;
        IsGroupSelected = false;
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
            IsGroupSelected = true;
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

    // Mirror SelectedConversation onto each sidebar item's IsSelected so the
    // active conversation can render with a Discord-style highlight.
    private void SyncSidebarSelection(object? item)
    {
        foreach (var c in Contacts) c.IsSelected = ReferenceEquals(c, item);
        foreach (var g in Groups) g.IsSelected = ReferenceEquals(g, item);
        foreach (var p in Places)
            foreach (var ch in p.Channels)
                ch.IsSelected = ReferenceEquals(ch, item);
    }

    public void DeselectConversation()
    {
        SelectedConversation = null;
        SyncSidebarSelection(null);
        ChatTitle = "";
        IsContactSelected = false;
        IsPlaceSelected = false;
        IsGroupSelected = false;
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

        // Text macros (Discord-style): rewrite into the outgoing message body before
        // handing off to slash-command dispatch. /me is the only one that escapes
        // pure expansion (kept as a slash so the receiver sees the italic action).
        if (TryExpandTextMacro(text, out var expanded))
            text = expanded;

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
        var optimisticVm = new ChatMessageViewModel
        {
            Text = text,
            IsOwn = true,
            Timestamp = DateTime.Now,
            ReplyToPreview = replyPreview,
            ReplyToAuthor = replyAuthor,
        };
        Messages.Add(optimisticVm);
        PendingAckVms.Enqueue(optimisticVm);

        // Actual send logic will be wired via service layer
        OnMessageSend?.Invoke(text, replyMsgId, replyPreview, replyAuthor);
    }

    private void HandleCommand(string text)
    {
        var parts = text[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        OnCommandExecuted?.Invoke(parts[0], parts.Length > 1 ? parts[1..] : Array.Empty<string>());
    }

    /// <summary>
    /// Pre-send rewrite for IRC/Discord-style text macros. Returns false if the
    /// input is unchanged so the caller can use the original string.
    /// </summary>
    private bool TryExpandTextMacro(string text, out string expanded)
    {
        expanded = text;
        if (!text.StartsWith('/')) return false;

        var space = text.IndexOf(' ');
        var head = (space > 0 ? text[..space] : text).ToLowerInvariant();
        var tail = space > 0 ? text[(space + 1)..] : "";

        switch (head)
        {
            case "/shrug":
                expanded = string.IsNullOrEmpty(tail) ? @"¯\_(ツ)_/¯" : tail + @" ¯\_(ツ)_/¯";
                return true;
            case "/tableflip":
                expanded = string.IsNullOrEmpty(tail) ? "(╯°□°）╯︵ ┻━┻" : tail + " (╯°□°）╯︵ ┻━┻";
                return true;
            case "/unflip":
                expanded = string.IsNullOrEmpty(tail) ? "┬─┬ ノ( ゜-゜ノ)" : tail + " ┬─┬ ノ( ゜-゜ノ)";
                return true;
            case "/lenny":
                expanded = string.IsNullOrEmpty(tail) ? "( ͡° ͜ʖ ͡°)" : tail + " ( ͡° ͜ʖ ͡°)";
                return true;
            case "/me":
                // Italicized self-action, prefixed with own display name.
                if (string.IsNullOrWhiteSpace(tail)) return false;
                expanded = $"_* {OwnDisplayName} {tail}_";
                return true;
            default:
                return false;
        }
    }

    private void LoadChatHistory(string chatId)
    {
        Messages.Clear();
        PendingAckVms.Clear();
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

    /// <summary>
    /// Restamp avatar/initial/accent on every already-rendered message from a
    /// given sender. AddIncomingMessage / LoadChatHistoryForConversation snapshot
    /// the contact's profile at message-creation time, so a later profile change
    /// only shows up on the sidebar — already-visible message bubbles keep the
    /// stale avatar (or "?" placeholder) until this is called.
    /// </summary>
    public void RefreshMessagesFromSender(string senderId, string? accentColor,
        string? initial, Bitmap? avatar, bool hasAvatar)
    {
        foreach (var m in Messages)
        {
            if (m.IsOwn || m.IsSystem || m.IsSecurityAlert) continue;
            if (m.From != senderId) continue;
            if (accentColor is not null) m.SenderAccentColor = accentColor;
            if (initial is not null) m.SenderInitial = initial;
            m.SenderAvatar = avatar;
            m.HasSenderAvatar = hasAvatar;
        }
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

    public event Action<string, string, bool>? OnReactionSend; // msgId, emoji, add
    public void RequestReaction(string msgId, string emoji, bool add) => OnReactionSend?.Invoke(msgId, emoji, add);

    public event Action<string, string, string>? OnPinMessage; // msgId, preview, author
    public void RequestPin(string msgId, string preview, string author) => OnPinMessage?.Invoke(msgId, preview, author);

    public event Action<string, string, bool>? OnForwardMessage; // targetId, text, isGroup
    public void RequestForward(string targetId, string text, bool isGroup) => OnForwardMessage?.Invoke(targetId, text, isGroup);

    public event Action<string, string[]>? OnCommandExecuted;
    public event Action<string>? OnChatHistoryRequested;
    public event Action<string>? OnMemberListRequested;

    // FIFO of VMs awaiting their server-assigned MsgId. Mirrors the backend _pendingAck
    // queue so the UI stamps the correct VM on ACK — scanning Messages for "first own
    // null-MsgId" collides with legacy orphan entries loaded from history.
    public readonly Queue<ChatMessageViewModel> PendingAckVms = new();

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
    [ObservableProperty] private bool _isSelected;

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
    [ObservableProperty] private bool _isSelected;
}

public partial class PlaceItemViewModel : ViewModelBase
{
    [ObservableProperty] private string _placeId = "";
    [ObservableProperty] private string _name = "";
    // The underline pill below a place icon is bound to IsExpanded. Default
    // to false so the marker is only drawn on the place the user has actually
    // opened — Home/@ owns the active state until then.
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private ObservableCollection<ChannelItemViewModel> _channels = new();
    // Display rows for the sidebar: interleaved CategoryHeaderViewModel + ChannelItemViewModel.
    // Built by RebuildChannelTree from Channels + CategoryOrder. The flat Channels list stays
    // canonical for navigation/lookup; ChannelTree is purely for rendering.
    [ObservableProperty] private ObservableCollection<ViewModelBase> _channelTree = new();
    // Raw category names in stored order (from Place.Categories). Used as bucket keys against
    // each channel's raw Category, so this must NOT be sanitized (display sanitizing happens
    // on the header VM).
    public List<string> CategoryOrder { get; set; } = new();
    private readonly Dictionary<string, CategoryHeaderViewModel> _headers = new();
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

    // Rebuild the interleaved display list. Uncategorized channels render first (no header,
    // Discord-style), then each category in CategoryOrder as a collapsible header followed by
    // its channels. Header instances are reused across rebuilds so collapse state survives the
    // differential place sync. Channel VM instances are reused (passed in via Channels), so the
    // selection highlight (IsSelected) is preserved.
    public void RebuildChannelTree()
    {
        var byCat = new Dictionary<string, List<ChannelItemViewModel>>();
        var uncategorized = new List<ChannelItemViewModel>();
        foreach (var ch in Channels)
        {
            var cat = ch.Category;
            if (string.IsNullOrEmpty(cat) || !CategoryOrder.Contains(cat))
            {
                uncategorized.Add(ch);
            }
            else
            {
                if (!byCat.TryGetValue(cat, out var list)) { list = new(); byCat[cat] = list; }
                list.Add(ch);
            }
        }

        var rows = new List<ViewModelBase>();
        foreach (var ch in uncategorized) rows.Add(ch);
        foreach (var cat in CategoryOrder)
        {
            if (!_headers.TryGetValue(cat, out var header))
            {
                header = new CategoryHeaderViewModel { PlaceId = PlaceId, Name = cat, Owner = this };
                _headers[cat] = header;
            }
            rows.Add(header);
            if (!header.IsCollapsed && byCat.TryGetValue(cat, out var chans))
                foreach (var ch in chans) rows.Add(ch);
        }
        // Drop headers for categories that no longer exist
        foreach (var stale in _headers.Keys.Where(k => !CategoryOrder.Contains(k)).ToList())
            _headers.Remove(stale);

        ChannelTree.Clear();
        foreach (var row in rows) ChannelTree.Add(row);
    }
}

public partial class CategoryHeaderViewModel : ViewModelBase
{
    [ObservableProperty] private string _placeId = "";
    // Raw category name — the key used for bucketing and passed to SetChannelCategory.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _name = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChevronGlyph))]
    private bool _isCollapsed;

    // Set by PlaceItemViewModel.RebuildChannelTree so the header can ask its place to rebuild
    // when the user toggles collapse.
    public PlaceItemViewModel? Owner { get; set; }

    public string ChevronGlyph => IsCollapsed ? "▸" : "▾"; // ▸ collapsed, ▾ expanded

    // Strip control chars + bidi overrides for display (anti-spoofing). The raw Name stays
    // intact for bucket matching against channel.Category.
    public string DisplayName => Regex.Replace(Name ?? "",
        @"[\x00-\x1f\x7f\u200E\u200F\u202A-\u202E\u2066-\u2069]", "").Trim().ToUpperInvariant();

    partial void OnIsCollapsedChanged(bool value) => Owner?.RebuildChannelTree();
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
    [ObservableProperty] private bool _isSelected;
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
    // ShowTextBubble depends on Text + IsDeleted, so notify on changes.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTextBubble))]
    private string _text = "";
    [ObservableProperty] private bool _isOwn;
    [ObservableProperty] private bool _isSystem;
    [ObservableProperty] private DateTime _timestamp;
    [ObservableProperty] private int _ttl;
    [ObservableProperty] private bool _isSecurityAlert;

    // Discord-style message grouping. When ShowHeader is false the avatar +
    // sender name + timestamp row is hidden and the bubble sits flush with the
    // previous one. ShowDateSeparator inserts a "— Today / Yesterday —" row.
    [ObservableProperty] private bool _showHeader = true;
    [ObservableProperty] private bool _showDateSeparator;
    [ObservableProperty] private string _dateSeparatorText = "";
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTextBubble))]
    private bool _isDeleted;

    // True when the text bubble should render at all. Attachment-only messages
    // (empty Text, not deleted) hide the bubble so the image/file chip stands
    // alone — like Discord/WhatsApp.
    public bool ShowTextBubble => IsDeleted || !string.IsNullOrEmpty(Text);

    // Attachments
    [ObservableProperty] private ObservableCollection<AttachmentViewModel> _attachments = new();
    [ObservableProperty] private bool _hasAttachments;

    // Reactions
    [ObservableProperty] private ObservableCollection<ReactionViewModel> _reactions = new();
    [ObservableProperty] private bool _hasReactions;

    // Hidden by the in-chat Ctrl+F search bar. The ItemTemplate binds Visibility
    // to !IsSearchHidden so non-matching messages collapse without unloading.
    [ObservableProperty] private bool _isSearchHidden;

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
    // True only for incoming peer messages — excludes own, system, and security
    // alerts. The XAML uses this to gate the left-side avatar/header so system
    // notices don't render a stub avatar with a "?" placeholder.
    public bool IsIncoming => !IsOwn && !IsSystem && !IsSecurityAlert;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoadingPlaceholder))]
    [NotifyPropertyChangedFor(nameof(ShowFileChip))]
    private bool _isImage;

    [ObservableProperty] private Bitmap? _preview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoadingPlaceholder))]
    private bool _hasPreview;

    // Set when an image attachment's blob fetch/decode fails — falls back to
    // the plain file chip instead of an endless "loading" placeholder.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoadingPlaceholder))]
    [NotifyPropertyChangedFor(nameof(ShowFileChip))]
    private bool _loadFailed;

    // An image whose preview hasn't arrived yet — show a sized placeholder so the
    // bubble doesn't flash the file chip and then jump when the image swaps in.
    public bool ShowLoadingPlaceholder => IsImage && !HasPreview && !LoadFailed;

    // The plain file chip — non-image attachments, or images that failed to load.
    public bool ShowFileChip => !IsImage || LoadFailed;
}

public partial class QuickSwitchResultViewModel : ViewModelBase
{
    public object Item { get; init; } = null!;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _iconText = "?";
    [ObservableProperty] private string _accentColor = "#8b5cf6";
    public IBrush AccentBrush => ColorHelper.SafeParse(AccentColor);
}
