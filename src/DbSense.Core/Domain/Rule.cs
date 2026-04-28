namespace DbSense.Core.Domain;

public class Rule
{
    public Guid Id { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid? DestinationId { get; set; }
    public Guid? SourceRecordingId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Version { get; set; }
    public string Definition { get; set; } = "{}";
    public string Status { get; set; } = "draft";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ActivatedAt { get; set; }
}
