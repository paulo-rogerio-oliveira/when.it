namespace DbSense.Core.Reactions;

public interface IReactionDispatcher
{
    Task<ReactionResult> DispatchAsync(string type, ReactionContext ctx, CancellationToken ct = default);
}

public class ReactionDispatcher : IReactionDispatcher
{
    private readonly Dictionary<string, IReactionHandler> _byType;

    public ReactionDispatcher(IEnumerable<IReactionHandler> handlers)
    {
        _byType = handlers.ToDictionary(h => h.Type, StringComparer.OrdinalIgnoreCase);
    }

    public Task<ReactionResult> DispatchAsync(string type, ReactionContext ctx, CancellationToken ct = default)
    {
        if (!_byType.TryGetValue(type, out var handler))
            return Task.FromResult(new ReactionResult(false, $"Reaction type '{type}' não registrado."));
        return handler.ExecuteAsync(ctx, ct);
    }
}
