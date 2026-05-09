using System.Collections.Concurrent;
using System.Text.Json;
using DbSense.Core.Domain;
using DbSense.Core.Inference;
using DbSense.Core.Reactions;

namespace DbSense.Core.Rules;

public interface IRuleEngine
{
    // Processa um evento DML capturado. Pode:
    //   - completar pending matches existentes (companion required satisfez o gate);
    //   - criar novos pendings (trigger casou mas faltam required companions);
    //   - disparar matches imediatos (trigger casou e regra não tem required companions).
    IReadOnlyList<RuleMatch> OnEvent(
        Guid connectionId, string databaseName, ParsedDml dml, EventContext ctx);

    // Limpa pendings cuja deadline expirou sem completar todos os required.
    // Retornados só pra observabilidade — NÃO viram reaction.
    IReadOnlyList<ExpiredMatch> SweepExpired(DateTime now);
}

public record EventContext(DateTime Timestamp, long? TransactionId, int SessionId, int DmlIndex);

// Payload         = JSON publicado (após shape, se houver) — vai pra outbox/events_log
// RawPayload      = JSON cru { after, _meta } — usado pra resolver placeholders na reaction config
//                   (assim $.after.X funciona mesmo quando a rule tem shape que renomeia campos)
public record RuleMatch(Rule Rule, JsonElement Payload, JsonElement RawPayload, string IdempotencyKeySuffix);

public record ExpiredMatch(Rule Rule, DateTime TriggerTs, IReadOnlyList<string> MissingCompanions);

// Engine stateful (singleton): mantém pending matches por connection enquanto aguarda
// companions required dentro da janela de correlação.
//
// Decisões de design:
//   - Trigger é a "âncora": idempotency key é calculada na criação do pending (ou no
//     match imediato), garantindo que reentregas do mesmo evento de trigger não dupliquem.
//   - scope=time_window: companion deve chegar com ts ≤ deadline (trigger_ts + wait_ms).
//   - scope=transaction: companion deve compartilhar TransactionId com o trigger.
//     Deadline ainda existe (usa wait_ms) pra não vazar memória se a transação morrer.
//   - scope=none mas com required > 0 (não esperado da inferência atual, mas possível em
//     edição manual): tratamos como time_window com wait_ms.
//   - Companions optional (required=false) são IGNORADOS no MVP — não bloqueiam, não entram
//     no payload. Se quiser usá-los, refatorar payload pra incluir companions resolvidos.
//   - Payload publicado vem só do trigger (mantém compat com $.after.X dos shapes existentes).
public class RuleEngine : IRuleEngine
{
    private readonly IActiveRulesCache _cache;
    private readonly ConcurrentDictionary<Guid, ConnectionState> _state = new();

    private const int DefaultWaitMs = 30_000;

    public RuleEngine(IActiveRulesCache cache)
    {
        _cache = cache;
    }

