using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MtConductor.Github;

/// <summary>An open pull request as shown on a project card.</summary>
/// <param name="ChecksState">"success" | "failure" | "pending" | "none".</param>
/// <param name="ReviewDecision">APPROVED, CHANGES_REQUESTED, REVIEW_REQUIRED, or null when gh reports none.</param>
public sealed record PullRequestInfo(int Number, string Title, string Url, bool IsDraft, string ChecksState, string? ReviewDecision);

/// <summary>
/// Reads open pull requests by calling the GitHub CLI (<c>gh</c>). Results, including failures,
/// are cached per owner/repo so a missing or logged-out gh isn't called over and over.
/// </summary>
public sealed class GhPrClient
{
    private static readonly TimeSpan GhTimeout = TimeSpan.FromSeconds(15);

    private readonly TimeSpan _cacheDuration;
    private readonly Func<string, CancellationToken, Task<(int ExitCode, string StdOut)>> _runGh;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CacheEntry(DateTime ExpiresUtc, IReadOnlyList<PullRequestInfo>? Prs);

    /// <param name="cacheDuration">How long a result (or a failure) is reused per repo.</param>
    /// <param name="runGh">Runs gh for an owner/repo and returns its exit code and stdout. Tests pass a fake.</param>
    public GhPrClient(TimeSpan cacheDuration, Func<string, CancellationToken, Task<(int ExitCode, string StdOut)>>? runGh = null)
    {
        _cacheDuration = cacheDuration;
        _runGh = runGh ?? RunGhAsync;
    }

    /// <summary>Returns open PRs for the repo, or null when unknown (gh missing, not logged in, timeout, bad repo URL).</summary>
    public async Task<IReadOnlyList<PullRequestInfo>?> GetOpenPrsAsync(string? gitRepoUrl, CancellationToken ct)
    {
        var ownerRepo = ParseOwnerRepo(gitRepoUrl);
        if (ownerRepo is null)
            return null;

        if (_cache.TryGetValue(ownerRepo, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
            return cached.Prs;

        IReadOnlyList<PullRequestInfo>? result;
        try
        {
            var (exitCode, stdout) = await _runGh(ownerRepo, ct).ConfigureAwait(false);
            result = exitCode == 0 ? ParsePrJson(stdout) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller cancelled: don't cache anything
        }
        catch (Exception ex) when (ex is Win32Exception or JsonException or InvalidOperationException
                                       or IOException or OperationCanceledException)
        {
            result = null; // gh missing, bad output, or timed out
        }

        _cache[ownerRepo] = new CacheEntry(DateTime.UtcNow + _cacheDuration, result);
        return result;
    }

    private static readonly Regex OwnerRepoPattern = new(
        @"^(?<owner>[A-Za-z0-9][A-Za-z0-9-]{0,38})/(?<repo>[A-Za-z0-9._-]+?)(?:\.git)?/?$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// https://github.com/o/r(.git), git@github.com:o/r.git, ssh://git@github.com/o/r.git, o/r -> "o/r".
    /// Non-GitHub hosts or junk -> null.
    /// </summary>
    internal static string? ParseOwnerRepo(string? gitRepoUrl)
    {
        if (string.IsNullOrWhiteSpace(gitRepoUrl))
            return null;

        var s = gitRepoUrl.Trim();
        string path;

        if (s.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            // scp-like: git@github.com:owner/repo.git
            var colon = s.IndexOf(':');
            if (colon < 0)
                return null;
            var host = s[4..colon];
            if (!host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                return null;
            path = s[(colon + 1)..];
        }
        else if (s.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri))
                return null;
            if (uri.Scheme is not ("https" or "http" or "ssh" or "git"))
                return null;
            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && !uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))
                return null;
            path = uri.AbsolutePath.TrimStart('/');
        }
        else if (s.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
        {
            path = s["github.com/".Length..];
        }
        else
        {
            path = s;
        }

        var m = OwnerRepoPattern.Match(path);
        if (!m.Success)
            return null;
        var repo = m.Groups["repo"].Value;
        if (repo.Length == 0 || repo is "." or "..")
            return null;
        return $"{m.Groups["owner"].Value}/{repo}";
    }

    /// <summary>Parses the output of <c>gh pr list --json number,title,url,isDraft,statusCheckRollup,reviewDecision</c>.</summary>
    internal static IReadOnlyList<PullRequestInfo> ParsePrJson(string json)
    {
        var list = new List<PullRequestInfo>();
        if (string.IsNullOrWhiteSpace(json))
            return list;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected a JSON array from gh pr list.");

        foreach (var pr in doc.RootElement.EnumerateArray())
        {
            if (pr.ValueKind != JsonValueKind.Object)
                continue;

            var number = pr.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var num) ? num : 0;
            var title = GetString(pr, "title") ?? "";
            var url = GetString(pr, "url") ?? "";
            var isDraft = pr.TryGetProperty("isDraft", out var d) && d.ValueKind == JsonValueKind.True;
            var checks = pr.TryGetProperty("statusCheckRollup", out var rollup) ? RollupChecks(rollup) : "none";
            var review = GetString(pr, "reviewDecision");
            if (string.IsNullOrEmpty(review))
                review = null; // gh reports "" when there is no decision

            list.Add(new PullRequestInfo(number, title, url, isDraft, checks, review));
        }

        return list;
    }

