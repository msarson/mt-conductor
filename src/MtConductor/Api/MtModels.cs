using System.Text.Json;
using System.Text.Json.Serialization;

namespace MtConductor.Api;

// DTOs for the MultiTerminal REST API (http://localhost:5050). Only the fields MT Conductor uses.
// MT serializes C# properties as camelCase; a few routes use explicit snake_case names.

public sealed record MtHealth(string? Service, string? App, string? Version, int Pid, DateTime? StartedUtc);

public sealed record MtProjectList(int Count, List<MtProject> Projects);

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
