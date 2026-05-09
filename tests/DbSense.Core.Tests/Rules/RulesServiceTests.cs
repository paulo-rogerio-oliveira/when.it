using DbSense.Core.Domain;
using DbSense.Core.Rules;
using DbSense.Core.Tests.Helpers;
using FluentAssertions;

namespace DbSense.Core.Tests.Rules;

public class RulesServiceTests
{
    [Fact]
    public async Task Delete_Removes_Rule_Outbox_EventsLog_And_Reloads_When_Active()
    {
        using var factory = new TestDbContextFactory();
        var connectionId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();
        var unrelatedRuleId = Guid.NewGuid();

        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Connections.Add(new Connection
            {
                Id = connectionId,
                Name = "target",
                Server = "localhost",
                Database = "app",
                AuthType = "sql",
                Status = "active",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            ctx.Rules.AddRange(
                new Rule
                {
                    Id = ruleId,
                    ConnectionId = connectionId,
                    Name = "to delete",
                    Definition = """{"reaction":{"type":"rabbit"}}""",
                    Status = "active",
                    Version = 1,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                },
                new Rule
                {
                    Id = unrelatedRuleId,
                    ConnectionId = connectionId,
                    Name = "keep",
                    Definition = "{}",
                    Status = "draft",
                    Version = 1,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            ctx.EventsLog.AddRange(
                new EventLog
                {
                    Id = 30,
                    RuleId = ruleId,
                    ConnectionId = connectionId,
                    MatchedAt = DateTime.UtcNow,
                    SqlTimestamp = DateTime.UtcNow,
                    EventPayload = "{}",
                    IdempotencyKey = "delete",
                    PublishStatus = "pending"
                },
                new EventLog
                {
                    Id = 31,
                    RuleId = unrelatedRuleId,
                    ConnectionId = connectionId,
                    MatchedAt = DateTime.UtcNow,
                    SqlTimestamp = DateTime.UtcNow,
                    EventPayload = "{}",
                    IdempotencyKey = "keep",
                    PublishStatus = "pending"
                });
            ctx.Outbox.AddRange(
                new OutboxMessage
                {
                    Id = 40,
                    EventsLogId = 30,
                    Payload = "{}",
                    ReactionType = "rabbit",
                    ReactionConfig = "{}",
                    Status = "pending",
                    NextAttemptAt = DateTime.UtcNow
                },
                new OutboxMessage
                {
                    Id = 41,
                    EventsLogId = 31,
                    Payload = "{}",
                    ReactionType = "rabbit",
                    ReactionConfig = "{}",
                    Status = "pending",
                    NextAttemptAt = DateTime.UtcNow
                });
            await ctx.SaveChangesAsync();
        }

        var service = new RulesService(factory);
        var deleted = await service.DeleteAsync(ruleId);

        deleted.Should().BeTrue();
        await using var assertCtx = factory.CreateDbContext();
        assertCtx.Rules.Select(r => r.Id).Should().ContainSingle().Which.Should().Be(unrelatedRuleId);
        assertCtx.EventsLog.Select(e => e.Id).Should().ContainSingle().Which.Should().Be(31);
        assertCtx.Outbox.Select(o => o.EventsLogId).Should().ContainSingle().Which.Should().Be(31);
        assertCtx.WorkerCommands.Should().ContainSingle(c => c.Command == "reload_rules");
    }
}
