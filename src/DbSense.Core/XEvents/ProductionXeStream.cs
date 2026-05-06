using System.Globalization;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace DbSense.Core.XEvents;

// Sessão XE por conexão "active" (uso em produção, não em gravação).
// Espelha o esqueleto de RecordingCollector mas:
//   - sem filtro de session/host/login (escuta tudo no database)
//   - sem persistência em recording_events; o consumidor recebe os eventos in-memory
//   - lifecycle simples: Start/Stop por connectionId
public record CapturedSqlEvent(
    DateTime Timestamp,
    string EventType,
    int SessionId,
    string DatabaseName,
    string? ObjectName,
    string SqlText,
    string? Statement,
    long DurationUs,
    long? TransactionId,
    string? AppName,
    string? HostName,
    string? LoginName);

public static class ProductionXeStream
{
    public const string SelfAppName = "DbSense.Worker";

    public static async Task CreateAndStartSessionAsync(
        string targetCs, string sessionName, string database, CancellationToken ct)
    {
        // Filtra por database e exclui o próprio app (DbSense.Worker) pra não pegar
        // o tráfego do polling do ring_buffer e poluir o matcher.
        var pred = $"sqlserver.database_name = N{Q(database)} AND sqlserver.client_app_name <> N{Q(SelfAppName)}";
        var actions = "ACTION (sqlserver.database_name, sqlserver.session_id, sqlserver.client_app_name, sqlserver.client_hostname, sqlserver.username, sqlserver.transaction_id)";

        // Eventos capturados pra produção (matcher):
        //   - sql_batch_completed: cobre batches ad-hoc (SSMS, scripts) — batch_text contém o
        //     T-SQL inteiro, parser extrai DMLs e seus valores literais direto.
        //   - rpc_completed: cobre chamadas via EF/sp_executesql — batch_text contém o
        //     EXEC sp_executesql N'...', @p0=... completo. TryUnwrapSpExecuteSql desempacota
        //     o SQL embutido E popula o paramMap com os valores reais, fazendo @pN resolver.
        // sp_statement_completed é deliberadamente omitido: ele é granular MAS perde os valores
        // (statement só tem @pN sem os valores), e duplica com rpc_completed quando ambos
        // estão ativos (mesmos DMLs em eventos com timestamps diferentes; idempotency key
        // não dedupa). Os canais batch/rpc não se sobrepõem entre si: ad-hoc só emite batch,
        // sp_executesql só emite rpc.
        var ddl = $@"
IF EXISTS(SELECT 1 FROM sys.server_event_sessions WHERE name = N{Q(sessionName)})
    DROP EVENT SESSION {QuoteName(sessionName)} ON SERVER;

CREATE EVENT SESSION {QuoteName(sessionName)} ON SERVER
    ADD EVENT sqlserver.sql_batch_completed({actions} WHERE ({pred})),
    ADD EVENT sqlserver.rpc_completed({actions} WHERE ({pred}))
    ADD TARGET package0.ring_buffer(SET max_memory=(8192))
    WITH (EVENT_RETENTION_MODE=ALLOW_SINGLE_EVENT_LOSS, MAX_DISPATCH_LATENCY=1 SECONDS);

ALTER EVENT SESSION {QuoteName(sessionName)} ON SERVER STATE = START;
";

        await using var conn = new SqlConnection(targetCs);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task DropSessionIfExistsAsync(string targetCs, string sessionName, CancellationToken ct)
    {
        var sql = $@"
IF EXISTS(SELECT 1 FROM sys.server_event_sessions WHERE name = N{Q(sessionName)})
    DROP EVENT SESSION {QuoteName(sessionName)} ON SERVER;
";
        await using var conn = new SqlConnection(targetCs);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<string?> ReadRingBufferXmlAsync(string cs, string sessionName, CancellationToken ct)
    {
        const string sql = @"
SELECT CAST(t.target_data AS nvarchar(max))
FROM sys.dm_xe_sessions s
JOIN sys.dm_xe_session_targets t ON t.event_session_address = s.address
WHERE s.name = @name AND t.target_name = 'ring_buffer';";

        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqlParameter("@name", sessionName));
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    public static List<CapturedSqlEvent> ParseRingBuffer(string xml)
    {
        var events = new List<CapturedSqlEvent>();
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { return events; }

        foreach (var ev in doc.Descendants("event"))
        {
            var name = ev.Attribute("name")?.Value ?? string.Empty;
            var tsRaw = ev.Attribute("timestamp")?.Value;
            if (!DateTime.TryParse(tsRaw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts))
                continue;

            string? Data(string n) => ev.Elements("data")
                .FirstOrDefault(d => string.Equals(d.Attribute("name")?.Value, n, StringComparison.Ordinal))
                ?.Element("value")?.Value;
            string? Action(string n) => ev.Elements("action")
                .FirstOrDefault(d => string.Equals(d.Attribute("name")?.Value, n, StringComparison.Ordinal))
                ?.Element("value")?.Value;
            long? L(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
            int? I(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

            var sqlText = Data("batch_text") ?? Data("statement") ?? string.Empty;
            var statement = string.Equals(name, "sp_statement_completed", StringComparison.OrdinalIgnoreCase)
                ? Data("statement")
                : null;

            events.Add(new CapturedSqlEvent(
                Timestamp: ts,
                EventType: name,
                SessionId: I(Action("session_id")) ?? 0,
                DatabaseName: Action("database_name") ?? string.Empty,
                ObjectName: Data("object_name"),
                SqlText: sqlText,
                Statement: statement,
                DurationUs: L(Data("duration")) ?? 0,
                TransactionId: L(Action("transaction_id")),
                AppName: Action("client_app_name"),
                HostName: Action("client_hostname"),
                LoginName: Action("username")));
        }
        return events;
    }

    private static string Q(string v) => "'" + v.Replace("'", "''") + "'";
    private static string QuoteName(string v) => "[" + v.Replace("]", "]]") + "]";
}
