using System.Collections.Concurrent;
using System.ComponentModel;

namespace MtConductor.Github;

/// <summary>
/// Fallback for projects whose MT record has no gitRepoUrl: reads the <c>upstream</c> remote of
/// the project's own folder, else <c>origin</c> (<c>git -C path remote get-url ...</c>), but only
/// when the folder IS the repository root: a folder inside another repo (or a monorepo
/// subfolder) says nothing about its own project. Read-only, cached per path.
/// </summary>
public sealed class GitRemoteResolver
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _cacheDuration;
    private readonly Func<(string Path, string[] Args), CancellationToken, Task<(int ExitCode, string StdOut)>> _runGit;
    private readonly ConcurrentDictionary<string, (DateTime ExpiresUtc, string? Url)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public GitRemoteResolver(TimeSpan cacheDuration, Func<(string Path, string[] Args), CancellationToken, Task<(int ExitCode, string StdOut)>>? runGit = null)
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
                if (await IsRepoRootAsync(projectPath, ct).ConfigureAwait(false))
                {
                    // Forks: PRs are opened against upstream, so prefer it over origin.
                    foreach (var remote in new[] { "upstream", "origin" })
                    {
                        var (exit, stdout) = await _runGit((projectPath, ["remote", "get-url", remote]), ct).ConfigureAwait(false);
                        var line = stdout.Trim();
                        if (exit == 0 && line.Length > 0) { url = line; break; }
                    }
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

    private async Task<bool> IsRepoRootAsync(string projectPath, CancellationToken ct)
    {
        var (exit, stdout) = await _runGit((projectPath, ["rev-parse", "--show-toplevel"]), ct).ConfigureAwait(false);
        return exit == 0 && SamePath(stdout.Trim(), projectPath);
    }

    /// <summary>Git prints forward slashes; compare as normalised, case-insensitive full paths.</summary>
    internal static bool SamePath(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        try
        {
            static string Norm(string p) => Path.GetFullPath(p.Trim().Replace('/', '\\')).TrimEnd('\\');
            return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
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

    // fsmonitor off: a repo's own config must not get a program of its choosing run by a read-only lookup.
    private static Task<(int ExitCode, string StdOut)> RunGitAsync((string Path, string[] Args) request, CancellationToken ct) =>
        ProcessRunner.RunAsync("git", ["-c", "core.fsmonitor=false", "-C", request.Path, .. request.Args], GitTimeout, ct);
}
