using Avalonia;
using Avalonia.WebView.Desktop;
using Rede.Core.Services;
using System;
using System.IO;

namespace Rede.Desktop;

class Program
{
    /// <summary>True if the app was launched with --minimized (e.g. from OS autostart).</summary>
    public static bool StartMinimized { get; private set; }

    private static FileStream? _lockFile;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Crash capture. The call/audio path runs native code (PortAudio,
        // RNNoise) whose faults can't be caught by managed try/catch, and a
        // truly-unhandled managed exception on any thread tears the process
        // down with no window left to show it. Both modes previously left the
        // user with a silently-vanished app. Persist whatever we can to
        // ~/.rede/crash.log so the next occurrence is diagnosable.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog("UnhandledException", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        // Info flags handled before single-instance lock so they still work when
        // a REDE GUI is already running.
        if (Array.Exists(args, IsVersionFlag))
        {
            Console.WriteLine($"REDE v{UpdateService.Version}");
            return;
        }
        if (Array.Exists(args, IsHelpFlag))
        {
            PrintHelp();
            return;
        }

        // Single-instance enforcement: only one REDE process at a time.
        // Use a file lock in ~/.rede/ (cross-platform, works with self-contained binaries).
        if (!AcquireSingleInstanceLock())
        {
            Console.Error.WriteLine("REDE is already running.");
            Environment.Exit(1);
            return;
        }

        StartMinimized = Array.Exists(args, a =>
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-m", StringComparison.OrdinalIgnoreCase));

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            ReleaseSingleInstanceLock();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseDesktopWebView();

    private static void WriteCrashLog(string kind, Exception? ex)
    {
        try
        {
            var redeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rede");
            Directory.CreateDirectory(redeDir);
            var line = $"=== {DateTime.UtcNow:o} {kind} (REDE v{UpdateService.Version}) ===\n{ex}\n\n";
            File.AppendAllText(Path.Combine(redeDir, "crash.log"), line);
            Console.Error.WriteLine(line);
        }
        catch { /* last-ditch handler — never throw from here */ }
    }

    private static bool AcquireSingleInstanceLock()
    {
        try
        {
            var redeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rede");
            Directory.CreateDirectory(redeDir);
            var lockPath = Path.Combine(redeDir, ".lock");
            _lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            // Another instance holds the lock
            return false;
        }
        catch
        {
            // If we can't create the lock (permissions, etc.), allow startup
            return true;
        }
    }

    private static void ReleaseSingleInstanceLock()
    {
        try
        {
            _lockFile?.Dispose();
            _lockFile = null;
        }
        catch { }
    }

    private static bool IsVersionFlag(string a) =>
        a.Equals("--version", StringComparison.OrdinalIgnoreCase) ||
        a.Equals("--v", StringComparison.OrdinalIgnoreCase) ||
        a.Equals("-v", StringComparison.OrdinalIgnoreCase) ||
        a.Equals("-V", StringComparison.Ordinal);

    private static bool IsHelpFlag(string a) =>
        a.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
        a.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
        a.Equals("/?", StringComparison.Ordinal);

    private static void PrintHelp()
    {
        Console.WriteLine($"REDE v{UpdateService.Version}");
        Console.WriteLine("Secure end-to-end encrypted messenger.");
        Console.WriteLine();
        Console.WriteLine("Usage: REDE [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --version, -v       Print version and exit");
        Console.WriteLine("  --help, -h          Show this help");
        Console.WriteLine("  --minimized, -m     Start hidden in the system tray");
    }
}
