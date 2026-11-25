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