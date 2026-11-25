using System.Net;
using System.Text;
using MtConductor.Api;
using MtConductor.Cards;
using MtConductor.Github;

namespace MtConductor.Tests;

public class ConductorPollerTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 14, 0, 0, DateTimeKind.Local);

    private sealed class FakeMt(Func<string, (HttpStatusCode, string)?> route) : FakeHandler(request =>
    {
        var r = route(request.RequestUri!.PathAndQuery);
        if (r is null) throw new HttpRequestException("connection refused");
        return new HttpResponseMessage(r.Value.Item1)
        {
            Content = new StringContent(r.Value.Item2, Encoding.UTF8, "application/json"),
        };
    });

    private const string MyPr = """{"number":82,"title":"t","url":"https://github.com/o/r/pull/82","isDraft":false,"statusCheckRollup":[],"reviewDecision":"","author":{"login":"msarson"}}""";
    private const string OtherPr = """{"number":88,"title":"x","url":"https://github.com/o/r/pull/88","isDraft":false,"statusCheckRollup":[],"reviewDecision":"","author":{"login":"someone"}}""";

    /// <param name="generalPrs">What the capped general list returns (JSON array body).</param>
    /// <param name="login">gh's logged-in user; null = gh can't say.</param>
    private static (ConductorPoller Poller, FakeMt Mt, List<string> GhCalls) Create(
        Func<string, (HttpStatusCode, string)?> route, string? originUrl = null,
        string? generalPrs = null, string? login = "msarson")
    {
        var mt = new FakeMt(route);
        var ghCalls = new List<string>();
        var gh = new GhPrClient(TimeSpan.FromMinutes(2), (repo, mine, _) =>
        {
            lock (ghCalls) ghCalls.Add(repo);
            return Task.FromResult((0, mine ? $"[{MyPr}]" : generalPrs ?? $"[{MyPr},{OtherPr}]"));
        }, runGhArgs: (_, _) => Task.FromResult(login is null ? (1, "") : (0, login + "\n")));
        var client = new MtApiClient(new HttpClient(mt) { BaseAddress = new Uri(MtApiClient.DefaultBaseAddress) });
        var git = new GitRemoteResolver(TimeSpan.FromMinutes(10),
            originUrl is null ? (_, _) => Task.FromResult((128, "")) : FakeGit.Remotes(_ => (0, originUrl + "\n")));
        return (new ConductorPoller(client, gh, git, () => Now), mt, ghCalls);
    }

    private static (HttpStatusCode, string)? HealthyMt(string pathAndQuery) => pathAndQuery switch
    {
        "/api/health" => (HttpStatusCode.OK, """{"service":"multiterminal-rest-api","pid":1}"""),
        "/api/owner-profile" => (HttpStatusCode.OK, """{"configured":true,"fullName":"Mark Sarson"}"""),
        "/api/projects" => (HttpStatusCode.OK, """
            {"count":1,"projects":[{"id":"mt","name":"MultiTerminal","path":"F:\\github\\MultiTerminal","status":"Active",
              "teamLead":"Bob","gitRepoUrl":"https://github.com/o/r"}]}
            """),
        "/api/tasks?status=in_progress" => (HttpStatusCode.OK, """
            [{"id":"t1","title":"Thing","status":"in_progress","subStatus":"active","assignee":"Bob","projectId":"mt",
              "checklistJson":"[{\"item\":\"a\",\"status\":\"testing\"}]","isQuickTask":false}]
            """),
        "/api/tasks?status=todo" => (HttpStatusCode.OK, "[]"),
        "/api/tasks/inbox/Mark?unreadOnly=true&limit=200" => (HttpStatusCode.OK, """{"success":true,"messages":[]}"""),
        "/api/notifications?unreadOnly=true" => (HttpStatusCode.OK, "[]"),
        "/api/messaging/terminals" => (HttpStatusCode.OK, """[{"name":"Bob","isConnected":true}]"""),
        "/api/remote-mode" => (HttpStatusCode.OK, """{"remote_mode":false}"""),
        _ when pathAndQuery.StartsWith("/api/session-lineage/latest", StringComparison.Ordinal) => (HttpStatusCode.NotFound, ""),
        _ => (HttpStatusCode.NotFound, ""),
    };

    [Fact]
    public async Task Offline_MT_gives_offline_view()
    {
        var (poller, mt, _) = Create(_ => null);

        var view = await poller.RefreshAsync(CancellationToken.None);

        Assert.False(view.MtOnline);
        Assert.Single(mt.Requests); // only the health probe while offline
    }

    [Fact]
    public async Task Online_builds_cards_with_GETs_only_and_owner_inbox_id()
    {
        var (poller, mt, _) = Create(HealthyMt);

        var view = await poller.RefreshAsync(CancellationToken.None);

        Assert.True(view.MtOnline);
        Assert.False(view.RemoteMode);
        var card = Assert.Single(view.Cards);
        Assert.Equal("Bob", card.Pm);
        Assert.Equal(1, card.AwaitingTest);
        Assert.True(card.NeedsYou);
        Assert.Equal(["Bob"], card.ActiveAgents);
        Assert.All(mt.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Contains(mt.Requests, r => r.RequestUri!.AbsolutePath == "/api/tasks/inbox/Mark");
    }

    [Fact]
    public async Task Pull_requests_arrive_on_a_later_refresh()
    {
        var (poller, _, ghCalls) = Create(HealthyMt);

        await poller.RefreshAsync(CancellationToken.None);
        await poller.WaitForPrRefreshAsync();
        var view = await poller.RefreshAsync(CancellationToken.None);

        var card = view.Cards.Single();
        Assert.Equal(82, Assert.Single(card.PullRequests!).Number);   // only the viewer's PR is listed
        Assert.Equal(1, card.OtherPullRequests);
        Assert.Equal(["o/r"], ghCalls.Distinct());
    }

    [Fact]
    public async Task Project_without_gitRepoUrl_falls_back_to_folder_origin()
    {
        // The project folder must exist for the resolver to try git; use the temp folder.
        var folder = Path.GetTempPath().TrimEnd('\\').Replace("\\", "\\\\");
        (HttpStatusCode, string)? Route(string pq) => pq == "/api/projects"
            ? (HttpStatusCode.OK, $$"""{"count":1,"projects":[{"id":"x","name":"X","path":"{{folder}}","status":"Active"}]}""")
            : HealthyMt(pq);

        var (poller, _, ghCalls) = Create(Route, originUrl: "https://github.com/me/x.git");

        await poller.RefreshAsync(CancellationToken.None);
        await poller.WaitForPrRefreshAsync();
        var card = (await poller.RefreshAsync(CancellationToken.None)).Cards.Single();

        Assert.True(card.HasGitHubRepo);
        Assert.NotNull(card.PullRequests);
        Assert.Contains("me/x", ghCalls);
    }

    [Fact]
    public async Task Folder_remote_wins_over_MT_repo_url_so_forks_show_upstream_PRs()
    {
        var folder = Path.GetTempPath().TrimEnd('\\').Replace("\\", "\\\\");
        (HttpStatusCode, string)? Route(string pq) => pq == "/api/projects"
            ? (HttpStatusCode.OK, $$"""{"count":1,"projects":[{"id":"x","name":"X","path":"{{folder}}","status":"Active","gitRepoUrl":"https://github.com/me/fork"}]}""")
            : HealthyMt(pq);
        var (poller, _, ghCalls) = Create(Route, originUrl: "https://github.com/up/stream.git");

        await poller.RefreshAsync(CancellationToken.None);
        await poller.WaitForPrRefreshAsync();
        await poller.RefreshAsync(CancellationToken.None);

        Assert.Equal(["up/stream"], ghCalls.Distinct());
    }

    [Fact]
    public async Task MT_repo_url_is_used_when_the_folder_has_no_usable_remote()
    {
        var (poller, _, ghCalls) = Create(HealthyMt);   // git answers 128 (no remote)

        await poller.RefreshAsync(CancellationToken.None);
        await poller.WaitForPrRefreshAsync();

        Assert.Equal(["o/r"], ghCalls.Distinct());
    }

    [Fact]
    public async Task Own_PRs_come_from_the_author_query_even_when_the_capped_list_misses_them()
    {
        var (poller, _, _) = Create(HealthyMt, generalPrs: $"[{OtherPr}]");   // PR 82 fell off the 30-item list

        await poller.RefreshAsync(CancellationToken.None);
        await poller.WaitForPrRefreshAsync();
        var card = (await poller.RefreshAsync(CancellationToken.None)).Cards.Single();

        Assert.Equal([82], card.PullRequests!.Select(p => p.Number));
        Assert.Equal(1, card.OtherPullRequests);
        Assert.True(card.PrOwnerKnown);
    }

    [Fact]
    public async Task Unknown_gh_login_lists_PRs_neutrally()
    {
        var (poller, _, _) = Create(HealthyMt, login: null);

        await poller.RefreshAsync(CancellationToken.None);
        await poller.WaitForPrRefreshAsync();
        var card = (await poller.RefreshAsync(CancellationToken.None)).Cards.Single();

        Assert.False(card.PrOwnerKnown);
        Assert.Equal(2, card.PullRequests!.Count);
        Assert.Equal(0, card.OtherPullRequests);
    }

    [Fact]
    public async Task A_faulting_PR_refresh_warns_in_the_view_and_recovers_next_time()
    {
        var fail = true;
        var mt = new FakeMt(HealthyMt);
        var gh = new GhPrClient(TimeSpan.Zero, (_, _, _) =>
            fail ? throw new NotSupportedException("boom") : Task.FromResult((0, $"[{MyPr}]")),
            runGhArgs: (_, _) => Task.FromResult((0, "msarson\n")));
        var poller = new ConductorPoller(
            new MtApiClient(new HttpClient(mt) { BaseAddress = new Uri(MtApiClient.DefaultBaseAddress) }),
            gh, new GitRemoteResolver(TimeSpan.FromMinutes(10), (_, _) => Task.FromResult((128, ""))), () => Now);

        await poller.RefreshAsync(CancellationToken.None);
        await poller.WaitForPrRefreshAsync();
        Assert.Contains("PRs unavailable", (await poller.RefreshAsync(CancellationToken.None)).Warnings!);

        fail = false;
        await poller.WaitForPrRefreshAsync();
        await poller.RefreshAsync(CancellationToken.None);   // starts the next background refresh
        await poller.WaitForPrRefreshAsync();
        Assert.DoesNotContain("PRs unavailable", (await poller.RefreshAsync(CancellationToken.None)).Warnings!);
    }

    [Fact]
    public async Task When_only_the_own_PR_query_works_others_are_marked_unknown()
    {
        var mt = new FakeMt(HealthyMt);
        var gh = new GhPrClient(TimeSpan.Zero, (_, mine, _) => Task.FromResult(mine ? (0, $"[{MyPr}]") : (1, "")),
            runGhArgs: (_, _) => Task.FromResult((0, "msarson\n")));
        var poller = new ConductorPoller(
            new MtApiClient(new HttpClient(mt) { BaseAddress = new Uri(MtApiClient.DefaultBaseAddress) }),
            gh, new GitRemoteResolver(TimeSpan.FromMinutes(10), (_, _) => Task.FromResult((128, ""))), () => Now);

        await poller.RefreshAsync(CancellationToken.None);
        await poller.WaitForPrRefreshAsync();
        var card = (await poller.RefreshAsync(CancellationToken.None)).Cards.Single();

        Assert.Equal([82], card.PullRequests!.Select(p => p.Number));
        Assert.False(card.OthersKnown);
        Assert.Equal(0, card.OtherPullRequests);
    }

    [Fact]
    public async Task PR_lookups_run_a_few_projects_at_a_time()
    {
        var folder = Path.GetTempPath().TrimEnd('\\').Replace("\\", "\\\\");
        var projects = string.Join(",", Enumerable.Range(1, 12).Select(i =>
            $$"""{"id":"p{{i}}","name":"P{{i}}","path":"{{folder}}","status":"Active","gitRepoUrl":"https://github.com/o/r{{i}}"}"""));
        (HttpStatusCode, string)? Route(string pq) => pq == "/api/projects"
            ? (HttpStatusCode.OK, $$"""{"count":12,"projects":[{{projects}}]}""")
            : HealthyMt(pq);
        var running = 0;
        var peak = 0;
        var gh = new GhPrClient(TimeSpan.Zero, async (_, _, _) =>
        {
            var now = Interlocked.Increment(ref running);
            int seen;
            while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
            await Task.Delay(30);
            Interlocked.Decrement(ref running);
            return (0, "[]");
        }, runGhArgs: (_, _) => Task.FromResult((0, "msarson\n")));
        var poller = new ConductorPoller(
            new MtApiClient(new HttpClient(new FakeMt(Route)) { BaseAddress = new Uri(MtApiClient.DefaultBaseAddress) }),
            gh, new GitRemoteResolver(TimeSpan.FromMinutes(10), (_, _) => Task.FromResult((128, ""))), () => Now);

        await poller.RefreshAsync(CancellationToken.None);
        await poller.WaitForPrRefreshAsync();

        Assert.InRange(peak, 1, ConductorPoller.MaxConcurrentPrLookups);
    }

    [Fact]
    public async Task Missing_gh_shows_as_a_warning()
    {
        var mt = new FakeMt(HealthyMt);
        var gh = new GhPrClient(TimeSpan.Zero, (_, _, _) => throw new ToolNotFoundException("gh"),
            runGhArgs: (_, _) => throw new ToolNotFoundException("gh"));
        var poller = new ConductorPoller(
            new MtApiClient(new HttpClient(mt) { BaseAddress = new Uri(MtApiClient.DefaultBaseAddress) }),
            gh, new GitRemoteResolver(TimeSpan.FromMinutes(10), (_, _) => Task.FromResult((128, ""))), () => Now);

        await poller.RefreshAsync(CancellationToken.None);
        await poller.WaitForPrRefreshAsync();
        var view = await poller.RefreshAsync(CancellationToken.None);

        Assert.Contains("gh not found on PATH", view.Warnings!);
        Assert.Null(view.Cards.Single().PullRequests);
    }

    [Fact]
    public void MergePrs_keeps_one_entry_per_number_and_null_only_when_both_failed()
    {
        PullRequestInfo Pr(int n, string? author) => new(n, "t", "u", false, "none", null, author);

        var merged = ConductorPoller.MergePrs([Pr(2, "x"), Pr(1, "me")], [Pr(1, null)], "me")!;

        Assert.Equal([1, 2], merged.Select(p => p.Number));
        Assert.Equal("me", merged[0].Author);
        Assert.Equal([1], ConductorPoller.MergePrs(null, [Pr(1, "me")], "me")!.Select(p => p.Number));
        Assert.Null(ConductorPoller.MergePrs(null, null, "me"));
    }

    [Fact]
    public void Merge_removes_duplicate_ids_keeping_the_latest_and_converts_utc()
    {
        var utc = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Unspecified); // as MT sends it
        MtTask T(string id, string status, DateTime? at) => new(id, id, status, null, null, "p", null, at, null, null, false);

        var merged = ConductorPoller.MergeTasks(
            [T("a", "in_progress", utc.AddMinutes(1))],
            [T("a", "todo", utc), T("b", "todo", utc), T("", "todo", utc)]);

        Assert.Equal(["a", "b"], merged.Select(t => t.Id));
        Assert.Equal("in_progress", merged[0].Status);
        Assert.Equal(DateTimeKind.Local, merged[1].UpdatedAt!.Value.Kind);
        Assert.Equal(DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime(), merged[1].UpdatedAt);
    }

    [Fact]
    public void Utc_to_local_handles_each_kind()
    {
        var local = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Local);
        Assert.Equal(local, ConductorPoller.UtcToLocal(local));
        Assert.Null(ConductorPoller.UtcToLocal(null));
        var utc = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(utc.ToLocalTime(), ConductorPoller.UtcToLocal(utc));
    }

    [Fact]
    public async Task Failed_task_fetch_reuses_last_good_tasks_and_warns()
    {
        var failTasks = false;
        var (poller, _, _) = Create(pq => failTasks && pq.StartsWith("/api/tasks?", StringComparison.Ordinal)
            ? (HttpStatusCode.InternalServerError, "")
            : HealthyMt(pq));

        await poller.RefreshAsync(CancellationToken.None);
        failTasks = true;
        var view = await poller.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, view.Cards.Single().InProgress);
        Assert.Contains("tasks unavailable (showing last known)", view.Warnings!);
    }

    [Fact]
    public async Task Task_failure_on_first_poll_does_not_claim_last_known()
    {
        var (poller, _, _) = Create(pq => pq.StartsWith("/api/tasks?", StringComparison.Ordinal)
            ? (HttpStatusCode.InternalServerError, "")
            : HealthyMt(pq));

        var view = await poller.RefreshAsync(CancellationToken.None);

        Assert.Contains("tasks unavailable", view.Warnings!);
        Assert.DoesNotContain("tasks unavailable (showing last known)", view.Warnings!);
    }

    [Fact]
    public async Task Remote_mode_flip_is_timestamped_but_first_sighting_is_not()
    {
        var remote = true;
        var clock = Now;
        var mt = new FakeMt(pq => pq == "/api/remote-mode"
            ? (HttpStatusCode.OK, $$"""{"remote_mode":{{(remote ? "true" : "false")}}}""")
            : HealthyMt(pq));
        var poller = new ConductorPoller(
            new MtApiClient(new HttpClient(mt) { BaseAddress = new Uri(MtApiClient.DefaultBaseAddress) }),
            new GhPrClient(TimeSpan.FromMinutes(2), (_, _, _) => Task.FromResult((1, "")), (_, _) => Task.FromResult((1, ""))),
            new GitRemoteResolver(TimeSpan.FromMinutes(10), (_, _) => Task.FromResult((128, ""))),
            () => clock);

        Assert.Null((await poller.RefreshAsync(CancellationToken.None)).RemoteModeChangedAt);

        remote = false;              // e.g. the Owner typed in an MT pane
        clock = Now.AddMinutes(3);
        var view = await poller.RefreshAsync(CancellationToken.None);

        Assert.False(view.RemoteMode);
        Assert.Equal(Now.AddMinutes(3), view.RemoteModeChangedAt);
        Assert.All(mt.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));   // still read-only
    }

    [Fact]
    public async Task Remote_mode_flip_while_MT_was_offline_is_not_stamped_with_the_reconnect_time()
    {
        var remote = true;
        var online = true;
        var clock = Now;
        var mt = new FakeMt(pq => !online ? null : pq == "/api/remote-mode"
            ? (HttpStatusCode.OK, $$"""{"remote_mode":{{(remote ? "true" : "false")}}}""")
            : HealthyMt(pq));
        var poller = new ConductorPoller(
            new MtApiClient(new HttpClient(mt) { BaseAddress = new Uri(MtApiClient.DefaultBaseAddress) }),
            new GhPrClient(TimeSpan.FromMinutes(2), (_, _, _) => Task.FromResult((1, "")), (_, _) => Task.FromResult((1, ""))),
            new GitRemoteResolver(TimeSpan.FromMinutes(10), (_, _) => Task.FromResult((128, ""))),
            () => clock);

        await poller.RefreshAsync(CancellationToken.None);
        remote = false;
        clock = Now.AddMinutes(1);
        Assert.Equal(clock, (await poller.RefreshAsync(CancellationToken.None)).RemoteModeChangedAt);   // seen flipping: dated

        online = false;
        clock = Now.AddMinutes(2);
        Assert.False((await poller.RefreshAsync(CancellationToken.None)).MtOnline);

        remote = true;                 // flipped while we couldn't see it
        online = true;
        clock = Now.AddMinutes(9);
        var back = await poller.RefreshAsync(CancellationToken.None);

        Assert.True(back.RemoteMode);
        Assert.Null(back.RemoteModeChangedAt);   // first sighting again: no made-up time
    }

    [Fact]
    public async Task Open_learns_support_and_forgets_it_when_MT_restarts()
    {
        var pid = 1;
        var openStatus = HttpStatusCode.NotFound;   // older MT: bare 404 for the unknown route
        var (poller, mt, _) = Create(pq => pq switch
        {
            "/api/health" => (HttpStatusCode.OK, $$"""{"service":"multiterminal-rest-api","pid":{{pid}}}"""),
            "/api/projects/mt/open" => (openStatus, ""),
            _ => HealthyMt(pq),
        });

        var first = await poller.RefreshAsync(CancellationToken.None);
        Assert.Null(first.OpenSupported);
        Assert.Equal(1, poller.MtPid);

        Assert.Equal(MtOpenKind.Unsupported, (await poller.OpenProjectAsync("mt", CancellationToken.None)).Kind);
        Assert.False((await poller.RefreshAsync(CancellationToken.None)).OpenSupported);

        pid = 2;                                     // MT restarted, maybe as a newer build
        Assert.Null((await poller.RefreshAsync(CancellationToken.None)).OpenSupported);

        openStatus = HttpStatusCode.Accepted;
        Assert.Equal(MtOpenKind.Failed, (await poller.OpenProjectAsync("mt", CancellationToken.None)).Kind); // empty 202 body
        Assert.True((await poller.RefreshAsync(CancellationToken.None)).OpenSupported);

        Assert.Equal(2, mt.Requests.Count(r => r.Method == HttpMethod.Post));
        Assert.All(mt.Requests.Where(r => r.Method != HttpMethod.Get),
            r => Assert.EndsWith("/open", r.RequestUri!.AbsolutePath));   // Open is the only write
    }

    [Fact]
    public async Task Capability_turns_Open_on_but_its_absence_proves_nothing()
    {
        var caps = """["ca-embedded-v1","project-open-v1"]""";
        var pid = 1;
        var (poller, _, _) = Create(pq => pq == "/api/health"
            ? (HttpStatusCode.OK, $$"""{"service":"multiterminal-rest-api","pid":{{pid}},"capabilities":{{caps}}}""")
            : HealthyMt(pq));

        Assert.True((await poller.RefreshAsync(CancellationToken.None)).OpenSupported);   // no click needed

        // A restart onto a build of #103 from before the flag: unknown again, so Open is still tried.
        pid = 2;
        caps = """["ca-embedded-v1"]""";
        Assert.Null((await poller.RefreshAsync(CancellationToken.None)).OpenSupported);
    }

    [Fact]
    public async Task Pid_is_refreshed_just_before_open()
    {
        var pid = 1;
        var (poller, _, _) = Create(pq => pq == "/api/health"
            ? (HttpStatusCode.OK, $$"""{"service":"multiterminal-rest-api","pid":{{pid}}}""")
            : HealthyMt(pq));
        await poller.RefreshAsync(CancellationToken.None);

        pid = 7;   // MT restarted since the last poll
        Assert.Equal(7, await poller.RefreshMtPidAsync(CancellationToken.None));
        Assert.Equal(7, poller.MtPid);
    }

    [Fact]
    public async Task Pid_refresh_keeps_the_last_pid_when_MT_does_not_answer()
    {
        var up = true;
        var (poller, _, _) = Create(pq => up || pq != "/api/health" ? HealthyMt(pq) : null);
        await poller.RefreshAsync(CancellationToken.None);

        up = false;
        Assert.Equal(1, await poller.RefreshMtPidAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Offline_open_does_not_change_what_we_know_about_support()
    {
        var (poller, _, _) = Create(pq => pq == "/api/projects/mt/open" ? null : HealthyMt(pq)); // null = connection refused
        await poller.RefreshAsync(CancellationToken.None);

        Assert.Equal(MtOpenKind.Offline, (await poller.OpenProjectAsync("mt", CancellationToken.None)).Kind);
        Assert.Null(poller.OpenSupported);
    }

    [Fact]
    public async Task Session_lookups_are_throttled()
    {
        var (poller, mt, _) = Create(HealthyMt);

        await poller.RefreshAsync(CancellationToken.None);
        await poller.RefreshAsync(CancellationToken.None);

        Assert.Single(mt.Requests, r => r.RequestUri!.AbsolutePath == "/api/session-lineage/latest");
    }
}
