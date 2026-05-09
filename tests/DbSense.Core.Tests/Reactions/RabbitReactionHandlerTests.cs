using System.Text;
using System.Text.Json;
using DbSense.Core.Domain;
using DbSense.Core.Reactions;
using DbSense.Core.Security;
using DbSense.Core.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace DbSense.Core.Tests.Reactions;

// Cobre o publisher Rabbit usando uma IConnection/IModel mockados via NSubstitute.
// O FakePool entrega a connection mockada ao handler — assim cada ExecuteAsync passa
// pelo caminho real (CreateModel → ConfirmSelect → BasicPublish → WaitForConfirmsOrDie)
// sem precisar de broker real.
//
// Lote testado para cada caso: 4 DMLs (insert, 2 updates, delete) entregues
// individualmente como ReactionContexts — modela as duas situações pedidas:
//   * tudo numa única transação (ReactionScenarios.SameTransaction)
//   * espalhadas numa janela de tempo (ReactionScenarios.SeparatedInTime)
public class RabbitReactionHandlerTests
{
    private static (RabbitReactionHandler handler, TestDbContextFactory factory, FakePool pool) Build()
    {
        var factory = new TestDbContextFactory();
        var cipher = Substitute.For<ISecretCipher>();
        cipher.Decrypt(Arg.Any<byte[]>()).Returns("password");
        var pool = new FakePool();
        var handler = new RabbitReactionHandler(
            factory, cipher, pool, NullLogger<RabbitReactionHandler>.Instance);
        return (handler, factory, pool);
    }

    private static async Task<RabbitMqDestination> SeedDestinationAsync(TestDbContextFactory factory)
    {
        var dest = new RabbitMqDestination
        {
            Id = Guid.NewGuid(),
            Name = "test-dest",
            Host = "localhost",
            Port = 5672,
            VirtualHost = "/",
            Username = "guest",
            PasswordEncrypted = new byte[] { 1, 2, 3 },
            DefaultExchange = "events",
            Status = "active",
            CreatedAt = DateTime.UtcNow
        };
        await using var ctx = factory.CreateDbContext();
        ctx.RabbitMqDestinations.Add(dest);
        await ctx.SaveChangesAsync();
        return dest;
    }

