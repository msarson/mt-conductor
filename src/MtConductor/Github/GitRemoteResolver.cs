using System.Collections.Concurrent;
using System.ComponentModel;

namespace MtConductor.Github;

/// <summary>
/// Fallback for projects whose MT record has no gitRepoUrl: reads the <c>upstream</c> remote of
/// the project's own folder, else <c>origin</c> (<c>git -C path remote get-url ...</c>).
/// Read-only, cached per path.
/// </summary>
public sealed class GitRemoteResolver
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _cacheDuration;
    private readonly Func<(string Remote, string Path), CancellationToken, Task<(int ExitCode, string StdOut)>> _runGit;
    private readonly ConcurrentDictionary<string, (DateTime ExpiresUtc, string? Url)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public GitRemoteResolver(TimeSpan cacheDuration, Func<(string Remote, string Path), CancellationToken, Task<(int ExitCode, string StdOut)>>? runGit = null)
    {
        _cacheDuration = cacheDuration;
        _runGit = runGit ?? RunGitAsync;
    }

    /// <summary>The repo URL for the folder (upstream, else origin), or null (no folder, not a repo, no remote, git missing).</summary>
    public async Task<string?> GetRepoUrlAsync(string? projectPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return null;
        if (_cache.TryGetValue(projectPath, out var hit) && hit.ExpiresUtc > DateTime.UtcNow) return hit.Url;

        string? url = null;
        // Local paths on fixed drives only: a UNC or device path, or a mapped network drive, from MT
        // would make us contact a remote host.
        if (IsLocalPath(projectPath) && IsFixedDrive(projectPath) && Directory.Exists(projectPath))
        {
            try
            {
                // Forks: PRs are opened against upstream, so prefer it over origin.
                foreach (var remote in new[] { "upstream", "origin" })
                {
                    var (exit, stdout) = await _runGit((remote, projectPath), ct).ConfigureAwait(false);
                    var line = stdout.Trim();
                    if (exit == 0 && line.Length > 0) { url = line; break; }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or OperationCanceledException)
            {
                url = null;
            }
        }

        _cache[projectPath] = (DateTime.UtcNow + _cacheDuration, url);
        return url;
    }

    /// <summary>True when the path's drive is a fixed disk (not a mapped network drive, removable or unknown).</summary>
    internal static bool IsFixedDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path.Trim());
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool IsLocalPath(string path)
    {
        var p = path.Trim().Replace('/', '\\');
        if (p.StartsWith(@"\\", StringComparison.Ordinal)) return false;   // UNC, \\?\, \\.\
        return p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\';
    }

    private static Task<(int ExitCode, string StdOut)> RunGitAsync((string Remote, string Path) request, CancellationToken ct) =>
        ProcessRunner.RunAsync("git", ["-C", request.Path, "remote", "get-url", request.Remote], GitTimeout, ct);
}
