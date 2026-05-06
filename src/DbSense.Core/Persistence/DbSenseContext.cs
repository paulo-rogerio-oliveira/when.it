using DbSense.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace DbSense.Core.Persistence;

public class DbSenseContext : DbContext
{
    public const string Schema = "dbsense";

    public DbSenseContext(DbContextOptions<DbSenseContext> options) : base(options) { }

    public DbSet<Connection> Connections => Set<Connection>();
    public DbSet<RabbitMqDestination> RabbitMqDestinations => Set<RabbitMqDestination>();
    public DbSet<Recording> Recordings => Set<Recording>();
    public DbSet<RecordingEvent> RecordingEvents => Set<RecordingEvent>();
    public DbSet<Rule> Rules => Set<Rule>();
    public DbSet<EventLog> EventsLog => Set<EventLog>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<WorkerCommand> WorkerCommands => Set<WorkerCommand>();
    public DbSet<SetupInfo> SetupInfo => Set<SetupInfo>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<Connection>(e =>
        {
            e.ToTable("connections");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Server).HasMaxLength(500).IsRequired();
            e.Property(x => x.Database).HasMaxLength(200).IsRequired();
            e.Property(x => x.AuthType).HasMaxLength(20).IsRequired();
            e.Property(x => x.Username).HasMaxLength(200);
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
        });

        b.Entity<RabbitMqDestination>(e =>
        {
            e.ToTable("rabbitmq_destinations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Host).HasMaxLength(500).IsRequired();
            e.Property(x => x.VirtualHost).HasMaxLength(200).IsRequired();
            e.Property(x => x.Username).HasMaxLength(200).IsRequired();
            e.Property(x => x.DefaultExchange).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
        });

        b.Entity<Recording>(e =>
        {
            e.ToTable("recordings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.FilterHostName).HasMaxLength(200);
            e.Property(x => x.FilterAppName).HasMaxLength(200);
            e.Property(x => x.FilterLoginName).HasMaxLength(200);
            e.HasOne(x => x.Connection).WithMany().HasForeignKey(x => x.ConnectionId);
        });

        b.Entity<RecordingEvent>(e =>
        {
            e.ToTable("recording_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.EventType).HasMaxLength(50).IsRequired();
            e.Property(x => x.DatabaseName).HasMaxLength(200).IsRequired();
            e.Property(x => x.ObjectName).HasMaxLength(500);
            e.Property(x => x.AppName).HasMaxLength(200);
            e.Property(x => x.HostName).HasMaxLength(200);
            e.Property(x => x.LoginName).HasMaxLength(200);
            e.Property(x => x.ParsedPayload).HasColumnType("nvarchar(max)");
            e.HasOne(x => x.Recording).WithMany().HasForeignKey(x => x.RecordingId);
            e.HasIndex(x => new { x.RecordingId, x.EventTimestamp });
            e.HasIndex(x => new { x.RecordingId, x.TransactionId });
        });

        b.Entity<Rule>(e =>
        {
            e.ToTable("rules");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.Definition).IsRequired();
            e.Property(x => x.DestinationId).IsRequired(false);
            e.Property(x => x.SourceRecordingId).IsRequired(false);
            e.Property(x => x.Description).IsRequired(false);
            e.Property(x => x.ActivatedAt).IsRequired(false);
        });

        b.Entity<EventLog>(e =>
        {
            e.ToTable("events_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
            e.Property(x => x.PublishStatus).HasMaxLength(20).IsRequired();
            e.HasIndex(x => new { x.RuleId, x.MatchedAt });
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox");
            e.HasKey(x => x.Id);
            e.Property(x => x.Payload).IsRequired();
            e.Property(x => x.ReactionType).HasMaxLength(20).IsRequired();
            e.Property(x => x.ReactionConfig).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.LockedBy).HasMaxLength(100);
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasIndex(x => new { x.LockedBy, x.LockedUntil });
        });

        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Username).HasMaxLength(100).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(500).IsRequired();
            e.Property(x => x.Role).HasMaxLength(20).IsRequired();
            e.HasIndex(x => x.Username).IsUnique();
        });

        b.Entity<AuditLogEntry>(e =>
        {
            e.ToTable("audit_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(50).IsRequired();
            e.Property(x => x.EntityId).HasMaxLength(100).IsRequired();
        });

        b.Entity<WorkerCommand>(e =>
        {
            e.ToTable("worker_commands");
            e.HasKey(x => x.Id);
            e.Property(x => x.Command).HasMaxLength(50).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.HasIndex(x => new { x.Status, x.IssuedAt });
        });

        b.Entity<SetupInfo>(e =>
        {
            e.ToTable("setup_info");
            e.HasKey(x => x.Id);
            e.Property(x => x.SchemaVersion).HasMaxLength(50).IsRequired();
        });
    }
}
