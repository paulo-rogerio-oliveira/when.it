namespace DbSense.Core.Domain;

public class Connection
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
    public string AuthType { get; set; } = "sql";
    public string? Username { get; set; }
    public byte[]? PasswordEncrypted { get; set; }
    public string Status { get; set; } = "inactive";
    public DateTime? LastTestedAt { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
