using Microsoft.EntityFrameworkCore;

namespace DbSense.Core.Persistence;

// Adiciona colunas a recording_events que apareceram depois da v1 do schema.
// Idempotente — usa IF COL_LENGTH IS NULL antes de cada ALTER. Removível quando
// migrações versionadas (FluentMigrator/EFCore Migrations) forem adotadas.
public static class RecordingSchemaMigrator
{
    public static async Task EnsureUpToDateAsync(DbSenseContext ctx, CancellationToken ct = default)
    {
        // Provider InMemory (testes) não suporta ExecuteSqlRaw. Pula no-op.
        if (!ctx.Database.IsRelational()) return;
        if (!await ctx.Database.CanConnectAsync(ct)) return;

        const string sql = """
            IF OBJECT_ID('dbsense.recording_events', 'U') IS NULL RETURN;

            IF COL_LENGTH('dbsense.recording_events', 'ParsedPayload') IS NULL
                ALTER TABLE dbsense.recording_events ADD ParsedPayload nvarchar(max) NULL;
            """;

        await ctx.Database.ExecuteSqlRawAsync(sql, ct);
    }
}
