using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DbSense.Core.Domain;
using DbSense.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DbSense.Core.Inference;

public interface ILlmInferenceService
{
    bool IsEnabled { get; }
    Task<LlmInferenceOutcome> InferAsync(Guid recordingId, CancellationToken ct = default);
}

public record LlmInferenceOutcome(
    bool Enabled,
    bool Success,
    string? Error,
    InferredRulePreview? Rule,
    string? Reasoning,
    long? MainEventId,
    IReadOnlyList<ClassifiedEvent> Events,
    int? InputTokens,
    int? OutputTokens);

public class LlmInferenceService : ILlmInferenceService
{
    private const string SystemPrompt =
        "You are a SQL Server analyst helping a tool called DbSense identify business events from raw SQL captured during a recording session.\n" +
        "Your job: given the user-provided description and the raw events, choose the SINGLE event that best represents the business operation, mark companions and noise.\n" +
        "Always respond with ONLY a JSON object matching the requested schema. No markdown fences, no commentary.";

    private readonly IDbContextFactory<DbSenseContext> _contextFactory;
    private readonly IAnthropicClient _client;
    private readonly LlmOptions _options;
    private readonly ILogger<LlmInferenceService> _logger;

    public LlmInferenceService(
        IDbContextFactory<DbSenseContext> contextFactory,
        IAnthropicClient client,
        IOptions<LlmOptions> options,
        ILogger<LlmInferenceService> logger)
    {
        _contextFactory = contextFactory;
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled => _options.IsEnabled;

    public async Task<LlmInferenceOutcome> InferAsync(Guid recordingId, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return Disabled();

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var recording = await ctx.Recordings
            .Include(r => r.Connection)
            .FirstOrDefaultAsync(r => r.Id == recordingId, ct);
        if (recording is null)
            return Failure("Gravação não encontrada.");

        var events = await ctx.RecordingEvents
            .AsNoTracking()
            .Where(e => e.RecordingId == recordingId)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);
        if (events.Count == 0)
            return Failure("Nenhum evento capturado nesta gravação.");

        var prompt = BuildPrompt(recording, events);

        AnthropicMessageResult result;
        try
        {
            result = await _client.CreateMessageAsync(SystemPrompt, prompt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LLM call failed for recording {Id}", recordingId);
            return new LlmInferenceOutcome(true, false, ex.Message, null, null, null,
                Array.Empty<ClassifiedEvent>(), null, null);
        }

        LlmResponse? parsed;
        try
        {
            parsed = ParseResponseJson(result.Text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse LLM response: {Raw}", result.Text);
            return new LlmInferenceOutcome(true, false, "Resposta da IA não veio no formato esperado.",
                null, null, null, Array.Empty<ClassifiedEvent>(),
                result.InputTokens, result.OutputTokens);
        }

        if (parsed is null)
            return new LlmInferenceOutcome(true, false, "Resposta vazia da IA.",
                null, null, null, Array.Empty<ClassifiedEvent>(),
                result.InputTokens, result.OutputTokens);

        var mainEvent = events.FirstOrDefault(e => e.Id == parsed.MainEventId);
        if (mainEvent is null)
            return new LlmInferenceOutcome(true, false,
                $"IA escolheu evento {parsed.MainEventId} que não pertence à gravação.",
                null, parsed.Reasoning, parsed.MainEventId, Array.Empty<ClassifiedEvent>(),
                result.InputTokens, result.OutputTokens);

        var mainDmls = SqlParser.TryParseAll(mainEvent.SqlText);
        if (mainDmls.Count == 0)
            return new LlmInferenceOutcome(true, false,
                "IA escolheu um evento que não é um INSERT/UPDATE/DELETE reconhecível.",
                null, parsed.Reasoning, parsed.MainEventId,
                BuildClassifications(events, parsed),
                result.InputTokens, result.OutputTokens);

        // Se o evento escolhido tem múltiplos DMLs (caso de batch com vários statements),
        // o primeiro vira o main e os demais entram como companions automáticos required.
        var parsedDml = mainDmls[0];
        var companions = BuildCompanions(events, parsed);
        if (mainDmls.Count > 1)
        {
            var extras = mainDmls.Skip(1).Select(dml => new InferredCompanion(
                EventId: mainEvent.Id,
                Operation: dml.Operation.ToString().ToLowerInvariant(),
                Schema: dml.Schema,
                Table: dml.Table,
                Required: true));
            companions = companions.Concat(extras).ToList();
        }
        var preview = BuildPreview(recording, parsedDml, parsed, companions);
        var classifications = BuildClassifications(events, parsed, companions);

        return new LlmInferenceOutcome(true, true, null, preview, parsed.Reasoning,
            parsed.MainEventId, classifications, result.InputTokens, result.OutputTokens);
    }

    private const int DefaultWaitMs = 5000;

    private static IReadOnlyList<InferredCompanion> BuildCompanions(
        IList<RecordingEvent> events, LlmResponse llm)
    {
        if (llm.Companions is null || llm.Companions.Count == 0)
            return Array.Empty<InferredCompanion>();

        var byId = events.ToDictionary(e => e.Id);
        var result = new List<InferredCompanion>();
        foreach (var comp in llm.Companions)
        {
            if (!byId.TryGetValue(comp.EventId, out var ev)) continue;
            foreach (var dml in SqlParser.TryParseAll(ev.SqlText))
            {
                result.Add(new InferredCompanion(
                    EventId: ev.Id,
                    Operation: dml.Operation.ToString().ToLowerInvariant(),
                    Schema: dml.Schema,
                    Table: dml.Table,
                    Required: comp.Required));
            }
        }
        return result;
    }

    private static LlmInferenceOutcome Disabled() =>
        new(false, false, null, null, null, null, Array.Empty<ClassifiedEvent>(), null, null);

    private static LlmInferenceOutcome Failure(string error) =>
        new(true, false, error, null, null, null, Array.Empty<ClassifiedEvent>(), null, null);

    private static string BuildPrompt(Recording rec, IList<RecordingEvent> events)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Gravação");
        sb.AppendLine($"- Nome: {rec.Name}");
        sb.AppendLine($"- Descrição: {(string.IsNullOrWhiteSpace(rec.Description) ? "(não fornecida)" : rec.Description)}");
        sb.AppendLine($"- Database: {rec.Connection?.Database ?? "(desconhecido)"}");
        sb.AppendLine();

        sb.AppendLine("# Eventos capturados (ordem cronológica)");
        sb.AppendLine("Cada evento tem id, transaction_id (txn), timestamp e SQL.");
        sb.AppendLine();
        foreach (var ev in events)
        {
            sb.Append("- id=").Append(ev.Id);
            sb.Append(" txn=").Append(ev.TransactionId?.ToString() ?? "null");
            sb.Append(" type=").Append(ev.EventType);
            sb.Append(" db=").Append(ev.DatabaseName);
            if (!string.IsNullOrEmpty(ev.ObjectName)) sb.Append(" object=").Append(ev.ObjectName);
            sb.AppendLine();
            sb.AppendLine("  sql:");
            foreach (var line in TrimSql(ev.SqlText).Split('\n'))
                sb.Append("    ").AppendLine(line.TrimEnd('\r'));
        }
        sb.AppendLine();

        sb.AppendLine("# Tarefa");
        sb.AppendLine("1. Identifique o evento PRINCIPAL (`main_event_id`) que melhor representa a operação descrita.");
        sb.AppendLine("2. Identifique COMPANIONS: outros eventos DML que precisam ocorrer junto pra que a regra emita.");
        sb.AppendLine("   Considere companion qualquer DML que ocorra na mesma transação OU dentro de poucos segundos do main e que faça parte logicamente da mesma operação descrita.");
        sb.AppendLine("   Marque `required: true` quando o usuário descreveu explicitamente as duas operações (ex.: 'cadastrar cliente COM orçamento').");
        sb.AppendLine("   Marque `required: false` quando for um side-effect que pode ou não ocorrer (ex.: log opcional).");
        sb.AppendLine("3. O resto é ruído (selects de leitura, framework chatter, infra).");
        sb.AppendLine("4. Sugira nome em snake_case e descrição em PT-BR de uma frase.");
        sb.AppendLine("5. Se houver condição de negócio (ex.: status='APROVADO'), preencha `predicate_hint`.");
        sb.AppendLine();
        sb.AppendLine("# Formato da resposta (JSON puro, sem markdown)");
        sb.AppendLine("""
{
  "main_event_id": <number>,
  "companions": [
    { "event_id": <number>, "required": true|false }
  ],
  "noise_event_ids": [<numbers>],
  "reasoning": "<por que esse evento principal e por que esses companions, 1-3 frases em PT-BR>",
  "suggested_name": "<snake_case>",
  "suggested_description": "<frase em PT-BR>",
  "predicate_hint": "<expressão como 'after.col op valor', ou null>"
}
""");
        return sb.ToString();
    }

    private static string TrimSql(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return string.Empty;
        return sql.Length > 2000 ? sql[..2000] + "..." : sql;
    }

    private static LlmResponse? ParseResponseJson(string raw)
    {
        var trimmed = raw.Trim();

        // Remove cercas de código markdown se a LLM teimar.
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0) trimmed = trimmed[(firstNewline + 1)..];
            if (trimmed.EndsWith("```")) trimmed = trimmed[..^3];
            trimmed = trimmed.Trim();
        }

