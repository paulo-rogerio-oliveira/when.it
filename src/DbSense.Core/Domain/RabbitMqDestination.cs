namespace DbSense.Core.Domain;

public class RabbitMqDestination
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string VirtualHost { get; set; } = "/";
    public string Username { get; set; } = string.Empty;
    public byte[] PasswordEncrypted { get; set; } = Array.Empty<byte>();
    public bool UseTls { get; set; }
    public string DefaultExchange { get; set; } = string.Empty;
    public string Status { get; set; } = "inactive";
    public DateTime CreatedAt { get; set; }
}
