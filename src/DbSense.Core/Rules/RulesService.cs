using DbSense.Core.Domain;
using DbSense.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DbSense.Core.Rules;

public interface IRulesService
{
    Task<IReadOnlyList<(Rule Rule, string ConnectionName)>> ListAsync(CancellationToken ct = default);
    Task<(Rule Rule, string ConnectionName)?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Rule> CreateDraftAsync(
        Guid connectionId, Guid? sourceRecordingId,
        string name, string? description, string definitionJson,
        CancellationToken ct = default);
}

public class RulesService : IRulesService
{
    private readonly IDbContextFactory<DbSenseContext> _contextFactory;

    public RulesService(IDbContextFactory<DbSenseContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<IReadOnlyList<(Rule Rule, string ConnectionName)>> ListAsync(CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var rows = await ctx.Rules
            .AsNoTracking()
            .OrderByDescending(r => r.UpdatedAt)
            .Join(ctx.Connections.AsNoTracking(),
                r => r.ConnectionId, c => c.Id,
                (r, c) => new { r, ConnectionName = c.Name })
            .ToListAsync(ct);
        return rows.Select(x => (x.r, x.ConnectionName)).ToList();
    }

    public async Task<(Rule Rule, string ConnectionName)?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var row = await ctx.Rules
            .AsNoTracking()
            .Where(r => r.Id == id)
            .Join(ctx.Connections.AsNoTracking(),
                r => r.ConnectionId, c => c.Id,
                (r, c) => new { r, ConnectionName = c.Name })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : (row.r, row.ConnectionName);
    }

    public async Task<Rule> CreateDraftAsync(
        Guid connectionId, Guid? sourceRecordingId,
        string name, string? description, string definitionJson,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Nome obrigatório.", nameof(name));
        if (string.IsNullOrWhiteSpace(definitionJson))
            throw new ArgumentException("Definição obrigatória.", nameof(definitionJson));

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var connection = await ctx.Connections.FirstOrDefaultAsync(c => c.Id == connectionId, ct)
            ?? throw new InvalidOperationException("Conexão não encontrada.");

        var now = DateTime.UtcNow;
        var rule = new Rule
        {
            Id = Guid.NewGuid(),
            ConnectionId = connection.Id,
            SourceRecordingId = sourceRecordingId,
            Name = name.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Version = 1,
            Definition = definitionJson,
            Status = "draft",
            CreatedAt = now,
            UpdatedAt = now
        };
        ctx.Rules.Add(rule);
        await ctx.SaveChangesAsync(ct);
        return rule;
    }
}
