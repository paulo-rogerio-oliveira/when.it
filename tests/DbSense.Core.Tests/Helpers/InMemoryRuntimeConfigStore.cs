using DbSense.Core.Setup;

namespace DbSense.Core.Tests.Setup;

internal class InMemoryRuntimeConfigStore : IRuntimeConfigStore
{
    private string? _cs;

    public string? GetControlDbConnectionString() => _cs;

    public Task SetControlDbConnectionStringAsync(string connectionString, CancellationToken ct = default)
    {
        _cs = connectionString;
        return Task.CompletedTask;
    }
}