    private static JsonElement MakeConfig(Guid destId, string routingKey, string? exchange = null)
    {
        var json = $$"""
        {
          "destination_id": "{{destId}}",
          {{(exchange is null ? "" : $"\"exchange\": \"{exchange}\",")}}
          "routing_key": "{{routingKey}}",
          "headers": { "x-source": "test" }
        }
        """;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private sealed class FakePool : IRabbitConnectionPool
    {
        public IConnection? NextConnection { get; set; }
        public List<Guid> Calls { get; } = new();

        public IConnection GetOrCreate(RabbitMqDestination destination, string decryptedPassword)
        {
            Calls.Add(destination.Id);
            return NextConnection ?? throw new InvalidOperationException("FakePool: NextConnection não configurado.");
        }
    }

    // Devolve mocks já cabados: cada chamada a CreateBasicProperties() retorna
    // uma instância fresca, e cada BasicPublish grava (exchange, routingKey, body).
    private sealed record RabbitMocks(
        IConnection Connection,
        IModel Model,
        List<IBasicProperties> CapturedProps,
        List<(string Exchange, string RoutingKey, byte[] Body)> Publishes);

    private static RabbitMocks SetupMocks(Action<IModel>? onPublish = null)
    {
        var conn = Substitute.For<IConnection>();
        var model = Substitute.For<IModel>();
        conn.CreateModel().Returns(model);

        var capturedProps = new List<IBasicProperties>();
        model.CreateBasicProperties().Returns(_ =>
        {
            var p = Substitute.For<IBasicProperties>();
            capturedProps.Add(p);
            return p;
        });

        var publishes = new List<(string, string, byte[])>();
        model.When(m => m.BasicPublish(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<IBasicProperties>(), Arg.Any<ReadOnlyMemory<byte>>()))
            .Do(callInfo =>
            {
                var ex = (string)callInfo.Args()[0];
                var rk = (string)callInfo.Args()[1];
                var body = (ReadOnlyMemory<byte>)callInfo.Args()[4];
                publishes.Add((ex, rk, body.ToArray()));
                onPublish?.Invoke(model);
            });

        return new RabbitMocks(conn, model, capturedProps, publishes);
    }

    [Fact]
    public async Task Publishes_All_4Dml_InSameTransaction()
    {
        var (handler, factory, pool) = Build();
        using var _ = factory;
        var dest = await SeedDestinationAsync(factory);
        var mocks = SetupMocks();
        pool.NextConnection = mocks.Connection;

        var ruleId = Guid.NewGuid();
        var cfg = MakeConfig(dest.Id, routingKey: "orders.changed", exchange: "events");
        var contexts = ReactionScenarios.SameTransaction(ruleId, 1, cfg);

        var results = new List<ReactionResult>();
        foreach (var ctx in contexts)
            results.Add(await handler.ExecuteAsync(ctx));

        results.Should().HaveCount(4).And.OnlyContain(r => r.Success);
        mocks.Publishes.Should().HaveCount(4);
        mocks.Publishes.Should().OnlyContain(p =>
            p.Exchange == "events" && p.RoutingKey == "orders.changed");

        // O handler deve abrir/fechar 1 channel por evento (channels não são thread-safe).
        mocks.Connection.Received(4).CreateModel();
        mocks.Model.Received(4).ConfirmSelect();
        mocks.Model.Received(4).WaitForConfirmsOrDie(Arg.Any<TimeSpan>());

        // Body de cada publish bate com PayloadJson do contexto correspondente.
        for (int i = 0; i < contexts.Count; i++)
            Encoding.UTF8.GetString(mocks.Publishes[i].Body).Should().Be(contexts[i].PayloadJson);

        // Headers automáticos presentes em cada IBasicProperties.
        mocks.CapturedProps.Should().HaveCount(4);
        for (int i = 0; i < contexts.Count; i++)
        {
            var props = mocks.CapturedProps[i];
            var ctx = contexts[i];
            props.MessageId.Should().Be(ctx.IdempotencyKey);
            props.DeliveryMode.Should().Be((byte)2);
            props.ContentType.Should().Be("application/json");
            ((string)props.Headers["x-dbsense-rule-id"]).Should().Be(ctx.RuleId.ToString());
            ((int)props.Headers["x-dbsense-rule-version"]).Should().Be(ctx.RuleVersion);
            ((string)props.Headers["x-dbsense-idempotency-key"]).Should().Be(ctx.IdempotencyKey);
            ((long)props.Headers["x-dbsense-events-log-id"]).Should().Be(ctx.EventsLogId);
            // Header customizado vindo da config:
            ((string)props.Headers["x-source"]).Should().Be("test");
        }
    }

    [Fact]
    public async Task Publishes_All_4Dml_SeparatedInTime()
    {
        var (handler, factory, pool) = Build();
        using var _ = factory;
        var dest = await SeedDestinationAsync(factory);
        var mocks = SetupMocks();
        pool.NextConnection = mocks.Connection;

        var ruleId = Guid.NewGuid();
        var cfg = MakeConfig(dest.Id, routingKey: "audit.entry", exchange: "events");
        var contexts = ReactionScenarios.SeparatedInTime(ruleId, 2, cfg);

        var results = new List<ReactionResult>();
        foreach (var ctx in contexts)
        {
            results.Add(await handler.ExecuteAsync(ctx));
            await Task.Delay(20);
        }

        results.Should().HaveCount(4).And.OnlyContain(r => r.Success);
        mocks.Publishes.Should().HaveCount(4);

        // IdempotencyKeys (≡ MessageIds) devem ser distintos — events_log_id distinto por evento.
        mocks.CapturedProps.Select(p => (string)p.MessageId).Should().OnlyHaveUniqueItems();

        // O pool é consultado uma vez por evento mas devolve a mesma IConnection
        // (cache hit do publisher é responsabilidade do pool, não do handler).
        pool.Calls.Should().HaveCount(4).And.OnlyContain(id => id == dest.Id);
    }

    [Fact]
    public async Task Returns_Failure_When_BasicReturn_Fires_For_All_4Dml_Tx()
    {
        var (handler, factory, pool) = Build();
        using var _ = factory;
        var dest = await SeedDestinationAsync(factory);

        // Em cada BasicPublish o broker simulado responde via BasicReturn (mandatory=true,
        // routing key sem binding) — handler deve traduzir isso em ReactionResult.Success=false.
        IModel? captured = null;
        var mocks = SetupMocks(onPublish: model => captured = model);
        pool.NextConnection = mocks.Connection;

        // Configura o publish pra disparar BasicReturn nele mesmo. Precisa rodar APÓS
        // SetupMocks pra sobrescrever o callback simples por um que dispara o evento.
        mocks.Model.When(m => m.BasicPublish(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<IBasicProperties>(), Arg.Any<ReadOnlyMemory<byte>>()))
            .Do(callInfo =>
            {
                var ex = (string)callInfo.Args()[0];
                var rk = (string)callInfo.Args()[1];
                var body = (ReadOnlyMemory<byte>)callInfo.Args()[4];
                mocks.Publishes.Add((ex, rk, body.ToArray()));
                mocks.Model.BasicReturn += Raise.EventWith(mocks.Model, new BasicReturnEventArgs
                {
                    ReplyCode = 312,
                    ReplyText = "NO_ROUTE",
                    Exchange = ex,
                    RoutingKey = rk
                });
            });

        var cfg = MakeConfig(dest.Id, routingKey: "no.binding", exchange: "events");
        var contexts = ReactionScenarios.SameTransaction(Guid.NewGuid(), 1, cfg);

        var results = new List<ReactionResult>();
        foreach (var ctx in contexts) results.Add(await handler.ExecuteAsync(ctx));

        results.Should().HaveCount(4);
        results.Should().OnlyContain(r =>
            !r.Success && r.Error!.Contains("Mensagem retornada pelo broker") && r.Error.Contains("NO_ROUTE"));
        // Mesmo com retorno do broker, o WaitForConfirmsOrDie é chamado: BasicReturn
        // chega antes do ack, mas o publisher só consulta returnReason depois.
        mocks.Model.Received(4).WaitForConfirmsOrDie(Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task All_4Dml_Fail_When_DestinationId_Missing()
    {
        var (handler, factory, pool) = Build();
        using var _ = factory;

        var cfg = JsonDocument.Parse("""{ "routing_key": "x" }""").RootElement.Clone();
        var contexts = ReactionScenarios.SameTransaction(Guid.NewGuid(), 1, cfg);

        var results = new List<ReactionResult>();
        foreach (var ctx in contexts) results.Add(await handler.ExecuteAsync(ctx));

        results.Should().HaveCount(4);
        results.Should().OnlyContain(r => !r.Success && r.Error!.Contains("destination_id"));
        pool.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task All_4Dml_Fail_SeparatedInTime_When_Destination_NotFound()
    {
        var (handler, factory, pool) = Build();
        using var _ = factory;

        var unknown = Guid.NewGuid();
        var cfg = MakeConfig(unknown, routingKey: "x", exchange: "events");
        var contexts = ReactionScenarios.SeparatedInTime(Guid.NewGuid(), 1, cfg);

        var results = new List<ReactionResult>();
        foreach (var ctx in contexts)
        {
            results.Add(await handler.ExecuteAsync(ctx));
            await Task.Delay(20);
        }

        results.Should().HaveCount(4);
        results.Should().OnlyContain(r =>
            !r.Success && r.Error!.Contains(unknown.ToString()) && r.Error.Contains("não encontrado"));
        pool.Calls.Should().BeEmpty();
    }
}
