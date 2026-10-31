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
    string? GitHubLogin = null);   // gh's logged-in user; PRs by others are counted, not listed

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
    int UnmatchedQuestions);

/// <summary>
/// Turns one <see cref="ConductorSnapshot"/> into the cards the page renders. Pure and
/// deterministic (time comes from the snapshot) so it can be unit tested without MT.
/// </summary>
public static class ProjectCardBuilder
{
    private const int LeftOffMaxLength = 160;

    public static ConductorView Build(ConductorSnapshot s)
    {
        if (!s.MtOnline)
            return new ConductorView(false, s.Now, null, [], 0);

        var projects = s.Projects
            .Where(p => !string.Equals(p.Status, "Archived", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var tasksById = s.Tasks.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var connected = new HashSet<string>(
            (s.Terminals ?? []).Where(t => t.IsConnected).Select(t => t.Name),
            StringComparer.OrdinalIgnoreCase);

        // Unread notifications carry the agent's cwd; match it to the project whose path is
        // the longest prefix (worktrees live under the project folder).
        var notificationsByProject = new Dictionary<string, List<MtNotification>>(StringComparer.OrdinalIgnoreCase);
        var unmatched = 0;
        foreach (var n in s.Notifications ?? [])
        {
            if (!IsQuestion(n)) continue;
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
            if (m.TaskId is not null && tasksById.TryGetValue(m.TaskId, out var t) && t.ProjectId is not null)
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

        return new ConductorView(true, s.Now, s.RemoteMode, cards, unmatched);
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

        var awaitingTest = inProgress
            .SelectMany(t => MtJson.ParseChecklist(t.ChecklistJson))
            .Count(i => string.Equals(i.Status, "testing", StringComparison.OrdinalIgnoreCase));

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
            reasons.Add(Plural(questions.Count, "question or permission request", "questions or permission requests"));
        if (escalations > 0)
            reasons.Add(Plural(escalations, "escalation", "escalations"));
        if (awaitingTest > 0)
            reasons.Add(Plural(awaitingTest, "item waiting for you to test", "items waiting for you to test"));

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
        var task = inProgress.OrderByDescending(t => t.UpdatedAt ?? DateTime.MinValue).FirstOrDefault();
        if (task is not null)
        {
            var note = FirstLine(task.ContinuationNotes);
            return (Truncate(note is null ? task.Title : $"{task.Title}: {note}"), task.UpdatedAt);
        }

        if (s.LatestSessions.TryGetValue(p.Id, out var latest) && latest is not null)
        {
            var summary = CleanSessionSummary(FirstLine(latest.Summary ?? latest.Session?.Summary));
            if (summary is not null)
            {
                var at = ParseTime(latest.Session?.EndedAt) ?? ParseTime(latest.Session?.StartedAt);
                var who = latest.Session?.AgentName;
                return (Truncate(who is null ? summary : $"{who}: {summary}"), at);
            }
        }

        return (null, null);
    }

    // Session summaries end with stats like " — 959 edits, 173 reads, 71 searches"; drop them.
    private static readonly System.Text.RegularExpressions.Regex SessionStats =
        new(@"\s+[—-]+\s+\d+\s+\w+(,\s*\d+\s+\w+)*\s*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static string? CleanSessionSummary(string? summary)
    {
        if (summary is null) return null;
        var cleaned = SessionStats.Replace(summary, "").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    internal static bool IsQuestion(MtNotification n) =>
        string.Equals(n.NotificationType, "permission_request", StringComparison.OrdinalIgnoreCase)
        || string.Equals(n.NotificationType, "ask_owner", StringComparison.OrdinalIgnoreCase)
        || string.Equals(n.Title, "ask_user_question", StringComparison.OrdinalIgnoreCase);

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
