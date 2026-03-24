using System.Diagnostics;

namespace Rede.Core.Services;

public class UpdateService
{
    private readonly string _repoPath;
    private readonly string _branch;

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
