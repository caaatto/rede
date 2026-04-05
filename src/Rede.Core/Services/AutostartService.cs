using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Rede.Core.Services;

/// <summary>
/// Cross-platform OS autostart integration.
/// Linux: writes/removes ~/.config/autostart/rede.desktop
/// Windows: sets/removes HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Rede
/// </summary>
public static class AutostartService
{
    private const string AppName = "Rede";

    /// <summary>Returns the absolute path of the currently running Rede executable.</summary>
    public static string? GetExecutablePath()
    {
        var path = Environment.ProcessPath;
        return string.IsNullOrEmpty(path) ? null : path;
    }

    public static bool IsSupported =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static bool IsEnabled()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return File.Exists(LinuxDesktopFilePath());

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return WindowsGetRunValue() is not null;
        }
        catch { }
        return false;
    }

    /// <summary>Enables autostart. <paramref name="startMinimized"/> adds --minimized to the launch args.</summary>
    public static bool Enable(bool startMinimized)
    {
        var exe = GetExecutablePath();
        if (string.IsNullOrEmpty(exe)) return false;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var dir = Path.GetDirectoryName(LinuxDesktopFilePath());
                if (dir is not null) Directory.CreateDirectory(dir);

                var execLine = startMinimized ? $"{EscapeDesktopExec(exe)} --minimized" : EscapeDesktopExec(exe);
                var contents =
                    "[Desktop Entry]\n" +
                    "Type=Application\n" +
                    "Name=Rede\n" +
                    "Comment=Secure anonymous messenger\n" +
                    $"Exec={execLine}\n" +
                    "Terminal=false\n" +
                    "Categories=Network;InstantMessaging;\n" +
                    "X-GNOME-Autostart-enabled=true\n";

                File.WriteAllText(LinuxDesktopFilePath(), contents);
                return true;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var value = startMinimized ? $"\"{exe}\" --minimized" : $"\"{exe}\"";
                return WindowsSetRunValue(value);
            }
        }
        catch { }
        return false;
    }

    public static bool Disable()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var path = LinuxDesktopFilePath();
                if (File.Exists(path)) File.Delete(path);
                return true;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return WindowsDeleteRunValue();
        }
        catch { }
        return false;
    }

    // ---- Linux ----

    private static string LinuxDesktopFilePath()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var baseDir = !string.IsNullOrEmpty(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(baseDir, "autostart", "rede.desktop");
    }

    private static string EscapeDesktopExec(string path)
    {
        // Quote if path contains spaces — .desktop Exec uses double quotes with backslash escaping.
        if (path.IndexOf(' ') < 0 && path.IndexOf('"') < 0) return path;
        var escaped = path.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    // ---- Windows ----
    // Registry access via reflection-free direct P/Invoke to Microsoft.Win32.Registry would
    // require a package reference on non-Windows. Instead, we use Microsoft.Win32.Registry which
    // is part of the Windows-targeted BCL and only touched inside IsOSPlatform(Windows) guards.

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? WindowsGetRunValue()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
        return key?.GetValue(AppName) as string;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool WindowsSetRunValue(string value)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (key is null) return false;
        key.SetValue(AppName, value, Microsoft.Win32.RegistryValueKind.String);
        return true;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool WindowsDeleteRunValue()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (key is null) return true;
        if (key.GetValue(AppName) is not null)
            key.DeleteValue(AppName, throwOnMissingValue: false);
        return true;
    }
}
