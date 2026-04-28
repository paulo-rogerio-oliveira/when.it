using System.Net;
using System.Net.Http.Json;
using DbSense.Api.Tests.Infrastructure;
using DbSense.Contracts.Setup;
using DbSense.Core.Domain;
using DbSense.Core.Setup;
using FluentAssertions;

namespace DbSense.Api.Tests.Setup;

// Cada teste constrói sua própria factory para isolar o banco InMemory.
public class SetupEndpointsTests
{
    [Fact]
    public async Task GetStatus_Returns_NotProvisioned_On_Empty_Database()
    {
        await using var factory = new DbSenseApiFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/setup/status");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<SetupStatusResponse>();
        body!.Status.Should().Be("not_provisioned");
        body.SchemaVersion.Should().BeNull();
    }

    [Fact]
    public async Task GetStatus_Returns_PendingAdmin_When_SchemaProvisioned_But_NoAdmin()
    {
        await using var factory = new DbSenseApiFactory();

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.SetupInfo.Add(new SetupInfo
            {
                SchemaVersion = ProvisioningService.CurrentSchemaVersion,
                ProvisionedAt = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var body = await client.GetFromJsonAsync<SetupStatusResponse>("/api/setup/status");

        body!.Status.Should().Be("pending_admin");
        body.SchemaVersion.Should().Be(ProvisioningService.CurrentSchemaVersion);
    }

    [Fact]
    public async Task CreateAdmin_Returns_Ok_And_Persists_User()
    {
        await using var factory = new DbSenseApiFactory();
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/setup/create-admin",
            new CreateAdminRequest("admin", "password123"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<CreateAdminResponse>();
        body!.Username.Should().Be("admin");
        body.UserId.Should().NotBeEmpty();

        await using var ctx = await factory.CreateDbContextAsync();
        ctx.Users.Should().ContainSingle(u => u.Username == "admin" && u.Role == "admin");
    }

    [Fact]
    public async Task CreateAdmin_Returns_BadRequest_For_Short_Password()
    {
        await using var factory = new DbSenseApiFactory();
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/setup/create-admin",
            new CreateAdminRequest("admin", "abc"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateAdmin_Returns_Conflict_When_Admin_Already_Exists()
    {
        await using var factory = new DbSenseApiFactory();
        var client = factory.CreateClient();

        var first = await client.PostAsJsonAsync("/api/setup/create-admin",
            new CreateAdminRequest("admin", "password123"));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await client.PostAsJsonAsync("/api/setup/create-admin",
            new CreateAdminRequest("other", "password123"));
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
