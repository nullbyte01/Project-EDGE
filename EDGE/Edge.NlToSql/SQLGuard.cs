using System.Text.RegularExpressions;

namespace Edge.NlToSql;

public enum SqlRejection
{
    None,
    Empty,
    NotReadOnly,
    StackedStatements,
    MutatingKeyword
}

public sealed record SqlCheck(bool Allowed, SqlRejection Reason, string Detail)
{
    public static SqlCheck Ok() => new(true, SqlRejection.None, "allowed");
    public static SqlCheck Deny(SqlRejection reason, string detail) => new(false, reason, detail);
}

/// <summary>
/// The read-only guarantee, enforced in C#.
///
/// Deliberately NOT enforced by the prompt. "Please only write SELECT
/// statements" is a request; a small model will comply almost always, and
/// almost always is not a security property. Everything here is a pure
/// function over a string — no database, no model, no I/O — which is exactly
/// what makes it cheap to test exhaustively.
/// </summary>
public static partial class SqlGuard
{
    /// <summary>
    /// Cleans up what the model actually returned. It is instructed to emit raw
    /// SQL with no fences and no trailing semicolon, and it will disregard that
    /// some of the time. Stripping in code beats escalating the prompt.
    /// </summary>
    public static string Sanitise(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var text = raw.Replace("```sql", "", StringComparison.OrdinalIgnoreCase)
                      .Replace("```", "")
                      .Trim();

        // Take everything from the first SELECT or WITH, discarding any preamble.
        var select = text.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase);
        var with = text.IndexOf("WITH", StringComparison.OrdinalIgnoreCase);

        var start = (select, with) switch
        {
            ( < 0, < 0) => -1,
            ( < 0, _) => with,
            (_, < 0) => select,
            _ => Math.Min(select, with)
        };

        if (start >= 0) text = text[start..];

        return text.Trim().TrimEnd(';').TrimEnd();
    }

    public static SqlCheck Check(string? sql)
    {
        var text = (sql ?? string.Empty).Trim();

        if (text.Length == 0)
            return SqlCheck.Deny(SqlRejection.Empty, "no statement");

        var lower = text.ToLowerInvariant();

        if (!lower.StartsWith("select") && !lower.StartsWith("with"))
            return SqlCheck.Deny(SqlRejection.NotReadOnly,
                "only SELECT and WITH statements are permitted");

        // A trailing semicolon is stripped by Sanitise; anything left is a
        // second statement smuggled in behind the first.
        if (text.TrimEnd(';').Contains(';'))
            return SqlCheck.Deny(SqlRejection.StackedStatements,
                "multiple statements are not permitted");

        var mutating = MutatingKeyword().Match(text);
        if (mutating.Success)
            return SqlCheck.Deny(SqlRejection.MutatingKeyword,
                $"mutating keyword '{mutating.Value}' detected");

        return SqlCheck.Ok();
    }

    /// <summary>
    /// Word-boundary matching, so 'created' does not trip 'create' and
    /// 'updated_at' does not trip 'update'.
    ///
    /// Known limitation, accepted deliberately: a string literal containing one
    /// of these words — WHERE note = 'please delete' — is rejected too. This
    /// guard fails CLOSED. A false rejection is a mild annoyance; a false
    /// acceptance drops a table.
    /// </summary>
    [GeneratedRegex(
        @"\b(insert|update|delete|drop|alter|attach|detach|pragma|create|replace|vacuum|reindex|truncate)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex MutatingKeyword();
}