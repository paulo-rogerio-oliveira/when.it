using System.Net;
using DbSense.Api.Tests.Infrastructure;
using FluentAssertions;

namespace DbSense.Api.Tests;

public class HealthTests
{
    [Fact]
    public async Task Health_Endpoint_Returns_Ok()
    {
        await using var factory = new DbSenseApiFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/health");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("\"status\":\"ok\"");
    }
}
