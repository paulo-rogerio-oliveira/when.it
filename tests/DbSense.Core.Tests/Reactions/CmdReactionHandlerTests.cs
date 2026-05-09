using System.Text.Json;
using DbSense.Core.Reactions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbSense.Core.Tests.Reactions;

// Cobre o comportamento do handler quando recebe múltiplas DMLs:
//   * agrupadas em uma única transação (4 eventos com mesmo txId/timestamp)
//   * espalhadas em uma janela de tempo (4 eventos com timestamps distintos)
// Em ambos os casos cada evento entra como um ReactionContext separado, exatamente como
// o ReactionExecutorWorker processaria. As asserções verificam que o resultado é
// consistente para todos os eventos do lote.
public class CmdReactionHandlerTests
{
    private static CmdReactionHandler Build() =>
        new(NullLogger<CmdReactionHandler>.Instance);

    private static (string Exe, string[] Args) Shell(int exitCode) =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", $"exit {exitCode}" })
            : ("/bin/sh", new[] { "-c", $"exit {exitCode}" });

    private static JsonElement ConfigForExit(int exitCode)
    {
        var (exe, args) = Shell(exitCode);
        var json = $$"""
        {
          "executable": {{JsonSerializer.Serialize(exe)}},
          "args": {{JsonSerializer.Serialize(args)}},
          "send_payload_to_stdin": false,
          "timeout_ms": 5000
        }
        """;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public async Task Executes_All_4Dml_InSameTransaction_With_Success()
    {
        var handler = Build();
        var contexts = ReactionScenarios.SameTransaction(Guid.NewGuid(), 1, ConfigForExit(0));

        var results = new List<ReactionResult>();
        foreach (var ctx in contexts)
            results.Add(await handler.ExecuteAsync(ctx));

        results.Should().HaveCount(4);
        results.Should().OnlyContain(r => r.Success && r.ExitCode == 0);
    }

    [Fact]
    public async Task Executes_All_4Dml_SeparatedInTime_With_Success()
    {
        var handler = Build();
        var contexts = ReactionScenarios.SeparatedInTime(Guid.NewGuid(), 1, ConfigForExit(0));

        var results = new List<ReactionResult>();
        foreach (var ctx in contexts)
        {
            results.Add(await handler.ExecuteAsync(ctx));
            // Pequeno gap pra refletir que estes eventos chegam ao executor em
            // momentos distintos (entre 50 ms — não importa o valor, importa que
            // são execuções sequenciais e não numa rajada única).
            await Task.Delay(50);
        }

        results.Should().HaveCount(4);
        results.Should().OnlyContain(r => r.Success && r.ExitCode == 0);
    }

    [Fact]
    public async Task Returns_Failure_For_NonZero_ExitCode_Across_4Dml_Tx()
    {
        var handler = Build();
        var contexts = ReactionScenarios.SameTransaction(Guid.NewGuid(), 1, ConfigForExit(7));

        var results = new List<ReactionResult>();
        foreach (var ctx in contexts)
            results.Add(await handler.ExecuteAsync(ctx));

        results.Should().HaveCount(4);
        results.Should().OnlyContain(r => !r.Success && r.ExitCode == 7);
    }

    [Fact]
    public async Task Returns_Validation_Error_When_Executable_Missing_For_All_4Dml()
    {
        var handler = Build();
        var emptyConfig = JsonDocument.Parse("{}").RootElement.Clone();
        var contexts = ReactionScenarios.SeparatedInTime(Guid.NewGuid(), 1, emptyConfig);

        var results = new List<ReactionResult>();
        foreach (var ctx in contexts)
            results.Add(await handler.ExecuteAsync(ctx));

        results.Should().HaveCount(4);
        results.Should().OnlyContain(r => !r.Success && r.Error!.Contains("executable"));
    }
}
