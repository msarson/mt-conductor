using System.Text.Json;
using System.Text.Json.Serialization;

namespace MtConductor.Api;

// DTOs for the MultiTerminal REST API (http://localhost:5050). Only the fields MT Conductor uses.
// MT serializes C# properties as camelCase; a few routes use explicit snake_case names.

public sealed record MtHealth(string? Service, string? App, string? Version, int Pid, DateTime? StartedUtc,
    List<string>? Capabilities = null)
{
    /// <summary>Listed by MT builds that have POST /api/projects/{id}/open (ClarionLive/multiterminal#103).</summary>
    public const string ProjectOpenCapability = "project-open-v1";

    /// <summary>Listed by MT builds that have GET /api/projects/open-state (#103).</summary>
    public const string ProjectOpenStateCapability = "project-open-state-v1";

    public bool Has(string capability) =>
        Capabilities?.Contains(capability, StringComparer.OrdinalIgnoreCase) == true;
}

public sealed record MtProjectList(int Count, List<MtProject> Projects);

/// <summary>
/// GET /api/projects/open-state: projects open in a PM pane right now (same rule MT's Open uses).
/// A project that isn't listed has no pane open.
/// </summary>
public sealed record MtOpenState(List<MtOpenStateEntry>? Projects);

public sealed record MtOpenStateEntry(string? ProjectId, int OpenPanes);

/// <summary>What MT did with POST /api/projects/{id}/open (ClarionLive/multiterminal#103).</summary>
public enum MtOpenKind
{
    /// <summary>200: an open PM pane was brought to the front.</summary>
    Focused,
    /// <summary>202: a launch was started; MT may still ask its usual questions, or the Owner may cancel.</summary>
    Launching,
    /// <summary>404 with a problem body: MT doesn't know that project id.</summary>
    NotFound,
    /// <summary>503, or another error status: MT couldn't act (window not ready, open threw).</summary>
    Failed,
    /// <summary>Bare 404 or 405: this MT has no Open endpoint (older than #103).</summary>
    Unsupported,
    /// <summary>No connection: MT offline.</summary>
    Offline,
    /// <summary>Connected but no answer in time: MT's window may be showing a dialog. The open may still happen.</summary>
    NoAnswer,
}

public sealed record MtOpenResult(
    MtOpenKind Kind,
    string? ProjectName = null,
    string? PaneTitle = null,
    bool Floating = false,
    int OtherPanes = 0,
    string? Detail = null);

/// <summary>The 200/202 body of POST /api/projects/{id}/open.</summary>
internal sealed record MtOpenResponse(string? Result, string? ProjectId, string? ProjectName,
    string? PaneTitle, bool Floating, int OtherPanes);

/// <summary>ASP.NET ProblemDetails, the error body MT sends.</summary>
internal sealed record MtProblem(string? Title, string? Detail, int? Status);

public sealed record MtProject(
    string Id,
    string Name,
    string? Path,
    string? SourcePath,
    string? Status,          // Active | Parked | Archived
    string? TeamLead,
    string? GitRepoUrl,
    bool IsPinned,
    DateTime? LastOpenedAt,
    List<string>? TeamAgents);

public sealed record MtTask(
    string Id,
    string Title,
    string Status,           // todo | in_progress | done | suggestion
    string? SubStatus,       // active | paused | null
    string? Assignee,
    string? ProjectId,
    string? Priority,
    DateTime? UpdatedAt,
    string? ChecklistJson,
    string? ContinuationNotes,
    bool IsQuickTask);

public sealed record MtChecklistItem(string? Item, bool Done, string? Status);

public sealed record MtSessionLatest(MtSession? Session, string? Summary);

public sealed record MtSession(
    string? SessionId,
    string? TaskId,
    string? AgentName,
    string? Summary,
    string? StartedAt,
    string? EndedAt,
    string? ProjectPath);

public sealed record MtInboxResponse(bool Success, List<MtInboxMessage>? Messages, int UnreadCount, int TotalCount);

public sealed record MtInboxMessage(
    string Id,
    string? TaskId,
    string? TaskTitle,
    string? Type,            // ready_for_testing | escalation | task_complete | helper_request
    string? Summary,
    DateTime? CreatedAt,
    string? CreatedBy);

public sealed record MtNotification(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("notification_type")] string? NotificationType,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("agent_name")] string? AgentName,
    [property: JsonPropertyName("cwd")] string? Cwd,
    [property: JsonPropertyName("created_at")] DateTime? CreatedAt);

public sealed record MtTerminal(string? Id, string Name, bool IsConnected, string? DocId, DateTime? LastActiveAt);

public sealed record MtOwnerProfile(bool Configured, string? FullName);

public sealed record MtRemoteMode([property: JsonPropertyName("remote_mode")] bool RemoteMode);

internal static class MtJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Parses a task's checklistJson; returns an empty list for null or malformed JSON.</summary>
    public static IReadOnlyList<MtChecklistItem> ParseChecklist(string? checklistJson)
    {
        if (string.IsNullOrWhiteSpace(checklistJson)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<MtChecklistItem>>(checklistJson, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
