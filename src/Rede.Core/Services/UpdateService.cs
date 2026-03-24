using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Rede.Core.Services;

public class UpdateService
{
    private readonly string _repoPath;
    private readonly string _branch;

    private const string GitHubRepo = "caaatto/rede";
    private const string CurrentVersion = "2.0.1-beta";

    public event Action<string>? OnStatusUpdate;
    public event Action<string>? OnError;

    public UpdateService(string repoPath, string branch = "v2")
    {
        _repoPath = repoPath;
        _branch = branch;
    }

    /// <summary>
    /// Check if there are remote updates available.
    /// Returns (hasUpdates, localCommit, remoteCommit).
    /// </summary>
    public async Task<(bool HasUpdates, string LocalCommit, string RemoteCommit)> CheckForUpdatesAsync()
    {
        try
        {
            OnStatusUpdate?.Invoke("Checking for updates...");

            var fetchResult = await RunGitAsync("fetch", $"origin {_branch}");
            if (!fetchResult.Success)
            {
                OnError?.Invoke($"Failed to fetch: {fetchResult.Error}");
                return (false, "", "");
            }

            var localResult = await RunGitAsync("rev-parse", "HEAD");
            var remoteResult = await RunGitAsync("rev-parse", $"origin/{_branch}");

            if (!localResult.Success || !remoteResult.Success)
            {
                OnError?.Invoke("Failed to check commit hashes");
                return (false, "", "");
            }

            var local = localResult.Output.Trim();
            var remote = remoteResult.Output.Trim();
            var hasUpdates = local != remote;

            if (hasUpdates)
                OnStatusUpdate?.Invoke($"Update available: {remote[..8]}");
            else
                OnStatusUpdate?.Invoke("Up to date");

            return (hasUpdates, local, remote);
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Update check failed: {ex.Message}");
            return (false, "", "");
        }
    }

    /// <summary>
    /// Pull latest changes and rebuild. Returns true if successful.
    /// </summary>
    public async Task<bool> PullAndBuildAsync()
    {
        try
        {
            OnStatusUpdate?.Invoke("Pulling updates...");

            var pullResult = await RunGitAsync("pull", $"origin {_branch}");
            if (!pullResult.Success)
            {
                OnError?.Invoke($"Pull failed: {pullResult.Error}");
                return false;
            }

            OnStatusUpdate?.Invoke("Building...");

            var buildResult = await RunProcessAsync("dotnet", "build Rede.sln -c Release --nologo -v q");
            if (!buildResult.Success)
            {
                OnError?.Invoke($"Build failed: {buildResult.Error}");
                return false;
            }

            OnStatusUpdate?.Invoke("Update complete. Restart to apply.");
            return true;
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Update failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Get a summary of what changed since the given commit.
    /// </summary>
    public async Task<string?> GetChangelogAsync(string sinceCommit)
    {
        var result = await RunGitAsync("log", $"--oneline {sinceCommit}..origin/{_branch}");
        return result.Success ? result.Output.Trim() : null;
    }

    /// <summary>
    /// Detect the git repository root from the running executable's location.
    /// </summary>
    public static string? DetectRepoPath()
    {
        // Walk up from executable location looking for .git
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        // Also try the working directory
        dir = Environment.CurrentDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        return null;
    }

    // === GitHub Release-based updates (for standalone .exe downloads) ===

    /// <summary>
    /// Check GitHub Releases API for a newer version.
    /// Works without git — for standalone exe installations.
    /// </summary>
    public static async Task<ReleaseInfo?> CheckGitHubReleaseAsync()
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "Rede-Desktop");
            http.Timeout = TimeSpan.FromSeconds(10);

            var url = $"https://api.github.com/repos/{GitHubRepo}/releases";
            var json = await http.GetStringAsync(url);
            var releases = JsonDocument.Parse(json);

            foreach (var release in releases.RootElement.EnumerateArray())
            {
                var tag = release.GetProperty("tag_name").GetString();
                if (tag is null) continue;

                // Only consider beta releases on v2
                if (!tag.Contains("beta")) continue;
                if (tag == CurrentVersion || tag == $"v{CurrentVersion}") continue;

                // Newer if tag version is higher
                var remoteVer = ParseVersion(tag);
                var localVer = ParseVersion(CurrentVersion);
                if (remoteVer <= localVer) continue;

                // Find the right asset for this platform
                string assetName;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    assetName = "Rede-Desktop-win-x64.exe";
                else
                    assetName = "Rede-Desktop-linux-x64";

                string? downloadUrl = null;
                foreach (var asset in release.GetProperty("assets").EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString();
                    if (name == assetName)
                    {
                        downloadUrl = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }

                return new ReleaseInfo
                {
                    Tag = tag,
                    DownloadUrl = downloadUrl,
                    AssetName = assetName,
                };
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Download the new release and replace the current executable.
    /// </summary>
    public static async Task<bool> DownloadAndReplaceAsync(ReleaseInfo release, Action<string>? onStatus = null)
    {
        if (release.DownloadUrl is null) return false;

        try
        {
            onStatus?.Invoke("Downloading update...");

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "Rede-Desktop");
            http.Timeout = TimeSpan.FromMinutes(5);

            var bytes = await http.GetByteArrayAsync(release.DownloadUrl);

            var currentExe = Environment.ProcessPath;
            if (currentExe is null) return false;

            var backupPath = currentExe + ".old";
            var newPath = currentExe + ".new";

            // Write new binary
            await File.WriteAllBytesAsync(newPath, bytes);

            // On Linux, set executable permission
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.SetUnixFileMode(newPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            // Swap: current -> .old, new -> current
            if (File.Exists(backupPath)) File.Delete(backupPath);
            File.Move(currentExe, backupPath);
            File.Move(newPath, currentExe);

            onStatus?.Invoke($"Updated to {release.Tag}. Restart to apply.");
            return true;
        }
        catch (Exception ex)
        {
            onStatus?.Invoke($"Update failed: {ex.Message}");
            return false;
        }
    }

    private static int ParseVersion(string tag)
    {
        // "v2.0.1-beta" or "2.0.1-beta" -> 201
        var clean = tag.TrimStart('v').Split('-')[0];
        var parts = clean.Split('.');
        var major = parts.Length > 0 && int.TryParse(parts[0], out var a) ? a : 0;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var b) ? b : 0;
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var c) ? c : 0;
        return major * 10000 + minor * 100 + patch;
    }

    public class ReleaseInfo
    {
        public string Tag { get; set; } = "";
        public string? DownloadUrl { get; set; }
        public string AssetName { get; set; } = "";
    }

    private async Task<(bool Success, string Output, string Error)> RunGitAsync(string command, string args)
    {
        return await RunProcessAsync("git", $"{command} {args}");
    }

    private async Task<(bool Success, string Output, string Error)> RunProcessAsync(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = _repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            return (process.ExitCode == 0, output, error);
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }
}