        return JsonSerializer.Deserialize<LlmResponse>(trimmed, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    private static InferredRulePreview BuildPreview(
        Recording rec, ParsedDml dml, LlmResponse llm, IReadOnlyList<InferredCompanion> companions)
    {
        var operation = dml.Operation.ToString().ToLowerInvariant();
        var schema = dml.Schema ?? "dbo";

        var predicate = dml.Where.Select(w => new PredicateClause(
            $"after.{w.Column}", w.Operator, w.ValueLiteral)).ToList();

        var afterFields = dml.Operation == DmlOperation.Delete
            ? dml.Where.Select(w => w.Column).Distinct().ToList()
            : dml.Columns.ToList();

        var partitionKey = dml.Where.FirstOrDefault(w => IsIdLike(w.Column))?.Column
            ?? dml.Where.FirstOrDefault()?.Column;

        var name = !string.IsNullOrWhiteSpace(llm.SuggestedName) ? llm.SuggestedName!.Trim() : $"evento_{dml.Table}";
        var description = !string.IsNullOrWhiteSpace(llm.SuggestedDescription)
            ? llm.SuggestedDescription!.Trim()
            : $"Evento inferido em {schema}.{dml.Table}.";

        var scope = companions.Count > 0 ? "time_window" : "none";
        var definition = BuildRuleJson(rec, dml, schema, operation, predicate, afterFields, partitionKey,
            name, description, llm, companions, scope, DefaultWaitMs);

        return new InferredRulePreview(
            SuggestedName: name,
            SuggestedDescription: description,
            Database: rec.Connection?.Database ?? string.Empty,
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

    private static string BuildRuleJson(
        Recording rec, ParsedDml dml, string schema, string operation,
        IReadOnlyList<PredicateClause> predicate, IReadOnlyList<string> afterFields,
        string? partitionKey, string name, string description, LlmResponse llm,
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
                ["inference_engine"] = "llm",
                ["llm_reasoning"] = llm.Reasoning,
                ["llm_predicate_hint"] = llm.PredicateHint
            }
        };
        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject BuildShape(IReadOnlyList<string> afterFields)
    {
        var shape = new JsonObject();
        foreach (var f in afterFields.Distinct())
            shape[f] = $"$.after.{f}";
        shape["_meta"] = new JsonObject
        {
            ["source_table"] = "$trigger.table",
            ["captured_at"] = "$event.timestamp"
        };
        return shape;
    }

    private static bool IsIdLike(string col) =>
        col.Equals("id", StringComparison.OrdinalIgnoreCase)
        || col.EndsWith("_id", StringComparison.OrdinalIgnoreCase)
        || col.EndsWith("Id", StringComparison.Ordinal);

    private static IReadOnlyList<ClassifiedEvent> BuildClassifications(
        IList<RecordingEvent> events, LlmResponse llm, IReadOnlyList<InferredCompanion>? companions = null)
    {
        var compSet = new HashSet<long>(companions?.Select(c => c.EventId) ?? Enumerable.Empty<long>());
        var compRequired = new Dictionary<long, bool>(
            companions?.Select(c => new KeyValuePair<long, bool>(c.EventId, c.Required))
            ?? Enumerable.Empty<KeyValuePair<long, bool>>());
        // Compatibilidade com respostas antigas que ainda mandam correlation_event_ids.
        foreach (var id in llm.CorrelationEventIds ?? new List<long>())
            compSet.Add(id);
        var noiseSet = new HashSet<long>(llm.NoiseEventIds ?? new List<long>());
        var list = new List<ClassifiedEvent>(events.Count);
        foreach (var ev in events)
        {
            EventClassification cls;
            string? reason = null;
            if (ev.Id == llm.MainEventId) cls = EventClassification.Main;
            else if (compSet.Contains(ev.Id))
            {
                cls = EventClassification.Correlation;
                reason = compRequired.TryGetValue(ev.Id, out var req)
                    ? (req ? "companion required (IA)" : "companion opcional (IA)")
                    : "correlação (IA)";
            }
            else { cls = EventClassification.Noise; reason = noiseSet.Contains(ev.Id) ? "ruído (IA)" : "não classificado"; }
            list.Add(new ClassifiedEvent(ev.Id, cls, reason));
        }
        return list;
    }

    private record LlmResponse(
        [property: JsonPropertyName("main_event_id")] long MainEventId,
        [property: JsonPropertyName("companions")] List<LlmCompanion>? Companions,
        [property: JsonPropertyName("correlation_event_ids")] List<long>? CorrelationEventIds, // legado
        [property: JsonPropertyName("noise_event_ids")] List<long>? NoiseEventIds,
        [property: JsonPropertyName("reasoning")] string? Reasoning,
        [property: JsonPropertyName("suggested_name")] string? SuggestedName,
        [property: JsonPropertyName("suggested_description")] string? SuggestedDescription,
        [property: JsonPropertyName("predicate_hint")] string? PredicateHint);

    private record LlmCompanion(
        [property: JsonPropertyName("event_id")] long EventId,
        [property: JsonPropertyName("required")] bool Required);
}
