using DbSense.Contracts.Connections;
using DbSense.Core.Connections;
using DbSense.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbSense.Api.Controllers;

[ApiController]
[Route("api/connections")]
[Authorize]
public class ConnectionsController : ControllerBase
{
    private readonly IConnectionsService _service;

    public ConnectionsController(IConnectionsService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ConnectionListItem>>> List(CancellationToken ct)
    {
        var items = await _service.ListAsync(ct);
        return Ok(items.Select(ToListItem));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ConnectionDetail>> Get(Guid id, CancellationToken ct)
    {
        var c = await _service.GetAsync(id, ct);
        if (c is null) return NotFound();
        return Ok(ToDetail(c));
    }

    [HttpPost]
    public async Task<ActionResult<ConnectionDetail>> Create(
        [FromBody] CreateConnectionRequest req, CancellationToken ct)
    {
        try
        {
            var c = await _service.CreateAsync(
                req.Name, req.Server, req.Database, req.AuthType, req.Username, req.Password, ct);
            return CreatedAtAction(nameof(Get), new { id = c.Id }, ToDetail(c));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ConnectionDetail>> Update(
        Guid id, [FromBody] UpdateConnectionRequest req, CancellationToken ct)
    {
        try
        {
            var c = await _service.UpdateAsync(
                id, req.Name, req.Server, req.Database, req.AuthType,
                req.Username, req.Password, req.ClearPassword, ct);
            if (c is null) return NotFound();
            return Ok(ToDetail(c));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var deleted = await _service.DeleteAsync(id, ct);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("{id:guid}/test")]
    public async Task<ActionResult<ConnectionTestOutcome>> Test(Guid id, CancellationToken ct)
    {
        var r = await _service.TestAsync(id, ct);
        return Ok(new ConnectionTestOutcome(r.Success, r.Error, r.ElapsedMs));
    }

    [HttpPost("test")]
    public async Task<ActionResult<ConnectionTestOutcome>> TestAdHoc(
        [FromBody] CreateConnectionRequest req, CancellationToken ct)
    {
        var r = await _service.TestAdHocAsync(
            req.Server, req.Database, req.AuthType, req.Username, req.Password, ct);
        return Ok(new ConnectionTestOutcome(r.Success, r.Error, r.ElapsedMs));
    }

    private static ConnectionListItem ToListItem(Connection c) => new(
        c.Id, c.Name, c.Server, c.Database, c.AuthType, c.Username,
        c.Status, c.LastTestedAt, c.LastError, c.CreatedAt, c.UpdatedAt);

    private static ConnectionDetail ToDetail(Connection c) => new(
        c.Id, c.Name, c.Server, c.Database, c.AuthType, c.Username,
        c.PasswordEncrypted is { Length: > 0 },
        c.Status, c.LastTestedAt, c.LastError, c.CreatedAt, c.UpdatedAt);
}
