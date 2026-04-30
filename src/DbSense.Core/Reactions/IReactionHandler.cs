using System.Text.Json;

namespace DbSense.Core.Reactions;

public interface IReactionHandler
{
    string Type { get; }
    Task<ReactionResult> ExecuteAsync(ReactionContext ctx, CancellationToken ct = default);
}

public record ReactionContext(
    long EventsLogId,
    string PayloadJson,
    JsonElement Config,
    string IdempotencyKey,
    Guid RuleId,
    int RuleVersion);

public record ReactionResult(
    bool Success,
    string? Error,
    int? ExitCode = null,
    long? AffectedRows = null);
