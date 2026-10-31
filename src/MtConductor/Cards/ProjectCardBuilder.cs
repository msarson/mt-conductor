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
    IReadOnlyList<string>? StaleSources = null);   // sources that failed this tick (last good data reused)

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
    string OpenHint);

public sealed record ConductorView(
    bool MtOnline,
    DateTime UpdatedAt,
    bool? RemoteMode,
    IReadOnlyList<ProjectCard> Cards,
    int UnmatchedQuestions,
    IReadOnlyList<string>? Warnings = null);   // e.g. "inbox unavailable": counts may be incomplete

/// <summary>
/// Turns one <see cref="ConductorSnapshot"/> into the cards the page renders. Pure and
/// deterministic (time comes from the snapshot) so it can be unit tested without MT.
/// </summary>
public static class ProjectCardBuilder
{
    private const int LeftOffMaxLength = 160;

    /// <summary>
    /// Unread permission requests / questions older than this are ignored. MT only marks them
    /// read from the phone, and in remote mode the same type also covers "agent went idle", so
    /// without a cutoff the dot would stay lit for days.
    /// </summary>
    public static readonly TimeSpan QuestionMaxAge = TimeSpan.FromHours(24);

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
        var toDo = tasks.Count(t => t.Status == "todo");

        // Items reach "testing" as soon as they're coded, before review and while siblings are
        // still being coded. Only count a task's testing items once the whole task has been
        // handed over: every item is in testing or done.
        var awaitingTest = inProgress
            .Select(t => MtJson.ParseChecklist(t.ChecklistJson))
            .Where(items => items.Count > 0 && items.All(i => IsStatus(i, "testing") || IsStatus(i, "done") || i.Done))
            .Sum(items => items.Count(i => IsStatus(i, "testing")));

        var activeAgents = inProgress
            .Where(t => string.Equals(t.SubStatus, "active", StringComparison.OrdinalIgnoreCase)
                        && t.Assignee is not null && connected.Contains(t.Assignee))
            .Select(t => t.Assignee!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var questionCount = questions.Count + escalations;
        var reasons = new List<string>();
        if (questions.Count > 0)
            reasons.Add(Plural(questions.Count, "recent question or permission request", "recent questions or permission requests"));
        if (escalations > 0)
            reasons.Add(Plural(escalations, "escalation", "escalations"));
        if (awaitingTest > 0)
            reasons.Add(Plural(awaitingTest, "item ready for you to test", "items ready for you to test"));

        var (leftOff, leftOffAt) = LeftOff(p, s, inProgress);

        IReadOnlyList<PrBadge>? prs = null;
        var otherPrs = 0;
        if (s.PullRequests.TryGetValue(p.Id, out var prList) && prList is not null)
        {
            // Without a known login, or for PRs with no author, treat them as yours.
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
            toDo,
            awaitingTest,
            questionCount,
            prs,
            otherPrs,
            HasGitHubRepo: !string.IsNullOrWhiteSpace(p.GitRepoUrl),
            activeAgents,
            openHint);
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
