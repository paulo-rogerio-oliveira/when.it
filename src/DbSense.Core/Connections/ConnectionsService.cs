using System.Diagnostics;
using DbSense.Core.Domain;
using DbSense.Core.Persistence;
using DbSense.Core.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DbSense.Core.Connections;

public interface IConnectionsService
{
    Task<IReadOnlyList<Connection>> ListAsync(CancellationToken ct = default);
    Task<Connection?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Connection> CreateAsync(
        string name, string server, string database, string authType,
        string? username, string? password, CancellationToken ct = default);
    Task<Connection?> UpdateAsync(
        Guid id, string name, string server, string database, string authType,
        string? username, string? password, bool clearPassword, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<ConnectionTestResult> TestAsync(Guid id, CancellationToken ct = default);
    Task<ConnectionTestResult> TestAdHocAsync(
        string server, string database, string authType,
        string? username, string? password, CancellationToken ct = default);
}

public record ConnectionTestResult(bool Success, string? Error, long ElapsedMs);

public class ConnectionsService : IConnectionsService
{
    private readonly IDbContextFactory<DbSenseContext> _contextFactory;
    private readonly ISecretCipher _cipher;

    public ConnectionsService(IDbContextFactory<DbSenseContext> contextFactory, ISecretCipher cipher)
    {
        _contextFactory = contextFactory;
        _cipher = cipher;
    }

    public async Task<IReadOnlyList<Connection>> ListAsync(CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        return await ctx.Connections.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
    }

    public async Task<Connection?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        return await ctx.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<Connection> CreateAsync(
        string name, string server, string database, string authType,
        string? username, string? password, CancellationToken ct = default)
    {
        Validate(name, server, database, authType, username, password);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var conn = new Connection
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Server = server.Trim(),
            Database = database.Trim(),
            AuthType = authType.ToLowerInvariant(),
            Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim(),
            PasswordEncrypted = string.IsNullOrEmpty(password) ? null : _cipher.Encrypt(password),
            Status = "inactive",
            CreatedAt = now,
            UpdatedAt = now
        };
        ctx.Connections.Add(conn);
        await ctx.SaveChangesAsync(ct);
        return conn;
    }

    public async Task<Connection?> UpdateAsync(
        Guid id, string name, string server, string database, string authType,
        string? username, string? password, bool clearPassword, CancellationToken ct = default)
    {
        Validate(name, server, database, authType, username, password, isUpdate: true);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var conn = await ctx.Connections.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (conn is null) return null;

        conn.Name = name.Trim();
        conn.Server = server.Trim();
        conn.Database = database.Trim();
        conn.AuthType = authType.ToLowerInvariant();
        conn.Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        if (!string.IsNullOrEmpty(password))
            conn.PasswordEncrypted = _cipher.Encrypt(password);
        else if (clearPassword)
            conn.PasswordEncrypted = null;
        conn.UpdatedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync(ct);
        return conn;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var conn = await ctx.Connections.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (conn is null) return false;
        ctx.Connections.Remove(conn);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<ConnectionTestResult> TestAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var conn = await ctx.Connections.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (conn is null) return new ConnectionTestResult(false, "Conexão não encontrada.", 0);

        var password = conn.PasswordEncrypted is { Length: > 0 } ? _cipher.Decrypt(conn.PasswordEncrypted) : null;
        var result = await TryConnectAsync(conn.Server, conn.Database, conn.AuthType, conn.Username, password, ct);

        conn.LastTestedAt = DateTime.UtcNow;
        conn.LastError = result.Success ? null : result.Error;
        conn.Status = result.Success ? "active" : "error";
        conn.UpdatedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync(ct);
        return result;
    }

    public Task<ConnectionTestResult> TestAdHocAsync(
        string server, string database, string authType,
        string? username, string? password, CancellationToken ct = default)
        => TryConnectAsync(server, database, authType, username, password, ct);

    private static async Task<ConnectionTestResult> TryConnectAsync(
        string server, string database, string authType,
        string? username, string? password, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var cs = BuildConnectionString(server, database, authType, username, password);
            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);
            sw.Stop();
            return new ConnectionTestResult(true, null, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ConnectionTestResult(false, ex.Message, sw.ElapsedMilliseconds);
        }
    }

    private static string BuildConnectionString(
        string server, string database, string authType, string? username, string? password)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            TrustServerCertificate = true,
            Encrypt = true,
            ConnectTimeout = 10
        };
        if (string.Equals(authType, "windows", StringComparison.OrdinalIgnoreCase))
            b.IntegratedSecurity = true;
        else
        {
            b.UserID = username ?? string.Empty;
            b.Password = password ?? string.Empty;
        }
        return b.ConnectionString;
    }

    private static void Validate(
        string name, string server, string database, string authType,
        string? username, string? password, bool isUpdate = false)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Nome obrigatório.", nameof(name));
        if (string.IsNullOrWhiteSpace(server)) throw new ArgumentException("Servidor obrigatório.", nameof(server));
        if (string.IsNullOrWhiteSpace(database)) throw new ArgumentException("Database obrigatório.", nameof(database));
        var auth = authType?.ToLowerInvariant();
        if (auth != "sql" && auth != "windows") throw new ArgumentException("authType deve ser 'sql' ou 'windows'.", nameof(authType));
        if (auth == "sql")
        {
            if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("Username obrigatório para auth SQL.", nameof(username));
            if (!isUpdate && string.IsNullOrEmpty(password)) throw new ArgumentException("Password obrigatório para auth SQL.", nameof(password));
        }
    }
}
