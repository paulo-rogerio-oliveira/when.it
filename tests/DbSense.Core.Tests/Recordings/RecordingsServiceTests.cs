using DbSense.Core.Domain;
using DbSense.Core.Recordings;
using DbSense.Core.Tests.Helpers;
using FluentAssertions;

namespace DbSense.Core.Tests.Recordings;

public class RecordingsServiceTests
{
    [Fact]
    public async Task Delete_Removes_LinkedRules_Outbox_EventsLog_And_RecordingEvents()
    {
        using var factory = new TestDbContextFactory();
        var recordingId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var linkedRuleId = Guid.NewGuid();
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
            ctx.Recordings.Add(new Recording
            {
                Id = recordingId,
                ConnectionId = connectionId,
                Name = "capture",
                Status = "completed",
                StartedAt = DateTime.UtcNow.AddMinutes(-5),
                StoppedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5)
            });
            ctx.RecordingEvents.Add(new RecordingEvent
            {
                RecordingId = recordingId,
                EventTimestamp = DateTime.UtcNow,
                EventType = "rpc_completed",
                SessionId = 1,
                DatabaseName = "app",
                SqlText = "UPDATE dbo.Empresas SET Nome = 'A'"
            });
            ctx.Rules.AddRange(
                new Rule
                {
                    Id = linkedRuleId,
                    ConnectionId = connectionId,
                    SourceRecordingId = recordingId,
                    Name = "linked",
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
                    Name = "unrelated",
                    Definition = "{}",
                    Status = "draft",
                    Version = 1,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            ctx.EventsLog.AddRange(
                new EventLog
                {
                    Id = 10,
                    RuleId = linkedRuleId,
                    ConnectionId = connectionId,
                    MatchedAt = DateTime.UtcNow,
                    SqlTimestamp = DateTime.UtcNow,
                    EventPayload = "{}",
                    IdempotencyKey = "linked",
                    PublishStatus = "pending"
                },
                new EventLog
                {
                    Id = 11,
                    RuleId = unrelatedRuleId,
                    ConnectionId = connectionId,
                    MatchedAt = DateTime.UtcNow,
                    SqlTimestamp = DateTime.UtcNow,
                    EventPayload = "{}",
                    IdempotencyKey = "unrelated",
                    PublishStatus = "pending"
                });
            ctx.Outbox.AddRange(
                new OutboxMessage
                {
                    Id = 20,
                    EventsLogId = 10,
                    Payload = "{}",
                    ReactionType = "rabbit",
                    ReactionConfig = "{}",
                    Status = "pending",
                    NextAttemptAt = DateTime.UtcNow
                },
                new OutboxMessage
                {
                    Id = 21,
                    EventsLogId = 11,
                    Payload = "{}",
                    ReactionType = "rabbit",
                    ReactionConfig = "{}",
                    Status = "pending",
                    NextAttemptAt = DateTime.UtcNow
                });
            await ctx.SaveChangesAsync();
        }

        var service = new RecordingsService(factory);
        var deleted = await service.DeleteAsync(recordingId);

        deleted.Should().BeTrue();
        await using var assertCtx = factory.CreateDbContext();
        assertCtx.Recordings.Should().BeEmpty();
        assertCtx.RecordingEvents.Should().BeEmpty();
        assertCtx.Rules.Select(r => r.Id).Should().ContainSingle().Which.Should().Be(unrelatedRuleId);
        assertCtx.EventsLog.Select(e => e.Id).Should().ContainSingle().Which.Should().Be(11);
        assertCtx.Outbox.Select(o => o.EventsLogId).Should().ContainSingle().Which.Should().Be(11);
        assertCtx.WorkerCommands.Should().ContainSingle(c => c.Command == "reload_rules");
    }
}
