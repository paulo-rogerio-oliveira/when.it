using DbSense.Core.Domain;
using DbSense.Core.Persistence;
using DbSense.Core.XEvents;
using Microsoft.EntityFrameworkCore;

namespace DbSense.Worker.Workers;

// Polls dbsense.worker_commands and dispatches to the appropriate handler.
public class CommandProcessorWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IDbContextFactory<DbSenseContext> _contextFactory;
    private readonly IRecordingCollector _collector;
    private readonly ILogger<CommandProcessorWorker> _logger;

    public CommandProcessorWorker(
        IDbContextFactory<DbSenseContext> contextFactory,
        IRecordingCollector collector,
        ILogger<CommandProcessorWorker> logger)
    {
        _contextFactory = contextFactory;
        _collector = collector;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CommandProcessorWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error polling worker_commands.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { /* shutting down */ }
        }
    }

    private async Task ProcessPendingAsync(CancellationToken ct)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        if (!await ctx.Database.CanConnectAsync(ct)) return;

        var pending = await ctx.WorkerCommands
            .Where(c => c.Status == "pending")
            .OrderBy(c => c.Id)
            .Take(20)
            .ToListAsync(ct);

        foreach (var cmd in pending)
        {
            var (status, result) = await DispatchAsync(cmd, ct);
            cmd.Status = status;
            cmd.ProcessedAt = DateTime.UtcNow;
            cmd.Result = result;
        }

        if (pending.Count > 0)
            await ctx.SaveChangesAsync(ct);
    }

    private async Task<(string Status, string? Result)> DispatchAsync(WorkerCommand cmd, CancellationToken ct)
    {
        try
        {
            switch (cmd.Command)
            {
                case "start_recording":
                    if (cmd.TargetId is null)
                        return ("failed", "target_id ausente");
                    await _collector.StartAsync(cmd.TargetId.Value, ct);
                    return ("processed", "started");

                case "stop_recording":
                    if (cmd.TargetId is null)
                        return ("failed", "target_id ausente");
                    await _collector.StopAsync(cmd.TargetId.Value, ct);
                    return ("processed", "stopped");

                default:
                    _logger.LogDebug("Unknown command {Command} (id={Id}).", cmd.Command, cmd.Id);
                    return ("processed", "no-op (unknown command)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch command {Id} ({Command}).", cmd.Id, cmd.Command);
            return ("failed", ex.Message);
        }
    }
}
