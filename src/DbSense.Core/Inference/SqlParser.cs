using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbSense.Core.Inference;

public enum DmlOperation { Insert, Update, Delete }

public record ParsedPredicate(string Column, string Operator, string ValueLiteral);

public record ParsedDml(
    DmlOperation Operation,
    string? Schema,
    string Table,
    IReadOnlyList<string> Columns,                       // colunas afetadas (UPDATE SET / INSERT INTO ... ())
    IReadOnlyList<ParsedPredicate> Where,                // predicados extraídos do WHERE (igualdade simples)
    IReadOnlyDictionary<string, string?> Values);        // coluna → valor literal resolvido (NULL pra NULL real;
                                                         // ausente quando o valor não é extraível, e.g. expressão)

public static class SqlParser
{
    private static readonly IReadOnlyDictionary<string, string?> EmptyParams =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Extrai o primeiro statement DML reconhecido (INSERT/UPDATE/DELETE) do batch SQL.
    /// Retorna null para non-DML, SQL inválido ou DML cujo formato fugiu do reconhecível.
    /// </summary>
    public static ParsedDml? TryParse(string sqlText) => TryParseAll(sqlText).FirstOrDefault();

    /// <summary>
    /// Retorna todos os INSERT/UPDATE/DELETE encontrados no batch (em ordem).
    /// Ignora SETs/SELECTs/DECLAREs/etc. Desempacota sp_executesql resolvendo @pN com os
    /// valores passados na própria chamada — assim INSERT (...) VALUES (@p0, @p1) vira
    /// INSERT com Values populados.
    /// </summary>
    public static IReadOnlyList<ParsedDml> TryParseAll(string sqlText)
    {
        if (string.IsNullOrWhiteSpace(sqlText)) return Array.Empty<ParsedDml>();

        var unwrapped = TryUnwrapSpExecuteSql(sqlText);
        if (unwrapped is not null)
        {
            var inner = ParseAllFromScript(unwrapped.Value.Sql, unwrapped.Value.Params);
            if (inner.Count > 0) return inner;
        }

        return ParseAllFromScript(sqlText, EmptyParams);
    }

