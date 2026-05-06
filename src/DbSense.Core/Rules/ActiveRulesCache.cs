using System.Collections.Concurrent;
using DbSense.Core.Domain;
using DbSense.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DbSense.Core.Rules;

public interface IActiveRulesCache
{
    // Snapshot atual: regras ativas agrupadas por conexão.
    IReadOnlyDictionary<Guid, IReadOnlyList<Rule>> Snapshot { get; }

    // Conexões que precisam de coletor XE rodando.
    IReadOnlyCollection<Guid> ActiveConnectionIds { get; }

    // Recarrega do banco. Threadsafe.
    Task RefreshAsync(CancellationToken ct = default);
}

public class ActiveRulesCache : IActiveRulesCache
{
    private readonly IDbContextFactory<DbSenseContext> _contextFactory;
    private volatile Dictionary<Guid, IReadOnlyList<Rule>> _byConnection = new();

    public ActiveRulesCache(IDbContextFactory<DbSenseContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public IReadOnlyDictionary<Guid, IReadOnlyList<Rule>> Snapshot => _byConnection;

    public IReadOnlyCollection<Guid> ActiveConnectionIds => _byConnection.Keys.ToList();

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        if (!await ctx.Database.CanConnectAsync(ct)) return;

        var rules = await ctx.Rules.AsNoTracking()
            .Where(r => r.Status == "active")
            .ToListAsync(ct);

        var grouped = rules
            .GroupBy(r => r.ConnectionId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Rule>)g.ToList());

        _byConnection = grouped;
    }
}
