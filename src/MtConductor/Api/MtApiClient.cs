using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MtConductor.Api;

/// <summary>
/// Client for MultiTerminal's local REST API. Every method returns null (or an Offline result)
/// on any failure (MT offline, timeout, unexpected shape) instead of throwing, so the UI can show
/// "MT offline" or "unknown" and keep polling. Everything is a GET except
/// <see cref="OpenProjectAsync"/>, which the Owner triggers by clicking Open on a card.
/// </summary>
public sealed class MtApiClient
{
    public const string DefaultBaseAddress = "http://localhost:5050/";

    private readonly HttpClient _http;

    public MtApiClient(HttpClient http)
    {
        _http = http;
    }

    public static MtApiClient CreateDefault(TimeSpan? timeout = null) =>
        new(new HttpClient { BaseAddress = new Uri(DefaultBaseAddress), Timeout = timeout ?? TimeSpan.FromSeconds(4) });

    public Task<MtHealth?> GetHealthAsync(CancellationToken ct) => GetAsync<MtHealth>("api/health", ct);

    public async Task<IReadOnlyList<MtProject>?> GetProjectsAsync(CancellationToken ct) =>
        (await GetAsync<MtProjectList>("api/projects", ct))?.Projects;

    /// <summary>Tasks with the given status (todo, in_progress, ...), all projects, excluding quick tasks.</summary>
    public Task<List<MtTask>?> GetTasksAsync(string status, CancellationToken ct) =>
        GetAsync<List<MtTask>>($"api/tasks?status={Uri.EscapeDataString(status)}", ct);

    /// <summary>Latest session for a project path. Found=false on 404; null on failure.</summary>
    public async Task<(bool Found, MtSessionLatest? Latest)?> GetLatestSessionAsync(string projectPath, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(
                $"api/session-lineage/latest?projectPath={Uri.EscapeDataString(projectPath)}", ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return (false, null);
            if (!resp.IsSuccessStatusCode) return null;
            var latest = await resp.Content.ReadFromJsonAsync<MtSessionLatest>(MtJson.Options, ct);
            return (latest is not null, latest);
        }
        catch (Exception ex) when (IsExpected(ex, ct))
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<MtInboxMessage>?> GetUnreadInboxAsync(string userId, CancellationToken ct) =>
        (await GetAsync<MtInboxResponse>(
            $"api/tasks/inbox/{Uri.EscapeDataString(userId)}?unreadOnly=true&limit=200", ct))?.Messages;

    public Task<List<MtNotification>?> GetUnreadNotificationsAsync(CancellationToken ct) =>
        GetAsync<List<MtNotification>>("api/notifications?unreadOnly=true&limit=500", ct);

    public Task<List<MtTerminal>?> GetConnectedTerminalsAsync(CancellationToken ct) =>
        GetAsync<List<MtTerminal>>("api/messaging/terminals", ct);

    public Task<MtOwnerProfile?> GetOwnerProfileAsync(CancellationToken ct) =>
        GetAsync<MtOwnerProfile>("api/owner-profile", ct);

    /// <summary>
    /// Which projects have a PM pane open, as projectId → pane count (only projects with at least
    /// one). Null when MT can't say (no such route, window not ready, offline).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>?> GetOpenStateAsync(CancellationToken ct)
    {
        // MT answers this on its UI thread, which a modal dialog can hold up: give it a short
        // limit of its own so it never delays the rest of the poll.
        MtOpenState? state;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(OpenStateTimeout);
            state = await GetAsync<MtOpenState>("api/projects/open-state", limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        if (state?.Projects is null) return null;
        var open = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in state.Projects)
            if (p is not null && !string.IsNullOrEmpty(p.ProjectId) && p.OpenPanes > 0) open[p.ProjectId] = p.OpenPanes;
        return open;
    }

    internal static readonly TimeSpan OpenStateTimeout = TimeSpan.FromSeconds(2);

    public Task<MtRemoteMode?> GetRemoteModeAsync(CancellationToken ct) =>
        GetAsync<MtRemoteMode>("api/remote-mode", ct);

    /// <summary>
    /// Asks MT to bring the project's PM pane to the front, or to launch the project if no pane
    /// has it open (ClarionLive/multiterminal#103). Never throws for MT being offline or old:
    /// those come back as <see cref="MtOpenKind.Offline"/> and <see cref="MtOpenKind.Unsupported"/>.
    /// An unknown project is a 404 with a problem+json body; a missing route is a bare 404.
    /// </summary>
    public async Task<MtOpenResult> OpenProjectAsync(string projectId, CancellationToken ct)
    {
        try
        {
            using var content = new ByteArrayContent([]);
            using var resp = await _http.PostAsync($"api/projects/{Uri.EscapeDataString(projectId)}/open", content, ct);
            var isProblem = resp.Content.Headers.ContentType?.MediaType == "application/problem+json";

            switch (resp.StatusCode)
            {
                case HttpStatusCode.OK:
                case HttpStatusCode.Accepted:
                    // MT answered, so a body we can't read is a failure, not "offline".
                    var body = await ReadJsonOrNullAsync<MtOpenResponse>(resp, ct);
                    if (body is null) return new MtOpenResult(MtOpenKind.Failed, Detail: "MT's answer couldn't be read.");
                    var kind = resp.StatusCode == HttpStatusCode.OK ? MtOpenKind.Focused : MtOpenKind.Launching;
                    return new MtOpenResult(kind, body.ProjectName, body.PaneTitle, body.Floating, Math.Max(0, body.OtherPanes));

                // Relies on MT not adding status-code pages: today a missing route is a bare 404,
                // while #103's unknown-project 404 always carries a problem+json body (pinned by tests).
                case HttpStatusCode.NotFound when !isProblem:
                case HttpStatusCode.MethodNotAllowed:
                    return new MtOpenResult(MtOpenKind.Unsupported);

                default:
                    var detail = isProblem ? (await ReadJsonOrNullAsync<MtProblem>(resp, ct))?.Detail : null;
                    return new MtOpenResult(
                        resp.StatusCode == HttpStatusCode.NotFound ? MtOpenKind.NotFound : MtOpenKind.Failed,
                        Detail: detail ?? $"MT answered {(int)resp.StatusCode}.");
            }
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // MT runs the open on its UI thread; a modal dialog there holds the reply back.
            return new MtOpenResult(MtOpenKind.NoAnswer);
        }
        catch (Exception ex) when (IsExpected(ex, ct))
        {
            return new MtOpenResult(MtOpenKind.Offline);
        }
    }

    private static async Task<T?> ReadJsonOrNullAsync<T>(HttpResponseMessage resp, CancellationToken ct) where T : class
    {
        try
        {
            return await resp.Content.ReadFromJsonAsync<T>(MtJson.Options, ct);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The owner's inbox user id, derived the way MT does it: first word of the owner
    /// profile's FullName, falling back to "Owner".
    /// </summary>
    public static string InboxUserIdFor(MtOwnerProfile? profile)
    {
        var first = profile?.FullName?.Split(' ')[0];
        return string.IsNullOrEmpty(first) ? "Owner" : first;
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        try
        {
            using var resp = await _http.GetAsync(path, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<T>(MtJson.Options, ct);
        }
        catch (Exception ex) when (IsExpected(ex, ct))
        {
            return null;
        }
    }

    // Connection refused, timeouts (TaskCanceledException not caused by our token) and bad JSON
    // are expected when MT is offline or changes shape. Caller cancellation still propagates.
    private static bool IsExpected(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or JsonException or NotSupportedException
        || (ex is TaskCanceledException && !ct.IsCancellationRequested);
}
