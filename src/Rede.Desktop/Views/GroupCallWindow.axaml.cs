using System;
using System.IO;
using System.Text;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Rede.Core.Services;

namespace Rede.Desktop.Views;

public partial class GroupCallWindow : Window
{
    private GroupCallService? _service;
    private GCallTokenInfo? _token;
    private string? _identity;
    private string? _e2eeKeyBase64;

    public GroupCallWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Configure the window with call data. Call before showing.
    /// </summary>
    public void Configure(GroupCallService service, GCallTokenInfo token, string identity, byte[]? e2eeKey)
    {
        _service = service;
        _token = token;
        _identity = identity;
        _e2eeKeyBase64 = e2eeKey is { Length: 32 } ? Convert.ToBase64String(e2eeKey) : null;
        Title = $"Rede Call · {token.Scope.Kind}";

        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (_token is null) return;

        var htmlPath = ResolveGCallHtml();
        if (htmlPath is null)
        {
            ShowError("Call assets missing", "index.html not found in Assets/gcall/.");
            return;
        }

        // Build file:// URL with query params. WebView control loads the local
        // bundled page; LiveKit JS there will connect to the SFU with the token.
        var q = new StringBuilder();
        q.Append("?url=").Append(Uri.EscapeDataString(_token.Url));
        q.Append("&token=").Append(Uri.EscapeDataString(_token.Token));
        q.Append("&room=").Append(Uri.EscapeDataString(_token.Room));
        q.Append("&identity=").Append(Uri.EscapeDataString(_identity ?? "anon"));
        if (_e2eeKeyBase64 is not null)
            q.Append("&key=").Append(Uri.EscapeDataString(_e2eeKeyBase64));

        var uri = new Uri("file://" + htmlPath + q.ToString());

        var webView = this.FindControl<AvaloniaWebView.WebView>("WebView");
        if (webView is not null)
        {
            webView.Url = uri;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // Tell the server we left the call — regardless of how the window closed.
        _service?.EndCall();
    }

    private static string? ResolveGCallHtml()
    {
        // Bundled next to the executable (CopyToOutputDirectory=PreserveNewest).
        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDir, "Assets", "gcall", "index.html");
        if (File.Exists(candidate)) return candidate;

        // Dev fallback: walk up looking for Rede.Desktop/Assets/gcall
        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "src", "Rede.Desktop", "Assets", "gcall", "index.html");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private void ShowError(string title, string message)
    {
        Title = title + " — " + message;
        var loading = this.FindControl<TextBlock>("LoadingDetail");
        if (loading is not null) loading.Text = message;
    }
}
