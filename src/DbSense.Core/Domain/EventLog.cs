namespace DbSense.Core.Domain;

public class EventLog
{
    public long Id { get; set; }
    public Guid RuleId { get; set; }
    public Guid ConnectionId { get; set; }
    public DateTime MatchedAt { get; set; }
    public DateTime SqlTimestamp { get; set; }
    public string EventPayload { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string PublishStatus { get; set; } = "pending";
    public int PublishAttempts { get; set; }
    public string? LastError { get; set; }
    public DateTime? PublishedAt { get; set; }
}
