using System.Text;
using System.Text.Json;
using DbSense.Core.Persistence;
using DbSense.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace DbSense.Core.Reactions;

// Publisher RabbitMQ com confirm + mandatory:
//   1. Resolve destination_id em dbsense.rabbitmq_destinations.
//   2. Reusa IConnection do pool (singleton); cria 1 IModel (channel) por execução
//      (channels não são thread-safe).
//   3. ConfirmSelect + WaitForConfirmsOrDie pra garantir que o broker recebeu.
//   4. mandatory=true + handler de BasicReturn pra detectar mensagem que ficou sem
//      binding pra routing key (importante: confirm sem binding ainda volta como ack).
//   5. Headers automáticos: x-dbsense-rule-id / x-dbsense-rule-version /
//      x-dbsense-idempotency-key. Headers do usuário (config.headers) são merged por cima.
public class RabbitReactionHandler : IReactionHandler
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(10);

    private readonly IDbContextFactory<DbSenseContext> _contextFactory;
    private readonly ISecretCipher _cipher;
    private readonly IRabbitConnectionPool _pool;
    private readonly ILogger<RabbitReactionHandler> _logger;

    public string Type => "rabbit";

    public RabbitReactionHandler(
        IDbContextFactory<DbSenseContext> contextFactory,
        ISecretCipher cipher,
        IRabbitConnectionPool pool,
        ILogger<RabbitReactionHandler> logger)
    {
        _contextFactory = contextFactory;
        _cipher = cipher;
        _pool = pool;
        _logger = logger;
    }

    public async Task<ReactionResult> ExecuteAsync(ReactionContext ctx, CancellationToken ct = default)
    {
        var destIdStr = TryGetString(ctx.Config, "destination_id");
        if (!Guid.TryParse(destIdStr, out var destId))
            return new ReactionResult(false, "config.destination_id ausente ou inválido.");

        await using var dbCtx = await _contextFactory.CreateDbContextAsync(ct);
        var dest = await dbCtx.RabbitMqDestinations.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == destId, ct);
        if (dest is null)
            return new ReactionResult(false, $"RabbitMQ destination {destId} não encontrado.");

        var password = dest.PasswordEncrypted is { Length: > 0 }
            ? _cipher.Decrypt(dest.PasswordEncrypted)
            : string.Empty;

        var exchange = TryGetString(ctx.Config, "exchange") ?? dest.DefaultExchange ?? string.Empty;
        var routingKey = TryGetString(ctx.Config, "routing_key") ?? string.Empty;
        var bodyText = TryGetString(ctx.Config, "body") ?? ctx.PayloadJson;

        _logger.LogInformation(
            "Publicando reaction Rabbit: events_log={EventsLogId}, destination={DestinationId}, host={Host}, exchange='{Exchange}', routing_key='{RoutingKey}'.",
            ctx.EventsLogId,
            destId,
            dest.Host,
            exchange,
            routingKey);

        IConnection conn;
        try
        {
            conn = _pool.GetOrCreate(dest, password ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao obter conexão Rabbit (dest={Id}).", destId);
            return new ReactionResult(false, $"Falha ao conectar em {dest.Host}: {ex.Message}");
        }

        // BasicPublish + WaitForConfirms são síncronos no client 6.x — Task.Run pra
        // não bloquear o thread do worker.
        var result = await Task.Run(() => Publish(conn, exchange, routingKey, bodyText, ctx), ct);
        if (result.Success)
        {
            _logger.LogInformation(
                "Reaction Rabbit publicada com sucesso: events_log={EventsLogId}, exchange='{Exchange}', routing_key='{RoutingKey}'.",
                ctx.EventsLogId,
                exchange,
                routingKey);
        }
        else
        {
            _logger.LogWarning(
                "Reaction Rabbit falhou: events_log={EventsLogId}, exchange='{Exchange}', routing_key='{RoutingKey}'. Erro: {Error}",
                ctx.EventsLogId,
                exchange,
                routingKey,
                result.Error);
        }
        return result;
    }

    private ReactionResult Publish(
        IConnection conn, string exchange, string routingKey, string bodyText, ReactionContext ctx)
    {
        IModel channel;
        try { channel = conn.CreateModel(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateModel falhou ao publicar reaction Rabbit events_log={EventsLogId}.", ctx.EventsLogId);
            return new ReactionResult(false, $"CreateModel falhou: {ex.Message}");
        }

        try
        {
            try { channel.ConfirmSelect(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConfirmSelect falhou ao publicar reaction Rabbit events_log={EventsLogId}.", ctx.EventsLogId);
                return new ReactionResult(false, $"ConfirmSelect falhou: {ex.Message}");
            }

            // mandatory=true: se a routing key não tem binding, broker devolve via
            // BasicReturn antes do ack. Capturamos pra reportar como falha.
            string? returnReason = null;
            void OnReturn(object? _, BasicReturnEventArgs args)
            {
                returnReason = $"{args.ReplyCode} {args.ReplyText} exchange='{args.Exchange}' routingKey='{args.RoutingKey}'";
            }
            channel.BasicReturn += OnReturn;

            try
            {
                var props = channel.CreateBasicProperties();
                props.ContentType = "application/json";
                props.MessageId = ctx.IdempotencyKey;
                props.DeliveryMode = 2; // persistent
                props.Headers = BuildHeaders(ctx);

                var body = Encoding.UTF8.GetBytes(bodyText ?? string.Empty);

                channel.BasicPublish(
                    exchange: exchange,
                    routingKey: routingKey,
                    mandatory: true,
                    basicProperties: props,
                    body: body);

                // Bloqueia até receber confirm do broker (ou timeout).
                channel.WaitForConfirmsOrDie(ConfirmTimeout);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "BasicPublish/confirm falhou ao publicar reaction Rabbit events_log={EventsLogId}, exchange='{Exchange}', routing_key='{RoutingKey}'.",
                    ctx.EventsLogId,
                    exchange,
                    routingKey);
                return new ReactionResult(false, ex.Message);
            }
            finally
            {
                channel.BasicReturn -= OnReturn;
            }

            if (returnReason is not null)
            {
                _logger.LogWarning(
                    "Broker retornou mensagem Rabbit sem binding: events_log={EventsLogId}, {ReturnReason}",
                    ctx.EventsLogId,
                    returnReason);
                return new ReactionResult(false, $"Mensagem retornada pelo broker (sem binding pra routing key): {returnReason}");
            }

            return new ReactionResult(true, null);
        }
        finally
        {
            try { channel.Close(); } catch { /* ignore */ }
            try { channel.Dispose(); } catch { /* ignore */ }
        }
    }

    private static IDictionary<string, object?> BuildHeaders(ReactionContext ctx)
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["x-dbsense-rule-id"] = ctx.RuleId.ToString(),
            ["x-dbsense-rule-version"] = ctx.RuleVersion,
            ["x-dbsense-idempotency-key"] = ctx.IdempotencyKey,
            ["x-dbsense-events-log-id"] = ctx.EventsLogId
        };

        if (ctx.Config.TryGetProperty("headers", out var hEl) && hEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in hEl.EnumerateObject())
                headers[prop.Name] = JsonValueToHeaderValue(prop.Value);
        }
        return headers;
    }

    private static object? JsonValueToHeaderValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.TryGetInt64(out var l) ? (object)l : v.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => v.GetRawText()
    };

    private static string? TryGetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
