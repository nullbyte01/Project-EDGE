using Microsoft.Extensions.AI;

namespace Edge.NlToSql;

public enum SqlOutcome
{
    /// <summary>Valid on the first attempt.</summary>
    Generated,
    /// <summary>First attempt failed validation; the repaired attempt passed.</summary>
    Repaired,
    /// <summary>Both attempts failed EXPLAIN. The question is likely out of scope.</summary>
    Failed,
    /// <summary>The model produced something the read-only guard refused.</summary>
    Blocked
}

public sealed record SqlGeneration(
    SqlOutcome Outcome,
    string Sql,
    string? Error,
    int ModelCalls);

/// <summary>
/// Turns one English question into one validated SELECT.
///
/// This is a DETERMINISTIC pipeline, not an autonomous agent — a deliberate
/// choice. The ONNX chat client does not support automatic function calling,
/// so tool dispatch could not be delegated to the model even if we wanted it.
/// Doing it in code is also the safer design: the model proposes SQL, it never
/// decides whether to touch the database.
///
/// Takes IChatClient (mocked in tests) and SqlExecutor (a REAL in-memory
/// SQLite in tests — it runs in microseconds, and a mock would be both slower
/// to write and less truthful about what SQLite actually accepts).
/// </summary>
public sealed class NlToSqlEngine(
    IChatClient client,
    SqlExecutor executor,
    string schemaDdl,
    ChatOptions? options = null)
{
    private readonly ChatOptions _options = options ?? new ChatOptions
    {
        Temperature = 0.0f,      // SQL generation wants determinism, not creativity
        MaxOutputTokens = 200
    };

    public async Task<SqlGeneration> GenerateAsync(
        string question,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var calls = 0;

        var sql = SqlGuard.Sanitise(await AskAsync(Prompt.Generate(schemaDdl, question)));
        var guard = SqlGuard.Check(sql);
        if (!guard.Allowed)
            return new SqlGeneration(SqlOutcome.Blocked, sql, guard.Detail, calls);

        if (executor.TryValidate(sql, out var firstError))
            return new SqlGeneration(SqlOutcome.Generated, sql, null, calls);

        // Exactly ONE repair attempt. A second failure means the question is
        // out of scope for this schema, and further rounds just burn GPU time
        // producing variations on the same wrong answer.
        var repaired = SqlGuard.Sanitise(
            await AskAsync(Prompt.Repair(schemaDdl, question, sql, firstError)));

        var repairedGuard = SqlGuard.Check(repaired);
        if (!repairedGuard.Allowed)
            return new SqlGeneration(SqlOutcome.Blocked, repaired, repairedGuard.Detail, calls);

        return executor.TryValidate(repaired, out var secondError)
            ? new SqlGeneration(SqlOutcome.Repaired, repaired, firstError, calls)
            : new SqlGeneration(SqlOutcome.Failed, repaired, secondError, calls);

        async Task<string> AskAsync(string prompt)
        {
            calls++;
            var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };
            var response = await client.GetResponseAsync(messages, _options, cancellationToken);
            return response.Text ?? string.Empty;
        }
    }
}