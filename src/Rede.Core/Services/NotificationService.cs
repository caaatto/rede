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
    private string? _ringtonePath;
    private readonly object _ringLock = new();
    private CancellationTokenSource? _ringCts;
    private Process? _ringProcess;

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

    /// <summary>Path to the looping ringtone played while an incoming call is ringing.</summary>
    public void SetRingtonePath(string path)
    {
        _ringtonePath = File.Exists(path) ? path : null;
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
            Show($"{groupName} - {senderName}", preview);
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

    // Ordered list of candidate CLI players for a WAV path. The first one that
    // actually launches wins; if it isn't installed Process.Start throws and we
    // fall through to the next. Previously Linux only tried `paplay` with no
    // fallback — on a system without PulseAudio/PipeWire utils the sound silently
    // failed (the installer ships the ALSA *library* for the call engine, not a
    // command-line player).
    private static (string File, string[] Args)[]? BuildPlayerCandidates(string soundPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new[]
            {
                ("pw-play", new[] { soundPath }),                  // PipeWire
                ("paplay",  new[] { soundPath }),                  // PulseAudio
                ("aplay",   new[] { "-q", soundPath }),            // ALSA (alsa-utils)
                ("ffplay",  new[] { "-nodisp", "-autoexit", "-loglevel", "quiet", soundPath }),
                ("play",    new[] { "-q", soundPath }),            // SoX
            };
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new[]
            {
                ("powershell", new[] { "-NoProfile", "-NonInteractive", "-Command",
                    $"(New-Object System.Media.SoundPlayer '{soundPath.Replace("'", "''")}').PlaySync()" }),
            };
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new[] { ("afplay", new[] { soundPath }) };
        }
        return null;
    }

    /// <summary>
    /// Play one WAV synchronously through the first working backend. Returns true
    /// if a player ran to a clean exit (0). When a process is started, onStarted
    /// is invoked with it so a caller can kill it (used to stop the ringtone mid-play).
    /// </summary>
    private static bool PlayOnceBlocking(string soundPath, Action<Process?>? onStarted = null)
    {
        var candidates = BuildPlayerCandidates(soundPath);
        if (candidates is null) return false;
        foreach (var (file, args) in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = file,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var a in args) psi.ArgumentList.Add(a);

                using var p = Process.Start(psi);
                if (p is null) continue;
                onStarted?.Invoke(p);
                p.WaitForExit();
                // Exit 0 = played. Non-zero (e.g. aplay with no device) → try next.
                if (p.ExitCode == 0) return true;
            }
            catch { /* player not installed — try the next candidate */ }
        }
        return false;
    }

    private void PlaySound()
    {
        if (!_soundEnabled || _soundPath is null) return;
        var soundPath = _soundPath;
        // Fire-and-forget — don't block the message handler.
        Task.Run(() => PlayOnceBlocking(soundPath));
    }

    /// <summary>
    /// Start looping the ringtone (incoming call). Idempotent — a second call
    /// while already ringing is a no-op. Gated behind SoundEnabled so muting
    /// notification sound also silences the ring. Stop with <see cref="StopRingtone"/>.
    /// </summary>
    public void StartRingtone()
    {
        if (!_soundEnabled || _ringtonePath is null) return;
        var ringPath = _ringtonePath;
        CancellationTokenSource cts;
        CancellationToken token;
        lock (_ringLock)
        {
            if (_ringCts is not null) return; // already ringing
            cts = new CancellationTokenSource();
            _ringCts = cts;
            token = cts.Token;
        }

        Task.Run(() =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    // Kill the current ring playback immediately when StopRingtone cancels.
                    using var reg = token.Register(() =>
                    {
                        lock (_ringLock)
                        {
                            try { _ringProcess?.Kill(entireProcessTree: true); } catch { }
                        }
                    });
                    var played = PlayOnceBlocking(ringPath, p =>
                    {
                        lock (_ringLock) _ringProcess = p;
                    });
                    lock (_ringLock) _ringProcess = null;
                    // No working player on this system — don't busy-loop forever.
                    if (!played && !token.IsCancellationRequested) break;
                    // Gap between rings so the loop sounds like a phone cadence even
                    // if the ringtone file has no built-in trailing silence.
                    try { token.WaitHandle.WaitOne(1500); } catch { }
                }
            }
            catch { }
            finally
            {
                // Clear our own CTS so a later call can ring again — but only if
                // StopRingtone hasn't already swapped in a fresh one.
                lock (_ringLock)
                {
                    if (ReferenceEquals(_ringCts, cts)) _ringCts = null;
                }
                cts.Dispose();
            }
        }, token);
    }

    /// <summary>Stop the ringtone loop and kill any in-flight playback.</summary>
    public void StopRingtone()
    {
        CancellationTokenSource? cts;
        lock (_ringLock)
        {
            cts = _ringCts;
            _ringCts = null;
            try { _ringProcess?.Kill(entireProcessTree: true); } catch { }
            _ringProcess = null;
        }
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
    }
}
