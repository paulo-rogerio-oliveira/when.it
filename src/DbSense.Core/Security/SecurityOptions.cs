namespace DbSense.Core.Security;

public class SecurityOptions
{
    public const string SectionName = "Security";

    public string EncryptionKey { get; set; } = string.Empty;
    public string JwtSecret { get; set; } = string.Empty;
    public int JwtExpirationHours { get; set; } = 8;
    public string JwtIssuer { get; set; } = "dbsense";
    public string JwtAudience { get; set; } = "dbsense";
}
