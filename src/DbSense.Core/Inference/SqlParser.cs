using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbSense.Core.Inference;

public enum DmlOperation { Insert, Update, Delete }

public record ParsedPredicate(string Column, string Operator, string ValueLiteral);

public record ParsedDml(
    DmlOperation Operation,
    string? Schema,
    string Table,
    IReadOnlyList<string> Columns,           // colunas afetadas (UPDATE SET / INSERT INTO ... ())
    IReadOnlyList<ParsedPredicate> Where);   // predicados extraídos do WHERE (igualdade simples)

public static class SqlParser
{
    /// <summary>
    /// Extrai o primeiro statement DML reconhecido (INSERT/UPDATE/DELETE) do batch SQL.
    /// Retorna null para non-DML, SQL inválido ou DML cujo formato fugiu do reconhecível.
    /// </summary>
    /// <summary>Compatibilidade: retorna o primeiro DML do batch.</summary>
    public static ParsedDml? TryParse(string sqlText) => TryParseAll(sqlText).FirstOrDefault();

    /// <summary>
    /// Retorna todos os INSERT/UPDATE/DELETE encontrados no batch (em ordem).
    /// Ignora SETs/SELECTs/DECLAREs/etc. Desempacota sp_executesql.
    /// </summary>
    public static IReadOnlyList<ParsedDml> TryParseAll(string sqlText)
    {
        if (string.IsNullOrWhiteSpace(sqlText)) return Array.Empty<ParsedDml>();

        var unwrapped = TryUnwrapSpExecuteSql(sqlText);
        if (unwrapped is not null)
        {
            var inner = ParseAllFromScript(unwrapped);
            if (inner.Count > 0) return inner;
        }

        return ParseAllFromScript(sqlText);
    }

    private static IReadOnlyList<ParsedDml> ParseAllFromScript(string sqlText)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sqlText);
        var tree = parser.Parse(reader, out IList<ParseError> errors);
        if (errors is { Count: > 0 } || tree is not TSqlScript script) return Array.Empty<ParsedDml>();

        var results = new List<ParsedDml>();
        foreach (var batch in script.Batches)
        {
            foreach (var stmt in batch.Statements)
            {
                var parsed = stmt switch
                {
                    UpdateStatement u => ParseUpdate(u),
                    InsertStatement i => ParseInsert(i),
                    DeleteStatement d => ParseDelete(d),
                    _ => null
                };
                if (parsed is not null) results.Add(parsed);
            }
        }
        return results;
    }

    private static string? TryUnwrapSpExecuteSql(string sqlText)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sqlText);
        var tree = parser.Parse(reader, out IList<ParseError> errors);
        if (errors is { Count: > 0 } || tree is not TSqlScript script) return null;

        foreach (var batch in script.Batches)
        {
            foreach (var stmt in batch.Statements)
            {
                if (stmt is not ExecuteStatement exec) continue;
                if (exec.ExecuteSpecification?.ExecutableEntity is not ExecutableProcedureReference proc) continue;

                var name = proc.ProcedureReference?.ProcedureReference?.Name?
                    .BaseIdentifier?.Value;
                if (!string.Equals(name, "sp_executesql", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Primeiro parâmetro é o SQL embutido (string literal).
                var firstParam = proc.Parameters?.FirstOrDefault();
                if (firstParam?.ParameterValue is StringLiteral lit && lit.Value is not null)
                    return lit.Value;
            }
        }
        return null;
    }

    private static ParsedDml? ParseUpdate(UpdateStatement stmt)
    {
        var spec = stmt.UpdateSpecification;
        if (spec?.Target is not NamedTableReference tref) return null;

        var (schema, table) = ExtractTableName(tref);
        if (table is null) return null;

        var columns = spec.SetClauses
            .OfType<AssignmentSetClause>()
            .Select(c => c.Column?.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value)
            .Where(c => !string.IsNullOrEmpty(c))
            .Cast<string>()
            .ToList();

        var where = spec.WhereClause is not null ? ExtractEqualities(spec.WhereClause.SearchCondition) : new();
        return new ParsedDml(DmlOperation.Update, schema, table, columns, where);
    }

    private static ParsedDml? ParseInsert(InsertStatement stmt)
    {
        var spec = stmt.InsertSpecification;
        if (spec?.Target is not NamedTableReference tref) return null;

        var (schema, table) = ExtractTableName(tref);
        if (table is null) return null;

        var columns = spec.Columns?
            .Select(c => c.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value)
            .Where(c => !string.IsNullOrEmpty(c))
            .Cast<string>()
            .ToList() ?? new();

        return new ParsedDml(DmlOperation.Insert, schema, table, columns, new List<ParsedPredicate>());
    }

    private static ParsedDml? ParseDelete(DeleteStatement stmt)
    {
        var spec = stmt.DeleteSpecification;
        if (spec?.Target is not NamedTableReference tref) return null;

        var (schema, table) = ExtractTableName(tref);
        if (table is null) return null;

        var where = spec.WhereClause is not null ? ExtractEqualities(spec.WhereClause.SearchCondition) : new();
        return new ParsedDml(DmlOperation.Delete, schema, table, new List<string>(), where);
    }

    private static (string? Schema, string? Table) ExtractTableName(NamedTableReference tref)
    {
        var ids = tref.SchemaObject?.Identifiers;
        if (ids is null || ids.Count == 0) return (null, null);
        return ids.Count switch
        {
            1 => (null, ids[0].Value),
            2 => (ids[0].Value, ids[1].Value),
            _ => (ids[^2].Value, ids[^1].Value)  // db.schema.table → schema, table
        };
    }

    private static List<ParsedPredicate> ExtractEqualities(BooleanExpression expr)
    {
        var list = new List<ParsedPredicate>();
        Walk(expr);
        return list;

        void Walk(BooleanExpression e)
        {
            switch (e)
            {
                case BooleanBinaryExpression bin when bin.BinaryExpressionType == BooleanBinaryExpressionType.And:
                    Walk(bin.FirstExpression);
                    Walk(bin.SecondExpression);
                    break;

                case BooleanComparisonExpression cmp:
                    if (cmp.ComparisonType is not (BooleanComparisonType.Equals
                        or BooleanComparisonType.NotEqualToBrackets
                        or BooleanComparisonType.NotEqualToExclamation)) break;

                    var match = ExtractColumnLiteral(cmp.FirstExpression, cmp.SecondExpression)
                        ?? ExtractColumnLiteral(cmp.SecondExpression, cmp.FirstExpression);
                    if (match is null) break;
                    var op = cmp.ComparisonType == BooleanComparisonType.Equals ? "eq" : "ne";
                    list.Add(new ParsedPredicate(match.Value.Column, op, match.Value.Value));
                    break;
            }
        }
    }

    private static (string Column, string Value)? ExtractColumnLiteral(
        ScalarExpression a, ScalarExpression b)
    {
        if (a is ColumnReferenceExpression cref && b is Literal lit)
        {
            var col = cref.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value;
            if (string.IsNullOrEmpty(col) || lit.Value is null) return null;
            return (col, lit.Value);
        }
        return null;
    }
}
