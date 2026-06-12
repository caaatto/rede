using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Rede.Desktop.Controls;

/// <summary>
/// In-app notification toast: a small topmost, non-activating window pinned to
/// the bottom-right of the primary screen's working area. Used on Windows, where
/// WinRT toasts from an unpackaged exe (no Start-menu shortcut carrying the
/// AppUserModelID) are silently dropped by the OS — the app renders its own
/// toast instead. A single window is reused: a notification arriving while one
/// is visible replaces the text and restarts the dismiss timer. Clicking the
/// toast restores + activates the main window.
/// </summary>
public class ToastWindow : Window
{
    private const int DismissAfterSeconds = 6;
    private const int ScreenMarginPx = 16;

    private static ToastWindow? _current;

    private readonly DispatcherTimer _timer;
    private readonly TextBlock _titleText;
    private readonly TextBlock _bodyText;
    private readonly Window _mainWindow;

    public static void ShowToast(Window mainWindow, string title, string body)
    {
        if (_current is { } visible)
        {
            visible._titleText.Text = title;
            visible._bodyText.Text = body;
            visible._timer.Stop();
            visible._timer.Start();
            return;
        }

        var toast = new ToastWindow(mainWindow, title, body);
        _current = toast;
        toast.Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, toast)) _current = null;
        };
        toast.Show();
    }

    private ToastWindow(Window mainWindow, string title, string body)
    {
        _mainWindow = mainWindow;

        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;   // never steal focus from whatever the user is doing
        Topmost = true;
        CanResize = false;
        SizeToContent = SizeToContent.Height;
        Width = 360;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        _titleText = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#e0e0e8")),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _bodyText = new TextBlock
        {
            Text = body,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#9a9aa8")),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(_titleText);
        content.Children.Add(_bodyText);

        // Accent bar left + dark card, matching the app's dark cinematic theme.
        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#16161f")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2a2a3a")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(0),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Children =
                {
                    new Border
                    {
                        Width = 3,
                        Background = new SolidColorBrush(Color.Parse("#8b5cf6")),
                        CornerRadius = new CornerRadius(10, 0, 0, 10),
                        [Grid.ColumnProperty] = 0,
                    },
                    new Border
                    {
                        Padding = new Thickness(14, 12),
                        Child = content,
                        [Grid.ColumnProperty] = 1,
                    },
                },
            },
        };
        Content = card;
        Cursor = new Cursor(StandardCursorType.Hand);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(DismissAfterSeconds) };
        _timer.Tick += (_, _) => Close();
        _timer.Start();

        PointerPressed += OnToastClicked;
        Opened += (_, _) => PositionBottomRight();
    }

    private void PositionBottomRight()
    {
        var screen = Screens.Primary ?? (Screens.ScreenCount > 0 ? Screens.All[0] : null);
        if (screen is null) return;
        var wa = screen.WorkingArea;
        var widthPx = (int)Math.Ceiling(Bounds.Width * screen.Scaling);
        var heightPx = (int)Math.Ceiling(Bounds.Height * screen.Scaling);
        Position = new PixelPoint(
            wa.X + wa.Width - widthPx - ScreenMarginPx,
            wa.Y + wa.Height - heightPx - ScreenMarginPx);
    }

    private void OnToastClicked(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            // Window may be hidden in the tray — Show before activating.
            _mainWindow.Show();
            if (_mainWindow.WindowState == WindowState.Minimized)
                _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }
        catch { }
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
