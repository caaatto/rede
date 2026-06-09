using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using AvaloniaWebView;

namespace Rede.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void RegisterServices()
    {
        base.RegisterServices();
        AvaloniaWebViewBuilder.Initialize(default);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Keep the process alive when the main window is hidden to the tray.
            // The user (tray "Quit" or Ctrl+Q) explicitly triggers Shutdown().
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();

        // Avalonia 11.3.x bug: the Linux DBus tray (DBusTrayIconImpl) converts the XAML-declared
        // TrayIcon.Icon to an ARGB pixmap during Application init — before Skia is ready — which
        // silently yields all-zero bytes, and no NewIcon signal is ever emitted to correct it
        // (so the tray shows a blank/black icon forever). Re-assigning the icon once the framework
        // is loaded (Skia up) forces a fresh conversion + NewIcon emission.
        Dispatcher.UIThread.Post(RefreshTrayIcon, DispatcherPriority.Loaded);
    }

    private void RefreshTrayIcon()
    {
        try
        {
            var icons = TrayIcon.GetIcons(this);
            if (icons is not { Count: > 0 }) return;
            using var stream = AssetLoader.Open(new System.Uri("avares://Rede.Desktop/Assets/icon.png"));
            icons[0].Icon = new WindowIcon(stream);
        }
        catch { }
    }

    // ---- TrayIcon handlers ----

    private MainWindow? GetMainWindow() =>
        ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow as MainWindow
            : null;

    private void TrayIcon_Clicked(object? sender, System.EventArgs e)
    {
        // Left-click on tray icon = toggle main window visibility.
        ShowMainWindow();
    }

    private void TrayShow_Click(object? sender, System.EventArgs e) => ShowMainWindow();

    private void TrayQuit_Click(object? sender, System.EventArgs e)
    {
        if (GetMainWindow() is { } w)
            w.ForceQuit();
        else if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private void ShowMainWindow()
    {
        if (GetMainWindow() is not { } w) return;
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }
}
