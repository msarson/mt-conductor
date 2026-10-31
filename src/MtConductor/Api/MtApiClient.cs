using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MtConductor.Api;

/// <summary>
/// Read-only client for MultiTerminal's local REST API. Every method returns null on any
/// failure (MT offline, timeout, unexpected shape) instead of throwing, so the UI can show
/// "MT offline" or "unknown" and keep polling. It only issues GET requests.
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
        GetAsync<List<MtNotification>>("api/notifications?unreadOnly=true", ct);

    public Task<List<MtTerminal>?> GetConnectedTerminalsAsync(CancellationToken ct) =>
        GetAsync<List<MtTerminal>>("api/messaging/terminals", ct);

    public Task<MtOwnerProfile?> GetOwnerProfileAsync(CancellationToken ct) =>
        GetAsync<MtOwnerProfile>("api/owner-profile", ct);

    public Task<MtRemoteMode?> GetRemoteModeAsync(CancellationToken ct) =>
        GetAsync<MtRemoteMode>("api/remote-mode", ct);

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
