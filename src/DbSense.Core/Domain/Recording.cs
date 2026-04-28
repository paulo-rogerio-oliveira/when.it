namespace DbSense.Core.Domain;

public class Recording
{
    public Guid Id { get; set; }
    public Guid ConnectionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? StoppedAt { get; set; }
    public string Status { get; set; } = "recording";
    public int? FilterSessionId { get; set; }
    public string? FilterHostName { get; set; }
    public string? FilterAppName { get; set; }
    public string? FilterLoginName { get; set; }
    public int EventCount { get; set; }
    public DateTime CreatedAt { get; set; }

    public Connection? Connection { get; set; }
}
