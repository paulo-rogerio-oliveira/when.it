using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbSense.Core.Domain;
using DbSense.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DbSense.Core.Reactions;

public interface IOutboxEnqueuer
{
    // Recebe a regra resolvida + payload do evento, expande placeholders, e
    // grava events_log + outbox na mesma transação.
    Task<EnqueueResult> EnqueueAsync(EnqueueRequest req, CancellationToken ct = default);
}

// Payload         = JSON publicado (após shape se houver) — gravado em events_log/outbox.Payload.
// RawPayload      = JSON cru com { after, _meta } — usado pra resolver placeholders na reaction
//                   config; expandir contra Payload quebraria $.after.X quando há shape ativo.
public record EnqueueRequest(
    Rule Rule,
    JsonElement Payload,
    JsonElement RawPayload,
    DateTime SqlTimestamp,
    string IdempotencyKeySuffix);

public record EnqueueResult(long EventsLogId, long OutboxId, string IdempotencyKey);

public class OutboxEnqueuer : IOutboxEnqueuer
{
    private readonly IDbContextFactory<DbSenseContext> _contextFactory;

    public OutboxEnqueuer(IDbContextFactory<DbSenseContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<EnqueueResult> EnqueueAsync(EnqueueRequest req, CancellationToken ct = default)
    {
        var (reactionType, configJson) = ExtractReaction(req.Rule.Definition)
            ?? throw new InvalidOperationException("Regra não tem reaction configurada.");

        // Resolve contra o RAW (não o shaped): placeholders $.after.X / $trigger.X / $event.X
        // sempre apontam pros campos do evento original, independente de a rule ter shape ativo.
        var resolvedConfig = PlaceholderExpander.Expand(
            configJson, req.RawPayload, req.Rule.Id, req.Rule.Version);

        var idempotencyKey = ComputeIdempotencyKey(req.Rule.Id, req.Rule.Version, req.IdempotencyKeySuffix);
        var payloadJson = req.Payload.GetRawText();
        var now = DateTime.UtcNow;

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);

        // EnableRetryOnFailure exige que transações user-initiated rodem dentro do ExecutionStrategy
        // pra que o bloco inteiro seja replay-safe.
        var strategy = ctx.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await ctx.Database.BeginTransactionAsync(ct);

            var log = new EventLog
            {
                RuleId = req.Rule.Id,
                ConnectionId = req.Rule.ConnectionId,
                MatchedAt = now,
                SqlTimestamp = req.SqlTimestamp,
                EventPayload = payloadJson,
                IdempotencyKey = idempotencyKey,
                PublishStatus = "pending",
                PublishAttempts = 0
            };
            ctx.EventsLog.Add(log);
            await ctx.SaveChangesAsync(ct);

            var outbox = new OutboxMessage
            {
                EventsLogId = log.Id,
                Payload = payloadJson,
                ReactionType = reactionType,
                ReactionConfig = resolvedConfig,
                Status = "pending",
                Attempts = 0,
                NextAttemptAt = now
            };
            ctx.Outbox.Add(outbox);
            await ctx.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);
            return new EnqueueResult(log.Id, outbox.Id, idempotencyKey);
        });
    }

    private static (string Type, string ConfigJson)? ExtractReaction(string definitionJson)
    {
        using var doc = JsonDocument.Parse(definitionJson);
        if (!doc.RootElement.TryGetProperty("reaction", out var reaction)
            || reaction.ValueKind != JsonValueKind.Object)
            return null;
        if (!reaction.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            return null;
        var type = typeEl.GetString()!;
        var configJson = reaction.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object
            ? config.GetRawText()
            : "{}";
        return (type, configJson);
    }

    private static string ComputeIdempotencyKey(Guid ruleId, int version, string suffix)
    {
        var raw = $"{ruleId:N}:{version}:{suffix}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..32].ToLowerInvariant();
    }
}
