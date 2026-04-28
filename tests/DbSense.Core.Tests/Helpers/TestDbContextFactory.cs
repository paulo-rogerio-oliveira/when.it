using DbSense.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DbSense.Core.Tests.Helpers;

internal sealed class TestDbContextFactory : IDbContextFactory<DbSenseContext>, IDisposable
{
    private readonly DbContextOptions<DbSenseContext> _options;

    public TestDbContextFactory(string? databaseName = null)
    {
        _options = new DbContextOptionsBuilder<DbSenseContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString())
            .Options;
    }

    public DbSenseContext CreateDbContext() => new(_options);

    public void Dispose()
    {
        using var ctx = CreateDbContext();
        ctx.Database.EnsureDeleted();
    }
}
