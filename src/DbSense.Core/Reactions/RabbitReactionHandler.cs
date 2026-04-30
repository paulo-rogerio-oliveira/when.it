namespace DbSense.Core.Reactions;

// Stub: a tabela rabbitmq_destinations e o pool de conexões ainda não foram implementados.
// Quando esse handler for promovido a executar de verdade, ele deve:
// 1. Resolver destination_id em dbsense.rabbitmq_destinations (host/credenciais decriptadas).
// 2. Manter pool de IConnection por destination_id.
// 3. Publicar em config.exchange/routing_key com confirmSelect e mandatory=true.
// 4. Adicionar headers automáticos x-idempotency-key, x-rule-id, x-rule-version, content-type.
public class RabbitReactionHandler : IReactionHandler
{
    public string Type => "rabbit";

    public Task<ReactionResult> ExecuteAsync(ReactionContext ctx, CancellationToken ct = default) =>
        Task.FromResult(new ReactionResult(
            false,
            "Reaction 'rabbit' ainda não implementada — falta cadastro de rabbitmq_destinations e publisher."));
}
