using DbSense.Contracts.RabbitDestinations;
using DbSense.Core.Domain;
using DbSense.Core.RabbitDestinations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbSense.Api.Controllers;

[ApiController]
[Route("api/rabbit-destinations")]
[Authorize]
public class RabbitDestinationsController : ControllerBase
{
    private readonly IRabbitDestinationsService _service;

    public RabbitDestinationsController(IRabbitDestinationsService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<RabbitDestinationListItem>>> List(CancellationToken ct)
    {
        var items = await _service.ListAsync(ct);
        return Ok(items.Select(ToListItem));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RabbitDestinationDetail>> Get(Guid id, CancellationToken ct)
    {
        var d = await _service.GetAsync(id, ct);
        if (d is null) return NotFound();
        return Ok(ToDetail(d));
    }

    [HttpPost]
    public async Task<ActionResult<RabbitDestinationDetail>> Create(
        [FromBody] CreateRabbitDestinationRequest req, CancellationToken ct)
    {
        try
        {
            var d = await _service.CreateAsync(
                req.Name, req.Host, req.Port, req.VirtualHost, req.Username,
                req.Password, req.UseTls, req.DefaultExchange, ct);
            return CreatedAtAction(nameof(Get), new { id = d.Id }, ToDetail(d));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<RabbitDestinationDetail>> Update(
        Guid id, [FromBody] UpdateRabbitDestinationRequest req, CancellationToken ct)
    {
        try
        {
            var d = await _service.UpdateAsync(
                id, req.Name, req.Host, req.Port, req.VirtualHost, req.Username,
                req.Password, req.UseTls, req.DefaultExchange, req.ClearPassword, ct);
            if (d is null) return NotFound();
            return Ok(ToDetail(d));
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
    public async Task<ActionResult<RabbitDestinationTestOutcome>> Test(Guid id, CancellationToken ct)
    {
        var r = await _service.TestAsync(id, ct);
        return Ok(new RabbitDestinationTestOutcome(r.Success, r.Error, r.ElapsedMs));
    }

    [HttpPost("test")]
    public async Task<ActionResult<RabbitDestinationTestOutcome>> TestAdHoc(
        [FromBody] CreateRabbitDestinationRequest req, CancellationToken ct)
    {
        var r = await _service.TestAdHocAsync(
            req.Host, req.Port, req.VirtualHost, req.Username, req.Password, req.UseTls, ct);
        return Ok(new RabbitDestinationTestOutcome(r.Success, r.Error, r.ElapsedMs));
    }

    private static RabbitDestinationListItem ToListItem(RabbitMqDestination d) => new(
        d.Id, d.Name, d.Host, d.Port, d.VirtualHost, d.Username, d.UseTls,
        d.DefaultExchange, d.Status, d.LastTestedAt, d.LastError, d.CreatedAt);

    private static RabbitDestinationDetail ToDetail(RabbitMqDestination d) => new(
        d.Id, d.Name, d.Host, d.Port, d.VirtualHost, d.Username,
        d.PasswordEncrypted is { Length: > 0 }, d.UseTls,
        d.DefaultExchange, d.Status, d.LastTestedAt, d.LastError, d.CreatedAt);
}
