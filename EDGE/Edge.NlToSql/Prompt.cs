namespace Edge.NlToSql;

/// <summary>
/// Builds the prompts. Pure string work, kept separate from the engine so it
/// can be tuned without touching control flow.
/// </summary>
public static class Prompt
{
    /// <summary>
    /// Two things here are load-bearing and easy to underestimate:
    ///
    /// 1. **Dialect anchoring.** Small models drift toward T-SQL — TOP instead
    ///    of LIMIT, GETDATE() instead of date(). Naming the dialect once is not
    ///    enough; the rules and the examples both have to reinforce it.
    /// 2. **Few-shot over instruction.** "Return only SQL" gets ignored far more
    ///    often than a pair of worked examples showing exactly that shape. The
    ///    examples ARE the format specification.
    /// </summary>
    public static string Generate(string schemaDdl, string question) => $"""
        You are a SQLite expert. Translate the question into ONE SQLite SELECT statement.

        SCHEMA:
        {schemaDdl}

        RULES:
        - SQLite dialect only. Use LIMIT, never TOP. Use date(), never GETDATE().
        - Reference only tables and columns present in the schema above.
        - Output raw SQL only. No markdown fences, no explanation, no semicolon.

        EXAMPLE
        Q: How many customers are in Pune?
        A: SELECT COUNT(*) FROM customers WHERE city = 'Pune'

        EXAMPLE
        Q: What did each customer spend in total?
        A: SELECT c.name, SUM(oi.qty * oi.unit_price) AS total FROM customers c JOIN orders o ON o.customer_id = c.id JOIN order_items oi ON oi.order_id = o.id GROUP BY c.name

        Q: {question}
        A:
        """;

    /// <summary>
    /// The repair prompt. Feeds back the failed SQL and the verbatim SQLite
    /// error — the error text is the useful part, since it names the offending
    /// table or column precisely.
    /// </summary>
    public static string Repair(string schemaDdl, string question, string failedSql, string error) => $"""
        You are a SQLite expert. Your previous attempt failed.

        SCHEMA:
        {schemaDdl}

        QUESTION: {question}
        YOUR SQL: {failedSql}
        SQLITE ERROR: {error}

        Return the corrected SQLite SELECT statement. Raw SQL only, no fences,
        no explanation, no semicolon.
        """;
}