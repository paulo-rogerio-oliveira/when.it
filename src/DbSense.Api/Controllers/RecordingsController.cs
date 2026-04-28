using DbSense.Contracts.Inference;
using DbSense.Contracts.Recordings;
using DbSense.Core.Domain;
using DbSense.Core.Inference;
using DbSense.Core.Recordings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbSense.Api.Controllers;

[ApiController]
[Route("api/recordings")]
[Authorize]
public class RecordingsController : ControllerBase
{
    private readonly IRecordingsService _service;
    private readonly IInferenceService _inference;
    private readonly ILlmInferenceService _llm;

    public RecordingsController(
        IRecordingsService service,
        IInferenceService inference,
        ILlmInferenceService llm)
    {
        _service = service;
        _inference = inference;
        _llm = llm;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<RecordingListItem>>> List(CancellationToken ct)
    {
        var rows = await _service.ListAsync(ct);
        return Ok(rows.Select(x => ToList(x.Recording, x.ConnectionName)));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RecordingDetail>> Get(Guid id, CancellationToken ct)
    {
        var row = await _service.GetAsync(id, ct);
        if (row is null) return NotFound();
        return Ok(ToDetail(row.Value.Recording, row.Value.ConnectionName));
    }

    [HttpPost]
    public async Task<ActionResult<RecordingDetail>> Create(
        [FromBody] CreateRecordingRequest req, CancellationToken ct)
    {
        try
        {
            var rec = await _service.StartAsync(
                req.ConnectionId, req.Name, req.Description,
                req.FilterHostName, req.FilterAppName, req.FilterLoginName, ct);
            var row = await _service.GetAsync(rec.Id, ct);
            return CreatedAtAction(nameof(Get), new { id = rec.Id },
                ToDetail(row!.Value.Recording, row.Value.ConnectionName));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{id:guid}/stop")]
    public async Task<ActionResult<RecordingDetail>> Stop(Guid id, CancellationToken ct)
    {
        var rec = await _service.StopAsync(id, ct);
        if (rec is null) return NotFound();
        var row = await _service.GetAsync(id, ct);
        return Ok(ToDetail(row!.Value.Recording, row.Value.ConnectionName));
    }

    [HttpPost("{id:guid}/discard")]
    public async Task<ActionResult<RecordingDetail>> Discard(Guid id, CancellationToken ct)
    {
        var rec = await _service.DiscardAsync(id, ct);
        if (rec is null) return NotFound();
        var row = await _service.GetAsync(id, ct);
        return Ok(ToDetail(row!.Value.Recording, row.Value.ConnectionName));
    }

    [HttpPost("{id:guid}/infer-rule")]
    public async Task<ActionResult<InferRuleResponse>> InferRule(Guid id, CancellationToken ct)
    {
        var heuristicTask = _inference.InferAsync(id, ct);
        var llmTask = _llm.IsEnabled
            ? _llm.InferAsync(id, ct)
            : Task.FromResult(new LlmInferenceOutcome(false, false, null, null, null, null,
                Array.Empty<ClassifiedEvent>(), null, null));

        await Task.WhenAll(heuristicTask, llmTask);

        var heuristic = ToHeuristicDto(heuristicTask.Result);
        var llm = ToLlmDto(llmTask.Result);
        return Ok(new InferRuleResponse(heuristic, llm));
    }

    private static HeuristicInferenceDto ToHeuristicDto(InferenceOutcome o) => new(
        o.Success, o.Error, ToPayload(o.Rule), ToEvents(o.Events));

    private static LlmInferenceDto ToLlmDto(LlmInferenceOutcome o) => new(
        o.Enabled, o.Success, o.Error, ToPayload(o.Rule), o.Reasoning, o.MainEventId,
        ToEvents(o.Events), o.InputTokens, o.OutputTokens);

    private static InferredRulePayload? ToPayload(InferredRulePreview? r)
    {
        if (r is null) return null;
        return new InferredRulePayload(
            r.SuggestedName, r.SuggestedDescription, r.Database, r.Schema, r.Table, r.Operation,
            r.Predicate.Select(p => new InferredPredicateClause(p.Field, p.Op, p.Value)).ToList(),
            r.AfterFields, r.PartitionKey,
            r.Companions
                .Select(c => new InferredCompanionDto(c.EventId, c.Operation, c.Schema, c.Table, c.Required))
                .ToList(),
            r.CorrelationWaitMs, r.CorrelationScope,
            r.DefinitionJson);
    }

    private static IReadOnlyList<ClassifiedEventDto> ToEvents(IReadOnlyList<ClassifiedEvent> events) =>
        events.Select(e => new ClassifiedEventDto(
            e.EventId, e.Classification.ToString().ToLowerInvariant(), e.Reason)).ToList();

    [HttpGet("{id:guid}/events")]
    public async Task<ActionResult<RecordingEventsPage>> Events(
        Guid id, [FromQuery] long? after, [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var (items, total) = await _service.ListEventsAsync(id, after, limit, ct);
        var nextCursor = items.Count > 0 ? items[^1].Id : (long?)null;
        return Ok(new RecordingEventsPage(items.Select(ToEvent).ToList(), nextCursor, total));
    }

    private static RecordingListItem ToList(Recording r, string connectionName) => new(
        r.Id, r.ConnectionId, connectionName, r.Name, r.Description,
        r.Status, r.StartedAt, r.StoppedAt, r.EventCount);

    private static RecordingDetail ToDetail(Recording r, string connectionName) => new(
        r.Id, r.ConnectionId, connectionName, r.Name, r.Description,
        r.Status, r.StartedAt, r.StoppedAt, r.EventCount,
        r.FilterHostName, r.FilterAppName, r.FilterLoginName, r.FilterSessionId);

    private static RecordingEventItem ToEvent(RecordingEvent e) => new(
        e.Id, e.EventTimestamp, e.EventType, e.SessionId, e.DatabaseName,
        e.ObjectName, e.SqlText, e.DurationUs, e.RowCount,
        e.AppName, e.HostName, e.LoginName, e.TransactionId);
}
