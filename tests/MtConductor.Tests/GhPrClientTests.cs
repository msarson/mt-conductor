using System.Text.Json;
using MtConductor.Github;

namespace MtConductor.Tests;

public class GhPrClientTests
{
    // Shape copied from real `gh pr list --json number,title,url,isDraft,statusCheckRollup,reviewDecision`
    // output (note reviewDecision is "" rather than null when there is no decision).
    private const string SampleJson = """
    [
      {"isDraft":false,"number":96,"reviewDecision":"","statusCheckRollup":[
        {"__typename":"CheckRun","completedAt":"2026-10-31T11:29:41Z","conclusion":"SUCCESS",
         "detailsUrl":"https://github.com/ClarionLive/multiterminal/actions/runs/1/job/2",
         "name":"build · test · lint (windows-latest)","startedAt":"2026-10-31T11:14:43Z",
         "status":"COMPLETED","workflowName":"CI"}],
       "title":"Ask before opening a project that is already open","url":"https://github.com/ClarionLive/multiterminal/pull/96"},
      {"isDraft":true,"number":95,"reviewDecision":"CHANGES_REQUESTED","statusCheckRollup":[
        {"__typename":"CheckRun","conclusion":"","name":"build","status":"IN_PROGRESS"},
        {"__typename":"StatusContext","context":"ci/legacy","state":"SUCCESS"}],
       "title":"Draft work","url":"https://github.com/ClarionLive/multiterminal/pull/95"},
      {"isDraft":false,"number":94,"reviewDecision":"APPROVED","statusCheckRollup":[
        {"__typename":"CheckRun","conclusion":"SUCCESS","name":"a","status":"COMPLETED"},
        {"__typename":"StatusContext","context":"ci/other","state":"ERROR"}],
       "title":"Broken","url":"https://github.com/ClarionLive/multiterminal/pull/94"},
      {"isDraft":false,"number":93,"reviewDecision":null,"statusCheckRollup":[],
       "title":"No checks","url":"https://github.com/ClarionLive/multiterminal/pull/93"}
    ]
    """;

    [Theory]
    [InlineData("https://github.com/o/r", "o/r")]
    [InlineData("https://github.com/o/r.git", "o/r")]
    [InlineData("https://github.com/o/r/", "o/r")]
    [InlineData("http://github.com/ClarionLive/MultiTerminal.git", "ClarionLive/MultiTerminal")]
    [InlineData("git@github.com:o/r.git", "o/r")]
    [InlineData("git@github.com:o/r", "o/r")]
    [InlineData("ssh://git@github.com/o/r.git", "o/r")]
    [InlineData("github.com/o/my.repo", "o/my.repo")]
    [InlineData("o/r", "o/r")]
    [InlineData("  msarson/mt-conductor  ", "msarson/mt-conductor")]
    public void ParseOwnerRepo_accepts_github_forms(string input, string expected)
    {
        Assert.Equal(expected, GhPrClient.ParseOwnerRepo(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("junk")]
    [InlineData("https://gitlab.com/o/r.git")]
    [InlineData("git@gitlab.com:o/r.git")]
    [InlineData("https://github.com/o")]
    [InlineData("https://github.com/o/r/pull/1")]
    [InlineData("o/r/extra")]
    [InlineData("o/r; rm -rf /")]
    [InlineData("--repo/x")]
    [InlineData("C:\\src\\repo")]
    public void ParseOwnerRepo_rejects_non_github_or_junk(string? input)
    {
        Assert.Null(GhPrClient.ParseOwnerRepo(input));
    }

    [Fact]
    public void ParsePrJson_reads_realistic_sample()
    {
        var prs = GhPrClient.ParsePrJson(SampleJson);

        Assert.Equal(4, prs.Count);

        var first = prs[0];
        Assert.Equal(96, first.Number);
        Assert.Equal("Ask before opening a project that is already open", first.Title);
        Assert.Equal("https://github.com/ClarionLive/multiterminal/pull/96", first.Url);
        Assert.False(first.IsDraft);
        Assert.Equal("success", first.ChecksState);
        Assert.Null(first.ReviewDecision); // "" normalised to null

        Assert.True(prs[1].IsDraft);
        Assert.Equal("pending", prs[1].ChecksState);
        Assert.Equal("CHANGES_REQUESTED", prs[1].ReviewDecision);

        Assert.Equal("failure", prs[2].ChecksState);
        Assert.Equal("APPROVED", prs[2].ReviewDecision);

        Assert.Equal("none", prs[3].ChecksState);
        Assert.Null(prs[3].ReviewDecision);
    }

    [Fact]
    public void ParsePrJson_empty_array_gives_empty_list()
    {
        Assert.Empty(GhPrClient.ParsePrJson("[]"));
    }

    [Theory]
    [InlineData("""[]""", "none")]
    [InlineData("""null""", "none")]
    [InlineData("""[{"__typename":"CheckRun","status":"COMPLETED","conclusion":"SUCCESS"}]""", "success")]
    [InlineData("""[{"__typename":"CheckRun","status":"COMPLETED","conclusion":"NEUTRAL"},{"__typename":"CheckRun","status":"COMPLETED","conclusion":"SKIPPED"}]""", "success")]
    [InlineData("""[{"__typename":"StatusContext","state":"SUCCESS"}]""", "success")]
    [InlineData("""[{"__typename":"CheckRun","status":"COMPLETED","conclusion":"FAILURE"}]""", "failure")]
    [InlineData("""[{"__typename":"CheckRun","status":"COMPLETED","conclusion":"CANCELLED"}]""", "failure")]
    [InlineData("""[{"__typename":"CheckRun","status":"COMPLETED","conclusion":"TIMED_OUT"}]""", "failure")]
    [InlineData("""[{"__typename":"CheckRun","status":"COMPLETED","conclusion":"ACTION_REQUIRED"}]""", "failure")]
    [InlineData("""[{"__typename":"StatusContext","state":"FAILURE"}]""", "failure")]
    [InlineData("""[{"__typename":"StatusContext","state":"ERROR"}]""", "failure")]
    [InlineData("""[{"__typename":"CheckRun","status":"IN_PROGRESS","conclusion":""}]""", "pending")]
    [InlineData("""[{"__typename":"CheckRun","status":"QUEUED","conclusion":null}]""", "pending")]
    [InlineData("""[{"__typename":"StatusContext","state":"PENDING"}]""", "pending")]
    [InlineData("""[{"__typename":"StatusContext","state":"EXPECTED"}]""", "pending")]
    // failure beats pending, pending beats success
    [InlineData("""[{"__typename":"CheckRun","status":"IN_PROGRESS"},{"__typename":"StatusContext","state":"FAILURE"}]""", "failure")]
    [InlineData("""[{"__typename":"CheckRun","status":"COMPLETED","conclusion":"SUCCESS"},{"__typename":"StatusContext","state":"PENDING"}]""", "pending")]
    public void RollupChecks_computes_state(string json, string expected)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, GhPrClient.RollupChecks(doc.RootElement));
    }

