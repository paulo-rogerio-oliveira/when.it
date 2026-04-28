using DbSense.Core.Domain;
using DbSense.Core.Persistence;
using DbSense.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace DbSense.Core.Setup;

public interface IAdminUserService
{
    Task<User> CreateAdminAsync(string username, string password, CancellationToken ct = default);
}

public class AdminUserService : IAdminUserService
{
    private readonly IDbContextFactory<DbSenseContext> _contextFactory;
    private readonly IPasswordHasher _hasher;

    public AdminUserService(IDbContextFactory<DbSenseContext> contextFactory, IPasswordHasher hasher)
    {
        _contextFactory = contextFactory;
        _hasher = hasher;
    }

    public async Task<User> CreateAdminAsync(string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("Username required", nameof(username));
        if (password.Length < 8) throw new ArgumentException("Password must be at least 8 characters", nameof(password));

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);

        var hasAdmin = await ctx.Users.AnyAsync(u => u.Role == "admin", ct);
        if (hasAdmin)
            throw new InvalidOperationException("Admin user already exists.");

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username.Trim(),
            PasswordHash = _hasher.Hash(password),
            Role = "admin",
            CreatedAt = DateTime.UtcNow
        };

        ctx.Users.Add(user);
        await ctx.SaveChangesAsync(ct);
        return user;
    }
}
