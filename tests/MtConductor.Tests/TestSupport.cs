namespace MtConductor.Tests;

/// <summary>Records every request and answers with whatever the test's function returns (or throws).</summary>
internal class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }
}

/// <summary>Fake git for GitRemoteResolver: answers rev-parse with the repo root and remote lookups from a function.</summary>
internal static class FakeGit
{
    /// <param name="remote">Answer for <c>remote get-url NAME</c>.</param>
    /// <param name="topLevel">What <c>rev-parse --show-toplevel</c> prints; null = the folder itself (a repo root).</param>
    public static Func<(string Path, string[] Args), CancellationToken, Task<(int ExitCode, string StdOut)>> Remotes(
        Func<string, (int ExitCode, string StdOut)> remote, string? topLevel = null) =>
        (req, _) => Task.FromResult(req.Args[0] == "rev-parse"
            ? (0, (topLevel ?? req.Path).Replace('\\', '/') + "\n")
            : remote(req.Args[^1]));
}