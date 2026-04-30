namespace DbSense.Core.Domain;

public class OutboxMessage
{
    public long Id { get; set; }
    public long EventsLogId { get; set; }
    public string Payload { get; set; } = string.Empty;
    public string ReactionType { get; set; } = string.Empty;   // "cmd" | "sql" | "rabbit"
    public string ReactionConfig { get; set; } = "{}";          // JSON com placeholders já resolvidos
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public string? LockedBy { get; set; }
    public DateTime? LockedUntil { get; set; }
    public string? LastError { get; set; }
}
