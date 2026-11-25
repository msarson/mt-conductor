using System.Globalization;
using MtConductor.Api;
using MtConductor.Github;

namespace MtConductor.Cards;

/// <summary>Everything one refresh read from MT and gh. Null collections mean "couldn't read".</summary>
public sealed record ConductorSnapshot(
    bool MtOnline,
    IReadOnlyList<MtProject> Projects,
    IReadOnlyList<MtTask> Tasks,
    IReadOnlyList<MtInboxMessage>? Inbox,
    IReadOnlyList<MtNotification>? Notifications,
    IReadOnlyList<MtTerminal>? Terminals,
    IReadOnlyDictionary<string, MtSessionLatest?> LatestSessions,
    IReadOnlyDictionary<string, IReadOnlyList<PullRequestInfo>?> PullRequests,
    bool? RemoteMode,
    DateTime Now,
    string? GitHubLogin = null,    // gh's logged-in user; PRs by others are counted, not listed
    IReadOnlyList<string>? StaleSources = null,
    IReadOnlySet<string>? PrOthersUnknown = null);   // project ids whose list of others' PRs couldn't be read   // sources that failed this tick (last good data reused)

public sealed record PrBadge(int Number, string Title, string Url, string Checks, bool IsDraft, string? Review);

public sealed record ProjectCard(
    string Id,
    string Name,
    string? Pm,
    string Status,
    bool IsPinned,
    bool NeedsYou,
    IReadOnlyList<string> NeedsYouReasons,
    string? LeftOff,
    string? LeftOffAge,
    int InProgress,
    int ToDo,
    int AwaitingTest,
    int Questions,
    IReadOnlyList<PrBadge>? PullRequests,   // yours; null = unknown (no GitHub repo, or gh unavailable)
    int OtherPullRequests,                  // open PRs by other people in the same repo
    bool HasGitHubRepo,
    IReadOnlyList<string> ActiveAgents,     // connected agents whose active task is in this project
    string OpenHint,
    bool PrOwnerKnown = true,               // false: gh's login is unknown, so PRs aren't split into yours and others'
    // What's behind the numbers, for hover text: task titles (at most TitleListMax, then "+N more").
    IReadOnlyList<string>? InProgressTitles = null,
    IReadOnlyList<string>? ToDoTitles = null,
    IReadOnlyList<string>? AwaitingTestTip = null,       // hover lines: a header, then "Title (3 items)" per task with items ready to test
    IReadOnlyList<string>? ReasonDetails = null,
    string? PrRepo = null,                              // owner/repo the PRs come from (for the PR line's tooltip)
    string? GitHubLogin = null,                         // gh login "yours" means; null = unknown
    bool OthersKnown = true);                           // false: only your PRs could be read, others' are unknown        // parallel to NeedsYouReasons: hover text, "" when there is none

public sealed record ConductorView(
    bool MtOnline,
    DateTime UpdatedAt,
    bool? RemoteMode,
    IReadOnlyList<ProjectCard> Cards,
    int UnmatchedQuestions,
    IReadOnlyList<string>? Warnings = null,    // e.g. "inbox unavailable": counts may be incomplete
    DateTime? RemoteModeChangedAt = null,      // when MT's remote mode last flipped while we watched
    bool? OpenSupported = null);               // MT has POST /api/projects/{id}/open; null = not tried yet

/// <summary>
/// Turns one <see cref="ConductorSnapshot"/> into the cards the page renders. Pure and
/// deterministic (time comes from the snapshot) so it can be unit tested without MT.
/// </summary>
public static class ProjectCardBuilder
{
    private const int LeftOffMaxLength = 160;
    internal const int TitleListMax = 10;

    /// <summary>
    /// Unread permission requests / questions older than this are ignored. MT only marks them
    /// read from the phone, and in remote mode the same type also covers "agent went idle", so
    /// without a cutoff the dot would stay lit for days.
    /// </summary>
    public static readonly TimeSpan QuestionMaxAge = TimeSpan.FromHours(24);

    private const string ConductorErrorPrefix = "conductor error:";
    private const string StaleViewWarning = "showing last good view";

