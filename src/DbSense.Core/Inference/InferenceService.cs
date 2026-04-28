using System.Text.Json;
using System.Text.Json.Nodes;
using DbSense.Core.Domain;
using DbSense.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DbSense.Core.Inference;

public interface IInferenceService
{
    Task<InferenceOutcome> InferAsync(Guid recordingId, CancellationToken ct = default);
}

public enum EventClassification { Main, Correlation, Noise }

public record ClassifiedEvent(long EventId, EventClassification Classification, string? Reason);

public record InferredRulePreview(
    string SuggestedName,
    string SuggestedDescription,
    string Database,
    string? Schema,
    string Table,
    string Operation,                  // insert | update | delete
    IReadOnlyList<PredicateClause> Predicate,
    IReadOnlyList<string> AfterFields,
    string? PartitionKey,
    IReadOnlyList<InferredCompanion> Companions,
    int CorrelationWaitMs,
    string CorrelationScope,           // "time_window" | "transaction" | "none"
    string DefinitionJson);

public record PredicateClause(string Field, string Op, string Value);

public record InferredCompanion(
    long EventId,
    string Operation,
    string? Schema,
    string Table,
    bool Required);

public record InferenceOutcome(
    bool Success,
    string? Error,
    InferredRulePreview? Rule,
    IReadOnlyList<ClassifiedEvent> Events);

public class InferenceService : IInferenceService
{
    private static readonly string[] NoisePrefixes =
    {
        "set ", "declare ", "use ", "exec sp_reset_connection",
        "exec sp_describe_first_result_set", "select @@", "print "
    };

    private readonly IDbContextFactory<DbSenseContext> _contextFactory;

    public InferenceService(IDbContextFactory<DbSenseContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<InferenceOutcome> InferAsync(Guid recordingId, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);

        var recording = await ctx.Recordings
            .Include(r => r.Connection)
            .FirstOrDefaultAsync(r => r.Id == recordingId, ct);
        if (recording is null)
            return new InferenceOutcome(false, "Gravação não encontrada.", null, Array.Empty<ClassifiedEvent>());

        var events = await ctx.RecordingEvents
            .AsNoTracking()
            .Where(e => e.RecordingId == recordingId)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);

        if (events.Count == 0)
            return new InferenceOutcome(false, "Nenhum evento capturado nesta gravação.", null,
                Array.Empty<ClassifiedEvent>());

        // Passo 1 — explode cada evento em N candidatos DML (um batch pode ter vários
        // INSERTs/UPDATEs/DELETEs num único sql_batch_completed).
        var candidates = new List<DmlCandidate>();
        var eventNoiseReason = new Dictionary<long, string?>();
        foreach (var ev in events)
        {
            var dmls = SqlParser.TryParseAll(ev.SqlText);
            if (dmls.Count == 0)
            {
                var reason = LooksLikeNoise(ev.SqlText, out var noiseReason)
                    ? noiseReason
                    : "não-DML ou SQL não reconhecido";
                eventNoiseReason[ev.Id] = reason;
                continue;
            }
            for (int i = 0; i < dmls.Count; i++)
                candidates.Add(new DmlCandidate(ev, i, dmls[i]));
        }

        if (candidates.Count == 0)
            return new InferenceOutcome(false, "Nenhum INSERT/UPDATE/DELETE detectado.", null,
                events.Select(ev => new ClassifiedEvent(
                    ev.Id, EventClassification.Noise,
                    eventNoiseReason.GetValueOrDefault(ev.Id))).ToList());

        // Passo 2 — escolher o main: prefere UPDATE > INSERT > DELETE, desempate por #colunas.
        var main = candidates
            .OrderBy(c => OperationPriority(c.Parsed.Operation))
            .ThenByDescending(c => c.Parsed.Columns.Count)
            .First();

        // Passo 3 — companions: qualquer outro candidato (a) no mesmo evento, (b) na mesma transação,
        // ou (c) dentro de ±wait_ms a partir do timestamp do main.
        var companions = DetectCompanions(main, candidates);
        var companionEventIds = new HashSet<long>(companions.Select(c => c.EventId));

        // Passo 4 — classificação por evento. Um evento pode conter o main + companions ao mesmo
        // tempo (caso de multi-INSERT no mesmo batch); priorizamos main na visualização.
        var finalClassified = events.Select(ev =>
        {
            if (ev.Id == main.Event.Id)
                return new ClassifiedEvent(ev.Id, EventClassification.Main, null);
            if (companionEventIds.Contains(ev.Id))
                return new ClassifiedEvent(ev.Id, EventClassification.Correlation,
                    "companion (mesmo batch, mesma transação ou janela de tempo)");
            return new ClassifiedEvent(ev.Id, EventClassification.Noise,
                eventNoiseReason.GetValueOrDefault(ev.Id) ?? "fora da janela de correlação");
        }).ToList();