    [Fact]
    public async Task GetOpenPrsAsync_caches_per_repo()
    {
        var calls = new List<string>();
        var client = new GhPrClient(TimeSpan.FromMinutes(5), (repo, _) =>
        {
            lock (calls) calls.Add(repo);
            return Task.FromResult((0, SampleJson));
        });

        var a1 = await client.GetOpenPrsAsync("https://github.com/o/r.git", CancellationToken.None);
        var a2 = await client.GetOpenPrsAsync("git@github.com:o/r.git", CancellationToken.None);
        var b1 = await client.GetOpenPrsAsync("o/other", CancellationToken.None);

        Assert.NotNull(a1);
        Assert.Same(a1, a2);
        Assert.NotNull(b1);
        Assert.Equal(new[] { "o/r", "o/other" }, calls);
    }

    [Fact]
    public async Task GetOpenPrsAsync_refetches_after_cache_expires()
    {
        var count = 0;
        var client = new GhPrClient(TimeSpan.Zero, (_, _) =>
        {
            Interlocked.Increment(ref count);
            return Task.FromResult((0, "[]"));
        });

        await client.GetOpenPrsAsync("o/r", CancellationToken.None);
        await client.GetOpenPrsAsync("o/r", CancellationToken.None);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task GetOpenPrsAsync_returns_null_on_nonzero_exit_and_caches_it()
    {
        var count = 0;
        var client = new GhPrClient(TimeSpan.FromMinutes(5), (_, _) =>
        {
            Interlocked.Increment(ref count);
            return Task.FromResult((1, ""));
        });

        Assert.Null(await client.GetOpenPrsAsync("o/r", CancellationToken.None));
        Assert.Null(await client.GetOpenPrsAsync("o/r", CancellationToken.None));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GetOpenPrsAsync_returns_null_when_gh_missing()
    {
        var client = new GhPrClient(TimeSpan.FromMinutes(5),
            (_, _) => throw new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified"));

        Assert.Null(await client.GetOpenPrsAsync("o/r", CancellationToken.None));
    }

    [Fact]
    public async Task GetOpenPrsAsync_returns_null_on_bad_json()
    {
        var client = new GhPrClient(TimeSpan.FromMinutes(5), (_, _) => Task.FromResult((0, "not json")));

        Assert.Null(await client.GetOpenPrsAsync("o/r", CancellationToken.None));
    }

    [Fact]
    public async Task GetOpenPrsAsync_returns_null_for_bad_url_without_calling_gh()
    {
        var count = 0;
        var client = new GhPrClient(TimeSpan.FromMinutes(5), (_, _) =>
        {
            Interlocked.Increment(ref count);
            return Task.FromResult((0, "[]"));
        });

        Assert.Null(await client.GetOpenPrsAsync("https://gitlab.com/o/r", CancellationToken.None));
        Assert.Null(await client.GetOpenPrsAsync(null, CancellationToken.None));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task GetOpenPrsAsync_propagates_caller_cancellation_and_does_not_cache()
    {
        var count = 0;
        var client = new GhPrClient(TimeSpan.FromMinutes(5), (_, ct) =>
        {
            Interlocked.Increment(ref count);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult((0, "[]"));
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetOpenPrsAsync("o/r", cts.Token));

        Assert.NotNull(await client.GetOpenPrsAsync("o/r", CancellationToken.None));
        Assert.Equal(2, count);
    }
}
