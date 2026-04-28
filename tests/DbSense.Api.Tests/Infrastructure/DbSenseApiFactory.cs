using System.Security.Cryptography;
using DbSense.Core.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DbSense.Api.Tests.Infrastructure;

public class DbSenseApiFactory : WebApplicationFactory<Program>
{
    public string DatabaseName { get; } = $"dbsense-test-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:EncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["Security:JwtSecret"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
                ["Security:JwtExpirationHours"] = "8",
                ["Security:JwtIssuer"] = "dbsense-test",
                ["Security:JwtAudience"] = "dbsense-test",
                ["ConnectionStrings:ControlDb"] = "Server=unused;Database=unused;"
            });
        });

        builder.ConfigureServices(services =>
        {
            // Remove o registro do DbContextFactory que aponta para SQL Server
            // e troca por um in-memory isolado por instância de factory.
            var descriptors = services
                .Where(d =>
                    d.ServiceType == typeof(IDbContextFactory<DbSenseContext>) ||
                    d.ServiceType == typeof(DbContextOptions<DbSenseContext>) ||
                    d.ServiceType == typeof(DbContextOptions))
                .ToList();
            foreach (var d in descriptors) services.Remove(d);

            services.AddDbContextFactory<DbSenseContext>(opts =>
                opts.UseInMemoryDatabase(DatabaseName));
        });
    }

    public async Task<DbSenseContext> CreateDbContextAsync()
    {
        var factory = Services.GetRequiredService<IDbContextFactory<DbSenseContext>>();
        return await factory.CreateDbContextAsync();
    }
}
