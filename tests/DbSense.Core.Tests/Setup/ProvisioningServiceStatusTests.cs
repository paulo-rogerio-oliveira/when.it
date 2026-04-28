using DbSense.Core.Domain;
using DbSense.Core.Setup;
using DbSense.Core.Tests.Helpers;
using FluentAssertions;

namespace DbSense.Core.Tests.Setup;

// Cobre apenas GetStatusAsync — TestConnection/Provision dependem de SQL Server real
// e ficam para os testes de integração.
public class ProvisioningServiceStatusTests
{
    [Fact]
    public async Task GetStatus_Returns_NotProvisioned_When_SetupInfo_Missing()
    {
        using var factory = new TestDbContextFactory();
        var svc = new ProvisioningService(factory, new InMemoryRuntimeConfigStore());

        var status = await svc.GetStatusAsync();

        status.Status.Should().Be("not_provisioned");
        status.SchemaVersion.Should().BeNull();
        status.ProvisionedAt.Should().BeNull();
    }

    [Fact]
    public async Task GetStatus_Returns_PendingAdmin_When_SetupInfo_Exists_But_No_Admin()
    {
        using var factory = new TestDbContextFactory();
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.SetupInfo.Add(new SetupInfo
            {
                SchemaVersion = ProvisioningService.CurrentSchemaVersion,
                ProvisionedAt = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        var svc = new ProvisioningService(factory, new InMemoryRuntimeConfigStore());
        var status = await svc.GetStatusAsync();

        status.Status.Should().Be("pending_admin");
        status.SchemaVersion.Should().Be(ProvisioningService.CurrentSchemaVersion);
        status.ProvisionedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task GetStatus_Returns_Ready_When_SetupInfo_And_Admin_Exist()
    {
        using var factory = new TestDbContextFactory();
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.SetupInfo.Add(new SetupInfo
            {
                SchemaVersion = ProvisioningService.CurrentSchemaVersion,
                ProvisionedAt = DateTime.UtcNow
            });
            ctx.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                Username = "admin",
                PasswordHash = "x",
                Role = "admin",
                CreatedAt = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        var svc = new ProvisioningService(factory, new InMemoryRuntimeConfigStore());
        var status = await svc.GetStatusAsync();

        status.Status.Should().Be("ready");
    }
}
