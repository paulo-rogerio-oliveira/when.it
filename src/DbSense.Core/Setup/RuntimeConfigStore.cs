using System.Text.Json;
using DbSense.Core.Security;

namespace DbSense.Core.Setup;

public interface IRuntimeConfigStore
{
    string? GetControlDbConnectionString();
    Task SetControlDbConnectionStringAsync(string connectionString, CancellationToken ct = default);
}

public class FileRuntimeConfigStore : IRuntimeConfigStore
{
    private readonly string _path;
    private readonly ISecretCipher _cipher;
    private readonly object _lock = new();
    private string? _cached;

    public FileRuntimeConfigStore(string path, ISecretCipher cipher)
    {
        _path = path;
        _cipher = cipher;
        _cached = TryLoad();
    }

    public string? GetControlDbConnectionString()
    {
        lock (_lock) return _cached;
    }

    public async Task SetControlDbConnectionStringAsync(string connectionString, CancellationToken ct = default)
    {
        var encrypted = Convert.ToBase64String(_cipher.Encrypt(connectionString));
        var payload = JsonSerializer.Serialize(new RuntimeConfigFile(encrypted));
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(_path, payload, ct);
        lock (_lock) _cached = connectionString;
    }

    private string? TryLoad()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var json = File.ReadAllText(_path);
            var file = JsonSerializer.Deserialize<RuntimeConfigFile>(json);
            if (file?.ControlDbCipher is null) return null;
            return _cipher.Decrypt(Convert.FromBase64String(file.ControlDbCipher));
        }
        catch
        {
            return null;
        }
    }

    private record RuntimeConfigFile(string? ControlDbCipher);
}
