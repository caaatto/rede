using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using AvaloniaWebView;
using Rede.Core.Services;

namespace Rede.Desktop.Views;

public partial class GroupCallWindow : Window
{
    private GroupCallService? _service;
    private GCallTokenInfo? _token;
    private string? _displayName;
    private string? _e2eeKeyBase64;
    private bool _initPosted;

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
    /// The SFrame key is NEVER passed via the URL — it is injected into the
    /// page via ExecuteScriptAsync after navigation completes, so it never
    /// ends up in process arguments, browser history, or referer headers.
    /// </summary>
    public void Configure(GroupCallService service, GCallTokenInfo token, string displayName, byte[]? e2eeKey)
    {
        _service = service;
        _token = token;
        _displayName = displayName;
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

        // Load the bundled HTML without any secrets in the URL. The page boots
        // into a "waiting for __redeInit" state and connects only once the
        // host injects the config via ExecuteScriptAsync.
        var uri = new Uri("file://" + htmlPath);

        var webView = this.FindControl<WebView>("WebView");
        if (webView is null) return;

        webView.NavigationCompleted += OnNavigationCompleted;
        webView.Url = uri;
    }

    private async void OnNavigationCompleted(object? sender, WebViewCore.Events.WebViewUrlLoadedEventArg e)
    {
        if (sender is not WebView webView) return;
        if (_token is null) return;
        if (_initPosted) return; // only inject once; later subframe loads must not leak
        _initPosted = true;

        // Build the config as a JSON object and serialize it so quoting/escaping
        // is handled correctly. This is the ONLY place the raw E2EE key leaves
        // managed memory, and it only ever lives in the JS heap of the
        // bundled local page — no URL, no process args, no logs.
        var cfg = new JsonObject
        {
            ["url"] = _token.Url,
            ["token"] = _token.Token,
            ["room"] = _token.Room,
            ["identity"] = _token.Identity, // per-room pseudonym from server
            ["displayName"] = _displayName ?? "",
            ["e2eeKey"] = _e2eeKeyBase64 ?? "",
        };
        var json = cfg.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        // Wrap in a self-zeroing call so the config object is not left reachable
        // on window.* after __redeInit has consumed it.
        var script = "(function(){try{var c=" + json + ";window.__redeInit&&window.__redeInit(c);c.e2eeKey='';c.token='';}catch(e){console.error(e);}})();";

        try
        {
            await webView.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            ShowError("WebView init failed", ex.Message);
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
