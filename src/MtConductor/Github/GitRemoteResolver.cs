using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

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
    private readonly Func<string, CancellationToken, Task<(int ExitCode, string StdOut)>> _runGit;
    private readonly ConcurrentDictionary<string, (DateTime ExpiresUtc, string? Url)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public GitRemoteResolver(TimeSpan cacheDuration, Func<string, CancellationToken, Task<(int ExitCode, string StdOut)>>? runGit = null)
    {
        _cacheDuration = cacheDuration;
        _runGit = runGit ?? RunGitAsync;
    }

    /// <summary>The origin URL for the folder, or null (no folder, not a repo, no origin, git missing).</summary>
    public async Task<string?> GetOriginUrlAsync(string? projectPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return null;
        if (_cache.TryGetValue(projectPath, out var hit) && hit.ExpiresUtc > DateTime.UtcNow) return hit.Url;

        string? url = null;
        // Local paths only: a UNC or device path from MT would make us contact a remote host.
        if (IsLocalPath(projectPath) && Directory.Exists(projectPath))
        {
            try
            {
                // Forks: PRs are opened against upstream, so prefer it over origin.
                foreach (var remote in new[] { "upstream", "origin" })
                {
                    var (exit, stdout) = await _runGit($"{remote}|{projectPath}", ct).ConfigureAwait(false);
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

    internal static bool IsLocalPath(string path)
    {
        var p = path.Trim().Replace('/', '\\');
        if (p.StartsWith(@"\\", StringComparison.Ordinal)) return false;   // UNC, \\?\, \\.\
        return p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\';
    }

    // request is "remote|path" so tests can fake per-remote answers through one delegate.
    private static async Task<(int ExitCode, string StdOut)> RunGitAsync(string request, CancellationToken ct)
    {
        var split = request.IndexOf('|');
        var remote = request[..split];
        var projectPath = request[(split + 1)..];
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-C", projectPath, "remote", "get-url", remote })
            psi.ArgumentList.Add(arg);
        psi.Environment["GIT_CONFIG_COUNT"] = "0";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = new Process { StartInfo = psi };
        process.Start();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(GitTimeout);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
            return (process.ExitCode, stdout);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
            ct.ThrowIfCancellationRequested();
            return (-1, "");
        }
    }
}
