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
    internal const string CurrentVersion = "2.20.7-beta";

    /// <summary>The version this build identifies as (e.g. for `REDE --version`).</summary>
    public static string Version => CurrentVersion;

    /// <summary>
    /// Path to a file that records the last successfully installed release tag.
    /// Prevents re-prompting for an update that was already downloaded and swapped.
    /// </summary>
    private static string InstalledTagPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rede", ".last-update");

    /// <summary>
    /// Ed25519 public key (base64, 32 bytes) of the release signing key. When set,
    /// downloaded update binaries MUST be accompanied by a valid detached signature
    /// (asset: "&lt;binary&gt;.sig", base64-encoded Ed25519 signature of the raw binary).
    /// When empty (default), signature verification is skipped and the update flow
    /// falls back to the SHA256 checksums mitigation.
    ///
    /// Release workflow to enable: generate an Ed25519 keypair out-of-band
    /// (e.g. with libsodium), embed the 32-byte public key here as base64, keep the
    /// private key offline, and sign each release binary so that
    /// "sodium_sign_detached(sig, binary_bytes, sk)" produces the .sig asset.
    /// </summary>
    private const string ReleaseSigningPublicKeyB64 = "SPON95u43RxzipArSW1Ntyk9eQ6hHCaf8UJlzOR+vas=";

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

                // Skip if this tag was already installed (prevents re-prompt loop
                // when the binary was swapped but CurrentVersion wasn't bumped)
                var installedTag = ReadInstalledTag();
                if (installedTag is not null && (tag == installedTag || tag == $"v{installedTag}" || $"v{tag}" == installedTag)) continue;

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

            // C2: Validate downloaded binary — magic bytes + signature + SHA256 hash.
            if (!IsValidExecutable(bytes))
            {
                onStatus?.Invoke("Update failed: invalid binary.");
                return false;
            }

            if (!await VerifyReleaseAssetAsync(release.Tag, release.AssetName, bytes, onStatus))
                return false;

            onStatus?.Invoke("Installing...");

            var currentExe = Environment.ProcessPath;
            if (currentExe is null) return false;

            // Check if we need elevated privileges (binary in root-owned dir like /usr/bin)
            var needsElevation = !RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                                 && NeedsRootPrivileges(currentExe);

            if (needsElevation)
            {
                // Write new binary to temp dir (user-writable), then pkexec to swap
                var tempNew = Path.Combine(Path.GetTempPath(), $"rede-update-{Guid.NewGuid():N}");
                await File.WriteAllBytesAsync(tempNew, bytes);
                File.SetUnixFileMode(tempNew,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

                onStatus?.Invoke("Requesting root privileges...");

                // pkexec runs a shell one-liner: backup old, move new into place.
                // Paths are shell-escaped so an install path containing quotes, spaces, or
                // shell metacharacters can't inject additional commands into the root shell.
                var script = $"mv -f {ShEscape(currentExe)} {ShEscape(currentExe + ".old")} && " +
                             $"mv -f {ShEscape(tempNew)} {ShEscape(currentExe)} && " +
                             $"chmod 755 {ShEscape(currentExe)}";
                // Use ArgumentList so pkexec receives the argv we built — no Windows-argv
                // re-parsing of a single Arguments string.
                var psi = new ProcessStartInfo("pkexec")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("sh");
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(script);
                using var proc = Process.Start(psi);
                if (proc is null) { File.Delete(tempNew); return false; }
                await proc.WaitForExitAsync();

                if (proc.ExitCode != 0)
                {
                    File.Delete(tempNew);
                    var err = await proc.StandardError.ReadToEndAsync();
                    onStatus?.Invoke($"Update failed: privilege elevation denied or failed. {err}");
                    return false;
                }
            }
            else
            {
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
            }

            // Persist the installed tag to prevent re-prompt on next launch
            WriteInstalledTag(release.Tag);

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
    /// Public composed check: verify an asset downloaded from a given release tag passes
    /// Ed25519 signature AND/OR SHA256SUMS validation. Returns true if verified, false if
    /// verification fails. Callers that need to gate non-binary assets (e.g. rnnoise native
    /// libs) share this logic so any downloaded blob from our GitHub releases gets the same
    /// integrity guarantees as the main binary update path.
    /// </summary>
    public static async Task<bool> VerifyReleaseAssetAsync(string tag, string assetName, byte[] bytes, Action<string>? onStatus = null)
    {
        var sigResult = await VerifyReleaseSignatureAsync(tag, assetName, bytes);
        if (sigResult == false)
        {
            onStatus?.Invoke($"Verification failed for {assetName}: signature invalid or missing.");
            return false;
        }

        var hashVerified = await VerifyReleaseHashAsync(tag, assetName, bytes);
        if (hashVerified == false)
        {
            onStatus?.Invoke($"Verification failed for {assetName}: SHA256 hash mismatch! Download may be compromised.");
            return false;
        }

        if (sigResult is null && hashVerified is null)
        {
            onStatus?.Invoke($"Verification failed for {assetName}: unsigned and no SHA256SUMS asset. Refusing to use.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Verify the Ed25519 detached signature of a release asset against the embedded
    /// release signing public key.
    /// Returns true if verified, false if invalid/missing (hard fail),
    /// null if release signing is not configured for this build (skip).
    /// </summary>
    private static async Task<bool?> VerifyReleaseSignatureAsync(string tag, string assetName, byte[] bytes)
    {
        if (string.IsNullOrEmpty(ReleaseSigningPublicKeyB64)) return null;

        byte[] pubKey;
        try
        {
            pubKey = Convert.FromBase64String(ReleaseSigningPublicKeyB64);
            if (pubKey.Length != 32) return false;
        }
        catch { return false; }

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "Rede-Desktop");
            http.Timeout = TimeSpan.FromSeconds(10);

            var url = $"https://api.github.com/repos/{GitHubRepo}/releases/tags/{tag}";
            var json = await http.GetStringAsync(url);
            var releaseDoc = JsonDocument.Parse(json);

            var sigAssetName = assetName + ".sig";
            string? sigUrl = null;
            foreach (var asset in releaseDoc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                if (name == sigAssetName)
                {
                    sigUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }

            if (sigUrl is null) return false; // signing configured but sig asset missing

            var sigB64 = (await http.GetStringAsync(sigUrl)).Trim();
            var sig = Convert.FromBase64String(sigB64);
            if (sig.Length != 64) return false;

            return Sodium.PublicKeyAuth.VerifyDetached(sig, bytes, pubKey);
        }
        catch
        {
            return false; // any error during fetch/parse/verify → reject
        }
    }

    /// <summary>
    /// C2: Verify SHA256 hash of a release asset against the checksums file from that release.
    /// Returns true if verified, false if mismatch, null if no checksum file available.
    /// </summary>
    private static async Task<bool?> VerifyReleaseHashAsync(string tag, string assetName, byte[] bytes)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "Rede-Desktop");
            http.Timeout = TimeSpan.FromSeconds(10);

            var url = $"https://api.github.com/repos/{GitHubRepo}/releases/tags/{tag}";
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
                if (parts.Length >= 2 && parts[1].Trim('*') == assetName)
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

    /// <summary>
    /// Escape a string for safe inclusion inside a POSIX shell single-quoted segment.
    /// Wraps the value in 'single quotes' and replaces any internal quote with '\''.
    /// Output is safe to concatenate directly into a sh -c script.
    /// </summary>
    private static string ShEscape(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Check if installing to <paramref name="path"/> requires root (e.g. /usr/bin,
    /// /usr/local/bin, /opt/...). Replacing the binary works via rename, which
    /// needs write+execute on the *directory* — file write permission alone isn't
    /// enough, and the previous version of this check missed that distinction so
    /// updates silently took the no-elevation path and then failed at File.Move.
    /// </summary>
    private static bool NeedsRootPrivileges(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return false;

            // Probe the directory by creating (and deleting) a tiny throwaway file.
            // This is the canonical "can I add/remove entries here?" check on POSIX.
            var probe = Path.Combine(dir, $".rede-write-probe-{Guid.NewGuid():N}");
            try
            {
                using (var _ = File.Create(probe)) { }
                File.Delete(probe);
                return false;
            }
            catch (UnauthorizedAccessException) { return true; }
            catch (IOException ex) when (ex.HResult == unchecked((int)0x80070005)) { return true; }
        }
        catch
        {
            // If the probe failed for some other reason (read-only fs, etc.) we err on
            // the safe side and try without elevation — the move will surface the
            // actual error instead of us silently demanding pkexec.
            return false;
        }
        return false;
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

    private static string? ReadInstalledTag()
    {
        try
        {
            var path = InstalledTagPath;
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch { return null; }
    }

    private static void WriteInstalledTag(string tag)
    {
        try
        {
            var dir = Path.GetDirectoryName(InstalledTagPath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(InstalledTagPath, tag);
        }
        catch { }
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
