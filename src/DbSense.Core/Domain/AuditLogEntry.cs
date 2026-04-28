namespace DbSense.Core.Domain;

public class AuditLogEntry
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string? Payload { get; set; }
    public DateTime Timestamp { get; set; }
}