    private static IReadOnlyList<ParsedDml> ParseAllFromScript(
        string sqlText, IReadOnlyDictionary<string, string?> paramMap)
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
                    UpdateStatement u => ParseUpdate(u, paramMap),
                    InsertStatement i => ParseInsert(i, paramMap),
                    DeleteStatement d => ParseDelete(d, paramMap),
                    _ => null
                };
                if (parsed is not null) results.Add(parsed);
            }
        }
        return results;
    }

    // Desempacota sp_executesql retornando o SQL embutido E o mapa de parâmetros nomeados
    // pros valores fornecidos. Formato típico do EF:
    //   EXEC sp_executesql N'INSERT ... VALUES (@p0, @p1)',
    //                      N'@p0 nvarchar(20), @p1 nvarchar(200)',
    //                      @p0 = N'val0', @p1 = N'val1'
    // Os params 0 e 1 são SQL e declarações de tipo (descartados). Do índice 2 em diante
    // vêm os args nomeados que populam o mapa.
    private static (string Sql, IReadOnlyDictionary<string, string?> Params)? TryUnwrapSpExecuteSql(string sqlText)
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

                var parameters = proc.Parameters;
                if (parameters is null || parameters.Count == 0) continue;

                if (parameters[0].ParameterValue is not StringLiteral lit || lit.Value is null) continue;

                var paramMap = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                // parameters[1] = string com declarações ('@p0 nvarchar(20), @p1 ...'); ignoramos
                for (int i = 2; i < parameters.Count; i++)
                {
                    var p = parameters[i];
                    var varName = p.Variable?.Name;
                    if (string.IsNullOrEmpty(varName)) continue;
                    paramMap[varName] = ExtractScalarValue(p.ParameterValue, EmptyParams);
                }
                return (lit.Value, paramMap);
            }
        }
        return null;
    }

    private static ParsedDml? ParseUpdate(UpdateStatement stmt, IReadOnlyDictionary<string, string?> paramMap)
    {
        var spec = stmt.UpdateSpecification;
        if (spec?.Target is not NamedTableReference tref) return null;

        var (schema, table) = ExtractTableName(tref);
        if (table is null) return null;

        var columns = new List<string>();
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var setClause in spec.SetClauses.OfType<AssignmentSetClause>())
        {
            var col = setClause.Column?.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value;
            if (string.IsNullOrEmpty(col)) continue;
            columns.Add(col);
            var resolved = ExtractScalarValue(setClause.NewValue, paramMap);
            if (resolved is not null || setClause.NewValue is NullLiteral)
                values[col] = resolved;
        }

        // WHERE também pode usar @p — passa o paramMap pra resolução.
        var where = spec.WhereClause is not null ? ExtractEqualities(spec.WhereClause.SearchCondition, paramMap) : new();
        return new ParsedDml(DmlOperation.Update, schema, table, columns, where, values);
    }

    private static ParsedDml? ParseInsert(InsertStatement stmt, IReadOnlyDictionary<string, string?> paramMap)
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

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        // Suporta INSERT INTO t (cols) VALUES (...): pegamos a primeira linha (multi-row INSERT
        // não é o cenário típico de DML do EF).
        if (spec.InsertSource is ValuesInsertSource vis && vis.RowValues.Count > 0)
        {
            var row = vis.RowValues[0];
            for (int i = 0; i < columns.Count && i < row.ColumnValues.Count; i++)
            {
                var resolved = ExtractScalarValue(row.ColumnValues[i], paramMap);
                if (resolved is not null || row.ColumnValues[i] is NullLiteral)
                    values[columns[i]] = resolved;
            }
        }

        return new ParsedDml(DmlOperation.Insert, schema, table, columns, new List<ParsedPredicate>(), values);
    }

    private static ParsedDml? ParseDelete(DeleteStatement stmt, IReadOnlyDictionary<string, string?> paramMap)
    {
        var spec = stmt.DeleteSpecification;
        if (spec?.Target is not NamedTableReference tref) return null;

        var (schema, table) = ExtractTableName(tref);
        if (table is null) return null;

        var where = spec.WhereClause is not null ? ExtractEqualities(spec.WhereClause.SearchCondition, paramMap) : new();
        return new ParsedDml(DmlOperation.Delete, schema, table, new List<string>(),
            where, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
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

    private static List<ParsedPredicate> ExtractEqualities(
        BooleanExpression expr, IReadOnlyDictionary<string, string?> paramMap)
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

                    var match = ExtractColumnValue(cmp.FirstExpression, cmp.SecondExpression, paramMap)
                        ?? ExtractColumnValue(cmp.SecondExpression, cmp.FirstExpression, paramMap);
                    if (match is null) break;
                    var op = cmp.ComparisonType == BooleanComparisonType.Equals ? "eq" : "ne";
                    list.Add(new ParsedPredicate(match.Value.Column, op, match.Value.Value));
                    break;
            }
        }
    }

    private static (string Column, string Value)? ExtractColumnValue(
        ScalarExpression a, ScalarExpression b, IReadOnlyDictionary<string, string?> paramMap)
    {
        if (a is not ColumnReferenceExpression cref) return null;
        var col = cref.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value;
        if (string.IsNullOrEmpty(col)) return null;

        var value = ExtractScalarValue(b, paramMap);
        if (value is null) return null;
        return (col, value);
    }

    // Resolve uma expressão escalar pro seu valor literal (string), aplicando o mapa
    // de parâmetros do sp_executesql quando a expressão for uma referência @pN.
    // Retorna null pra NULL real e pra expressões não suportadas (binárias, function calls).
    private static string? ExtractScalarValue(
        ScalarExpression? expr, IReadOnlyDictionary<string, string?> paramMap)
    {
        if (expr is null) return null;
        switch (expr)
        {
            case NullLiteral:
                return null;
            case Literal l:
                return l.Value;
            case UnaryExpression u
                when u.UnaryExpressionType == UnaryExpressionType.Negative
                  && u.Expression is Literal nl:
                return "-" + nl.Value;
            case VariableReference v:
                return paramMap.TryGetValue(v.Name, out var pv) ? pv : null;
            default:
                return null;
        }
    }
}
