using DbSense.Core.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace DbSense.Api.Infrastructure;

public class JwtBearerPostConfigure : IPostConfigureOptions<JwtBearerOptions>
{
    private readonly IJwtService _jwt;

    public JwtBearerPostConfigure(IJwtService jwt)
    {
        _jwt = jwt;
    }

    public void PostConfigure(string? name, JwtBearerOptions options)
    {
        options.TokenValidationParameters = _jwt.GetValidationParameters();
    }
}
