using MtConductor.Github;

namespace MtConductor.Tests;

public class GitRemoteResolverTests
{
    [Fact]
    public async Task Upstream_remote_wins_over_origin_for_forks()
    {
        var calls = new List<string>();
        var resolver = new GitRemoteResolver(TimeSpan.FromMinutes(1), FakeGit.Remotes(remote =>
        {
            calls.Add(remote);
            return remote == "upstream"
                ? (0, "https://github.com/ClarionLive/multiterminal.git\n")
                : (0, "https://github.com/msarson/multiterminal.git\n");
        }));

        var url = await resolver.GetRepoUrlAsync(Path.GetTempPath(), CancellationToken.None);

        Assert.Equal("https://github.com/ClarionLive/multiterminal.git", url);
        Assert.Equal(["upstream"], calls);
    }

    [Fact]
    public async Task Origin_used_when_there_is_no_upstream()
    {
        var resolver = new GitRemoteResolver(TimeSpan.FromMinutes(1), FakeGit.Remotes(remote =>
            remote == "origin" ? (0, "https://github.com/me/x\n") : (2, "")));
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

    [Fact]
    public async Task A_folder_inside_another_repo_does_not_borrow_its_remote()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mono", "sub");   // need not exist for the root check, but must for the resolver
        Directory.CreateDirectory(folder);
        try
        {
            var calls = new List<string>();
            var resolver = new GitRemoteResolver(TimeSpan.FromMinutes(1), FakeGit.Remotes(remote =>
            {
                calls.Add(remote);
                return (0, "https://github.com/o/mono\n");
            }, topLevel: Path.Combine(Path.GetTempPath(), "mono")));

            Assert.Null(await resolver.GetRepoUrlAsync(folder, CancellationToken.None));
            Assert.Empty(calls);   // never even asked for a remote
        }
        finally
        {
            Directory.Delete(Path.Combine(Path.GetTempPath(), "mono"), recursive: true);
        }
    }

    [Fact]
    public async Task Repo_root_matches_whatever_the_slashes_and_case()
    {
        var path = Path.GetTempPath().TrimEnd('\\');
        var resolver = new GitRemoteResolver(TimeSpan.FromMinutes(1), FakeGit.Remotes(
            _ => (0, "https://github.com/o/r\n"), topLevel: path.ToUpperInvariant().Replace('\\', '/')));

        Assert.Equal("https://github.com/o/r", await resolver.GetRepoUrlAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task Git_failing_on_rev_parse_means_no_repo()
    {
        var resolver = new GitRemoteResolver(TimeSpan.FromMinutes(1), (_, _) => Task.FromResult((128, "")));
        Assert.Null(await resolver.GetRepoUrlAsync(Path.GetTempPath(), CancellationToken.None));
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
    public void Only_fixed_drives_count()
    {
        Assert.True(GitRemoteResolver.IsFixedDrive(Environment.SystemDirectory));   // the system drive is a fixed disk
        Assert.False(GitRemoteResolver.IsFixedDrive(@"Q:\no\such\drive") && !Directory.Exists(@"Q:\"));   // not mounted: unknown, not fixed
        Assert.False(GitRemoteResolver.IsFixedDrive(@"\\server\share\proj"));
        Assert.False(GitRemoteResolver.IsFixedDrive(""));
    }

    [Fact]
    public async Task Git_is_not_run_for_paths_on_drives_that_are_not_fixed()
    {
        var calls = 0;
        var resolver = new GitRemoteResolver(TimeSpan.FromMinutes(1), (_, _) => { calls++; return Task.FromResult((0, "https://github.com/o/r\n")); });

        // Q: isn't a mounted drive here, so it reports as unknown, never as fixed.
        if (Directory.Exists(@"Q:\")) return;
        Assert.Null(await resolver.GetRepoUrlAsync(@"Q:\proj", CancellationToken.None));
        Assert.Equal(0, calls);
    }
}