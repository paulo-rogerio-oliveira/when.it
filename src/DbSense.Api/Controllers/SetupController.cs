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

    // Persiste no escopo User do Windows (HKEY_CURRENT_USER\Environment) os
    // segredos que hoje vêm via process env do Electron. Depois disso o app
    // sobe sozinho mesmo sem dbsense.config.json — o próximo boot herda do SO.
    //
    // Os valores ConnectionStrings__ControlDb vêm do request (a connection que
    // o usuário acabou de testar e provisionar no setup). Já as chaves de
    // criptografia/JWT são lidas do process.env atual: o Electron já passou
    // os valores corretos (gerados ou herdados do system env), então só replico
    // pro escopo persistente — evita o usuário ter que digitar segredos.
    [HttpPost("finalize")]
    public ActionResult<FinalizeSetupResponse> Finalize([FromBody] FinalizeSetupRequest req)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Ok(new FinalizeSetupResponse(
                Success: true,
                Error: "Persistência de env vars só é suportada no Windows; nada foi alterado.",
                EnvVarsPersisted: false,
                PersistedKeys: Array.Empty<string>()));
        }

        var persisted = new List<string>();
        try
        {
            var cs = ProvisioningService.BuildConnectionString(
                req.Server, req.Database, req.AuthType, req.Username, req.Password);
            Environment.SetEnvironmentVariable(
                "ConnectionStrings__ControlDb", cs, EnvironmentVariableTarget.User);
            persisted.Add("ConnectionStrings__ControlDb");

            // Chaves só são persistidas se já existem no process env — não inventamos
            // valores aqui; o Electron é o source-of-truth.
            foreach (var key in new[] { "Security__EncryptionKey", "Security__JwtSecret" })
            {
                var current = Environment.GetEnvironmentVariable(key);
                if (string.IsNullOrEmpty(current)) continue;
                Environment.SetEnvironmentVariable(key, current, EnvironmentVariableTarget.User);
                persisted.Add(key);
            }

            return Ok(new FinalizeSetupResponse(true, null, true, persisted.ToArray()));
        }
        catch (Exception ex)
        {
            return Ok(new FinalizeSetupResponse(
                false, ex.Message, persisted.Count > 0, persisted.ToArray()));
        }
    }
}
