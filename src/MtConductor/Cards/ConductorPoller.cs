using MtConductor.Api;
using MtConductor.Github;

namespace MtConductor.Cards;

/// <summary>
/// Gathers one <see cref="ConductorSnapshot"/> from MT's REST API (and gh for PRs) per call and
/// builds the view. Slow sources are refreshed less often than the main tick:
/// session summaries every <see cref="SessionRefresh"/>, PRs in the background via
/// <see cref="GhPrClient"/>'s own cache. Read-only: only GET requests, never touches the MT process.
/// </summary>
public sealed class ConductorPoller
{
    public static readonly TimeSpan SessionRefresh = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan OwnerProfileRefresh = TimeSpan.FromMinutes(5);

    private readonly MtApiClient _mt;
    private readonly GhPrClient _gh;
    private readonly Func<DateTime> _now;

    private readonly Dictionary<string, MtSessionLatest?> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _sessionsFetchedAt = DateTime.MinValue;

    private string _inboxUserId = "Owner";
    private DateTime _profileFetchedAt = DateTime.MinValue;

    private readonly GitRemoteResolver _git;

    private readonly object _prLock = new();
    private Dictionary<string, IReadOnlyList<PullRequestInfo>?> _prs = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string?> _repoUrls = new(StringComparer.OrdinalIgnoreCase);
    private string? _gitHubLogin;

    private IReadOnlyList<MtTask> _lastTasks = [];
    private bool _haveTasks;

