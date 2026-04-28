namespace DbSense.Core.Domain;

public class SetupInfo
{
    public int Id { get; set; }
    public string SchemaVersion { get; set; } = string.Empty;
    public DateTime ProvisionedAt { get; set; }
}
