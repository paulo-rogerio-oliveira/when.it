using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DbSense.Core.Security;

public interface IJwtService
{
    string IssueToken(Guid userId, string username, string role);
    TokenValidationParameters GetValidationParameters();
}

public class JwtService : IJwtService
{
    private readonly SecurityOptions _options;
    private readonly SymmetricSecurityKey _key;

    public JwtService(IOptions<SecurityOptions> options)
    {
        _options = options.Value;
        if (string.IsNullOrWhiteSpace(_options.JwtSecret))
            throw new InvalidOperationException("Security:JwtSecret is not configured.");

        var bytes = Convert.FromBase64String(_options.JwtSecret);
        if (bytes.Length < 32)
            throw new InvalidOperationException("Security:JwtSecret must be >= 256 bits.");
        _key = new SymmetricSecurityKey(bytes);
    }

    public string IssueToken(Guid userId, string username, string role)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, username),
            new Claim(ClaimTypes.Role, role),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var creds = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _options.JwtIssuer,
            audience: _options.JwtAudience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddHours(_options.JwtExpirationHours),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public TokenValidationParameters GetValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidIssuer = _options.JwtIssuer,
        ValidateAudience = true,
        ValidAudience = _options.JwtAudience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = _key,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(1)
    };
}
