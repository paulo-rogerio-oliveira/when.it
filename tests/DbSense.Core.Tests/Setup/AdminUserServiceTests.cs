using DbSense.Core.Domain;
using DbSense.Core.Security;
using DbSense.Core.Setup;
using DbSense.Core.Tests.Helpers;
using FluentAssertions;

namespace DbSense.Core.Tests.Setup;

public class AdminUserServiceTests
{
    private static (AdminUserService svc, TestDbContextFactory factory) Build()
    {
        var factory = new TestDbContextFactory();
        var svc = new AdminUserService(factory, new BCryptPasswordHasher());
        return (svc, factory);
    }

    [Fact]
    public async Task CreateAdmin_Persists_User_With_Hashed_Password()
    {
        var (svc, factory) = Build();
        using var _ = factory;

        var created = await svc.CreateAdminAsync("admin", "password123");

        created.Username.Should().Be("admin");
        created.Role.Should().Be("admin");
        created.PasswordHash.Should().NotBe("password123").And.StartWith("$2");

        await using var ctx = factory.CreateDbContext();
        ctx.Users.Should().ContainSingle().Which.Id.Should().Be(created.Id);
    }

    [Fact]
    public async Task CreateAdmin_Trims_Username()
    {
        var (svc, factory) = Build();
        using var _ = factory;

        var created = await svc.CreateAdminAsync("  alice  ", "password123");

        created.Username.Should().Be("alice");
    }

    [Fact]
    public async Task CreateAdmin_Throws_When_Admin_Already_Exists()
    {
        var (svc, factory) = Build();
        using var _ = factory;

        await svc.CreateAdminAsync("admin", "password123");

        var act = () => svc.CreateAdminAsync("other", "password123");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAdmin_Rejects_Blank_Username(string username)
    {
        var (svc, factory) = Build();
        using var _ = factory;

        var act = () => svc.CreateAdminAsync(username, "password123");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreateAdmin_Rejects_Short_Password()
    {
        var (svc, factory) = Build();
        using var _ = factory;

        var act = () => svc.CreateAdminAsync("admin", "short");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*8*");
    }

    [Fact]
    public async Task CreateAdmin_Allows_New_Admin_When_Only_Operator_Exists()
    {
        var (svc, factory) = Build();
        using var _ = factory;

        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                Username = "op1",
                PasswordHash = "x",
                Role = "operator",
                CreatedAt = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        var act = () => svc.CreateAdminAsync("admin", "password123");
        await act.Should().NotThrowAsync();
    }
}
