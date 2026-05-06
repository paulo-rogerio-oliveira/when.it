using System.Collections.Concurrent;
using DbSense.Core.Domain;
using DbSense.Core.Inference;
using DbSense.Core.Persistence;
using DbSense.Core.Reactions;
using DbSense.Core.Rules;
using DbSense.Core.Security;
using DbSense.Core.XEvents;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DbSense.Worker.Workers;

// Orquestra: cache de regras ativas → ciclo de vida das sessões XE por conexão →
// poll do ring buffer → parse SQL → engine → enqueue outbox.
//
// Decisões pro MVP:
//   - Refresh do cache a cada CacheRefreshInterval (30s) E sob comando reload_rules
//     (que entra direto no cache via DI; o CommandProcessorWorker chama RefreshAsync).
//   - Um session XE por connection com regras ativas. Quando a connection sai do cache
//     (todas as regras pausadas/arquivadas), drop da session.
//   - Watermark por (connectionId, timestamp) pra evitar reprocessar eventos do ring buffer.
public class RuleMatcherWorker : BackgroundService
{
    private static readonly TimeSpan CacheRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IDbContextFactory<DbSenseContext> _contextFactory;
    private readonly IActiveRulesCache _cache;
    private readonly IRuleEngine _engine;
    private readonly IOutboxEnqueuer _enqueuer;
    private readonly ISecretCipher _cipher;
    private readonly ILogger<RuleMatcherWorker> _logger;

    private readonly ConcurrentDictionary<Guid, ActiveCollector> _collectors = new();
    private DateTime _lastCacheRefresh = DateTime.MinValue;

    public RuleMatcherWorker(
        IDbContextFactory<DbSenseContext> contextFactory,
        IActiveRulesCache cache,
        IRuleEngine engine,
        IOutboxEnqueuer enqueuer,
        ISecretCipher cipher,
        ILogger<RuleMatcherWorker> logger)
    {
        _contextFactory = contextFactory;
        _cache = cache;
        _engine = engine;
        _enqueuer = enqueuer;
        _cipher = cipher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RuleMatcherWorker started.");

        // Primeiro carregamento.
        await TryRefreshCacheAsync(stoppingToken, "Falha no refresh inicial do cache de regras.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - _lastCacheRefresh > CacheRefreshInterval)
                    await TryRefreshCacheAsync(stoppingToken, "Falha no refresh do cache de regras.");

                await ReconcileCollectorsAsync(stoppingToken);

                // Faz uma rodada de polling em todos os collectors ativos.
                foreach (var kvp in _collectors)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    try { await PollAndDispatchAsync(kvp.Value, stoppingToken); }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Falha no poll do connectionId={ConnectionId}.", kvp.Key);
                    }
                }