        var rule = BuildPreview(recording, main.Parsed, companions);
        return new InferenceOutcome(true, null, rule, finalClassified);
    }

    private const int DefaultWaitMs = 5000;

    private sealed record DmlCandidate(RecordingEvent Event, int StatementIndex, ParsedDml Parsed);

    private static IReadOnlyList<InferredCompanion> DetectCompanions(
        DmlCandidate main, IReadOnlyList<DmlCandidate> candidates)
    {
        var mainTs = main.Event.EventTimestamp;
        var window = TimeSpan.FromMilliseconds(DefaultWaitMs);
        var companions = new List<InferredCompanion>();

        foreach (var c in candidates)
        {
            if (c.Event.Id == main.Event.Id && c.StatementIndex == main.StatementIndex) continue;
            var sameEvent = c.Event.Id == main.Event.Id;
            var sameTxn = main.Event.TransactionId is not null
                && c.Event.TransactionId == main.Event.TransactionId;
            var inWindow = (c.Event.EventTimestamp - mainTs).Duration() <= window;
            if (!sameEvent && !sameTxn && !inWindow) continue;

            companions.Add(new InferredCompanion(
                EventId: c.Event.Id,
                Operation: c.Parsed.Operation.ToString().ToLowerInvariant(),
                Schema: c.Parsed.Schema,
                Table: c.Parsed.Table,
                Required: true));
        }

        return companions;
    }

    private static int OperationPriority(DmlOperation op) => op switch
    {
        DmlOperation.Update => 0,
        DmlOperation.Insert => 1,
        DmlOperation.Delete => 2,
        _ => 3
    };

    private static bool LooksLikeNoise(string sql, out string reason)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            reason = "vazio";
            return true;
        }
        var trimmed = sql.TrimStart();
        var lower = trimmed.ToLowerInvariant();
        foreach (var p in NoisePrefixes)
        {
            if (lower.StartsWith(p))
            {
                reason = $"começa com '{p.Trim()}'";
                return true;
            }
        }
        if (lower.StartsWith("select"))
        {
            // SELECT puro vira ruído (não persiste). UPDATE/INSERT/DELETE seriam pegos pelo parser.
            reason = "SELECT (leitura)";
            return true;
        }
        reason = string.Empty;
        return false;
    }

    private static InferredRulePreview BuildPreview(
        Recording rec, ParsedDml dml, IReadOnlyList<InferredCompanion> companions)
    {
        var operation = dml.Operation.ToString().ToLowerInvariant();
        var schema = dml.Schema ?? "dbo";

        var predicate = dml.Where.Select(w => new PredicateClause(
            $"after.{w.Column}", w.Operator, w.ValueLiteral)).ToList();

        var afterFields = dml.Operation == DmlOperation.Delete
            ? dml.Where.Select(w => w.Column).Distinct().ToList()
            : dml.Columns.ToList();

        var partitionKey = dml.Where
            .FirstOrDefault(w => IsIdLike(w.Column))?.Column
            ?? dml.Where.FirstOrDefault()?.Column;

        var name = SuggestName(dml.Table, dml.Operation, predicate);
        var description = SuggestDescription(dml, schema, companions);
        var scope = companions.Count > 0 ? "time_window" : "none";

        var definition = BuildRuleJson(rec, dml, schema, operation, predicate, afterFields, partitionKey,
            name, description, companions, scope, DefaultWaitMs);

        return new InferredRulePreview(
            SuggestedName: name,
            SuggestedDescription: description,
            Database: GuessDatabase(rec),
            Schema: schema,
            Table: dml.Table,
            Operation: operation,
            Predicate: predicate,
            AfterFields: afterFields,
            PartitionKey: partitionKey is null ? null : $"$.after.{partitionKey}",
            Companions: companions,
            CorrelationWaitMs: DefaultWaitMs,
            CorrelationScope: scope,
            DefinitionJson: definition);
    }

    private static string GuessDatabase(Recording rec) => rec.Connection?.Database ?? string.Empty;

    private static bool IsIdLike(string col) =>
        col.Equals("id", StringComparison.OrdinalIgnoreCase)
        || col.EndsWith("_id", StringComparison.OrdinalIgnoreCase)
        || col.EndsWith("Id", StringComparison.Ordinal);

    private static string SuggestName(string table, DmlOperation op, IReadOnlyList<PredicateClause> predicate)
    {
        var verb = op switch
        {
            DmlOperation.Insert => "criar",
            DmlOperation.Update => "atualizar",
            DmlOperation.Delete => "remover",
            _ => "evento"
        };
        var snake = ToSnakeCase(table);
        var hint = predicate.FirstOrDefault(p => !IsIdLike(p.Field.Replace("after.", "")));
        if (hint is { Op: "eq" })
        {
            return $"{verb}_{snake}_{ToSnakeCase(hint.Value.Trim('\''))}";
        }
        return $"{verb}_{snake}";
    }

    private static string SuggestDescription(
        ParsedDml dml, string schema, IReadOnlyList<InferredCompanion> companions)
    {
        var verb = dml.Operation switch
        {
            DmlOperation.Insert => "INSERT em",
            DmlOperation.Update => "UPDATE em",
            DmlOperation.Delete => "DELETE em",
            _ => "evento em"
        };
        var cond = dml.Where.Count > 0
            ? $" com {string.Join(" AND ", dml.Where.Select(w => $"{w.Column}={w.ValueLiteral}"))}"
            : string.Empty;
        var comp = companions.Count > 0
            ? $" + {companions.Count} companion{(companions.Count > 1 ? "s" : "")} requerido{(companions.Count > 1 ? "s" : "")}"
            : string.Empty;
        return $"Emitido quando ocorre {verb} {schema}.{dml.Table}{cond}{comp}.";
    }

    private static string BuildRuleJson(
        Recording rec, ParsedDml dml, string schema, string operation,
        IReadOnlyList<PredicateClause> predicate, IReadOnlyList<string> afterFields,
        string? partitionKey, string name, string description,
        IReadOnlyList<InferredCompanion> companions, string scope, int waitMs)
    {
        var obj = new JsonObject
        {
            ["name"] = name,
            ["version"] = 1,
            ["connection_id"] = rec.ConnectionId.ToString(),
            ["trigger"] = new JsonObject
            {
                ["event_kind"] = "dml",
                ["operation"] = operation,
                ["database"] = rec.Connection?.Database ?? string.Empty,
                ["schema"] = schema,
                ["table"] = dml.Table,
                ["predicate"] = predicate.Count == 0
                    ? new JsonObject()
                    : new JsonObject
                    {
                        ["all"] = new JsonArray(predicate.Select(p =>
                            (JsonNode)new JsonObject
                            {
                                ["field"] = p.Field,
                                ["op"] = p.Op,
                                ["value"] = p.Value
                            }).ToArray())
                    }
            },
            ["correlation"] = new JsonObject
            {
                ["scope"] = scope,
                ["wait_ms"] = waitMs,
                ["companions"] = new JsonArray(companions.Select(c =>
                    (JsonNode)new JsonObject
                    {
                        ["event_kind"] = "dml",
                        ["operation"] = c.Operation,
                        ["schema"] = c.Schema ?? "dbo",
                        ["table"] = c.Table,
                        ["required"] = c.Required
                    }).ToArray())
            },
            ["shape"] = BuildShape(afterFields),
            ["partition_key"] = partitionKey is null ? null : $"$.after.{partitionKey}",
            ["destination"] = null,
            ["reliability"] = new JsonObject
            {
                ["dedupe_window_s"] = 60,
                ["max_publish_attempts"] = 5,
                ["backoff_strategy"] = "exponential"
            },
            ["metadata"] = new JsonObject
            {
                ["description"] = description,
                ["source_recording_ids"] = new JsonArray(rec.Id.ToString()),
                ["inferred_from_examples"] = 1,
                ["inference_engine"] = "heuristic"
            }
        };
        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject BuildShape(IReadOnlyList<string> afterFields)
    {
        var shape = new JsonObject();
        foreach (var f in afterFields.Distinct())
            shape[ToSnakeCase(f)] = $"$.after.{f}";
        shape["_meta"] = new JsonObject
        {
            ["source_table"] = "$trigger.table",
            ["captured_at"] = "$event.timestamp"
        };
        return shape;
    }

    private static string ToSnakeCase(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var sb = new System.Text.StringBuilder(value.Length + 4);
        for (int i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(value[i - 1]) || (i + 1 < value.Length && char.IsLower(value[i + 1]))))
                sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

}
