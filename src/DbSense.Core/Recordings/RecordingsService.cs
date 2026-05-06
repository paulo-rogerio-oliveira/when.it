using System.Text.Json;
using DbSense.Core.Domain;
using DbSense.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DbSense.Core.Recordings;

public interface IRecordingsService
{
    Task<IReadOnlyList<(Recording Recording, string ConnectionName)>> ListAsync(CancellationToken ct = default);
    Task<(Recording Recording, string ConnectionName)?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Recording> StartAsync(
        Guid connectionId, string name, string? description,
        string? filterHostName, string? filterAppName, string? filterLoginName,
        CancellationToken ct = default);
    Task<Recording?> StopAsync(Guid id, CancellationToken ct = default);
    Task<Recording?> DiscardAsync(Guid id, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<(IReadOnlyList<RecordingEvent> Items, int Total)> ListEventsAsync(
        Guid id, long? afterId, int limit, CancellationToken ct = default);
}

public class RecordingsService : IRecordingsService
{
    private readonly IDbContextFactory<DbSenseContext> _contextFactory;

    public RecordingsService(IDbContextFactory<DbSenseContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<IReadOnlyList<(Recording Recording, string ConnectionName)>> ListAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var rows = await ctx.Recordings
            .AsNoTracking()
            .OrderByDescending(r => r.StartedAt)
            .Join(ctx.Connections.AsNoTracking(),
                r => r.ConnectionId, c => c.Id,
                (r, c) => new { r, ConnectionName = c.Name })
            .ToListAsync(ct);
        return rows.Select(x => (x.r, x.ConnectionName)).ToList();
    }

    public async Task<(Recording Recording, string ConnectionName)?> GetAsync(
        Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var row = await ctx.Recordings
            .AsNoTracking()
            .Where(r => r.Id == id)
            .Join(ctx.Connections.AsNoTracking(),
                r => r.ConnectionId, c => c.Id,
                (r, c) => new { r, ConnectionName = c.Name })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : (row.r, row.ConnectionName);
    }

    public async Task<Recording> StartAsync(
        Guid connectionId, string name, string? description,
        string? filterHostName, string? filterAppName, string? filterLoginName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Nome obrigatório.", nameof(name));

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var connection = await ctx.Connections.FirstOrDefaultAsync(c => c.Id == connectionId, ct)
            ?? throw new InvalidOperationException("Conexão não encontrada.");

        var now = DateTime.UtcNow;
        var rec = new Recording
        {
            Id = Guid.NewGuid(),
            ConnectionId = connection.Id,
            Name = name.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            FilterHostName = string.IsNullOrWhiteSpace(filterHostName) ? null : filterHostName.Trim(),
            FilterAppName = string.IsNullOrWhiteSpace(filterAppName) ? null : filterAppName.Trim(),
            FilterLoginName = string.IsNullOrWhiteSpace(filterLoginName) ? null : filterLoginName.Trim(),
            Status = "recording",
            StartedAt = now,
            EventCount = 0,
            CreatedAt = now
        };
        ctx.Recordings.Add(rec);
        ctx.WorkerCommands.Add(BuildCommand("start_recording", rec.Id, new
        {
            connectionId = rec.ConnectionId,
            filterHostName = rec.FilterHostName,
            filterAppName = rec.FilterAppName,
            filterLoginName = rec.FilterLoginName
        }, now));
        await ctx.SaveChangesAsync(ct);
        return rec;
    }

    public async Task<Recording?> StopAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var rec = await ctx.Recordings.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rec is null) return null;
        if (rec.Status != "recording") return rec;

        var now = DateTime.UtcNow;
        rec.Status = "completed";
        rec.StoppedAt = now;
        ctx.WorkerCommands.Add(BuildCommand("stop_recording", rec.Id, payload: null, now));
        await ctx.SaveChangesAsync(ct);
        return rec;
    }

    public async Task<Recording?> DiscardAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var rec = await ctx.Recordings.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rec is null) return null;

        var now = DateTime.UtcNow;
        if (rec.Status == "recording")
        {
            ctx.WorkerCommands.Add(BuildCommand("stop_recording", rec.Id, payload: null, now));
            rec.StoppedAt = now;
        }
        rec.Status = "discarded";
        await ctx.SaveChangesAsync(ct);
        return rec;
    }

    // Deleção física da gravação. Bloqueia se a gravação ainda está rodando — o usuário
    // precisa parar antes (evita race com o RecordingCollector tentando persistir eventos
    // enquanto o registro já não existe mais). Regras que referenciavam a gravação ficam
    // preservadas: só desassociam (SourceRecordingId = NULL).
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var rec = await ctx.Recordings.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rec is null) return false;
        if (rec.Status == "recording")
            throw new InvalidOperationException("Pare a gravação antes de excluí-la.");

        await ctx.Rules
            .Where(r => r.SourceRecordingId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.SourceRecordingId, (Guid?)null), ct);

        await ctx.RecordingEvents
            .Where(e => e.RecordingId == id)
            .ExecuteDeleteAsync(ct);

        ctx.Recordings.Remove(rec);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<(IReadOnlyList<RecordingEvent> Items, int Total)> ListEventsAsync(
        Guid id, long? afterId, int limit, CancellationToken ct = default)
    {
        if (limit <= 0 || limit > 500) limit = 100;
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var query = ctx.RecordingEvents.AsNoTracking().Where(e => e.RecordingId == id);
        var total = await query.CountAsync(ct);
        var items = await query
            .Where(e => afterId == null || e.Id > afterId)
            .OrderBy(e => e.Id)
            .Take(limit)
            .ToListAsync(ct);
        return (items, total);
    }

    private static WorkerCommand BuildCommand(string command, Guid targetId, object? payload, DateTime now) => new()
    {
        Command = command,
        TargetId = targetId,
        Payload = payload is null ? null : JsonSerializer.Serialize(payload),
        IssuedAt = now,
        Status = "pending"
    };
}