    public IReadOnlyList<RuleMatch> OnEvent(
        Guid connectionId, string databaseName, ParsedDml dml, EventContext ctx)
    {
        if (!_cache.Snapshot.TryGetValue(connectionId, out var rules) || rules.Count == 0)
            return Array.Empty<RuleMatch>();

        var afterFields = BuildAfterFields(dml);
        var beforeFields = BuildBeforeFields(dml);
        var operation = OperationToString(dml.Operation);
        var connState = _state.GetOrAdd(connectionId, _ => new ConnectionState());
        var completed = new List<RuleMatch>();

        lock (connState.Sync)
        {
            // 1) Tenta satisfazer companions de pending matches existentes.
            //    Iteramos de trás pra frente pra remover sem invalidar índices.
            for (int i = connState.Pending.Count - 1; i >= 0; i--)
            {
                var pm = connState.Pending[i];

                // Filtros de scope: time_window olha o relógio, transaction olha a TX.
                if (pm.Scope == "transaction")
                {
                    if (ctx.TransactionId is null || ctx.TransactionId != pm.TriggerTransactionId)
                        continue;
                }
                else
                {
                    // time_window (e fallback do "none" com required > 0)
                    if (ctx.Timestamp > pm.Deadline) continue;
                }

                var idx = pm.RequiredPending.FindIndex(c => CompanionMatches(c, dml, operation));
                if (idx < 0) continue;

                pm.RequiredPending.RemoveAt(idx);
                if (pm.RequiredPending.Count == 0)
                {
                    completed.Add(new RuleMatch(pm.Rule, pm.ShapedPayload, pm.RawPayload, pm.IdempotencyKeySuffix));
                    connState.Pending.RemoveAt(i);
                }
            }

            // 2) Avalia trigger: o evento atual pode disparar regras (criando pending
            //    ou match imediato se não há required companions).
            foreach (var rule in rules)
            {
                if (!TryMatchTrigger(rule, databaseName, dml, afterFields)) continue;

                var (scope, waitMs, requiredCompanions) = ParseCorrelation(rule);
                var rawPayload = BuildPayloadElement(
                    afterFields, beforeFields, ctx.Timestamp, dml.Table, dml.Schema, operation);
                var shaped = TryApplyShape(rule, rawPayload) ?? rawPayload;
                var idemSuffix = BuildIdempotencyKeySuffix(connectionId, ctx, dml);

                if (requiredCompanions.Count == 0)
                {
                    completed.Add(new RuleMatch(rule, shaped, rawPayload, idemSuffix));
                    continue;
                }

                connState.Pending.Add(new PendingMatch
                {
                    Rule = rule,
                    TriggerTs = ctx.Timestamp,
                    Deadline = ctx.Timestamp.AddMilliseconds(waitMs),
                    TriggerTransactionId = ctx.TransactionId,
                    Scope = scope,
                    ShapedPayload = shaped,
                    RawPayload = rawPayload,
                    IdempotencyKeySuffix = idemSuffix,
                    RequiredPending = requiredCompanions
                });
            }
        }

        return completed;
    }

    public IReadOnlyList<ExpiredMatch> SweepExpired(DateTime now)
    {
        var expired = new List<ExpiredMatch>();
        foreach (var kvp in _state)
        {
            var state = kvp.Value;
            lock (state.Sync)
            {
                for (int i = state.Pending.Count - 1; i >= 0; i--)
                {
                    var pm = state.Pending[i];
                    if (now <= pm.Deadline) continue;

                    expired.Add(new ExpiredMatch(
                        pm.Rule,
                        pm.TriggerTs,
                        pm.RequiredPending
                            .Select(c => $"{c.Operation} {c.Schema ?? "?"}.{c.Table}")
                            .ToList()));
                    state.Pending.RemoveAt(i);
                }
            }
        }
        return expired;
    }

    // ============================================================
    // Trigger matching (mantido do MVP anterior)
    // ============================================================

    private static JsonElement? TryApplyShape(Rule rule, JsonElement rawPayload)
    {
        try
        {
            using var def = JsonDocument.Parse(rule.Definition);
            if (!def.RootElement.TryGetProperty("shape", out var shape)) return null;
            if (shape.ValueKind != JsonValueKind.Object) return null;

            var expanded = PlaceholderExpander.Expand(shape.GetRawText(), rawPayload, rule.Id, rule.Version);
            return JsonDocument.Parse(expanded).RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static bool TryMatchTrigger(
        Rule rule, string databaseName, ParsedDml dml, IReadOnlyDictionary<string, string> after)
    {
        JsonElement trigger;
        try
        {
            using var doc = JsonDocument.Parse(rule.Definition);
            if (!doc.RootElement.TryGetProperty("trigger", out var t)) return false;
            trigger = t.Clone();
        }
        catch
        {
            return false;
        }

        if (TryGetString(trigger, "table") is { } table
            && !string.Equals(table, dml.Table, StringComparison.OrdinalIgnoreCase))
            return false;

        if (TryGetString(trigger, "schema") is { } schema
            && !string.IsNullOrWhiteSpace(schema)
            && dml.Schema is not null
            && !string.Equals(schema, dml.Schema, StringComparison.OrdinalIgnoreCase))
            return false;

        if (TryGetString(trigger, "database") is { } db
            && !string.IsNullOrWhiteSpace(db)
            && !string.Equals(db, databaseName, StringComparison.OrdinalIgnoreCase))
            return false;

        var ruleOp = TryGetString(trigger, "operation")?.ToLowerInvariant();
        var dmlOp = OperationToString(dml.Operation);
        if (!string.IsNullOrWhiteSpace(ruleOp) && ruleOp != "any" && ruleOp != dmlOp)
            return false;

        if (trigger.TryGetProperty("predicate", out var pred))
        {
            if (!EvaluatePredicate(pred, after)) return false;
        }
        return true;
    }

    private static bool EvaluatePredicate(JsonElement node, IReadOnlyDictionary<string, string> after)
    {
        if (node.ValueKind == JsonValueKind.Array)
            return node.EnumerateArray().All(c => EvaluateClause(c, after));

        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.Array)
                return all.EnumerateArray().All(c => EvaluatePredicate(c, after));
            if (node.TryGetProperty("any", out var any) && any.ValueKind == JsonValueKind.Array)
                return any.EnumerateArray().Any(c => EvaluatePredicate(c, after));
            if (node.TryGetProperty("not", out var not))
                return !EvaluatePredicate(not, after);
            return EvaluateClause(node, after);
        }
        return true;
    }

