namespace DbSense.Contracts.Recordings;

public record RecordingListItem(
    Guid Id,
    Guid ConnectionId,
    string ConnectionName,
    string Name,
    string? Description,
    string Status,
    DateTime StartedAt,
    DateTime? StoppedAt,
    int EventCount);

public record RecordingDetail(
    Guid Id,
    Guid ConnectionId,
    string ConnectionName,
    string Name,
    string? Description,
    string Status,
    DateTime StartedAt,
    DateTime? StoppedAt,
    int EventCount,
    string? FilterHostName,
    string? FilterAppName,
    string? FilterLoginName,
    int? FilterSessionId);

public record CreateRecordingRequest(
    Guid ConnectionId,
    string Name,
    string? Description,
    string? FilterHostName,
    string? FilterAppName,
    string? FilterLoginName);

public record RecordingEventItem(
    long Id,
    DateTime EventTimestamp,
    string EventType,
    int SessionId,
    string DatabaseName,
    string? ObjectName,
    string SqlText,
    long DurationUs,
    long? RowCount,
    string? AppName,
    string? HostName,
    string? LoginName,
    long? TransactionId,
    string? ParsedPayload);

public record RecordingEventsPage(
    IReadOnlyList<RecordingEventItem> Items,
    long? NextCursor,
    int Total);
