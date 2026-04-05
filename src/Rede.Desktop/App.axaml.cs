using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
