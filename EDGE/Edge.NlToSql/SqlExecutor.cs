using System.Text;
using Microsoft.Data.Sqlite;

namespace Edge.NlToSql;

/// <summary>
/// Validates and runs SQL against the live connection.
///
/// The validation trick worth knowing: <c>EXPLAIN &lt;sql&gt;</c> makes SQLite
/// parse and plan the statement without executing it. Syntax errors and
/// references to non-existent tables or columns all throw here, cheaply, before
/// anything touches data. It will not catch runtime issues like a type
/// mismatch — but those are not the failures a model produces.
/// </summary>
public sealed class SqlExecutor(SqliteConnection connection)
{
    public const int DefaultMaxRows = 50;

    public bool TryValidate(string sql, out string error)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"EXPLAIN {sql}";
            using var reader = cmd.ExecuteReader();   // plans, does not execute
            error = string.Empty;
            return true;
        }
        catch (SqliteException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Runs the statement. Re-checks the guard rather than trusting the caller:
    /// this is the last line before the database, so it does not assume an
    /// earlier check happened.
    /// </summary>
    public QueryResult Run(string sql, int maxRows = DefaultMaxRows)
    {
        var check = SqlGuard.Check(sql);
        if (!check.Allowed)
            throw new InvalidOperationException($"Blocked by guard: {check.Detail}");

        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();

        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(reader.GetName)
            .ToArray();

        var rows = new List<string[]>();
        var truncated = false;

        while (reader.Read())
        {
            if (rows.Count == maxRows) { truncated = true; break; }

            rows.Add(Enumerable.Range(0, reader.FieldCount)
                .Select(i => reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToString() ?? "")
                .ToArray());
        }

        return new QueryResult(columns, rows, truncated);
    }
}

public sealed record QueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<string[]> Rows,
    bool Truncated)
{
    /// <summary>Fixed-width ASCII table. Column widths sized to the widest cell.</summary>
    public string ToAsciiTable()
    {
        if (Rows.Count == 0) return "(no rows)";

        var widths = Columns
            .Select((c, i) => Math.Max(c.Length, Rows.Max(r => r[i].Length)))
            .ToArray();

        var sb = new StringBuilder();

        sb.AppendLine(string.Join("  ", Columns.Select((c, i) => c.PadRight(widths[i]))));
        sb.AppendLine(string.Join("  ", widths.Select(w => new string('-', w))));

        foreach (var row in Rows)
            sb.AppendLine(string.Join("  ", row.Select((v, i) => v.PadRight(widths[i]))));

        if (Truncated) sb.AppendLine($"... truncated");

        return sb.ToString().TrimEnd();
    }
}