using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Rede.Desktop.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty] private ViewModelBase _currentView;
    [ObservableProperty] private string _connectionStatus = "Disconnected";
    [ObservableProperty] private bool _isConnected;

    // Sidebar state
    [ObservableProperty] private bool _isSidebarCollapsed;
    [ObservableProperty] private ObservableCollection<ContactItemViewModel> _contacts = new();
    [ObservableProperty] private ObservableCollection<GroupItemViewModel> _groups = new();
    [ObservableProperty] private object? _selectedConversation;

    // Chat state
    [ObservableProperty] private ObservableCollection<ChatMessageViewModel> _messages = new();
    [ObservableProperty] private string _chatTitle = "";
    [ObservableProperty] private string _inputText = "";
    [ObservableProperty] private int _ttlSeconds;

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
        if (item is ContactItemViewModel contact)
        {
            ChatTitle = contact.DisplayName;
            LoadChatHistory(contact.UserId);
        }
        else if (item is GroupItemViewModel group)
        {
            ChatTitle = $"# {group.Name}";
            LoadChatHistory(group.GroupId);
        }
    }

    [RelayCommand]
    private void SendMessage()
    {
        var text = InputText?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        InputText = "";

        if (text.StartsWith('/'))
        {
            HandleCommand(text);
            return;
        }

        // Add message to UI immediately (optimistic)
        Messages.Add(new ChatMessageViewModel
        {
            Text = text,
            IsOwn = true,
            Timestamp = DateTime.Now,
        });

        // Actual send logic will be wired via service layer
        OnMessageSend?.Invoke(text);
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

    public void AddIncomingMessage(string from, string text, DateTime timestamp, bool isSystem = false)
    {
        Messages.Add(new ChatMessageViewModel
        {
            From = from,
            Text = text,
            IsOwn = false,
            IsSystem = isSystem,
            Timestamp = timestamp,
        });
    }

    public void AddSystemMessage(string text)
    {
        var isAlert = text.Contains("[SECURITY]") || text.Contains("[WARNING]");
        Messages.Add(new ChatMessageViewModel
        {
            Text = text,
            IsSystem = !isAlert,
            IsSecurityAlert = isAlert,
            Timestamp = DateTime.Now,
        });
    }

    public event Action<string>? OnMessageSend;
    public event Action<string, string[]>? OnCommandExecuted;
    public event Action<string>? OnChatHistoryRequested;

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

    public string Initial => string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName[..1].ToUpperInvariant();
}

public partial class GroupItemViewModel : ViewModelBase
{
    [ObservableProperty] private string _groupId = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _hasUnread;
    [ObservableProperty] private int _memberCount;
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

    public string TimeString => Timestamp.ToString("h:mm tt").ToLowerInvariant();
    public bool HasTtl => Ttl > 0;
    public string TtlDisplay => Ttl > 0 ? $"\u23f1 {Ttl}d" : "";
}
