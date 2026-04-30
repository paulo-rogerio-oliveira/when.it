using Microsoft.EntityFrameworkCore;

namespace DbSense.Core.Persistence;

// Migração one-shot do schema de dbsense.outbox para o formato com
// ReactionType/ReactionConfig/LastError. Detecta colunas antigas (Exchange/RoutingKey/Headers)
// e nesse caso faz DROP + CREATE — perde dados em outbox (aceitável em dev; não há FK pra ele).
//
// Removível quando migrações versionadas (FluentMigrator/EFCore Migrations) forem adotadas
// (seção 3.2 da spec).
public static class OutboxSchemaMigrator
{
    public static async Task EnsureUpToDateAsync(DbSenseContext ctx, CancellationToken ct = default)
    {
        if (!await ctx.Database.CanConnectAsync(ct)) return;

        var tableState = await ProbeAsync(ctx, ct);
        switch (tableState)
        {
            case TableState.Missing:
                // EnsureCreated cria. Nada a fazer aqui.
                return;
            case TableState.UpToDate:
                return;
            case TableState.Legacy:
                await DropAndRecreateAsync(ctx, ct);
                return;
        }
    }

    private enum TableState { Missing, Legacy, UpToDate }

    private static async Task<TableState> ProbeAsync(DbSenseContext ctx, CancellationToken ct)
    {
        const string sql = """
            SELECT
                CASE WHEN OBJECT_ID('dbsense.outbox', 'U') IS NULL THEN 0 ELSE 1 END AS Present,
                CASE WHEN COL_LENGTH('dbsense.outbox', 'Exchange') IS NULL THEN 0 ELSE 1 END AS HasExchange,
                CASE WHEN COL_LENGTH('dbsense.outbox', 'ReactionType') IS NULL THEN 0 ELSE 1 END AS HasReactionType;
            """;

        var conn = ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await ctx.Database.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return TableState.Missing;
        var present = reader.GetInt32(0) == 1;
        var hasExchange = reader.GetInt32(1) == 1;
        var hasReactionType = reader.GetInt32(2) == 1;
        if (!present) return TableState.Missing;
        if (hasReactionType && !hasExchange) return TableState.UpToDate;
        return TableState.Legacy;
    }

    private static async Task DropAndRecreateAsync(DbSenseContext ctx, CancellationToken ct)
    {
        const string sql = """
            DROP TABLE dbsense.outbox;

            CREATE TABLE dbsense.outbox (
                Id              bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_outbox PRIMARY KEY,
                EventsLogId     bigint NOT NULL,
                Payload         nvarchar(max) NOT NULL,
                ReactionType    nvarchar(20) NOT NULL,
                ReactionConfig  nvarchar(max) NOT NULL,
                [Status]        nvarchar(20) NOT NULL,
                Attempts        int NOT NULL,
                NextAttemptAt   datetime2 NOT NULL,
                LockedBy        nvarchar(100) NULL,
                LockedUntil     datetime2 NULL,
                LastError       nvarchar(max) NULL
            );

            CREATE INDEX IX_outbox_Status_NextAttemptAt
                ON dbsense.outbox ([Status], NextAttemptAt);

            CREATE INDEX IX_outbox_LockedBy_LockedUntil
                ON dbsense.outbox (LockedBy, LockedUntil);
            """;

        await ctx.Database.ExecuteSqlRawAsync(sql, ct);
    }
}
