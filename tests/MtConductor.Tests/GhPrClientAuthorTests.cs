using MtConductor.Github;

namespace MtConductor.Tests;

public class GhPrClientAuthorTests
{
    [Fact]
    public void Author_login_is_parsed_and_optional()
    {
        var prs = GhPrClient.ParsePrJson("""
            [{"number":96,"title":"a","url":"u","isDraft":false,"statusCheckRollup":[],"reviewDecision":"","author":{"login":"msarson","is_bot":false}},
             {"number":88,"title":"b","url":"u","isDraft":false,"statusCheckRollup":[],"reviewDecision":""}]
            """);
        Assert.Equal("msarson", prs[0].Author);
        Assert.Null(prs[1].Author);
    }

    [Fact]
    public async Task Viewer_login_is_cached_and_null_on_failure()
    {
        var calls = 0;
        var ok = new GhPrClient(TimeSpan.FromMinutes(5), runGh: null, runGhArgs: (args, _) =>
        {
            calls++;
            Assert.Equal(["api", "user", "--jq", ".login"], args);
            return Task.FromResult((0, "msarson\n"));
        });
        Assert.Equal("msarson", await ok.GetViewerLoginAsync(CancellationToken.None));
        Assert.Equal("msarson", await ok.GetViewerLoginAsync(CancellationToken.None));
        Assert.Equal(1, calls);

        var failing = new GhPrClient(TimeSpan.FromMinutes(5), runGh: null, runGhArgs: (_, _) => Task.FromResult((1, "")));
        Assert.Null(await failing.GetViewerLoginAsync(CancellationToken.None));
    }
}
