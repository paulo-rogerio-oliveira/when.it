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
    Task<Rule?> UpdateAsync(
        Guid id,
        string name, string? description, string definitionJson,
        CancellationToken ct = default);
    Task<Rule?> ActivateAsync(Guid id, CancellationToken ct = default);
    Task<Rule?> PauseAsync(Guid id, CancellationToken ct = default);
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

    public async Task<Rule?> UpdateAsync(
        Guid id,
        string name, string? description, string definitionJson,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Nome obrigatório.", nameof(name));
        if (string.IsNullOrWhiteSpace(definitionJson))
            throw new ArgumentException("Definição obrigatória.", nameof(definitionJson));

        // Valida JSON minimamente
        try { System.Text.Json.JsonDocument.Parse(definitionJson); }
        catch (System.Text.Json.JsonException ex)
        {
            throw new ArgumentException($"Definição não é JSON válido: {ex.Message}", nameof(definitionJson));
        }

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var rule = await ctx.Rules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null) return null;

        if (rule.Status is "active" or "archived")
            throw new InvalidOperationException(
                $"Regra em status '{rule.Status}' não pode ser editada. Pause antes de editar.");

        rule.Name = name.Trim();
        rule.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        rule.Definition = definitionJson;
        rule.Version += 1;
        rule.UpdatedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync(ct);
        return rule;
    }

    public async Task<Rule?> ActivateAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var rule = await ctx.Rules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null) return null;

        if (!HasReaction(rule.Definition))
            throw new InvalidOperationException("Regra não pode ser ativada sem reaction configurada.");

        rule.Status = "active";
        rule.ActivatedAt ??= DateTime.UtcNow;
        rule.UpdatedAt = DateTime.UtcNow;

        await EnqueueReloadCommandAsync(ctx, ct);
        await ctx.SaveChangesAsync(ct);
        return rule;
    }

    public async Task<Rule?> PauseAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var rule = await ctx.Rules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null) return null;

        rule.Status = "paused";
        rule.UpdatedAt = DateTime.UtcNow;

        await EnqueueReloadCommandAsync(ctx, ct);
        await ctx.SaveChangesAsync(ct);
        return rule;
    }

    private static bool HasReaction(string definitionJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(definitionJson);
            return doc.RootElement.TryGetProperty("reaction", out var r)
                && r.ValueKind == System.Text.Json.JsonValueKind.Object
                && r.TryGetProperty("type", out var t)
                && t.ValueKind == System.Text.Json.JsonValueKind.String
                && !string.IsNullOrWhiteSpace(t.GetString());
        }
        catch
        {
            return false;
        }
    }

    private static Task EnqueueReloadCommandAsync(DbSenseContext ctx, CancellationToken ct)
    {
        ctx.WorkerCommands.Add(new WorkerCommand
        {
            Command = "reload_rules",
            IssuedAt = DateTime.UtcNow,
            Status = "pending"
        });
        return Task.CompletedTask;
    }
}
