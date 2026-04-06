using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Rede.Core.Services;

/// <summary>
/// Cross-platform desktop notification service.
/// Linux: notify-send (libnotify), Windows: PowerShell toast, macOS: osascript.
/// Privacy-first: default mode shows no sender/content in notifications.
/// </summary>
public class NotificationService
{
    private bool _enabled = true;
    private bool _showContent; // false = privacy mode (default)
    private bool _soundEnabled = true;
    private string _ownStatus = "online";
    private string? _soundPath;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>
    /// When true, notifications show sender name and message preview.
    /// When false (default), notifications only say "New message" — no metadata leaked to OS.
    /// </summary>
    public bool ShowContent
    {
        get => _showContent;
        set => _showContent = value;
    }

    /// <summary>When true (default), play a notification sound on incoming messages.</summary>
    public bool SoundEnabled
    {
        get => _soundEnabled;
        set => _soundEnabled = value;
    }

    /// <summary>
    /// Set the user's current status. Notifications are suppressed when DND.
    /// </summary>
    public string OwnStatus
    {
        get => _ownStatus;
        set => _ownStatus = value ?? "online";
    }

    /// <summary>
    /// Set the path to the notification sound file (WAV).
    /// Call once at startup with the resolved Assets path.
    /// </summary>
    public void SetSoundPath(string path)
    {
        _soundPath = File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Show a desktop notification for an incoming message.
    /// </summary>
    public void ShowMessageNotification(string senderName, string messageText)
    {
        if (!_enabled) return;
        if (_ownStatus == "dnd") return;
        // Don't notify for control messages
        if (messageText.Contains("\"__rede_ctrl\"")) return;

        PlaySound();
        if (_showContent)
        {
            var preview = messageText.Length > 200 ? messageText[..200] + "..." : messageText;
            Show(senderName, preview);
        }
        else
        {
            Show("REDE", "New message");
        }
    }

    /// <summary>
    /// Show a notification for group/channel messages.
    /// </summary>
    public void ShowGroupNotification(string groupName, string senderName, string messageText)
    {
        if (!_enabled) return;
        if (_ownStatus == "dnd") return;
        if (messageText.Contains("\"__rede_ctrl\"")) return;

        PlaySound();
        if (_showContent)
        {
            var preview = messageText.Length > 200 ? messageText[..200] + "..." : messageText;
            Show($"{groupName} — {senderName}", preview);
        }
        else
        {
            Show("REDE", "New message");
        }
    }

    private static void Show(string title, string body)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                ShowLinuxNotification(title, body);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                ShowWindowsNotification(title, body);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                ShowMacNotification(title, body);
        }
        catch
        {
            // Notification failure should never crash the app
        }
    }

    private static void ShowLinuxNotification(string title, string body)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "notify-send",
            ArgumentList = { "--app-name=REDE", "--urgency=normal", "--expire-time=5000",
                             SanitizeMarkup(title), SanitizeMarkup(body) },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(3000);
    }

    private static void ShowWindowsNotification(string title, string body)
    {
        var script = $@"
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType = WindowsRuntime] | Out-Null
$xml = [Windows.Data.Xml.Dom.XmlDocument]::new()
$xml.LoadXml('<toast><visual><binding template=""ToastGeneric""><text>{EscapeXml(title)}</text><text>{EscapeXml(body)}</text></binding></visual></toast>')
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('REDE').Show([Windows.UI.Notifications.ToastNotification]::new($xml))";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", script },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(5000);
    }

    private static void ShowMacNotification(string title, string body)
    {
        var script = $"display notification \"{EscapeAppleScript(body)}\" with title \"{EscapeAppleScript(title)}\"";
        var psi = new ProcessStartInfo
        {
            FileName = "osascript",
            ArgumentList = { "-e", script },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(3000);
    }

    private static string SanitizeMarkup(string text)
    {
        // Strip potential markup injection for notify-send (Pango markup)
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    private static string EscapeXml(string text)
    {
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                   .Replace("\"", "&quot;").Replace("'", "&apos;");
    }

    private static string EscapeAppleScript(string text)
    {
        // H1: Strip control chars and newlines to prevent AppleScript injection
        var s = System.Text.RegularExpressions.Regex.Replace(text, @"[\x00-\x1f\x7f]", " ");
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private void PlaySound()
    {
        if (!_soundEnabled || _soundPath is null) return;
        try
        {
            ProcessStartInfo psi;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Try paplay (PulseAudio/PipeWire) first, fall back to aplay (ALSA)
                psi = new ProcessStartInfo
                {
                    FileName = "paplay",
                    ArgumentList = { _soundPath },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    ArgumentList = { "-NoProfile", "-NonInteractive", "-Command",
                        $"(New-Object System.Media.SoundPlayer '{_soundPath.Replace("'", "''")}').PlaySync()" },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                psi = new ProcessStartInfo
                {
                    FileName = "afplay",
                    ArgumentList = { _soundPath },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
            }
            else return;

            // Fire-and-forget — don't block the message handler
            Task.Run(() =>
            {
                try
                {
                    using var p = Process.Start(psi);
                    p?.WaitForExit(5000);
                }
                catch { }
            });
        }
        catch { }
    }
}
