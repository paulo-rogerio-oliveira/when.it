using System.Diagnostics;
using DbSense.Core.Domain;
using DbSense.Core.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DbSense.Core.Setup;

public interface IProvisioningService
{
    Task<ConnectionTestResult> TestConnectionAsync(
        string server, string database, string authType, string? username, string? password,
        CancellationToken ct = default);

    Task<ProvisionResult> ProvisionAsync(
        string server, string database, string authType, string? username, string? password,
        CancellationToken ct = default);

    Task<SetupStatus> GetStatusAsync(CancellationToken ct = default);
}

public record ConnectionTestResult(bool Success, string? Error, long ElapsedMs);
public record ProvisionResult(
    bool Success,
    string? Error,
    int TablesCreated,
    string SchemaVersion,
    string? ErrorCode = null,
    string? Hint = null);
public record SetupStatus(string Status, string? SchemaVersion, DateTime? ProvisionedAt);

public class ProvisioningService : IProvisioningService
{
    public const string CurrentSchemaVersion = "0.1.0";

    private readonly IDbContextFactory<DbSenseContext> _contextFactory;
    private readonly IRuntimeConfigStore _runtimeStore;

    public ProvisioningService(
        IDbContextFactory<DbSenseContext> contextFactory,
        IRuntimeConfigStore runtimeStore)
    {
        _contextFactory = contextFactory;
        _runtimeStore = runtimeStore;
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(
        string server, string database, string authType, string? username, string? password,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Conecta no master para validar servidor + credenciais sem exigir
            // que o database alvo já exista (será criado no Provision).
            var cs = BuildConnectionString(server, "master", authType, username, password);
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

    public async Task<ProvisionResult> ProvisionAsync(
        string server, string database, string authType, string? username, string? password,
        CancellationToken ct = default)
    {
        try
        {
            await EnsureDatabaseExistsAsync(server, database, authType, username, password, ct);

            var cs = BuildConnectionString(server, database, authType, username, password);

            // Persiste a connection string para que requests subsequentes
            // (ex.: create-admin, login, dashboard) usem o mesmo banco.
            await _runtimeStore.SetControlDbConnectionStringAsync(cs, ct);

            await using (var ctx = await _contextFactory.CreateDbContextAsync(ct))
            {
                await ctx.Database.EnsureCreatedAsync(ct);

                var existing = await ctx.SetupInfo.FirstOrDefaultAsync(ct);
                if (existing is null)
                {
                    ctx.SetupInfo.Add(new SetupInfo
                    {
                        SchemaVersion = CurrentSchemaVersion,
                        ProvisionedAt = DateTime.UtcNow
                    });
                    await ctx.SaveChangesAsync(ct);
                }
            }

            return new ProvisionResult(true, null, 11, CurrentSchemaVersion);
        }
        catch (SqlException ex)
        {
            var (code, hint) = ClassifySqlError(ex, database);
            return new ProvisionResult(false, ex.Message, 0, CurrentSchemaVersion, code, hint);
        }
        catch (Exception ex)
        {
            return new ProvisionResult(false, ex.Message, 0, CurrentSchemaVersion, "unknown", null);
        }
    }

    private static (string code, string? hint) ClassifySqlError(SqlException ex, string database)
    {
        foreach (SqlError err in ex.Errors)
        {
            switch (err.Number)
            {
                case 5170:
                case 1802:
                    return (
                        "orphan_database_files",
                        $"Existem arquivos .mdf/.ldf antigos no disco do servidor com o nome '{database}', mas o database não está registrado no SQL Server. " +
                        "Apague os arquivos manualmente (ou anexe via SSMS) ou escolha outro nome de database.");
                case 1801:
                    return ("database_already_exists", $"O database '{database}' já existe. Use outro nome ou continue com este se ele já contém o schema dbsense.");
                case 262:
                case 15247:
                    return ("permission_denied", "O usuário informado não tem permissão para CREATE DATABASE no servidor. Use uma conta com a role 'dbcreator' ou sysadmin, ou crie o database manualmente.");
                case 18456:
                    return ("login_failed", "Login inválido. Verifique usuário e senha.");
                case 4060:
                    return ("cannot_open_database", $"Não foi possível abrir o database '{database}'. Verifique permissões do usuário.");
                case 53:
                case 40:
                    return ("server_unreachable", "Não foi possível conectar ao servidor SQL informado.");
            }
        }
        return ("sql_error", null);
    }

    private static async Task EnsureDatabaseExistsAsync(
        string server, string database, string authType, string? username, string? password,
        CancellationToken ct)
    {
        var masterCs = BuildConnectionString(server, "master", authType, username, password);
        await using var conn = new SqlConnection(masterCs);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
IF DB_ID(@name) IS NULL
BEGIN
    DECLARE @sql NVARCHAR(MAX) = N'CREATE DATABASE ' + QUOTENAME(@name);
    EXEC(@sql);
END";
        var p = cmd.CreateParameter();
        p.ParameterName = "@name";
        p.Value = database;
        cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SetupStatus> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
            if (!await ctx.Database.CanConnectAsync(ct))
                return new SetupStatus("not_provisioned", null, null);

            var info = await ctx.SetupInfo.OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
            if (info is null)
                return new SetupStatus("not_provisioned", null, null);

            var hasAdmin = await ctx.Users.AnyAsync(u => u.Role == "admin", ct);
            return new SetupStatus(
                hasAdmin ? "ready" : "pending_admin",
                info.SchemaVersion,
                info.ProvisionedAt);
        }
        catch
        {
            return new SetupStatus("not_provisioned", null, null);
        }
    }

    public static string BuildConnectionString(
        string server, string database, string authType, string? username, string? password)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            TrustServerCertificate = true,
            Encrypt = true,
            ConnectTimeout = 10
        };

        if (string.Equals(authType, "windows", StringComparison.OrdinalIgnoreCase))
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = username ?? string.Empty;
            builder.Password = password ?? string.Empty;
        }

        return builder.ConnectionString;
    }
}
