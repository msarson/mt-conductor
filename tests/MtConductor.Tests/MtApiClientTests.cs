using System.Net;
using System.Text;
using MtConductor.Api;

namespace MtConductor.Tests;

public class MtApiClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

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
}
