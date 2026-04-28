namespace DbSense.Contracts.Rules;

public record RuleListItem(
    Guid Id,
    Guid ConnectionId,
    string ConnectionName,
    string Name,
    string? Description,
    int Version,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ActivatedAt);

public record RuleDetail(
    Guid Id,
    Guid ConnectionId,
    string ConnectionName,
    Guid? DestinationId,
    Guid? SourceRecordingId,
    string Name,
    string? Description,
    int Version,
    string Definition,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ActivatedAt);

public record CreateRuleRequest(
    Guid ConnectionId,
    Guid? SourceRecordingId,
    string Name,
    string? Description,
    string Definition);
