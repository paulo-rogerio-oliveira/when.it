using System.Text.Json;
using DbSense.Contracts.Rules;
using DbSense.Core.Domain;
using DbSense.Core.Reactions;
using DbSense.Core.Rules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbSense.Api.Controllers;

[ApiController]
[Route("api/rules")]
[Authorize]
public class RulesController : ControllerBase
{
    private readonly IRulesService _service;
    private readonly IOutboxEnqueuer _enqueuer;

    public RulesController(IRulesService service, IOutboxEnqueuer enqueuer)
    {
        _service = service;
        _enqueuer = enqueuer;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<RuleListItem>>> List(CancellationToken ct)
    {
        var rows = await _service.ListAsync(ct);
        return Ok(rows.Select(x => ToList(x.Rule, x.ConnectionName)));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RuleDetail>> Get(Guid id, CancellationToken ct)
    {
        var row = await _service.GetAsync(id, ct);
        if (row is null) return NotFound();
        return Ok(ToDetail(row.Value.Rule, row.Value.ConnectionName));
    }

    [HttpPost]
    public async Task<ActionResult<RuleDetail>> Create(
        [FromBody] CreateRuleRequest req, CancellationToken ct)
    {
        try
        {
            var rule = await _service.CreateDraftAsync(
                req.ConnectionId, req.SourceRecordingId,
                req.Name, req.Description, req.Definition, ct);
            var row = await _service.GetAsync(rule.Id, ct);
            return CreatedAtAction(nameof(Get), new { id = rule.Id },
                ToDetail(row!.Value.Rule, row.Value.ConnectionName));
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

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<RuleDetail>> Update(
        Guid id, [FromBody] UpdateRuleRequest req, CancellationToken ct)
    {
        try
        {
            var updated = await _service.UpdateAsync(id, req.Name, req.Description, req.Definition, ct);
            if (updated is null) return NotFound();
            var row = await _service.GetAsync(updated.Id, ct);
            return Ok(ToDetail(row!.Value.Rule, row.Value.ConnectionName));
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

    [HttpPost("{id:guid}/activate")]
    public async Task<ActionResult<RuleDetail>> Activate(Guid id, CancellationToken ct)
    {
        try
        {
            var updated = await _service.ActivateAsync(id, ct);
            if (updated is null) return NotFound();
            var row = await _service.GetAsync(updated.Id, ct);
            return Ok(ToDetail(row!.Value.Rule, row.Value.ConnectionName));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{id:guid}/pause")]
    public async Task<ActionResult<RuleDetail>> Pause(Guid id, CancellationToken ct)
    {
        var updated = await _service.PauseAsync(id, ct);
        if (updated is null) return NotFound();
        var row = await _service.GetAsync(updated.Id, ct);
        return Ok(ToDetail(row!.Value.Rule, row.Value.ConnectionName));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var ok = await _service.DeleteAsync(id, ct);
        return ok ? NoContent() : NotFound();
    }

    [HttpPost("{id:guid}/test-reaction")]
    public async Task<ActionResult<TestReactionResponse>> TestReaction(
        Guid id, [FromBody] TestReactionRequest? req, CancellationToken ct)
    {
        var row = await _service.GetAsync(id, ct);
        if (row is null) return NotFound();
        var rule = row.Value.Rule;

        JsonElement payload;
        if (req?.Payload is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } provided)
            payload = provided;
        else
        {
            using var doc = JsonDocument.Parse("""{ "after": { "id": 1 }, "before": null }""");
            payload = doc.RootElement.Clone();
        }

        try
        {
            // Endpoint de teste manual: o payload fornecido pelo usuário serve tanto
            // como shaped (gravado em events_log) quanto como raw (alvo dos placeholders
            // na reaction config) — não há shape pra aplicar nem trigger pra registrar.
            var result = await _enqueuer.EnqueueAsync(new EnqueueRequest(
                rule, payload, payload, DateTime.UtcNow, $"test:{Guid.NewGuid():N}"), ct);
            using var doc = JsonDocument.Parse(rule.Definition);
            var type = doc.RootElement.TryGetProperty("reaction", out var r)
                && r.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()! : "unknown";
            return Ok(new TestReactionResponse(result.EventsLogId, result.OutboxId, result.IdempotencyKey, type));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private static RuleListItem ToList(Rule r, string connectionName) => new(
        r.Id, r.ConnectionId, connectionName, r.Name, r.Description, r.Version,
        r.Status, r.CreatedAt, r.UpdatedAt, r.ActivatedAt);

    private static RuleDetail ToDetail(Rule r, string connectionName) => new(
        r.Id, r.ConnectionId, connectionName, r.DestinationId, r.SourceRecordingId,
        r.Name, r.Description, r.Version, r.Definition, r.Status,
        r.CreatedAt, r.UpdatedAt, r.ActivatedAt);
}