                // Drena pendings expirados (trigger casou mas required companions não chegaram a tempo).
                foreach (var ex in _engine.SweepExpired(DateTime.UtcNow))
                {
                    _logger.LogWarning(
                        "Pending match expirou sem completar — rule={Rule} trigger_ts={Ts} faltando=[{Missing}].",
                        ex.Rule.Name, ex.TriggerTs, string.Join(", ", ex.MissingCompanions));
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro no loop do RuleMatcherWorker.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        // Drop sessões ao parar. Timeout curto pra não pendurar o host se o SQL alvo
        // não responde — XE sessions órfãs são limpadas no próximo start de qualquer forma.
        using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var c in _collectors.Values)
        {
            try { await ProductionXeStream.DropSessionIfExistsAsync(c.TargetCs, c.SessionName, shutdownCts.Token); }
            catch { /* shutting down */ }
        }
    }

    // Avança _lastCacheRefresh independente do resultado pra não martelar o banco
    // de controle a cada 1s quando ele estiver fora — respeita o intervalo de 30s.
    private async Task TryRefreshCacheAsync(CancellationToken ct, string failureMessage)
    {
        try { await _cache.RefreshAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogError(ex, "{Message}", failureMessage); }
        finally { _lastCacheRefresh = DateTime.UtcNow; }
    }

    private async Task ReconcileCollectorsAsync(CancellationToken ct)
    {
        var desired = new HashSet<Guid>(_cache.ActiveConnectionIds);

        // Drop o que não é mais desejado.
        foreach (var existing in _collectors.Keys.ToList())
        {
            if (desired.Contains(existing)) continue;
            if (_collectors.TryRemove(existing, out var c))
            {
                try { await ProductionXeStream.DropSessionIfExistsAsync(c.TargetCs, c.SessionName, ct); }
                catch (Exception ex) { _logger.LogError(ex, "Falha ao parar XE session {Name}.", c.SessionName); }
                _logger.LogInformation("Coletor de produção encerrado para connection={ConnectionId}.", existing);
            }
        }

        // Cria os que faltam.
        foreach (var connectionId in desired)
        {
            if (_collectors.ContainsKey(connectionId)) continue;
            try
            {
                var collector = await BuildCollectorAsync(connectionId, ct);
                if (collector is null) continue;
                _collectors[connectionId] = collector;
                _logger.LogInformation("Coletor de produção iniciado para connection={ConnectionId} ({Db}).",
                    connectionId, collector.Database);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao iniciar coletor para connection={ConnectionId}.", connectionId);
            }
        }
    }

    private async Task<ActiveCollector?> BuildCollectorAsync(Guid connectionId, CancellationToken ct)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var conn = await ctx.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == connectionId, ct);
        if (conn is null)
        {
            _logger.LogWarning("Connection {Id} ausente no banco, pulando.", connectionId);
            return null;
        }

        var password = conn.PasswordEncrypted is { Length: > 0 } ? _cipher.Decrypt(conn.PasswordEncrypted) : null;
        var cs = BuildConnectionString(conn.Server, conn.Database, conn.AuthType, conn.Username, password);
        var sessionName = $"dbsense_match_{connectionId:N}";

        await ProductionXeStream.CreateAndStartSessionAsync(cs, sessionName, conn.Database, ct);
        return new ActiveCollector(connectionId, conn.Database, cs, sessionName);
    }

    private async Task PollAndDispatchAsync(ActiveCollector c, CancellationToken ct)
    {
        var xml = await ProductionXeStream.ReadRingBufferXmlAsync(c.TargetCs, c.SessionName, ct);
        if (string.IsNullOrEmpty(xml)) return;

        var parsed = ProductionXeStream.ParseRingBuffer(xml)
            .OrderBy(e => e.Timestamp)
            .ToList();

        var newEvents = new List<CapturedSqlEvent>();
        int countAtCurrentTs = 0;
        DateTime? lastTs = null;

        foreach (var ev in parsed)
        {
            if (lastTs != ev.Timestamp)
            {
                lastTs = ev.Timestamp;
                countAtCurrentTs = 0;
            }
            countAtCurrentTs++;

            if (ev.Timestamp < c.WatermarkTs) continue;
            if (ev.Timestamp == c.WatermarkTs && countAtCurrentTs <= c.SeenAtWatermark) continue;
            newEvents.Add(ev);
        }

        if (newEvents.Count == 0) return;

        foreach (var ev in newEvents)
        {
            try
            {
                await DispatchEventAsync(c.ConnectionId, ev, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao processar evento {Type} ts={Ts}.", ev.EventType, ev.Timestamp);
            }
        }

        var maxTs = newEvents.Max(e => e.Timestamp);
        var atMax = parsed.Count(e => e.Timestamp == maxTs);
        c.SetWatermark(maxTs, atMax);
    }

    private async Task DispatchEventAsync(Guid connectionId, CapturedSqlEvent ev, CancellationToken ct)
    {
        // SqlText é sempre o batch_text (sql_batch_completed pra ad-hoc, rpc_completed
        // pra sp_executesql) — contém os valores dos @pN no caso da RPC.
        var sqlText = ev.SqlText;
        if (string.IsNullOrWhiteSpace(sqlText)) return;

        var dmls = SqlParser.TryParseAll(sqlText);
        if (dmls.Count == 0) return;

        for (int i = 0; i < dmls.Count; i++)
        {
            var dml = dmls[i];
            var ctx = new EventContext(ev.Timestamp, ev.TransactionId, ev.SessionId, i);
            var matches = _engine.OnEvent(connectionId, ev.DatabaseName, dml, ctx);
            foreach (var m in matches)
                await EnqueueMatchAsync(m, ev.Timestamp, ct);
        }
    }

    private async Task EnqueueMatchAsync(RuleMatch m, DateTime sqlTimestamp, CancellationToken ct)
    {
        try
        {
            var result = await _enqueuer.EnqueueAsync(
                new EnqueueRequest(m.Rule, m.Payload, m.RawPayload, sqlTimestamp, m.IdempotencyKeySuffix), ct);
            _logger.LogInformation(
                "Match: rule={Rule} → outbox#{Outbox}.", m.Rule.Name, result.OutboxId);
        }
        catch (DbUpdateException dup) when (IsUniqueViolation(dup))
        {
            // Idempotency key colidiu: já enqueuamos esse mesmo trigger. Skip silencioso.
            _logger.LogDebug("Match já enqueuado (idempotency hit) — rule={Rule}.", m.Rule.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao enqueuar match da rule={Rule}.", m.Rule.Name);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException sql && (sql.Number == 2627 || sql.Number == 2601);

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
            ApplicationName = ProductionXeStream.SelfAppName
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

    private sealed class ActiveCollector
    {
        public Guid ConnectionId { get; }
        public string Database { get; }
        public string TargetCs { get; }
        public string SessionName { get; }
        public DateTime WatermarkTs { get; private set; } = DateTime.MinValue;
        public int SeenAtWatermark { get; private set; }

        public ActiveCollector(Guid connectionId, string database, string targetCs, string sessionName)
        {
            ConnectionId = connectionId;
            Database = database;
            TargetCs = targetCs;
            SessionName = sessionName;
        }

        public void SetWatermark(DateTime ts, int countAtTs)
        {
            if (ts > WatermarkTs)
            {
                WatermarkTs = ts;
                SeenAtWatermark = countAtTs;
            }
            else if (ts == WatermarkTs)
            {
                SeenAtWatermark = countAtTs;
            }
        }
    }
}
