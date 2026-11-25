using MtConductor.Github;

namespace MtConductor.Tests;

public class GitRemoteResolverTests
{
    [Fact]
    public async Task Upstream_remote_wins_over_origin_for_forks()
    {
        var calls = new List<string>();
        var resolver = new GitRemoteResolver(TimeSpan.FromMinutes(1), (req, _) =>
        {
            calls.Add(req.Remote);
            return Task.FromResult(req.Remote == "upstream"
                ? (0, "https://github.com/ClarionLive/multiterminal.git\n")
                : (0, "https://github.com/msarson/multiterminal.git\n"));
        });

        var url = await resolver.GetRepoUrlAsync(Path.GetTempPath(), CancellationToken.None);

        Assert.Equal("https://github.com/ClarionLive/multiterminal.git", url);
        Assert.Equal(["upstream"], calls);
    }

    [Fact]
    public async Task Origin_used_when_there_is_no_upstream()
    {
        var resolver = new GitRemoteResolver(TimeSpan.FromMinutes(1), (req, _) =>
            Task.FromResult(req.Remote == "origin" ? (0, "https://github.com/me/x\n") : (2, "")));
        Assert.Equal("https://github.com/me/x", await resolver.GetRepoUrlAsync(Path.GetTempPath(), CancellationToken.None));
    }

    [Fact]
    public async Task Missing_folder_or_origin_means_no_repo()
    {
        var resolver = new GitRemoteResolver(TimeSpan.FromMinutes(1), (_, _) => Task.FromResult((128, "")));
        Assert.Null(await resolver.GetRepoUrlAsync(@"Z:\definitely\not\here", CancellationToken.None));
        Assert.Null(await resolver.GetRepoUrlAsync(Path.GetTempPath(), CancellationToken.None));
        Assert.Null(await resolver.GetRepoUrlAsync(null, CancellationToken.None));
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
}
