using System.Net;
using System.Text;
using MtConductor.Api;
using MtConductor.Cards;
using MtConductor.Github;

namespace MtConductor.Tests;

public class ConductorPollerTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 14, 0, 0, DateTimeKind.Local);

    private sealed class FakeMt(Func<string, (HttpStatusCode, string)?> route) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var r = route(request.RequestUri!.PathAndQuery);
            if (r is null) throw new HttpRequestException("connection refused");
            return Task.FromResult(new HttpResponseMessage(r.Value.Item1)
            {
                Content = new StringContent(r.Value.Item2, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (ConductorPoller Poller, FakeMt Mt, List<string> GhCalls) Create(
        Func<string, (HttpStatusCode, string)?> route, string? originUrl = null)
    {
        var mt = new FakeMt(route);
        var ghCalls = new List<string>();
        var gh = new GhPrClient(TimeSpan.FromMinutes(2), (repo, _) =>
        {
            lock (ghCalls) ghCalls.Add(repo);
            return Task.FromResult((0, """
                [{"number":82,"title":"t","url":"https://github.com/o/r/pull/82","isDraft":false,"statusCheckRollup":[],"reviewDecision":"","author":{"login":"msarson"}},
                 {"number":88,"title":"x","url":"https://github.com/o/r/pull/88","isDraft":false,"statusCheckRollup":[],"reviewDecision":"","author":{"login":"someone"}}]
                """));
        }, runGhArgs: (_, _) => Task.FromResult((0, "msarson\n")));
        var client = new MtApiClient(new HttpClient(mt) { BaseAddress = new Uri(MtApiClient.DefaultBaseAddress) });
        var git = new GitRemoteResolver(TimeSpan.FromMinutes(10),
            (_, _) => Task.FromResult(originUrl is null ? (128, "") : (0, originUrl + "\n")));
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
    public async Task Upstream_remote_wins_over_origin_for_forks()
    {
        var calls = new List<string>();
        var resolver = new GitRemoteResolver(TimeSpan.FromMinutes(1), (req, _) =>
        {
            calls.Add(req.Split('|')[0]);
            return Task.FromResult(req.StartsWith("upstream|")
                ? (0, "https://github.com/ClarionLive/multiterminal.git\n")
                : (0, "https://github.com/msarson/multiterminal.git\n"));
        });

        var url = await resolver.GetOriginUrlAsync(Path.GetTempPath(), CancellationToken.None);

        Assert.Equal("https://github.com/ClarionLive/multiterminal.git", url);
        Assert.Equal(["upstream"], calls);
    }

    [Fact]
    public async Task Origin_used_when_there_is_no_upstream()
    {
        var resolver = new GitRemoteResolver(TimeSpan.FromMinutes(1), (req, _) =>
            Task.FromResult(req.StartsWith("origin|") ? (0, "https://github.com/me/x\n") : (2, "")));
        Assert.Equal("https://github.com/me/x", await resolver.GetOriginUrlAsync(Path.GetTempPath(), CancellationToken.None));
    }

    [Fact]
    public async Task Missing_folder_or_origin_means_no_repo()
    {
        var resolver = new GitRemoteResolver(TimeSpan.FromMinutes(1), (_, _) => Task.FromResult((128, "")));
        Assert.Null(await resolver.GetOriginUrlAsync(@"Z:\definitely\not\here", CancellationToken.None));
        Assert.Null(await resolver.GetOriginUrlAsync(Path.GetTempPath(), CancellationToken.None));
        Assert.Null(await resolver.GetOriginUrlAsync(null, CancellationToken.None));
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

    [Theory]
    [InlineData(@"C:\work\proj", true)]
    [InlineData("C:/work/proj", true)]
    [InlineData(@"\\server\share\proj", false)]
    [InlineData(@"\\?\C:\proj", false)]
    [InlineData(@"\\.\pipe\x", false)]
    [InlineData(@"relative\proj", false)]
    public void Only_local_drive_paths_are_resolved(string path, bool expected)
    {
        Assert.Equal(expected, GitRemoteResolver.IsLocalPath(path));
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
            new GhPrClient(TimeSpan.FromMinutes(2), (_, _) => Task.FromResult((1, "")), (_, _) => Task.FromResult((1, ""))),
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
    public async Task Session_lookups_are_throttled()
    {
        var (poller, mt, _) = Create(HealthyMt);

        await poller.RefreshAsync(CancellationToken.None);
        await poller.RefreshAsync(CancellationToken.None);

        Assert.Single(mt.Requests, r => r.RequestUri!.AbsolutePath == "/api/session-lineage/latest");
    }
}
