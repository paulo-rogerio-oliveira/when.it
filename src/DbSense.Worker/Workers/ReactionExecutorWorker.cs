using System.Text.Json;
using DbSense.Core.Persistence;
using DbSense.Core.Domain;
using DbSense.Core.Reactions;
using Microsoft.EntityFrameworkCore;

namespace DbSense.Worker.Workers;

// Lê dbsense.outbox em status pending, faz lock pessimista (UPDATE...OUTPUT com READPAST
// para não bloquear outros workers) e despacha pelo IReactionDispatcher. Sucesso → processed.
// Falha → backoff exponencial até MaxAttempts e então marca failed (DLQ).
public class ReactionExecutorWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);
    private const int BatchSize = 25;
    private const int MaxAttempts = 5;

    private readonly IDbContextFactory<DbSenseContext> _contextFactory;
    private readonly IReactionDispatcher _dispatcher;
    private readonly ILogger<ReactionExecutorWorker> _logger;
    private readonly string _instanceId;

    public ReactionExecutorWorker(
        IDbContextFactory<DbSenseContext> contextFactory,
        IReactionDispatcher dispatcher,
        IConfiguration config,
        ILogger<ReactionExecutorWorker> logger)
    {
        _contextFactory = contextFactory;
        _dispatcher = dispatcher;
        _logger = logger;
        _instanceId = config["Worker:InstanceId"] ?? Environment.MachineName;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ReactionExecutorWorker started (instance={Instance}).", _instanceId);

        // Auto-migra o schema do outbox para o formato com ReactionType/Config/LastError.
        // Idempotente; apenas relevante em dev (em prod seria migration versionada).
        try
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync(stoppingToken);
            await OutboxSchemaMigrator.EnsureUpToDateAsync(ctx, stoppingToken);
            await RecordingSchemaMigrator.EnsureUpToDateAsync(ctx, stoppingToken);
            _logger.LogInformation("Outbox + Recording schema verificados/migrados.");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Falha ao migrar schema do outbox: {Message}. " +
                "Para resolver manualmente: DROP TABLE dbsense.outbox; e reinicie o worker.",
                ex.Message);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await PollOnceAsync(stoppingToken);
                if (processed == 0)
                {
                    try { await Task.Delay(PollInterval, stoppingToken); }
                    catch (OperationCanceledException) { /* shutting down */ }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro no loop do ReactionExecutorWorker.");
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
                catch (OperationCanceledException) { /* shutting down */ }
            }
        }
    }

    private async Task<int> PollOnceAsync(CancellationToken ct)
    {
        var locked = await LockBatchAsync(ct);
        if (locked.Count == 0) return 0;

        foreach (var msg in locked)
            await ProcessAsync(msg, ct);

        return locked.Count;
    }

    private async Task<List<OutboxMessage>> LockBatchAsync(CancellationToken ct)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        if (!await ctx.Database.CanConnectAsync(ct)) return new();

        // UPDATE...OUTPUT com READPAST: cada worker pega um lote distinto sem se bloquear.
        // Colunas em PascalCase porque o EF Core mapeia properties → colunas com o nome da property
        // (não há HasColumnName em DbSenseContext.OutboxMessage).
        const string sql = """
            UPDATE TOP (@batch) o
            SET [Status] = 'processing',
                LockedBy = @me,
                LockedUntil = @lockUntil,
                Attempts = Attempts + 1
            OUTPUT
                inserted.Id,
                inserted.EventsLogId,
                inserted.Payload,
                inserted.ReactionType,
                inserted.ReactionConfig,
                inserted.[Status],
                inserted.Attempts,
                inserted.NextAttemptAt,
                inserted.LockedBy,
                inserted.LockedUntil,
                inserted.LastError
            FROM dbsense.outbox AS o WITH (ROWLOCK, READPAST)
            WHERE o.[Status] = 'pending' AND o.NextAttemptAt <= SYSUTCDATETIME();
            """;

        var batchParam = new Microsoft.Data.SqlClient.SqlParameter("@batch", BatchSize);
        var meParam = new Microsoft.Data.SqlClient.SqlParameter("@me", _instanceId);
        var lockUntilParam = new Microsoft.Data.SqlClient.SqlParameter("@lockUntil", DateTime.UtcNow.Add(LockDuration));

        var rows = await ctx.Outbox
            .FromSqlRaw(sql, batchParam, meParam, lockUntilParam)
            .AsNoTracking()
            .ToListAsync(ct);
        return rows;
    }

    private async Task ProcessAsync(OutboxMessage msg, CancellationToken ct)
    {
        var ruleId = await GetRuleIdAsync(msg.EventsLogId, ct);
        var ruleVersion = 0;
        Rule? rule = null;
        if (ruleId.HasValue)
        {
            await using var ctxRule = await _contextFactory.CreateDbContextAsync(ct);
            rule = await ctxRule.Rules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == ruleId.Value, ct);
            ruleVersion = rule?.Version ?? 0;
        }

        ReactionResult result;
        try
        {
            using var configDoc = JsonDocument.Parse(msg.ReactionConfig);
            var reactionCtx = new ReactionContext(
                EventsLogId: msg.EventsLogId,
                PayloadJson: msg.Payload,
                Config: configDoc.RootElement.Clone(),
                IdempotencyKey: await GetIdempotencyKeyAsync(msg.EventsLogId, ct) ?? "",
                RuleId: rule?.Id ?? Guid.Empty,
                RuleVersion: ruleVersion);

            result = await _dispatcher.DispatchAsync(msg.ReactionType, reactionCtx, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao despachar outbox {Id} ({Type}).", msg.Id, msg.ReactionType);
            result = new ReactionResult(false, ex.Message);
        }

        await UpdateOutcomeAsync(msg, result, ct);
    }

    private async Task<Guid?> GetRuleIdAsync(long eventsLogId, CancellationToken ct)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        return await ctx.EventsLog.AsNoTracking()
            .Where(e => e.Id == eventsLogId)
            .Select(e => (Guid?)e.RuleId)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<string?> GetIdempotencyKeyAsync(long eventsLogId, CancellationToken ct)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        return await ctx.EventsLog.AsNoTracking()
            .Where(e => e.Id == eventsLogId)
            .Select(e => e.IdempotencyKey)
            .FirstOrDefaultAsync(ct);
    }

    private async Task UpdateOutcomeAsync(OutboxMessage msg, ReactionResult result, CancellationToken ct)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var dbMsg = await ctx.Outbox.FirstOrDefaultAsync(o => o.Id == msg.Id, ct);
        var dbLog = await ctx.EventsLog.FirstOrDefaultAsync(e => e.Id == msg.EventsLogId, ct);
        if (dbMsg is null) return;

        if (result.Success)
        {
            dbMsg.Status = "processed";
            dbMsg.LastError = null;
            dbMsg.LockedBy = null;
            dbMsg.LockedUntil = null;
            if (dbLog is not null)
            {
                dbLog.PublishStatus = "published";
                dbLog.PublishedAt = DateTime.UtcNow;
                dbLog.LastError = null;
                dbLog.PublishAttempts = msg.Attempts;
            }
        }
        else
        {
            var attempts = msg.Attempts;
            dbMsg.LastError = result.Error;
            dbMsg.LockedBy = null;
            dbMsg.LockedUntil = null;
            if (attempts >= MaxAttempts)
            {
                dbMsg.Status = "failed";
                if (dbLog is not null)
                {
                    dbLog.PublishStatus = "dead_lettered";
                    dbLog.LastError = result.Error;
                    dbLog.PublishAttempts = attempts;
                }
            }
            else
            {
                dbMsg.Status = "pending";
                dbMsg.NextAttemptAt = DateTime.UtcNow.Add(BackoffFor(attempts));
                if (dbLog is not null)
                {
                    dbLog.PublishStatus = "pending";
                    dbLog.LastError = result.Error;
                    dbLog.PublishAttempts = attempts;
                }
            }
        }

        await ctx.SaveChangesAsync(ct);
    }

    private static TimeSpan BackoffFor(int attempts)
    {
        var seconds = Math.Min(300, Math.Pow(2, attempts));
        return TimeSpan.FromSeconds(seconds);
    }
}