    private static bool EvaluateClause(JsonElement clause, IReadOnlyDictionary<string, string> after)
    {
        var field = TryGetString(clause, "field");
        var op = TryGetString(clause, "op")?.ToLowerInvariant();
        var value = TryGetString(clause, "value");
        if (field is null || op is null) return true;

        const string prefix = "after.";
        var bare = field.StartsWith("$." + prefix, StringComparison.Ordinal) ? field[("$." + prefix).Length..]
                 : field.StartsWith(prefix, StringComparison.Ordinal) ? field[prefix.Length..]
                 : field;

        var has = after.TryGetValue(bare, out var actual);
        return op switch
        {
            "eq" => has && string.Equals(Normalize(actual), Normalize(value), StringComparison.OrdinalIgnoreCase),
            "ne" => !has || !string.Equals(Normalize(actual), Normalize(value), StringComparison.OrdinalIgnoreCase),
            // ops não suportados ainda: passa direto (não impede match) — TODO
            _ => true
        };
    }

    // ============================================================
    // Correlation parsing
    // ============================================================

    // Extrai (scope, wait_ms, required-companions) da definition. Tolerante a JSON malformado:
    // se algo der errado, devolve scope=none / sem companions (degrada pra trigger-only).
    private static (string Scope, int WaitMs, List<CompanionSpec> Required) ParseCorrelation(Rule rule)
    {
        try
        {
            using var doc = JsonDocument.Parse(rule.Definition);
            if (!doc.RootElement.TryGetProperty("correlation", out var corr)
                || corr.ValueKind != JsonValueKind.Object)
                return ("none", DefaultWaitMs, new List<CompanionSpec>());

            var scope = TryGetString(corr, "scope")?.ToLowerInvariant() ?? "none";
            var waitMs = corr.TryGetProperty("wait_ms", out var w) && w.ValueKind == JsonValueKind.Number
                ? w.GetInt32()
                : DefaultWaitMs;

            var required = new List<CompanionSpec>();
            if (corr.TryGetProperty("companions", out var comps) && comps.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in comps.EnumerateArray())
                {
                    var isRequired = c.TryGetProperty("required", out var r)
                        && r.ValueKind == JsonValueKind.True;
                    if (!isRequired) continue;

                    var op = TryGetString(c, "operation")?.ToLowerInvariant() ?? "any";
                    var schema = TryGetString(c, "schema");
                    var table = TryGetString(c, "table");
                    if (string.IsNullOrWhiteSpace(table)) continue;
                    required.Add(new CompanionSpec(op, schema, table!));
                }
            }
            return (scope, waitMs, required);
        }
        catch
        {
            return ("none", DefaultWaitMs, new List<CompanionSpec>());
        }
    }

    private static bool CompanionMatches(CompanionSpec spec, ParsedDml dml, string dmlOperation)
    {
        if (!string.Equals(spec.Table, dml.Table, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(spec.Schema)
            && dml.Schema is not null
            && !string.Equals(spec.Schema, dml.Schema, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(spec.Operation)
            && spec.Operation != "any"
            && spec.Operation != dmlOperation)
            return false;
        return true;
    }

    // ============================================================
    // Payload + idempotency
    // ============================================================

    private static string OperationToString(DmlOperation op) => op switch
    {
        DmlOperation.Insert => "insert",
        DmlOperation.Update => "update",
        DmlOperation.Delete => "delete",
        _ => "unknown"
    };

    private static string BuildIdempotencyKeySuffix(Guid connectionId, EventContext ctx, ParsedDml dml) =>
        $"{connectionId:N}:{ctx.Timestamp:O}:{ctx.SessionId}:{ctx.DmlIndex}:{OperationToString(dml.Operation)}:{dml.Table}";

    private static JsonElement BuildPayloadElement(
        IReadOnlyDictionary<string, string> after,
        IReadOnlyDictionary<string, string> before,
        DateTime eventTimestamp,
        string triggerTable,
        string? triggerSchema,
        string triggerOperation)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WritePropertyName("after");
            w.WriteStartObject();
            foreach (var kvp in after)
            {
                if (string.IsNullOrEmpty(kvp.Value)) w.WriteNull(kvp.Key);
                else w.WriteString(kvp.Key, kvp.Value);
            }
            w.WriteEndObject();
            // before: derivado dos predicados eq do WHERE — só temos dados pras colunas que o
            // statement filtrou (ex.: WHERE id=5 AND status='A'). Pra colunas em SET sem filtro
            // correspondente no WHERE, before fica ausente (XEvent não captura row state).
            w.WritePropertyName("before");
            w.WriteStartObject();
            foreach (var kvp in before)
            {
                if (string.IsNullOrEmpty(kvp.Value)) w.WriteNull(kvp.Key);
                else w.WriteString(kvp.Key, kvp.Value);
            }
            w.WriteEndObject();
            // _meta carrega dados do trigger pra que placeholders como $trigger.table /
            // $event.timestamp possam ser resolvidos (PlaceholderExpander faz alias pra _meta).
            w.WritePropertyName("_meta");
            w.WriteStartObject();
            w.WriteString("captured_at", eventTimestamp.ToString("O"));
            w.WriteString("table", triggerTable);
            if (triggerSchema is not null) w.WriteString("schema", triggerSchema);
            w.WriteString("operation", triggerOperation);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        ms.Position = 0;
        using var doc = JsonDocument.Parse(ms.ToArray());
        return doc.RootElement.Clone();
    }

    // before só pode ser derivado dos predicados eq do WHERE: pra DELETE cobre a linha inteira
    // que conhecemos; pra UPDATE cobre as colunas filtradas (incluindo, quando aplicável, a coluna
    // que está sendo SET — ex.: UPDATE T SET status='B' WHERE status='A' → before.status='A').
    // INSERT não tem WHERE, então before fica vazio.
    private static IReadOnlyDictionary<string, string> BuildBeforeFields(ParsedDml dml)
    {
        if (dml.Operation == DmlOperation.Insert)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in dml.Where)
        {
            if (p.Operator == "eq")
                dict[p.Column] = p.ValueLiteral;
        }
        return dict;
    }

    private static IReadOnlyDictionary<string, string> BuildAfterFields(ParsedDml dml)
    {
        // Fontes de campo "after" (em ordem de prioridade):
        //   1) dml.Values: valores literais resolvidos do INSERT VALUES / UPDATE SET, incluindo
        //      parâmetros @pN desempacotados de sp_executesql. Mais preciso quando disponível.
        //   2) WHERE eq → after.col=value (cobre DELETE e refina UPDATE/INSERT quando o WHERE
        //      identifica a linha — ex.: UPDATE ... WHERE Id = @p2).
        //   3) dml.Columns sem valor → registra a chave com "" pra estrutura ficar previsível.
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (col, val) in dml.Values)
            dict[col] = val ?? string.Empty;

        foreach (var p in dml.Where)
        {
            if (p.Operator == "eq" && !dict.ContainsKey(p.Column))
                dict[p.Column] = p.ValueLiteral;
        }

        foreach (var col in dml.Columns)
            dict.TryAdd(col, string.Empty);

        return dict;
    }

    private static string? Normalize(string? v) => v?.Trim().Trim('\'', '"');

    private static string? TryGetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ============================================================
    // Tipos internos
    // ============================================================

    private sealed class ConnectionState
    {
        public object Sync { get; } = new();
        public List<PendingMatch> Pending { get; } = new();
    }

    private sealed class PendingMatch
    {
        public Rule Rule { get; init; } = null!;
        public DateTime TriggerTs { get; init; }
        public DateTime Deadline { get; init; }
        public long? TriggerTransactionId { get; init; }
        public string Scope { get; init; } = "none";
        public JsonElement ShapedPayload { get; init; }
        public JsonElement RawPayload { get; init; }
        public string IdempotencyKeySuffix { get; init; } = string.Empty;
        public List<CompanionSpec> RequiredPending { get; init; } = null!;
    }

    private sealed record CompanionSpec(string Operation, string? Schema, string Table);
}
