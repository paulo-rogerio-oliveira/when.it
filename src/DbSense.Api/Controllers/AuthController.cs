using DbSense.Contracts.Auth;
using DbSense.Core.Auth;
using Microsoft.AspNetCore.Mvc;

namespace DbSense.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _auth;

    public AuthController(IAuthService auth)
    {
        _auth = auth;
    }

    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(
        [FromBody] LoginRequest req,
        CancellationToken ct)
    {
        var outcome = await _auth.LoginAsync(req.Username, req.Password, ct);
        if (!outcome.Success)
            return Unauthorized(new { error = outcome.Error });

        return Ok(new LoginResponse(outcome.Token!, outcome.ExpiresAt!.Value, outcome.Username!, outcome.Role!));
    }
}
