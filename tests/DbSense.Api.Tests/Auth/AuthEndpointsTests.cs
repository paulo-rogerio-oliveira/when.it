using System.Net;
using System.Net.Http.Json;
using DbSense.Api.Tests.Infrastructure;
using DbSense.Contracts.Auth;
using DbSense.Contracts.Setup;
using FluentAssertions;

namespace DbSense.Api.Tests.Auth;

public class AuthEndpointsTests
{
    private static async Task SeedAdminAsync(HttpClient client, string username = "admin", string password = "password123")
    {
        var resp = await client.PostAsJsonAsync("/api/setup/create-admin",
            new CreateAdminRequest(username, password));
        resp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Login_Returns_Token_For_Valid_Credentials()
    {
        await using var factory = new DbSenseApiFactory();
        var client = factory.CreateClient();
        await SeedAdminAsync(client);

        var resp = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("admin", "password123"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<LoginResponse>();
        body!.Token.Should().NotBeNullOrWhiteSpace();
        body.Username.Should().Be("admin");
        body.Role.Should().Be("admin");
        body.ExpiresAt.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task Login_Returns_Unauthorized_For_Wrong_Password()
    {
        await using var factory = new DbSenseApiFactory();
        var client = factory.CreateClient();
        await SeedAdminAsync(client);

        var resp = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("admin", "wrong-password"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_Returns_Unauthorized_For_Unknown_User()
    {
        await using var factory = new DbSenseApiFactory();
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("ghost", "password123"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
