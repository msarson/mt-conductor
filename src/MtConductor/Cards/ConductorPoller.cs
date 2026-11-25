using MtConductor.Api;
using MtConductor.Github;

namespace MtConductor.Cards;

/// <summary>
/// Gathers one <see cref="ConductorSnapshot"/> from MT's REST API (and gh for PRs) per call and
/// builds the view. Slow sources are refreshed less often than the main tick:
/// session summaries every <see cref="SessionRefresh"/>, PRs in the background via
/// <see cref="GhPrClient"/>'s own cache. Refreshes only GET; the one write is
/// <see cref="OpenProjectAsync"/>, on the Owner's click. Never starts or stops the MT process.
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
    private HashSet<string> _prOthersUnknown = new(StringComparer.OrdinalIgnoreCase);   // projects whose general PR list failed
    // At most this many projects at once run git/gh, so a long project list doesn't start dozens of processes.
    internal const int MaxConcurrentPrLookups = 4;
    private readonly SemaphoreSlim _prGate = new(MaxConcurrentPrLookups);
    private bool _prFault;   // the last background PR refresh threw: shown as a warning, last good PRs kept

    private IReadOnlyList<MtTask> _lastTasks = [];
    private bool _haveTasks;

    // Remote mode as last seen and when it last flipped. MT flips it itself (desk activity,
    // idle timer, phone, presence sensor, its own switch); the API doesn't say which.
    private bool? _lastRemote;
    private DateTime? _remoteChangedAt;

    /// <summary>MT's process id from the last good health check (for AllowSetForegroundWindow).</summary>
    public int? MtPid { get; private set; }

    /// <summary>
    /// Whether this MT has the Open endpoint: null until an Open is tried, then true or false.
    /// Forgotten when MT's pid changes, since a restarted MT may be a different build.
    /// </summary>
    public bool? OpenSupported { get; private set; }

    /// <summary>
    /// Takes MT's pid and Open capability from a health answer. A restarted MT (new pid) may be a
    /// different build, so what we learnt about Open is forgotten. The "project-open-v1"
    /// capability turns Open on; its absence proves nothing, because builds of #103 from before
    /// the flag have the endpoint without it, so those still fall back to trying.
    /// </summary>
    private void ApplyHealth(MtHealth health)
    {
        if (health.Pid > 0 && health.Pid != MtPid)
        {
            MtPid = health.Pid;
            OpenSupported = null;
        }
        if (health.Has(MtHealth.ProjectOpenCapability)) OpenSupported = true;
    }

    /// <summary>
    /// Re-reads MT's pid just before Open, so a just-restarted MT still gets the right to take
    /// the foreground. Returns the pid, or the last known one if the health check fails.
    /// </summary>
    public async Task<int?> RefreshMtPidAsync(CancellationToken ct)
    {
        var health = await _mt.GetHealthAsync(ct);
        if (health is not null) ApplyHealth(health);
        return MtPid;
    }

    /// <summary>The Open button: asks MT to focus or launch the project, and learns whether MT supports it.</summary>
    public async Task<MtOpenResult> OpenProjectAsync(string projectId, CancellationToken ct)
    {
        var result = await _mt.OpenProjectAsync(projectId, ct);
        OpenSupported = result.Kind switch
        {
            MtOpenKind.Unsupported => false,
            MtOpenKind.Offline or MtOpenKind.NoAnswer => OpenSupported,
            _ => true,
        };
        return result;
    }

    // A flip while MT was unreachable can't be dated (we only learn of it on reconnect), so after an
    // offline tick the next value counts as a first sighting again.
    private void ForgetRemote()
    {
        _lastRemote = null;
        _remoteChangedAt = null;
    }

    private void TrackRemote(bool? polled, DateTime now)
    {
        if (polled is not { } on) return;   // unknown this tick: keep what we had
        if (_lastRemote is { } before && before != on) _remoteChangedAt = now;
        _lastRemote = on;
    }

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
        {
            ForgetRemote();
            return ProjectCardBuilder.Build(Offline(now));
        }

        ApplyHealth(health);

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
        {
            ForgetRemote();
            return ProjectCardBuilder.Build(Offline(now)); // health answered but the API isn't usable
        }

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
        bool prFault;
        HashSet<string> othersUnknown;
        lock (_prLock)
        {
            prFault = _prFault;
            othersUnknown = new(_prOthersUnknown, StringComparer.OrdinalIgnoreCase);
            prs = new(_prs, StringComparer.OrdinalIgnoreCase);
            repoUrls = new(_repoUrls, StringComparer.OrdinalIgnoreCase);
            gitHubLogin = _gitHubLogin;
        }

        if (_gh.GhMissing) stale.Add("gh not found on PATH");   // says why PRs are unknown
        if (prFault) stale.Add("PRs unavailable");

        // Fill in repos found from the project folders so cards know a repo exists.
        var withRepos = projects
            .Select(p => repoUrls.GetValueOrDefault(p.Id) is { } url ? p with { GitRepoUrl = url } : p)
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
            stale,
            othersUnknown);

        TrackRemote(remoteTask.Result?.RemoteMode, now);
        return ProjectCardBuilder.Build(snapshot) with
        {
            RemoteModeChangedAt = _remoteChangedAt,
            OpenSupported = OpenSupported,
        };
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
            _prRefresh = RefreshPrsSafelyAsync(snapshot, ct);
        }
    }

    /// <summary>
    /// Runs the PR refresh on the thread pool. A bug or unexpected failure must not vanish in an
    /// unobserved task: it is logged, the last good PRs stay, and the next view warns "PRs unavailable".
    /// </summary>
    private async Task RefreshPrsSafelyAsync(List<MtProject> snapshot, CancellationToken ct)
    {
        try
        {
            await Task.Run(() => RefreshPrsAsync(snapshot, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"MT Conductor PR refresh failed: {ex}");
            lock (_prLock) _prFault = true;
        }
    }

    private async Task RefreshPrsAsync(List<MtProject> snapshot, CancellationToken ct)
    {
        var login = await _gh.GetViewerLoginAsync(ct);
        var results = await Task.WhenAll(snapshot.Select(p => LookUpPrsAsync(p, login, ct)));        lock (_prLock)
        {
            _gitHubLogin = login;
            _prs = results.ToDictionary(r => r.Id, r => r.Prs, StringComparer.OrdinalIgnoreCase);
            _repoUrls = results.ToDictionary(r => r.Id, r => r.RepoUrl, StringComparer.OrdinalIgnoreCase);
            _prOthersUnknown = results.Where(r => r.OthersUnknown).Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _prFault = false;
        }
    }

    private async Task<(string Id, string? RepoUrl, IReadOnlyList<PullRequestInfo>? Prs, bool OthersUnknown)> LookUpPrsAsync(
        MtProject p, string? login, CancellationToken ct)
    {
        await _prGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The folder's own upstream (else origin) remote first: on a fork it points at
            // the repo the PRs go to, which MT's gitRepoUrl may not. MT's URL is the fallback.
            var folderUrl = await _git.GetRepoUrlAsync(p.SourcePath ?? p.Path, ct);
            var repoUrl = GhPrClient.ParseOwnerRepo(folderUrl) is not null
                ? folderUrl
                : string.IsNullOrWhiteSpace(p.GitRepoUrl) ? null : p.GitRepoUrl;
            if (repoUrl is null) return (p.Id, null, null, false);

            var general = await _gh.GetOpenPrsAsync(repoUrl, ct);
            // The general list is capped; ask for the Owner's own PRs separately.
            var mine = login is null ? null : await _gh.GetMyOpenPrsAsync(repoUrl, ct);
            return (p.Id, repoUrl, MergePrs(general, mine, login), general is null && mine is not null);
        }
        finally
        {
            _prGate.Release();
        }
    }

    /// <summary>
    /// Own PRs first, then the others from the capped general list, one entry per PR number.
    /// Null only when neither list could be read. When only the own-PRs query worked, the result is just
    /// the Owner's PRs and the caller must say that others are unknown (see <c>OthersUnknown</c> in the
    /// refresh), not imply there are none.
    /// </summary>
    internal static IReadOnlyList<PullRequestInfo>? MergePrs(
        IReadOnlyList<PullRequestInfo>? general, IReadOnlyList<PullRequestInfo>? mine, string? login)
    {
        if (general is null && mine is null) return null;
        var merged = new List<PullRequestInfo>();
        var seen = new HashSet<int>();
        foreach (var pr in mine ?? [])
            if (seen.Add(pr.Number)) merged.Add(pr.Author is null ? pr with { Author = login } : pr);
        foreach (var pr in general ?? [])
            if (seen.Add(pr.Number)) merged.Add(pr);
        return merged;
    }

    private static ConductorSnapshot Offline(DateTime now) =>
        new(false, [], [], null, null, null,
            new Dictionary<string, MtSessionLatest?>(), new Dictionary<string, IReadOnlyList<PullRequestInfo>?>(),
            null, now);
}
