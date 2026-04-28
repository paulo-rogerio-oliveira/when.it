using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using DbSense.Core.Auth;
using DbSense.Core.Domain;
using DbSense.Core.Security;
using DbSense.Core.Tests.Helpers;
using FluentAssertions;

namespace DbSense.Core.Tests.Auth;

public class AuthServiceTests
{
    private static (AuthService svc, TestDbContextFactory factory, BCryptPasswordHasher hasher) Build()
    {
        var factory = new TestDbContextFactory();
        var hasher = new BCryptPasswordHasher();
        var opts = new SecurityOptions
        {
            JwtSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
            JwtExpirationHours = 8,
            JwtIssuer = "dbsense-test",
            JwtAudience = "dbsense-test"
        };
        var jwt = new JwtService(TestOptions.Wrap(opts));
        var svc = new AuthService(factory, hasher, jwt, TestOptions.Wrap(opts));
        return (svc, factory, hasher);
    }

    private static async Task SeedUserAsync(TestDbContextFactory factory, BCryptPasswordHasher hasher,
        string username, string password, string role = "admin")
    {
        await using var ctx = factory.CreateDbContext();
        ctx.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = hasher.Hash(password),
            Role = role,
            CreatedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Login_Returns_Token_For_Valid_Credentials()
    {
        var (svc, factory, hasher) = Build();
        using var _ = factory;
        await SeedUserAsync(factory, hasher, "alice", "password123");

        var result = await svc.LoginAsync("alice", "password123");

        result.Success.Should().BeTrue();
        result.Token.Should().NotBeNullOrWhiteSpace();
        result.Username.Should().Be("alice");
        result.Role.Should().Be("admin");
        result.ExpiresAt.Should().BeAfter(DateTime.UtcNow);
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task Login_Issued_Token_Contains_User_Identity()
    {
        var (svc, factory, hasher) = Build();
        using var _ = factory;
        await SeedUserAsync(factory, hasher, "alice", "password123");

        var result = await svc.LoginAsync("alice", "password123");
        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(result.Token);

        parsed.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.UniqueName && c.Value == "alice");
    }

    [Fact]
    public async Task Login_Fails_For_Wrong_Password()
    {
        var (svc, factory, hasher) = Build();
        using var _ = factory;
        await SeedUserAsync(factory, hasher, "alice", "password123");

        var result = await svc.LoginAsync("alice", "wrong");

        result.Success.Should().BeFalse();
        result.Token.Should().BeNull();
        result.Error.Should().Be("Invalid credentials.");
    }

    [Fact]
    public async Task Login_Fails_For_Unknown_User()
    {
        var (svc, factory, _) = Build();
        using var _factoryGuard = factory;

        var result = await svc.LoginAsync("ghost", "password123");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid credentials.");
    }
}
