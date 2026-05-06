using System.Data;
using System.Text.Json;
using DbSense.Core.Persistence;
using DbSense.Core.Security;
using DbSense.Core.XEvents;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DbSense.Core.Reactions;

public class SqlReactionHandler : IReactionHandler
{
    private readonly IDbContextFactory<DbSenseContext> _contextFactory;
    private readonly ISecretCipher _cipher;

    public SqlReactionHandler(IDbContextFactory<DbSenseContext> contextFactory, ISecretCipher cipher)
    {
        _contextFactory = contextFactory;
        _cipher = cipher;
    }

    public string Type => "sql";

    public async Task<ReactionResult> ExecuteAsync(ReactionContext ctx, CancellationToken ct = default)
    {
        var connIdStr = TryGetString(ctx.Config, "connection_id");
        if (!Guid.TryParse(connIdStr, out var connId))
            return new ReactionResult(false, "config.connection_id ausente ou inválido.");

        var sql = TryGetString(ctx.Config, "sql");
        if (string.IsNullOrWhiteSpace(sql))
            return new ReactionResult(false, "config.sql ausente.");

        var commandTimeout = TryGetInt(ctx.Config, "command_timeout_ms", 10000);

        await using var dbCtx = await _contextFactory.CreateDbContextAsync(ct);
        var connection = await dbCtx.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == connId, ct);
        if (connection is null)
            return new ReactionResult(false, $"Conexão {connId} não encontrada.");

        var password = connection.PasswordEncrypted is { Length: > 0 }
            ? _cipher.Decrypt(connection.PasswordEncrypted)
            : null;

        var csb = new SqlConnectionStringBuilder
        {
            DataSource = connection.Server,
            InitialCatalog = connection.Database,
            TrustServerCertificate = true,
            Encrypt = true,
            ConnectTimeout = 10,
            // Marca a conexão da reaction como tráfego do próprio Worker — o filtro
            // das XE sessions (sql_batch_completed/rpc_completed) exclui client_app_name
            // = "DbSense.Worker", evitando que o INSERT/UPDATE da reaction polua a
            // própria gravação ou dispare uma rule recursivamente.
            ApplicationName = ProductionXeStream.SelfAppName
        };
        if (string.Equals(connection.AuthType, "windows", StringComparison.OrdinalIgnoreCase))
            csb.IntegratedSecurity = true;
        else
        {
            csb.UserID = connection.Username ?? "";
            csb.Password = password ?? "";
        }

        try
        {
            await using var sqlConn = new SqlConnection(csb.ConnectionString);
            await sqlConn.OpenAsync(ct);
            await using var cmd = sqlConn.CreateCommand();
            cmd.CommandTimeout = Math.Max(1, commandTimeout / 1000);

            if (sql.TrimStart().StartsWith("EXEC ", StringComparison.OrdinalIgnoreCase))
            {
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandText = sql.Trim()[5..].Trim().Split(' ', 2)[0];
            }
            else
            {
                cmd.CommandType = CommandType.Text;
                cmd.CommandText = sql;
            }

            if (ctx.Config.TryGetProperty("parameters", out var paramsEl)
                && paramsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in paramsEl.EnumerateObject())
                    cmd.Parameters.AddWithValue(p.Name, JsonValueToDbValue(p.Value));
            }

            var affected = await cmd.ExecuteNonQueryAsync(ct);
            return new ReactionResult(true, null, AffectedRows: affected);
        }
        catch (Exception ex)
        {
            return new ReactionResult(false, ex.Message);
        }
    }

    private static object JsonValueToDbValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null => DBNull.Value,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDouble(),
        JsonValueKind.String => (object?)v.GetString() ?? DBNull.Value,
        _ => v.GetRawText()
    };

    private static string? TryGetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int TryGetInt(JsonElement el, string name, int fallback) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i : fallback;
}
