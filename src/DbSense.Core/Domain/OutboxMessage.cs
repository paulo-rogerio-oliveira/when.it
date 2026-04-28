namespace DbSense.Core.Domain;

public class OutboxMessage
{
    public long Id { get; set; }
    public long EventsLogId { get; set; }
    public string Payload { get; set; } = string.Empty;
    public string Exchange { get; set; } = string.Empty;
    public string RoutingKey { get; set; } = string.Empty;
    public string? Headers { get; set; }
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public string? LockedBy { get; set; }
    public DateTime? LockedUntil { get; set; }
}
