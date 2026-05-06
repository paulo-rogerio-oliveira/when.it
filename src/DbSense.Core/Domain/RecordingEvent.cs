namespace DbSense.Core.Domain;

public class RecordingEvent
{
    public long Id { get; set; }
    public Guid RecordingId { get; set; }
    public DateTime EventTimestamp { get; set; }
    public string EventType { get; set; } = string.Empty;
    public int SessionId { get; set; }
    public string DatabaseName { get; set; } = string.Empty;
    public string? ObjectName { get; set; }
    public string SqlText { get; set; } = string.Empty;
    public string? Statement { get; set; }
    public long DurationUs { get; set; }
    public long? CpuTimeUs { get; set; }
    public long? Reads { get; set; }
    public long? Writes { get; set; }
    public long? RowCount { get; set; }
    public string? AppName { get; set; }
    public string? HostName { get; set; }
    public string? LoginName { get; set; }
    public long? TransactionId { get; set; }
    public string? RawPayload { get; set; }
    // JSON com o resultado de SqlParser.TryParseAll(SqlText) — array de DMLs com
    // schema/table/values/where resolvidos (incluindo @pN do sp_executesql substituídos).
    public string? ParsedPayload { get; set; }

    public Recording? Recording { get; set; }
}
