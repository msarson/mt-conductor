using MtConductor.Api;
using MtConductor.Cards;
using MtConductor.Github;

namespace MtConductor.Tests;

public class ProjectCardBuilderTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 14, 0, 0, DateTimeKind.Local);

    private static MtProject Project(string id, string name, string path, string? lead = null,
        string status = "Active", bool pinned = false, string? repo = null) =>
        new(id, name, path, null, status, lead, repo, pinned, null, null);

    private static MtTask Task(string id, string projectId, string status, string? checklist = null,
        string? assignee = null, string? sub = null, DateTime? updated = null, string? notes = null) =>
        new(id, $"Task {id}", status, sub, assignee, projectId, "normal", updated, checklist, notes, false);

    private static ConductorSnapshot Snapshot(
        IReadOnlyList<MtProject> projects,
        IReadOnlyList<MtTask>? tasks = null,
        IReadOnlyList<MtInboxMessage>? inbox = null,
        IReadOnlyList<MtNotification>? notifications = null,
        IReadOnlyList<MtTerminal>? terminals = null,
        Dictionary<string, MtSessionLatest?>? sessions = null,
        Dictionary<string, IReadOnlyList<PullRequestInfo>?>? prs = null) =>
        new(true, projects, tasks ?? [], inbox, notifications, terminals,
            sessions ?? [], prs ?? [], false, Now);

    [Fact]
    public void Offline_gives_no_cards()
    {
        var view = ProjectCardBuilder.Build(new ConductorSnapshot(false, [], [], null, null, null, new Dictionary<string, MtSessionLatest?>(), new Dictionary<string, IReadOnlyList<PullRequestInfo>?>(), null, Now));
        Assert.False(view.MtOnline);
        Assert.Empty(view.Cards);
    }

    [Fact]
    public void Archived_projects_are_hidden()
    {
        var view = ProjectCardBuilder.Build(Snapshot([
            Project("a", "Alpha", @"C:\a"),
            Project("b", "Beta", @"C:\b", status: "Archived"),
        ]));
        Assert.Equal(["Alpha"], view.Cards.Select(c => c.Name));
    }

    [Fact]
    public void Counts_tasks_and_checklist_items_in_testing()
    {
        var view = ProjectCardBuilder.Build(Snapshot(
            [Project("p", "MultiTerminal", @"F:\github\MultiTerminal", lead: "Bob")],
            [
                Task("1", "p", "in_progress", """[{"item":"x","status":"testing"},{"item":"y","status":"coding"}]"""),
                Task("2", "p", "in_progress", """[{"item":"z","status":"testing"}]"""),
                Task("3", "p", "todo"),
                Task("4", "p", "todo"),
                Task("5", "other", "todo"),
            ]));

        var card = Assert.Single(view.Cards);
        Assert.Equal("Bob", card.Pm);
        Assert.Equal(2, card.InProgress);
        Assert.Equal(2, card.ToDo);
        Assert.Equal(2, card.AwaitingTest);
        Assert.True(card.NeedsYou);
        Assert.Contains("2 items waiting for you to test", card.NeedsYouReasons);
    }

    [Fact]
    public void Quiet_project_does_not_need_you()
    {
        var view = ProjectCardBuilder.Build(Snapshot(
            [Project("p", "Quiet", @"C:\q")],
            [Task("1", "p", "in_progress", """[{"item":"x","status":"coding"}]""")]));
        var card = Assert.Single(view.Cards);
        Assert.False(card.NeedsYou);
        Assert.Empty(card.NeedsYouReasons);
    }

    [Fact]
    public void Notifications_match_project_by_longest_path_prefix_including_worktrees()
    {
        MtNotification Q(string cwd) => new(Guid.NewGuid().ToString(), "permission_request", "ask_user_question", "?", "Bob", cwd, Now);

        var view = ProjectCardBuilder.Build(Snapshot(
            [
                Project("mt", "MultiTerminal", @"F:\github\MultiTerminal"),
                Project("cond", "Conductor", @"f:\github\mt-conductor"),
                Project("nested", "Nested", @"F:\github\MultiTerminal\Tools\Sub"),
            ],
            notifications:
            [
                Q(@"F:\github\MultiTerminal\.claude\worktrees\39c8218d"),
                Q(@"F:/github/mt-conductor"),
                Q(@"F:\github\MultiTerminal\Tools\Sub\x"),
                Q(@"F:\github\MultiTerminalOther"),  // sibling folder with a shared prefix: no match
                Q(@"D:\elsewhere"),
            ]));

        Assert.Equal(1, view.Cards.Single(c => c.Id == "mt").Questions);
        Assert.Equal(1, view.Cards.Single(c => c.Id == "cond").Questions);
        Assert.Equal(1, view.Cards.Single(c => c.Id == "nested").Questions);
        Assert.Equal(2, view.UnmatchedQuestions);
    }

    [Fact]
    public void Informational_notifications_are_not_questions()
    {
        var view = ProjectCardBuilder.Build(Snapshot(
            [Project("p", "P", @"C:\p")],
            notifications: [new("n1", "task_complete", "done", "finished", "Bob", @"C:\p", Now)]));
        Assert.Equal(0, view.Cards.Single().Questions);
        Assert.False(view.Cards.Single().NeedsYou);
    }

    [Fact]
    public void Escalations_map_through_tasks_to_projects()
    {
        var view = ProjectCardBuilder.Build(Snapshot(
            [Project("p", "P", @"C:\p")],
            [Task("t1", "p", "in_progress")],
            inbox:
            [
                new("m1", "t1", "T", "escalation", "help", Now, "Diana"),
                new("m2", "t1", "T", "ready_for_testing", "ready", Now, "Diana"),  // counted via checklist, not inbox
                new("m3", "gone", "T", "escalation", "help", Now, "Diana"),
            ]));

        var card = view.Cards.Single();
        Assert.Equal(1, card.Questions);
        Assert.Contains("1 escalation", card.NeedsYouReasons);
        Assert.Equal(1, view.UnmatchedQuestions);
    }

    [Fact]
    public void Active_agents_need_a_connected_terminal_and_an_active_task()
    {
        var view = ProjectCardBuilder.Build(Snapshot(
            [Project("p", "P", @"C:\p")],
            [
                Task("1", "p", "in_progress", assignee: "Bob", sub: "active"),
                Task("2", "p", "in_progress", assignee: "Diana", sub: "paused"),
                Task("3", "p", "in_progress", assignee: "Alice", sub: "active"),
            ],
            terminals:
            [
                new("t1", "Bob", true, "d1", Now),
                new("t2", "Diana", true, "d2", Now),
                new("t3", "Alice", false, "d3", Now),
            ]));

        var card = view.Cards.Single();
        Assert.Equal(["Bob"], card.ActiveAgents);
        Assert.Contains("Bob", card.OpenHint);
    }

    [Fact]
    public void Left_off_uses_session_summary_when_no_task_is_in_progress()
    {
        var view = ProjectCardBuilder.Build(Snapshot(
            [Project("p", "P", @"C:\p")],
            [Task("1", "p", "todo", updated: Now.AddHours(-1), notes: "notes")],
            sessions: new()
            {
                ["p"] = new MtSessionLatest(
                    new MtSession("s", null, "Bob", null, "2026-10-29T10:00:00", "2026-10-29T12:00:00", @"C:\p"),
                    "## Opened PR #82, waiting on review\nmore detail"),
            }));

        var card = view.Cards.Single();
        Assert.Equal("Bob: Opened PR #82, waiting on review", card.LeftOff);
        Assert.Equal("2 days ago", card.LeftOffAge);
    }

    [Fact]
    public void Left_off_prefers_latest_in_progress_task_continuation_notes()
    {
        var view = ProjectCardBuilder.Build(Snapshot(
            [Project("p", "P", @"C:\p")],
            [
                Task("old", "p", "in_progress", updated: Now.AddDays(-3), notes: "old notes"),
                Task("new", "p", "in_progress", updated: Now.AddMinutes(-5), notes: "\n- Item 2 in testing, next item 3\n"),
            ],
            sessions: new()
            {
                ["p"] = new MtSessionLatest(new MtSession("s", null, "Bob", null, null, "2026-10-31T13:59:00", null), "session text"),
            }));

        var card = view.Cards.Single();
        Assert.Equal("Task new: Item 2 in testing, next item 3", card.LeftOff);
        Assert.Equal("5 min ago", card.LeftOffAge);
    }

    [Theory]
    [InlineData("Opened PR #82 — 959 edits, 173 reads, 71 searches", "Opened PR #82")]
    [InlineData("Fixture tests — 15 edits, 8 reads", "Fixture tests")]
    [InlineData("Plain summary", "Plain summary")]
    [InlineData("Range 1-2 kept", "Range 1-2 kept")]
    public void Session_summary_stats_are_stripped(string raw, string expected)
    {
        Assert.Equal(expected, ProjectCardBuilder.CleanSessionSummary(raw));
    }

    [Fact]
    public void Long_left_off_is_truncated()
    {
        var view = ProjectCardBuilder.Build(Snapshot(
            [Project("p", "P", @"C:\p")],
            [Task("1", "p", "in_progress", updated: Now, notes: new string('x', 400))]));
        var leftOff = view.Cards.Single().LeftOff!;
        Assert.Equal(160, leftOff.Length);
        Assert.EndsWith("…", leftOff);
    }

    [Fact]
    public void Pull_requests_known_unknown()
    {
        var view = ProjectCardBuilder.Build(Snapshot(
            [
                Project("a", "A", @"C:\a", repo: "https://github.com/o/a"),
                Project("b", "B", @"C:\b", repo: "https://github.com/o/b"),
                Project("c", "C", @"C:\c"),
            ],
            prs: new()
            {
                ["a"] = [new PullRequestInfo(81, "t", "u", false, "success", "APPROVED")],
                ["b"] = null,
            }));

        var a = view.Cards.Single(c => c.Id == "a");
        Assert.Equal(81, Assert.Single(a.PullRequests!).Number);
        Assert.Null(view.Cards.Single(c => c.Id == "b").PullRequests);
        Assert.True(view.Cards.Single(c => c.Id == "b").HasGitHubRepo);
        Assert.False(view.Cards.Single(c => c.Id == "c").HasGitHubRepo);
    }

    [Fact]
    public void Pull_requests_by_others_are_counted_not_listed()
    {
        var snapshot = Snapshot(
            [Project("a", "A", @"C:\a", repo: "https://github.com/o/a")],
            prs: new()
            {
                ["a"] =
                [
                    new PullRequestInfo(96, "mine", "u", false, "success", null, "MSarson"),
                    new PullRequestInfo(88, "theirs", "u", false, "success", null, "robertorenz"),
                    new PullRequestInfo(90, "no author", "u", false, "success", null, null),
                ],
            }) with { GitHubLogin = "msarson" };

        var card = ProjectCardBuilder.Build(snapshot).Cards.Single();

        Assert.Equal([96, 90], card.PullRequests!.Select(p => p.Number));
        Assert.Equal(1, card.OtherPullRequests);
    }

    [Fact]
    public void Unknown_login_lists_every_pull_request()
    {
        var card = ProjectCardBuilder.Build(Snapshot(
            [Project("a", "A", @"C:\a", repo: "https://github.com/o/a")],
            prs: new() { ["a"] = [new PullRequestInfo(88, "theirs", "u", false, "success", null, "robertorenz")] }))
            .Cards.Single();

        Assert.Single(card.PullRequests!);
        Assert.Equal(0, card.OtherPullRequests);
    }

    [Fact]
    public void Sorting_puts_needs_you_then_active_then_pinned_then_parked_last()
    {
        var view = ProjectCardBuilder.Build(Snapshot(
            [
                Project("parked", "Aaa parked", @"C:\1", status: "Parked"),
                Project("plain", "Bbb plain", @"C:\2"),
                Project("pinned", "Ccc pinned", @"C:\3", pinned: true),
                Project("active", "Ddd active", @"C:\4"),
                Project("needs", "Eee needs", @"C:\5"),
            ],
            [
                Task("t1", "active", "in_progress", assignee: "Bob", sub: "active"),
                Task("t2", "needs", "in_progress", """[{"item":"x","status":"testing"}]"""),
            ],
            terminals: [new("t", "Bob", true, "d", Now)]));

        Assert.Equal(["needs", "active", "pinned", "plain", "parked"], view.Cards.Select(c => c.Id));
    }

    [Theory]
    [InlineData(0.5, "just now")]
    [InlineData(5, "5 min ago")]
    [InlineData(60, "1 hour ago")]
    [InlineData(180, "3 hours ago")]
    [InlineData(60 * 30, "yesterday")]
    [InlineData(60 * 24 * 4, "4 days ago")]
    public void Relative_age(double minutesAgo, string expected)
    {
        Assert.Equal(expected, ProjectCardBuilder.RelativeAge(Now.AddMinutes(-minutesAgo), Now));
    }
}
