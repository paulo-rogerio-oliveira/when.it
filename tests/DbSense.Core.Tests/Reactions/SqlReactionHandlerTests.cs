using System.Text.Json;
using DbSense.Core.Reactions;
using DbSense.Core.Security;
using DbSense.Core.Tests.Helpers;
using FluentAssertions;
using NSubstitute;

namespace DbSense.Core.Tests.Reactions;

// Cobre o comportamento determinístico do handler para um lote de 4 DMLs (em uma
// transação OU espalhadas no tempo) sem depender de um SQL Server real. Casos que
// exigem execução do comando contra o banco (afetar linhas, SP) ficam para testes
// de integração.
public class SqlReactionHandlerTests
{
    private static (SqlReactionHandler handler, TestDbContextFactory factory) Build()
    {
        var factory = new TestDbContextFactory();
        var cipher = Substitute.For<ISecretCipher>();
        cipher.Decrypt(Arg.Any<byte[]>()).Returns("password");
        var handler = new SqlReactionHandler(factory, cipher);
        return (handler, factory);
    }

    private static JsonElement Config(string? connectionId, string? sql)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("{");
        if (connectionId is not null)
            sb.Append($"\"connection_id\": \"{connectionId}\"");
        if (connectionId is not null && sql is not null) sb.Append(',');
        if (sql is not null)
            sb.Append($"\"sql\": {JsonSerializer.Serialize(sql)}");
        sb.Append("}");
        return JsonDocument.Parse(sb.ToString()).RootElement.Clone();
    }

    [Fact]
    public async Task All_4Dml_InSameTransaction_Fail_With_MissingConnectionId()
    {
        var (handler, factory) = Build();
        using var _ = factory;
        var cfg = Config(connectionId: null, sql: "DELETE FROM x");
        var contexts = ReactionScenarios.SameTransaction(Guid.NewGuid(), 1, cfg);

        var results = new List<ReactionResult>();
        foreach (var ctx in contexts) results.Add(await handler.ExecuteAsync(ctx));

        results.Should().HaveCount(4);
        results.Should().OnlyContain(r => !r.Success && r.Error!.Contains("connection_id"));
    }

    [Fact]
    public async Task All_4Dml_SeparatedInTime_Fail_With_MissingConnectionId()
    {
        var (handler, factory) = Build();
        using var _ = factory;
        var cfg = Config(connectionId: null, sql: "DELETE FROM x");
        var contexts = ReactionScenarios.SeparatedInTime(Guid.NewGuid(), 1, cfg);

        var results = new List<ReactionResult>();
        foreach (var ctx in contexts)
        {
            results.Add(await handler.ExecuteAsync(ctx));
            await Task.Delay(20);
        }

        results.Should().HaveCount(4);
        results.Should().OnlyContain(r => !r.Success && r.Error!.Contains("connection_id"));
    }

    [Fact]
    public async Task All_4Dml_InSameTransaction_Fail_With_MissingSql()
    {
        var (handler, factory) = Build();
        using var _ = factory;
        var cfg = Config(connectionId: Guid.NewGuid().ToString(), sql: null);
        var contexts = ReactionScenarios.SameTransaction(Guid.NewGuid(), 1, cfg);

        var results = new List<ReactionResult>();
        foreach (var ctx in contexts) results.Add(await handler.ExecuteAsync(ctx));

        results.Should().HaveCount(4);
        results.Should().OnlyContain(r => !r.Success && r.Error!.Contains("sql"));
    }

    [Fact]
    public async Task All_4Dml_InSameTransaction_Fail_When_Connection_Not_Found()
    {
        var (handler, factory) = Build();
        using var _ = factory;
        var unknownId = Guid.NewGuid();
        var cfg = Config(unknownId.ToString(), "SELECT 1");
        var contexts = ReactionScenarios.SameTransaction(Guid.NewGuid(), 1, cfg);

        var results = new List<ReactionResult>();
        foreach (var ctx in contexts) results.Add(await handler.ExecuteAsync(ctx));

        results.Should().HaveCount(4);
        results.Should().OnlyContain(r =>
            !r.Success && r.Error!.Contains(unknownId.ToString()) && r.Error.Contains("não encontrada"));
    }

    [Fact]
    public async Task All_4Dml_SeparatedInTime_Fail_When_Connection_Not_Found()
    {
        var (handler, factory) = Build();
        using var _ = factory;
        var unknownId = Guid.NewGuid();
        var cfg = Config(unknownId.ToString(), "SELECT 1");
        var contexts = ReactionScenarios.SeparatedInTime(Guid.NewGuid(), 1, cfg);

        var results = new List<ReactionResult>();
        foreach (var ctx in contexts)
        {
            results.Add(await handler.ExecuteAsync(ctx));
            await Task.Delay(20);
        }

        results.Should().HaveCount(4);
        results.Should().OnlyContain(r => !r.Success && r.Error!.Contains("não encontrada"));
    }
}
