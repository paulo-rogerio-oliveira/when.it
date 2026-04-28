using DbSense.Core.Setup;
using Microsoft.EntityFrameworkCore;

namespace DbSense.Core.Persistence;

/// <summary>
/// Resolves the control DB connection string at request time from the runtime store
/// (populated by the setup wizard), falling back to the startup configuration.
/// </summary>
public class DynamicDbContextFactory : IDbContextFactory<DbSenseContext>
{
    private readonly IRuntimeConfigStore _store;
    private readonly string _fallbackConnectionString;

    public DynamicDbContextFactory(IRuntimeConfigStore store, string fallbackConnectionString)
    {
        _store = store;
        _fallbackConnectionString = fallbackConnectionString;
    }

    public DbSenseContext CreateDbContext()
    {
        var cs = _store.GetControlDbConnectionString() ?? _fallbackConnectionString;
        var options = new DbContextOptionsBuilder<DbSenseContext>()
            .UseSqlServer(cs, sql =>
            {
                sql.MigrationsHistoryTable("__EFMigrationsHistory", DbSenseContext.Schema);
                sql.EnableRetryOnFailure(5);
            })
            .Options;
        return new DbSenseContext(options);
    }
}