    /// <summary>
    /// Joins the in_progress and todo lists. They're fetched in parallel, so a task that changes
    /// status between the two requests can appear in both: keep one row per id (latest update).
    /// MT sends task timestamps as UTC without an offset; convert them to local time here so
    /// everything downstream compares local times.
    /// </summary>
    internal static IReadOnlyList<MtTask> MergeTasks(IEnumerable<MtTask> inProgress, IEnumerable<MtTask> todo) =>
        inProgress.Concat(todo)
            .Where(t => !string.IsNullOrEmpty(t.Id))
            .Select(t => t with { UpdatedAt = UtcToLocal(t.UpdatedAt) })
            .GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(t => t.UpdatedAt ?? DateTime.MinValue).First())
            .ToList();

    internal static DateTime? UtcToLocal(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Local } v => v,
        { Kind: DateTimeKind.Utc } v => v.ToLocalTime(),
        var v => DateTime.SpecifyKind(v.Value, DateTimeKind.Utc).ToLocalTime(),
    };
    private Task? _prRefresh;

    public ConductorPoller(MtApiClient mt, GhPrClient gh, GitRemoteResolver git, Func<DateTime>? now = null)
    {
        _mt = mt;
        _gh = gh;
        _git = git;
        _now = now ?? (() => DateTime.Now);
    }

    public async Task<ConductorView> RefreshAsync(CancellationToken ct)
    {
        var now = _now();

        var health = await _mt.GetHealthAsync(ct);
        if (health is null)
            return ProjectCardBuilder.Build(Offline(now));

        if (now - _profileFetchedAt > OwnerProfileRefresh)
        {
            var profile = await _mt.GetOwnerProfileAsync(ct);
            if (profile is not null)
            {
                _inboxUserId = MtApiClient.InboxUserIdFor(profile);
                _profileFetchedAt = now;
            }
        }

        var projectsTask = _mt.GetProjectsAsync(ct);
        var inProgressTask = _mt.GetTasksAsync("in_progress", ct);
        var todoTask = _mt.GetTasksAsync("todo", ct);
        var inboxTask = _mt.GetUnreadInboxAsync(_inboxUserId, ct);
        var notificationsTask = _mt.GetUnreadNotificationsAsync(ct);
        var terminalsTask = _mt.GetConnectedTerminalsAsync(ct);
        var remoteTask = _mt.GetRemoteModeAsync(ct);
        await Task.WhenAll(projectsTask, inProgressTask, todoTask, inboxTask, notificationsTask, terminalsTask, remoteTask);

        if (projectsTask.Result is null)
            return ProjectCardBuilder.Build(Offline(now)); // health answered but the API isn't usable

        // Every per-project dictionary below is keyed by id: drop rows without one, and duplicates.
        IReadOnlyList<MtProject> projects = projectsTask.Result
            .Where(p => !string.IsNullOrEmpty(p.Id))
            .DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A failed task fetch must not look like "no tasks": reuse the last good list instead.
        var stale = new List<string>();
        if (inProgressTask.Result is { } inProgress && todoTask.Result is { } todo)
        {
            _lastTasks = MergeTasks(inProgress, todo);
            _haveTasks = true;
        }
        else
        {
            stale.Add(_haveTasks ? "tasks unavailable (showing last known)" : "tasks unavailable");
        }
        var tasks = _lastTasks;

        // Notification times come from SQLite datetime('now'): UTC without an offset.
        var notifications = notificationsTask.Result?
            .Select(n => n with { CreatedAt = UtcToLocal(n.CreatedAt) })
            .ToList();

        if (now - _sessionsFetchedAt > SessionRefresh)
        {
            await RefreshSessionsAsync(projects, ct);
            _sessionsFetchedAt = now;
        }

        StartPrRefreshIfIdle(projects, ct);

        Dictionary<string, IReadOnlyList<PullRequestInfo>?> prs;
        Dictionary<string, string?> repoUrls;
        string? gitHubLogin;
        lock (_prLock)
        {
            prs = new(_prs, StringComparer.OrdinalIgnoreCase);
            repoUrls = new(_repoUrls, StringComparer.OrdinalIgnoreCase);
            gitHubLogin = _gitHubLogin;
        }

        // Fill in repos found from the project folders so cards know a repo exists.
        var withRepos = projects
            .Select(p => string.IsNullOrWhiteSpace(p.GitRepoUrl) && repoUrls.GetValueOrDefault(p.Id) is { } url
                ? p with { GitRepoUrl = url }
                : p)
            .ToList();

        var snapshot = new ConductorSnapshot(
            MtOnline: true,
            withRepos,
            tasks,
            inboxTask.Result,
            notifications,
            terminalsTask.Result,
            new Dictionary<string, MtSessionLatest?>(_sessions, StringComparer.OrdinalIgnoreCase),
            prs,
            remoteTask.Result?.RemoteMode,
            now,
            gitHubLogin,
            stale);

        return ProjectCardBuilder.Build(snapshot);
    }

    /// <summary>Waits for an in-flight PR refresh (tests and shutdown).</summary>
    public Task WaitForPrRefreshAsync()
    {
        lock (_prLock) return _prRefresh ?? Task.CompletedTask;
    }

    private async Task RefreshSessionsAsync(IReadOnlyList<MtProject> projects, CancellationToken ct)
    {
        var lookups = projects
            .Where(p => !string.IsNullOrWhiteSpace(p.Path))
            .Select(async p => (p.Id, Result: await _mt.GetLatestSessionAsync(p.Path!, ct)));

        foreach (var (id, result) in await Task.WhenAll(lookups))
        {
            if (result is null) continue;            // failed: keep what we had
            _sessions[id] = result.Value.Found ? result.Value.Latest : null;
        }
    }

    private void StartPrRefreshIfIdle(IReadOnlyList<MtProject> projects, CancellationToken ct)
    {
        lock (_prLock)
        {
            if (_prRefresh is { IsCompleted: false }) return;
            var snapshot = projects.ToList();
            _prRefresh = Task.Run(async () =>
            {
                var results = await Task.WhenAll(snapshot.Select(async p =>
                {
                    // MT's gitRepoUrl first; otherwise the project folder's own origin remote.
                    var repoUrl = !string.IsNullOrWhiteSpace(p.GitRepoUrl)
                        ? p.GitRepoUrl
                        : await _git.GetOriginUrlAsync(p.SourcePath ?? p.Path, ct);
                    var prs = repoUrl is null ? null : await _gh.GetOpenPrsAsync(repoUrl, ct);
                    return (p.Id, RepoUrl: repoUrl, Prs: prs);
                }));
                var login = await _gh.GetViewerLoginAsync(ct);
                lock (_prLock)
                {
                    _gitHubLogin = login;
                    _prs = results.ToDictionary(r => r.Id, r => r.Prs, StringComparer.OrdinalIgnoreCase);
                    _repoUrls = results.ToDictionary(r => r.Id, r => r.RepoUrl, StringComparer.OrdinalIgnoreCase);
                }
            }, ct);
        }
    }

    private static ConductorSnapshot Offline(DateTime now) =>
        new(false, [], [], null, null, null,
            new Dictionary<string, MtSessionLatest?>(), new Dictionary<string, IReadOnlyList<PullRequestInfo>?>(),
            null, now);
}
