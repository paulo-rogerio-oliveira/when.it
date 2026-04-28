using DbSense.Core.Persistence;
using DbSense.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DbSense.Core.Auth;

public interface IAuthService
{
    Task<LoginOutcome> LoginAsync(string username, string password, CancellationToken ct = default);
}

public record LoginOutcome(bool Success, string? Token, DateTime? ExpiresAt, string? Username, string? Role, string? Error);

public class AuthService : IAuthService
{
    private readonly IDbContextFactory<DbSenseContext> _contextFactory;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtService _jwt;
    private readonly SecurityOptions _options;

    public AuthService(
        IDbContextFactory<DbSenseContext> contextFactory,
        IPasswordHasher hasher,
        IJwtService jwt,
        IOptions<SecurityOptions> options)
    {
        _contextFactory = contextFactory;
        _hasher = hasher;
        _jwt = jwt;
        _options = options.Value;
    }

    public async Task<LoginOutcome> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);

        var user = await ctx.Users
            .FirstOrDefaultAsync(u => u.Username == username, ct);

        if (user is null || !_hasher.Verify(password, user.PasswordHash))
            return new LoginOutcome(false, null, null, null, null, "Invalid credentials.");

        var expiresAt = DateTime.UtcNow.AddHours(_options.JwtExpirationHours);
        var token = _jwt.IssueToken(user.Id, user.Username, user.Role);

        return new LoginOutcome(true, token, expiresAt, user.Username, user.Role, null);
    }
}
