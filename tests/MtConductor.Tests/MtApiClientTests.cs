using System.Net;
using System.Text;
using MtConductor.Api;

namespace MtConductor.Tests;

public class MtApiClientTests
{
    private static (MtApiClient Client, FakeHandler Handler) ClientReturning(HttpStatusCode code, string body)
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(code)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
        return (new MtApiClient(new HttpClient(handler) { BaseAddress = new Uri(MtApiClient.DefaultBaseAddress) }), handler);
    }

    [Fact]
    public async Task Offline_returns_null_instead_of_throwing()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("connection refused"));
        var client = new MtApiClient(new HttpClient(handler) { BaseAddress = new Uri(MtApiClient.DefaultBaseAddress) });

        Assert.Null(await client.GetHealthAsync(CancellationToken.None));
        Assert.Null(await client.GetProjectsAsync(CancellationToken.None));
        Assert.Null(await client.GetLatestSessionAsync("C:\\x", CancellationToken.None));
    }

    [Fact]
    public async Task Malformed_json_returns_null()
    {
        var (client, _) = ClientReturning(HttpStatusCode.OK, "<html>not json");
        Assert.Null(await client.GetHealthAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Server_error_returns_null()
    {
        var (client, _) = ClientReturning(HttpStatusCode.InternalServerError, "{}");
        Assert.Null(await client.GetProjectsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Projects_parse_camelCase_and_ignore_unknown_fields()
    {
        var (client, handler) = ClientReturning(HttpStatusCode.OK, """
            {"count":1,"projects":[{"id":"10e3b419","name":"MultiTerminal","path":"F:\\github\\MultiTerminal",
             "status":"Active","teamLead":"Bob","gitRepoUrl":"https://github.com/ClarionLive/MultiTerminal",
             "isPinned":true,"launchMode":0,"teamAgents":["Bob","Diana"],"somethingNew":{"x":1}}]}
            """);

        var projects = await client.GetProjectsAsync(CancellationToken.None);

        var p = Assert.Single(projects!);
        Assert.Equal("MultiTerminal", p.Name);
        Assert.Equal("Bob", p.TeamLead);
        Assert.Equal(@"F:\github\MultiTerminal", p.Path);
        Assert.True(p.IsPinned);
        Assert.Equal(["Bob", "Diana"], p.TeamAgents!);
        Assert.Equal("/api/projects", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
    }

    [Fact]
    public async Task Latest_session_404_means_not_found_not_failure()
    {
        var (client, handler) = ClientReturning(HttpStatusCode.NotFound, "");

        var result = await client.GetLatestSessionAsync(@"F:\github\x y", CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.Value.Found);
        Assert.Contains("projectPath=F%3A%5Cgithub%5Cx%20y", handler.Requests[0].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Notifications_parse_snake_case()
    {
        var (client, _) = ClientReturning(HttpStatusCode.OK, """
            [{"id":"67e90823","notification_type":"permission_request","title":"ask_user_question",
              "message":"Bob asked: ...","agent_name":"Bob","cwd":"F:\\github\\MultiTerminal\\.claude\\worktrees\\39c8218d",
              "created_at":"2026-10-30T18:29:11","read_at":null}]
            """);

        var n = Assert.Single((await client.GetUnreadNotificationsAsync(CancellationToken.None))!);
        Assert.Equal("permission_request", n.NotificationType);
        Assert.Equal("Bob", n.AgentName);
        Assert.StartsWith(@"F:\github\MultiTerminal", n.Cwd);
    }

    [Fact]
    public async Task Remote_mode_parses_snake_case()
    {
        var (client, _) = ClientReturning(HttpStatusCode.OK, """{"remote_mode":true}""");
        Assert.True((await client.GetRemoteModeAsync(CancellationToken.None))!.RemoteMode);
    }

    [Fact]
    public async Task Inbox_uses_user_id_and_unread_only()
    {
        var (client, handler) = ClientReturning(HttpStatusCode.OK, """
            {"success":true,"messages":[{"id":"b74a8c9e","taskId":"7f6e59a8","type":"ready_for_testing","summary":"s"}],
             "unreadCount":1,"totalCount":1}
            """);

        var msgs = await client.GetUnreadInboxAsync("Mark", CancellationToken.None);

        Assert.Equal("ready_for_testing", Assert.Single(msgs!).Type);
        Assert.Equal("/api/tasks/inbox/Mark", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("unreadOnly=true", handler.Requests[0].RequestUri!.Query);
    }

    [Theory]
    [InlineData("Mark Sarson", "Mark")]
    [InlineData("Mark", "Mark")]
    [InlineData("", "Owner")]
    [InlineData(" Mark", "Owner")] // MT's own Split(' ')[0] gives "" here, which we treat as unset
    [InlineData(null, "Owner")]
    public void Inbox_user_id_matches_MT_rule(string? fullName, string expected)
    {
        Assert.Equal(expected, MtApiClient.InboxUserIdFor(new MtOwnerProfile(true, fullName)));
    }

    [Fact]
    public void Inbox_user_id_without_profile_is_Owner()
    {
        Assert.Equal("Owner", MtApiClient.InboxUserIdFor(null));
    }

    [Fact]
    public void Checklist_parses_statuses_and_tolerates_bad_json()
    {
        var items = MtJson.ParseChecklist("""
            [{"item":"a","done":true,"status":"done"},{"item":"b","done":false,"status":"testing"},
             {"item":"legacy","done":false}]
            """);
        Assert.Equal(["done", "testing", null], items.Select(i => i.Status));

        Assert.Empty(MtJson.ParseChecklist(null));
        Assert.Empty(MtJson.ParseChecklist("not json"));
    }

    // Bodies copied from MT's ProjectOpenController (#103) and from a live probe of MT 3.0.1.
    private static (MtApiClient Client, FakeHandler Handler) OpenReturning(HttpStatusCode code, string? body, string mediaType)
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(code)
        {
            Content = body is null ? new ByteArrayContent([]) : new StringContent(body, Encoding.UTF8, mediaType),
        });
        return (new MtApiClient(new HttpClient(handler) { BaseAddress = new Uri(MtApiClient.DefaultBaseAddress) }), handler);
    }

    [Fact]
    public async Task Open_200_is_focused_with_pane_details_and_posts_to_the_escaped_route()
    {
        var (client, handler) = OpenReturning(HttpStatusCode.OK, """
            {"result":"focused","projectId":"a b","projectName":"MultiTerminal","paneTitle":"Bob - MultiTerminal (PM)","floating":true,"otherPanes":2}
            """, "application/json");

        var r = await client.OpenProjectAsync("a b", CancellationToken.None);

        Assert.Equal(new MtOpenResult(MtOpenKind.Focused, "MultiTerminal", "Bob - MultiTerminal (PM)", true, 2), r);
        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/api/projects/a%20b/open", req.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Open_202_is_launching()
    {
        var (client, _) = OpenReturning(HttpStatusCode.Accepted,
            """{"result":"launching","projectId":"x","projectName":"X"}""", "application/json");

        var r = await client.OpenProjectAsync("x", CancellationToken.None);

        Assert.Equal(MtOpenKind.Launching, r.Kind);
        Assert.Equal("X", r.ProjectName);
    }

    [Fact]
    public async Task Open_404_with_problem_body_is_unknown_project_with_MTs_detail()
    {
        var (client, _) = OpenReturning(HttpStatusCode.NotFound,
            """{"title":"Not Found","status":404,"detail":"Project 'x' not found"}""", "application/problem+json");

        var r = await client.OpenProjectAsync("x", CancellationToken.None);

        Assert.Equal(new MtOpenResult(MtOpenKind.NotFound, Detail: "Project 'x' not found"), r);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]          // older MT: no such route, empty body
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task Open_without_the_endpoint_is_unsupported(HttpStatusCode code)
    {
        var (client, _) = OpenReturning(code, null, "application/json");
        Assert.Equal(MtOpenKind.Unsupported, (await client.OpenProjectAsync("x", CancellationToken.None)).Kind);
    }

    [Fact]
    public async Task Open_503_is_failed_with_MTs_reason()
    {
        var (client, _) = OpenReturning(HttpStatusCode.ServiceUnavailable,
            """{"status":503,"detail":"MultiTerminal's window is not ready yet."}""", "application/problem+json");

        var r = await client.OpenProjectAsync("x", CancellationToken.None);

        Assert.Equal(MtOpenKind.Failed, r.Kind);
        Assert.Equal("MultiTerminal's window is not ready yet.", r.Detail);
    }

    [Fact]
    public async Task Open_when_MT_is_offline_is_offline_not_an_exception()
    {
        var client = new MtApiClient(new HttpClient(new FakeHandler(_ => throw new HttpRequestException("refused")))
            { BaseAddress = new Uri(MtApiClient.DefaultBaseAddress) });
        Assert.Equal(MtOpenKind.Offline, (await client.OpenProjectAsync("x", CancellationToken.None)).Kind);
    }

    [Fact]
    public async Task Open_timeout_is_no_answer_not_offline()
    {
        // HttpClient reports its own timeout as a TaskCanceledException the caller didn't ask for.
        var client = new MtApiClient(new HttpClient(new FakeHandler(_ => throw new TaskCanceledException("timeout")))
            { BaseAddress = new Uri(MtApiClient.DefaultBaseAddress) });
        Assert.Equal(MtOpenKind.NoAnswer, (await client.OpenProjectAsync("x", CancellationToken.None)).Kind);
    }

    [Fact]
    public async Task Open_cancelled_by_the_caller_still_throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new MtApiClient(new HttpClient(new FakeHandler(_ => throw new TaskCanceledException()))
            { BaseAddress = new Uri(MtApiClient.DefaultBaseAddress) });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.OpenProjectAsync("x", cts.Token));
    }
}
