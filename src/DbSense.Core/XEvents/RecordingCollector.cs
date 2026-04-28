using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;
using DbSense.Core.Domain;
using DbSense.Core.Persistence;
using DbSense.Core.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DbSense.Core.XEvents;

public interface IRecordingCollector
{
    Task StartAsync(Guid recordingId, CancellationToken ct = default);
    Task StopAsync(Guid recordingId, CancellationToken ct = default);
}

// Captura eventos via XEvents lendo do ring_buffer com Microsoft.Data.SqlClient.
// Trocamos XELiveEventStreamer por polling direto porque o XELite arrasta o
// System.Data.SqlClient legado, que quebra leituras streaming longas em
// SQL Server moderno (TryReadInternal/TDS errors).
public class RecordingCollector : IRecordingCollector
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IDbContextFactory<DbSenseContext> _contextFactory;
    private readonly ISecretCipher _cipher;
    private readonly ILogger<RecordingCollector> _logger;
    private readonly ConcurrentDictionary<Guid, ActiveSession> _active = new();

    public RecordingCollector(
        IDbContextFactory<DbSenseContext> contextFactory,
        ISecretCipher cipher,
        ILogger<RecordingCollector> logger)
    {
        _contextFactory = contextFactory;
        _cipher = cipher;
        _logger = logger;
    }

    public async Task StartAsync(Guid recordingId, CancellationToken ct = default)
    {
        if (_active.ContainsKey(recordingId))
        {
            _logger.LogWarning("Recording {Id} already active, ignoring duplicate start.", recordingId);
            return;
        }

        Recording rec;
        Connection conn;
        await using (var ctx = await _contextFactory.CreateDbContextAsync(ct))
        {
            rec = await ctx.Recordings.FirstOrDefaultAsync(r => r.Id == recordingId, ct)
                ?? throw new InvalidOperationException($"Recording {recordingId} não encontrada.");
            conn = await ctx.Connections.FirstOrDefaultAsync(c => c.Id == rec.ConnectionId, ct)
                ?? throw new InvalidOperationException($"Connection {rec.ConnectionId} não encontrada.");
        }

        var password = conn.PasswordEncrypted is { Length: > 0 } ? _cipher.Decrypt(conn.PasswordEncrypted) : null;
        var targetCs = BuildConnectionString(conn.Server, conn.Database, conn.AuthType, conn.Username, password);

        var sessionName = $"dbsense_rec_{recordingId:N}";
        await CreateAndStartSessionAsync(targetCs, sessionName, conn.Database,
            rec.FilterHostName, rec.FilterAppName, rec.FilterLoginName, ct);

        var cts = new CancellationTokenSource();
        var runTask = Task.Run(() => PollLoopAsync(recordingId, sessionName, targetCs, cts.Token));
        _active[recordingId] = new ActiveSession(sessionName, targetCs, cts, runTask);

        _logger.LogInformation("Recording {Id} started, XE session {Session}.", recordingId, sessionName);
    }

    public async Task StopAsync(Guid recordingId, CancellationToken ct = default)
    {
        if (!_active.TryRemove(recordingId, out var session))
        {
            _logger.LogInformation("Stop requested but no active session for {Id} (worker may have restarted).", recordingId);
            return;
        }

        session.Cts.Cancel();
        try { await session.RunTask.WaitAsync(TimeSpan.FromSeconds(15), ct); }
        catch (TimeoutException) { _logger.LogWarning("Recording {Id} poll did not finish within timeout.", recordingId); }
        catch (OperationCanceledException) { /* expected */ }

        try { await DropSessionIfExistsAsync(session.TargetCs, session.SessionName, ct); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to drop XE session {Session}.", session.SessionName); }

        session.Cts.Dispose();
        _logger.LogInformation("Recording {Id} stopped.", recordingId);
    }

    private async Task PollLoopAsync(Guid recordingId, string sessionName, string targetCs, CancellationToken token)
    {
        DateTime watermarkTs = DateTime.MinValue;
        int seenAtWatermark = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var xml = await ReadRingBufferXmlAsync(targetCs, sessionName, token);
                if (!string.IsNullOrEmpty(xml))
                {
                    var parsed = ParseRingBuffer(xml, recordingId)
                        .OrderBy(e => e.EventTimestamp)
                        .ToList();

                    var newEvents = new List<RecordingEvent>();
                    int countAtCurrentTs = 0;
                    DateTime? lastTs = null;

                    foreach (var ev in parsed)
                    {
                        if (lastTs != ev.EventTimestamp)
                        {
                            lastTs = ev.EventTimestamp;
                            countAtCurrentTs = 0;
                        }
                        countAtCurrentTs++;

                        if (ev.EventTimestamp < watermarkTs) continue;
                        if (ev.EventTimestamp == watermarkTs && countAtCurrentTs <= seenAtWatermark) continue;
                        newEvents.Add(ev);
                    }

                    if (newEvents.Count > 0)
                    {
                        await PersistAsync(recordingId, newEvents, token);

                        var maxTs = newEvents.Max(e => e.EventTimestamp);
                        var atMax = parsed.Count(e => e.EventTimestamp == maxTs);
                        if (maxTs > watermarkTs)
                        {
                            watermarkTs = maxTs;
                            seenAtWatermark = atMax;
                        }
                        else
                        {
                            seenAtWatermark = atMax;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ring buffer poll failed for recording {Id}.", recordingId);
            }

            try { await Task.Delay(PollInterval, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PersistAsync(Guid recordingId, List<RecordingEvent> events, CancellationToken token)
    {
        try
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync(CancellationToken.None);
            ctx.RecordingEvents.AddRange(events);
            var rec = await ctx.Recordings.FirstOrDefaultAsync(r => r.Id == recordingId, CancellationToken.None);
            if (rec is not null) rec.EventCount += events.Count;
            await ctx.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist {Count} events for recording {Id}.", events.Count, recordingId);
        }
    }

    private static async Task<string?> ReadRingBufferXmlAsync(string cs, string sessionName, CancellationToken ct)
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

    private static List<RecordingEvent> ParseRingBuffer(string xml, Guid recordingId)
    {
        var events = new List<RecordingEvent>();
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

            events.Add(new RecordingEvent
            {
                RecordingId = recordingId,
                EventTimestamp = ts,
                EventType = name,
                SessionId = I(Action("session_id")) ?? 0,
                DatabaseName = Action("database_name") ?? string.Empty,
                ObjectName = Data("object_name"),
                SqlText = sqlText,
                Statement = statement,
                DurationUs = L(Data("duration")) ?? 0,
                CpuTimeUs = L(Data("cpu_time")),
                Reads = L(Data("logical_reads")),
                Writes = L(Data("writes")),
                RowCount = L(Data("row_count")),
                AppName = Action("client_app_name"),
                HostName = Action("client_hostname"),
                LoginName = Action("username"),
                TransactionId = L(Action("transaction_id"))
            });
        }
        return events;
    }

    private static async Task CreateAndStartSessionAsync(
        string targetCs, string sessionName, string database,
        string? hostName, string? appName, string? loginName, CancellationToken ct)
    {
        var predicateParts = new List<string>
        {
            $"sqlserver.database_name = N{Q(database)}",
            // Exclui o próprio tráfego do Worker (polling do ring_buffer + DDL)
            // pra não poluir a gravação com nossa própria atividade.
            $"sqlserver.client_app_name <> N{Q("DbSense.Worker")}"
        };
        if (!string.IsNullOrWhiteSpace(hostName))
            predicateParts.Add($"sqlserver.client_hostname = N{Q(hostName)}");
        if (!string.IsNullOrWhiteSpace(appName))
            predicateParts.Add($"sqlserver.client_app_name = N{Q(appName)}");
        if (!string.IsNullOrWhiteSpace(loginName))
            predicateParts.Add($"sqlserver.username = N{Q(loginName)}");
        var pred = string.Join(" AND ", predicateParts);
        var actions = "ACTION (sqlserver.database_name, sqlserver.session_id, sqlserver.client_app_name, sqlserver.client_hostname, sqlserver.username, sqlserver.transaction_id)";

        var ddl = $@"
IF EXISTS(SELECT 1 FROM sys.server_event_sessions WHERE name = N{Q(sessionName)})
    DROP EVENT SESSION {QuoteName(sessionName)} ON SERVER;

CREATE EVENT SESSION {QuoteName(sessionName)} ON SERVER
    ADD EVENT sqlserver.sql_batch_completed({actions} WHERE ({pred})),
    ADD EVENT sqlserver.rpc_completed({actions} WHERE ({pred})),
    ADD EVENT sqlserver.sp_statement_completed({actions} WHERE ({pred}))
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

    private static async Task DropSessionIfExistsAsync(string targetCs, string sessionName, CancellationToken ct)
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

    private static string BuildConnectionString(
        string server, string database, string authType, string? username, string? password)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            TrustServerCertificate = true,
            Encrypt = true,
            ConnectTimeout = 10,
            ApplicationName = "DbSense.Worker"
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

    private static string Q(string v) => "'" + v.Replace("'", "''") + "'";
    private static string QuoteName(string v) => "[" + v.Replace("]", "]]") + "]";

    private sealed record ActiveSession(
        string SessionName,
        string TargetCs,
        CancellationTokenSource Cts,
        Task RunTask);
}
