using Microsoft.EntityFrameworkCore;

namespace DbSense.Core.Persistence;

// Garante que dbsense.rabbitmq_destinations tem as colunas que o modelo EF atual espera.
// EnsureCreatedAsync no provisioning só cria a tabela na primeira vez — quando colunas
// são adicionadas depois, instalações pré-existentes ficam com erro 207 ("Invalid column
// name") na primeira query do EF. Este migrator faz ALTER TABLE idempotente no startup.
//
// Removível quando migrações versionadas (FluentMigrator/EFCore Migrations) forem adotadas.
public static class RabbitDestinationsSchemaMigrator
{
    public static async Task EnsureUpToDateAsync(DbSenseContext ctx, CancellationToken ct = default)
    {
        if (!await ctx.Database.CanConnectAsync(ct)) return;

        // OBJECT_ID retorna NULL se a tabela ainda não existe — nesse caso EnsureCreatedAsync
        // que vai criar (com todas as colunas atuais), nada a fazer aqui.
        const string sql = """
            IF OBJECT_ID('dbsense.rabbitmq_destinations', 'U') IS NULL
                RETURN;

            IF COL_LENGTH('dbsense.rabbitmq_destinations', 'LastTestedAt') IS NULL
                ALTER TABLE dbsense.rabbitmq_destinations ADD LastTestedAt datetime2 NULL;

            IF COL_LENGTH('dbsense.rabbitmq_destinations', 'LastError') IS NULL
                ALTER TABLE dbsense.rabbitmq_destinations ADD LastError nvarchar(max) NULL;
            """;

        await ctx.Database.ExecuteSqlRawAsync(sql, ct);
    }
}
