using System.Text.Json;
using DbSense.Core.Reactions;

namespace DbSense.Core.Tests.Reactions;

// Helpers compartilhados pelos testes de cada IReactionHandler.
//
// Cada cenário simula a sequência (insert → update → delete) que o RuleEngine produziria
// a partir de um workload real. Os ReactionContexts entregues a um handler são as N
// linhas que sairiam do outbox para essa rule.
//
//  - SameTransaction: mesmas SqlTimestamp + transactionId, IdempotencyKey distinto
//    por linha. Modela 3+ DMLs de uma única transação.
//  - SeparatedInTime: SqlTimestamps distantes uma da outra e transactionId diferente.
//    Modela 3+ DMLs avulsas em uma janela de tempo.
internal static class ReactionScenarios
{
    public static IReadOnlyList<ReactionContext> SameTransaction(
        Guid ruleId, int ruleVersion, JsonElement config, long startEventId = 1000)
    {
        var txTs = new DateTime(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc);
        var txId = "0x000000000001";
        return new[]
        {
            BuildContext(startEventId,     "insert", ruleId, ruleVersion, config, txTs, txId,
                """{ "after": { "id": 1, "amount": 10 }, "before": null, "_meta": { "op": "insert" } }"""),
            BuildContext(startEventId + 1, "update", ruleId, ruleVersion, config, txTs, txId,
                """{ "after": { "id": 1, "amount": 25 }, "before": { "id": 1, "amount": 10 }, "_meta": { "op": "update" } }"""),
            BuildContext(startEventId + 2, "update", ruleId, ruleVersion, config, txTs, txId,
                """{ "after": { "id": 2, "amount": 99 }, "before": { "id": 2, "amount": 0 }, "_meta": { "op": "update" } }"""),
            BuildContext(startEventId + 3, "delete", ruleId, ruleVersion, config, txTs, txId,
                """{ "after": null, "before": { "id": 1, "amount": 25 }, "_meta": { "op": "delete" } }"""),
        };
    }

    public static IReadOnlyList<ReactionContext> SeparatedInTime(
        Guid ruleId, int ruleVersion, JsonElement config, long startEventId = 2000)
    {
        var t0 = new DateTime(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc);
        return new[]
        {
            BuildContext(startEventId,     "insert", ruleId, ruleVersion, config, t0,
                "0x00000000000A",
                """{ "after": { "id": 10 }, "before": null, "_meta": { "op": "insert" } }"""),
            BuildContext(startEventId + 1, "update", ruleId, ruleVersion, config, t0.AddSeconds(45),
                "0x00000000000B",
                """{ "after": { "id": 10, "qty": 2 }, "before": { "id": 10, "qty": 1 }, "_meta": { "op": "update" } }"""),
            BuildContext(startEventId + 2, "insert", ruleId, ruleVersion, config, t0.AddMinutes(2),
                "0x00000000000C",
                """{ "after": { "id": 11 }, "before": null, "_meta": { "op": "insert" } }"""),
            BuildContext(startEventId + 3, "delete", ruleId, ruleVersion, config, t0.AddMinutes(5),
                "0x00000000000D",
                """{ "after": null, "before": { "id": 10, "qty": 2 }, "_meta": { "op": "delete" } }"""),
        };
    }

    private static ReactionContext BuildContext(
        long eventsLogId, string opLabel, Guid ruleId, int ruleVersion, JsonElement config,
        DateTime sqlTs, string txId, string payloadJson)
    {
        // IdempotencyKey real é hash(ruleId + version + suffix); aqui só precisa ser único
        // por evento — o handler não revalida o formato.
        var key = $"{opLabel}-{eventsLogId:D4}-{txId}-{sqlTs.Ticks}";
        return new ReactionContext(eventsLogId, payloadJson, config, key, ruleId, ruleVersion);
    }
}
