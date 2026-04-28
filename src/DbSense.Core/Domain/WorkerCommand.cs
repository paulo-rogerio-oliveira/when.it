namespace DbSense.Core.Domain;

public class WorkerCommand
{
    public long Id { get; set; }
    public string Command { get; set; } = string.Empty;
    public Guid? TargetId { get; set; }
    public string? Payload { get; set; }
    public DateTime IssuedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string Status { get; set; } = "pending";
    public string? Result { get; set; }
}