    /// <summary>
    /// What to show when the refresh itself threw (our bug, not MT being down). After an online view
    /// the cards stay but are labelled as the last good ones, with their old UpdatedAt so they don't
    /// pretend to be fresh. After an offline view (or none) the page stays offline plus the error,
    /// rather than showing an empty online view.
    /// </summary>
    public static ConductorView RefreshFailed(ConductorView? last, string errorText, DateTime now)
    {
        // Replace earlier errors so repeated failures don't pile up.
        var warnings = (last?.Warnings ?? [])
            .Where(w => !w.StartsWith(ConductorErrorPrefix, StringComparison.Ordinal) && w != StaleViewWarning)
            .ToList();
        warnings.Add($"{ConductorErrorPrefix} {errorText}");

        if (last is { MtOnline: true })
        {
            warnings.Add(StaleViewWarning);
            return last with { Warnings = warnings };
        }
        return (last ?? new ConductorView(false, now, null, [], 0)) with { Warnings = warnings };
    }

    public static ConductorView Build(ConductorSnapshot s)
    {
        if (!s.MtOnline)
            return new ConductorView(false, s.Now, null, [], 0);

        var projects = s.Projects
            .Where(p => !string.Equals(p.Status, "Archived", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Tolerate duplicate or missing ids from the API rather than throwing.
        var tasksById = new Dictionary<string, MtTask>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in s.Tasks)
            if (!string.IsNullOrEmpty(t.Id)) tasksById.TryAdd(t.Id, t);
        var cardIds = new HashSet<string>(projects.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
        var connected = new HashSet<string>(
            (s.Terminals ?? []).Where(t => t.IsConnected).Select(t => t.Name),
            StringComparer.OrdinalIgnoreCase);

        // Unread notifications carry the agent's cwd; match it to the project whose path is
        // the longest prefix (worktrees live under the project folder).
        var notificationsByProject = new Dictionary<string, List<MtNotification>>(StringComparer.OrdinalIgnoreCase);
        var unmatched = 0;
        foreach (var n in s.Notifications ?? [])
        {
            if (!IsQuestion(n, s.Now)) continue;
            var project = MatchProjectByPath(projects, n.Cwd);
            if (project is null) { unmatched++; continue; }
            if (!notificationsByProject.TryGetValue(project.Id, out var list))
                notificationsByProject[project.Id] = list = [];
            list.Add(n);
        }

        // Inbox messages carry a taskId; map through the task list to a project.
        var escalationsByProject = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in s.Inbox ?? [])
        {
            if (!string.Equals(m.Type, "escalation", StringComparison.OrdinalIgnoreCase)) continue;
            // Escalations for tasks we don't have, or in projects without a card (archived), count as unmatched.
            if (m.TaskId is not null && tasksById.TryGetValue(m.TaskId, out var t)
                && t.ProjectId is not null && cardIds.Contains(t.ProjectId))
                escalationsByProject[t.ProjectId] = escalationsByProject.GetValueOrDefault(t.ProjectId) + 1;
            else
                unmatched++;
        }

        var cards = projects
            .Select(p => BuildCard(p, s, connected,
                notificationsByProject.GetValueOrDefault(p.Id) ?? [],
                escalationsByProject.GetValueOrDefault(p.Id)))
            .OrderByDescending(c => c.NeedsYou)
            .ThenByDescending(c => c.ActiveAgents.Count > 0)
            .ThenByDescending(c => c.IsPinned)
            .ThenBy(c => string.Equals(c.Status, "Parked", StringComparison.OrdinalIgnoreCase))
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var warnings = new List<string>(s.StaleSources ?? []);
        if (s.Inbox is null) warnings.Add("inbox unavailable");
        if (s.Notifications is null) warnings.Add("notifications unavailable");
        if (s.Terminals is null) warnings.Add("terminals unavailable");

        return new ConductorView(true, s.Now, s.RemoteMode, cards, unmatched, warnings);
    }

    private static ProjectCard BuildCard(
        MtProject p,
        ConductorSnapshot s,
        HashSet<string> connected,
        List<MtNotification> questions,
        int escalations)
    {
        var tasks = s.Tasks.Where(t => string.Equals(t.ProjectId, p.Id, StringComparison.OrdinalIgnoreCase) && !t.IsQuickTask).ToList();
        var inProgress = tasks.Where(t => t.Status == "in_progress").ToList();
        var toDoTasks = tasks.Where(t => t.Status == "todo").ToList();

        // Items reach "testing" as soon as they're coded, before review and while siblings are
        // still being coded. Only count a task's testing items once the whole task has been
        // handed over: every item is in testing or done.
        var awaiting = inProgress
            .Select(t => (Task: t, Items: MtJson.ParseChecklist(t.ChecklistJson)))
            .Where(x => x.Items.Count > 0 && x.Items.All(i => IsStatus(i, "testing") || IsStatus(i, "done") || i.Done))
            .Select(x => (x.Task.Title, Testing: x.Items.Count(i => IsStatus(i, "testing"))))
            .Where(x => x.Testing > 0)
            .ToList();
        var awaitingTest = awaiting.Sum(x => x.Testing);
        var awaitingTip = AwaitingTip(awaiting);

        var activeAgents = inProgress
            .Where(t => string.Equals(t.SubStatus, "active", StringComparison.OrdinalIgnoreCase)
                        && t.Assignee is not null && connected.Contains(t.Assignee))
            .Select(t => t.Assignee!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var questionCount = questions.Count + escalations;
        // Text and hover detail travel together so the two lists on the card can't misalign.
        var reasonPairs = new List<(string Text, string Detail)>();
        if (questions.Count > 0)
            reasonPairs.Add((Plural(questions.Count, "recent question or permission request", "recent questions or permission requests"), ""));
        if (escalations > 0)
            reasonPairs.Add((Plural(escalations, "escalation", "escalations"), ""));
        if (awaitingTest > 0)
            reasonPairs.Add((Plural(awaitingTest, "item ready for you to test", "items ready for you to test"), string.Join("\n", awaitingTip)));
        var reasons = reasonPairs.Select(r => r.Text).ToList();
        var reasonDetails = reasonPairs.Select(r => r.Detail).ToList();

        var (leftOff, leftOffAt) = LeftOff(p, s, inProgress);

        IReadOnlyList<PrBadge>? prs = null;
        var otherPrs = 0;
        if (s.PullRequests.TryGetValue(p.Id, out var prList) && prList is not null)
        {
            // Without a known login nobody can be called "you": list them all, neutrally.
            // PRs with no author are treated as yours.
            bool IsMine(PullRequestInfo pr) =>
                s.GitHubLogin is null || pr.Author is null
                || string.Equals(pr.Author, s.GitHubLogin, StringComparison.OrdinalIgnoreCase);

            prs = prList.Where(IsMine)
                .Select(pr => new PrBadge(pr.Number, pr.Title, pr.Url, pr.ChecksState, pr.IsDraft, pr.ReviewDecision))
                .ToList();
            otherPrs = prList.Count - prs.Count;
        }

        var openHint = activeAgents.Count > 0
            ? $"Probably open: {string.Join(", ", activeAgents)} {(activeAgents.Count == 1 ? "is" : "are")} working on an active task here. MT can't yet say which window."
            : "MT doesn't report which window a project is in yet.";

        return new ProjectCard(
            p.Id,
            p.Name,
            Pm: string.IsNullOrWhiteSpace(p.TeamLead) ? null : p.TeamLead,
            Status: p.Status ?? "Active",
            p.IsPinned,
            NeedsYou: reasons.Count > 0,
            reasons,
            leftOff,
            leftOffAt is null ? null : RelativeAge(leftOffAt.Value, s.Now),
            inProgress.Count,
            toDoTasks.Count,
            awaitingTest,
            questionCount,
            prs,
            otherPrs,
            HasGitHubRepo: !string.IsNullOrWhiteSpace(p.GitRepoUrl),
            activeAgents,
            openHint,
            PrOwnerKnown: s.GitHubLogin is not null,
            InProgressTitles: CapTitles(inProgress.Select(t => t.Title)),
            ToDoTitles: CapTitles(toDoTasks.Select(t => t.Title)),
            AwaitingTestTip: awaitingTip,
            ReasonDetails: reasonDetails,
            PrRepo: GhPrClient.ParseOwnerRepo(p.GitRepoUrl),
            GitHubLogin: s.GitHubLogin,
            OthersKnown: s.PrOthersUnknown?.Contains(p.Id) != true);
    }

    /// <summary>
    /// "Left off": the most recently updated in-progress task with the first line of its
    /// continuation notes (written by agents precisely to say where they stopped). Without one,
    /// the latest session summary, cleaned of its auto-generated stats.
    /// </summary>
    private static (string? Text, DateTime? At) LeftOff(MtProject p, ConductorSnapshot s, List<MtTask> inProgress)
    {
        (string Text, DateTime? At)? fromTask = null;
        var task = inProgress.OrderByDescending(t => t.UpdatedAt ?? DateTime.MinValue).FirstOrDefault();
        if (task is not null)
        {
            var note = FirstLine(task.ContinuationNotes);
            fromTask = (Truncate(note is null ? task.Title : $"{task.Title}: {note}"), task.UpdatedAt);
        }

        (string Text, DateTime? At)? fromSession = null;
        if (s.LatestSessions.TryGetValue(p.Id, out var latest) && latest is not null)
        {
            var summary = CleanSessionSummary(FirstLine(latest.Summary ?? latest.Session?.Summary));
            if (summary is not null)
            {
                var at = ParseTime(latest.Session?.EndedAt) ?? ParseTime(latest.Session?.StartedAt);
                var who = latest.Session?.AgentName;
                fromSession = (Truncate(who is null ? summary : $"{who}: {summary}"), at);
            }
        }

        // Continuation notes are written to say where work stopped, so they win unless a
        // session is clearly newer (more than an hour after the task was last touched).
        if (fromTask is { } t && fromSession is { } se
            && se.At is { } sessionAt && t.At is { } taskAt && sessionAt > taskAt + TimeSpan.FromHours(1))
            return se;
        if (fromTask is { } onlyTask) return onlyTask;
        if (fromSession is { } onlySession) return onlySession;
        return (null, null);
    }

    internal const string AwaitingTestHeader = "Tasks with items ready to test:";

    /// <summary>
    /// Hover text for the ready-to-test count: it lists tasks (not items), so it says so, and the
    /// overflow line carries the item total too: "+3 more tasks, 7 items".
    /// </summary>
    internal static IReadOnlyList<string> AwaitingTip(IReadOnlyList<(string Title, int Testing)> awaiting)
    {
        string Name(string t) => string.IsNullOrWhiteSpace(t) ? "(untitled)" : t.Trim();
        var lines = new List<string> { AwaitingTestHeader };
        lines.AddRange(awaiting.Take(TitleListMax)
            .Select(x => x.Testing == 1 ? Name(x.Title) : $"{Name(x.Title)} ({x.Testing} items)"));
        if (awaiting.Count > TitleListMax)
        {
            var rest = awaiting.Skip(TitleListMax).ToList();
            var items = rest.Sum(x => x.Testing);
            lines.Add($"+{Plural(rest.Count, "more task", "more tasks")}, {Plural(items, "item", "items")}");
        }
        return lines;
    }

    /// <summary>The first <see cref="TitleListMax"/> titles, then "+N more".</summary>
    internal static IReadOnlyList<string> CapTitles(IEnumerable<string> titles)
    {
        var all = titles.Select(t => string.IsNullOrWhiteSpace(t) ? "(untitled)" : t.Trim()).ToList();
        if (all.Count <= TitleListMax) return all;
        var capped = all.Take(TitleListMax).ToList();
        capped.Add($"+{all.Count - TitleListMax} more");
        return capped;
    }

    private static bool IsStatus(MtChecklistItem i, string status) =>
        string.Equals(i.Status, status, StringComparison.OrdinalIgnoreCase);

    // Session summaries end with stats like " — 959 edits, 173 reads, 71 searches"; drop them.
    private static readonly System.Text.RegularExpressions.Regex SessionStats =
        new(@"\s+[—-]+\s+\d+\s+\w+(,\s*\d+\s+\w+)*\s*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static string? CleanSessionSummary(string? summary)
    {
        if (summary is null) return null;
        var cleaned = SessionStats.Replace(summary, "").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    // MT folds permission prompts, questions and idle prompts into permission_request; it only
    // records them while remote mode is on, so this signal is incomplete at the desk.
    internal static bool IsQuestion(MtNotification n, DateTime now) =>
        string.Equals(n.NotificationType, "permission_request", StringComparison.OrdinalIgnoreCase)
        && n.CreatedAt is { } at && now - at <= QuestionMaxAge;

    internal static MtProject? MatchProjectByPath(IEnumerable<MtProject> projects, string? cwd)
    {
        var dir = NormalizePath(cwd);
        if (dir is null) return null;

        MtProject? best = null;
        var bestLength = -1;
        foreach (var p in projects)
        {
            foreach (var candidate in new[] { p.Path, p.SourcePath })
            {
                var root = NormalizePath(candidate);
                if (root is null) continue;
                var matches = dir.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || dir.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
                if (matches && root.Length > bestLength)
                {
                    best = p;
                    bestLength = root.Length;
                }
            }
        }
        return best;
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return path.Trim().Replace('/', '\\').TrimEnd('\\');
    }

    internal static string RelativeAge(DateTime then, DateTime now)
    {
        var span = now - then;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1)) return Plural((int)span.TotalHours, "hour ago", "hours ago");
        if (span < TimeSpan.FromDays(2)) return "yesterday";
        return $"{(int)span.TotalDays} days ago";
    }

    private static DateTime? ParseTime(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t.ToLocalTime()
            : null;

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('#', '-', '*', ' ').Trim();
            if (line.Length > 0) return line;
        }
        return null;
    }

    private static string Truncate(string text) =>
        text.Length <= LeftOffMaxLength ? text : text[..(LeftOffMaxLength - 1)].TrimEnd() + "…";

    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