    private static readonly HashSet<string> FailureValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "FAILURE", "ERROR", "CANCELLED", "TIMED_OUT", "ACTION_REQUIRED", "STARTUP_FAILURE",
    };

    /// <summary>Folds a statusCheckRollup array into "success" | "failure" | "pending" | "none".</summary>
    internal static string RollupChecks(JsonElement statusCheckRollup)
    {
        if (statusCheckRollup.ValueKind != JsonValueKind.Array)
            return "none";

        var any = false;
        var anyPending = false;

        foreach (var item in statusCheckRollup.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            any = true;

            var typename = GetString(item, "__typename");
            var isStatusContext = string.Equals(typename, "StatusContext", StringComparison.Ordinal)
                || (typename is null && item.TryGetProperty("state", out _) && !item.TryGetProperty("status", out _));

            if (isStatusContext)
            {
                var state = GetString(item, "state");
                if (state is not null && FailureValues.Contains(state))
                    return "failure";
                if (!string.Equals(state, "SUCCESS", StringComparison.OrdinalIgnoreCase))
                    anyPending = true; // PENDING, EXPECTED, or unknown
            }
            else
            {
                var conclusion = GetString(item, "conclusion");
                if (!string.IsNullOrEmpty(conclusion) && FailureValues.Contains(conclusion))
                    return "failure";
                var status = GetString(item, "status");
                if (!string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(conclusion))
                    anyPending = true; // QUEUED, IN_PROGRESS, WAITING, ...
            }
        }

        if (!any)
            return "none";
        return anyPending ? "pending" : "success";
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Default runner: launches gh directly (no shell). Returns (-1, "") on timeout.</summary>
    private static async Task<(int ExitCode, string StdOut)> RunGhAsync(string ownerRepo, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("gh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in new[]
                 {
                     "pr", "list", "--repo", ownerRepo, "--state", "open", "--limit", "30",
                     "--json", "number,title,url,isDraft,statusCheckRollup,reviewDecision",
                 })
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment["GIT_CONFIG_COUNT"] = "0";
        psi.Environment["MULTITERMINAL_GH_SIGN"] = "0";
        psi.Environment["GH_PROMPT_DISABLED"] = "1";
        psi.Environment["NO_COLOR"] = "1";

        using var process = new Process { StartInfo = psi };
        process.Start(); // throws Win32Exception when gh isn't installed; the caller maps that to null

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(GhTimeout);

        // Drain both streams concurrently so a chatty stderr can't block the child.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
            return (process.ExitCode, stdout);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            ct.ThrowIfCancellationRequested();
            return (-1, ""); // timed out
        }
    }
}
