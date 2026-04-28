using DbSense.Contracts.Rules;
using DbSense.Core.Domain;
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

    public RulesController(IRulesService service)
    {
        _service = service;
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

    private static RuleListItem ToList(Rule r, string connectionName) => new(
        r.Id, r.ConnectionId, connectionName, r.Name, r.Description, r.Version,
        r.Status, r.CreatedAt, r.UpdatedAt, r.ActivatedAt);

    private static RuleDetail ToDetail(Rule r, string connectionName) => new(
        r.Id, r.ConnectionId, connectionName, r.DestinationId, r.SourceRecordingId,
        r.Name, r.Description, r.Version, r.Definition, r.Status,
        r.CreatedAt, r.UpdatedAt, r.ActivatedAt);
}
