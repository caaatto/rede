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
    private const string CurrentVersion = "2.10.5-beta";

    public event Action<string>? OnStatusUpdate;
    public event Action<string>? OnError;

    public UpdateService(string repoPath, string branch = "main")
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
        // Walk up from executable location looking for .git (max 10 levels)
        var dir = AppContext.BaseDirectory;
        for (int i = 0; dir is not null && i < 10; i++)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        // Also try the working directory
        dir = Environment.CurrentDirectory;
        for (int i = 0; dir is not null && i < 10; i++)
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
                    assetName = "REDE.exe";
                else
                    assetName = "REDE";

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
            onStatus?.Invoke("Updating...");

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "Rede-Desktop");
            http.Timeout = TimeSpan.FromMinutes(5);

            // Download with progress tracking
            using var response = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            using var stream = await response.Content.ReadAsStreamAsync();
            using var ms = new MemoryStream();

            var buffer = new byte[81920];
            long downloaded = 0;
            var startTime = DateTime.UtcNow;
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                ms.Write(buffer, 0, bytesRead);
                downloaded += bytesRead;

                var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                var speed = elapsed > 0 ? downloaded / elapsed : 0;
                var speedStr = speed >= 1048576 ? $"{speed / 1048576:F1} MB/s"
                             : speed >= 1024 ? $"{speed / 1024:F0} KB/s"
                             : $"{speed:F0} B/s";

                if (totalBytes > 0)
                {
                    var pct = (int)(downloaded * 100 / totalBytes);
                    onStatus?.Invoke($"Downloading... {pct}% ({speedStr})");
                }
                else
                {
                    var dlStr = downloaded >= 1048576 ? $"{downloaded / 1048576.0:F1} MB" : $"{downloaded / 1024.0:F0} KB";
                    onStatus?.Invoke($"Downloading... {dlStr} ({speedStr})");
                }
            }

            var bytes = ms.ToArray();

            // C2: Validate downloaded binary — magic bytes + SHA256 hash if available
            if (!IsValidExecutable(bytes))
            {
                onStatus?.Invoke("Update failed: invalid binary.");
                return false;
            }

            // C2: Try to verify SHA256 hash from release checksums file
            var hashVerified = await VerifyReleaseHashAsync(release, bytes);
            if (hashVerified == false)
            {
                onStatus?.Invoke("Update failed: SHA256 hash mismatch! Download may be compromised.");
                return false;
            }
            // hashVerified == null means no checksum file available — proceed with warning

            onStatus?.Invoke("Installing...");

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

    /// <summary>
    /// C2: Verify SHA256 hash of downloaded binary against checksums file from the release.
    /// Returns true if verified, false if mismatch, null if no checksum file available.
    /// </summary>
    private static async Task<bool?> VerifyReleaseHashAsync(ReleaseInfo release, byte[] bytes)
    {
        try
        {
            // Look for SHA256SUMS asset in the same release
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "Rede-Desktop");
            http.Timeout = TimeSpan.FromSeconds(10);

            var url = $"https://api.github.com/repos/{GitHubRepo}/releases/tags/{release.Tag}";
            var json = await http.GetStringAsync(url);
            var releaseDoc = JsonDocument.Parse(json);

            string? checksumsUrl = null;
            foreach (var asset in releaseDoc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                if (name is "SHA256SUMS" or "checksums.txt")
                {
                    checksumsUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }

            if (checksumsUrl is null) return null; // No checksums file

            var checksums = await http.GetStringAsync(checksumsUrl);
            var actualHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

            foreach (var line in checksums.Split('\n'))
            {
                var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[1].Trim('*') == release.AssetName)
                {
                    return string.Equals(parts[0], actualHash, StringComparison.OrdinalIgnoreCase);
                }
            }

            return null; // Asset not found in checksums file
        }
        catch
        {
            return null; // Can't verify — proceed with caution
        }
    }

    private static bool IsValidExecutable(byte[] bytes)
    {
        if (bytes.Length < 4) return false;
        // ELF magic: 0x7F 'E' 'L' 'F'
        if (bytes[0] == 0x7F && bytes[1] == 0x45 && bytes[2] == 0x4C && bytes[3] == 0x46)
            return true;
        // PE (Windows) magic: 'M' 'Z'
        if (bytes[0] == 0x4D && bytes[1] == 0x5A)
            return true;
        return false;
    }

    private static int ParseVersion(string tag)
    {
        // "v2.0.1-beta" or "2.0.1-beta" -> 20001 * 10 + prerelease_order
        // M8: Include pre-release suffix in comparison
        var clean = tag.TrimStart('v');
        var dashIdx = clean.IndexOf('-');
        var versionPart = dashIdx >= 0 ? clean[..dashIdx] : clean;
        var suffix = dashIdx >= 0 ? clean[(dashIdx + 1)..].ToLowerInvariant() : "";

        var parts = versionPart.Split('.');
        var major = parts.Length > 0 && int.TryParse(parts[0], out var a) ? a : 0;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var b) ? b : 0;
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var c) ? c : 0;

        // Pre-release ordering: alpha=1, beta=2, rc=3, (stable)=4
        var preOrder = suffix switch
        {
            _ when suffix.StartsWith("alpha") => 1,
            _ when suffix.StartsWith("beta") => 2,
            _ when suffix.StartsWith("rc") => 3,
            "" => 4, // stable
            _ => 2, // default to beta-level for unknown
        };

        return (major * 10000 + minor * 100 + patch) * 10 + preOrder;
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
