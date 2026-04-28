using DbSense.Contracts.Setup;
using DbSense.Core.Setup;
using Microsoft.AspNetCore.Mvc;

namespace DbSense.Api.Controllers;

[ApiController]
[Route("api/setup")]
public class SetupController : ControllerBase
{
    private readonly IProvisioningService _provisioning;
    private readonly IAdminUserService _admin;

    public SetupController(IProvisioningService provisioning, IAdminUserService admin)
    {
        _provisioning = provisioning;
        _admin = admin;
    }

    [HttpGet("status")]
    public async Task<ActionResult<SetupStatusResponse>> GetStatus(CancellationToken ct)
    {
        var status = await _provisioning.GetStatusAsync(ct);
        return Ok(new SetupStatusResponse(status.Status, status.SchemaVersion, status.ProvisionedAt));
    }

    [HttpPost("test-connection")]
    public async Task<ActionResult<TestConnectionResponse>> TestConnection(
        [FromBody] TestConnectionRequest req,
        CancellationToken ct)
    {
        var r = await _provisioning.TestConnectionAsync(
            req.Server, req.Database, req.AuthType, req.Username, req.Password, ct);
        return Ok(new TestConnectionResponse(r.Success, r.Error, r.ElapsedMs));
    }

    [HttpPost("provision")]
    public async Task<ActionResult<ProvisionResponse>> Provision(
        [FromBody] ProvisionRequest req,
        CancellationToken ct)
    {
        var r = await _provisioning.ProvisionAsync(
            req.Server, req.Database, req.AuthType, req.Username, req.Password, ct);
        if (!r.Success)
            return BadRequest(new ProvisionResponse(false, r.Error, 0, r.SchemaVersion, r.ErrorCode, r.Hint));
        return Ok(new ProvisionResponse(true, null, r.TablesCreated, r.SchemaVersion));
    }

    [HttpPost("create-admin")]
    public async Task<ActionResult<CreateAdminResponse>> CreateAdmin(
        [FromBody] CreateAdminRequest req,
        CancellationToken ct)
    {
        try
        {
            var user = await _admin.CreateAdminAsync(req.Username, req.Password, ct);
            return Ok(new CreateAdminResponse(user.Id, user.Username));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
