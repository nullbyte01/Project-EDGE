using Edge.NlToSql;
using Microsoft.Extensions.AI;

Console.WriteLine("--- Verifying EDGE-102.3 ACs ---\n");

using var db = AnalyticsDatabase.CreateSeeded($"test_102_3_{Guid.NewGuid():N}");
var schema = db.DumpSchema();
var executor = new SqlExecutor(db.Connection);

// ── AC 1: Valid join query passes EXPLAIN on first attempt ─────────────────
var joinSql = """
    SELECT c.name 
    FROM customers c 
    JOIN orders o ON o.customer_id = c.id 
    JOIN order_items oi ON oi.order_id = o.id 
    WHERE oi.product LIKE '%Monitor%';
    """;

var client1 = new ScriptedChatClient(joinSql);
var engine1 = new NlToSqlEngine(client1, executor, schema);
var res1 = await engine1.GenerateAsync("Which customers ordered a monitor?");

var ac1Passed = res1.Outcome == SqlOutcome.Generated
             && res1.ModelCalls == 1
             && executor.TryValidate(res1.Sql, out _);
Console.WriteLine($"[AC 1] Valid join passes EXPLAIN on first attempt: {ac1Passed}");

// ── AC 2: Markdown fences and semicolons are stripped ───────────────────────
var fencedSql = "```sql\nSELECT COUNT(*) FROM orders;\n```";
var client2 = new ScriptedChatClient(fencedSql);
var engine2 = new NlToSqlEngine(client2, executor, schema);
var res2 = await engine2.GenerateAsync("How many orders?");

var ac2Passed = res2.Sql == "SELECT COUNT(*) FROM orders"
             && !res2.Sql.Contains("```")
             && !res2.Sql.EndsWith(';');
Console.WriteLine($"[AC 2] Strips markdown fences and trailing semicolons: {ac2Passed}");

// ── AC 3: Non-existent table triggers exactly ONE repair round ─────────────
var client3 = new ScriptedChatClient(
    "SELECT * FROM non_existent_table", // Attempt 1 (fails EXPLAIN)
    "SELECT * FROM customers"           // Attempt 2 (repair succeeds)
);
var engine3 = new NlToSqlEngine(client3, executor, schema);
var res3 = await engine3.GenerateAsync("List suppliers");

var ac3Passed = res3.Outcome == SqlOutcome.Repaired
             && res3.ModelCalls == 2
             && res3.Sql == "SELECT * FROM customers";
Console.WriteLine($"[AC 3] Triggers exactly one repair round when table is invalid: {ac3Passed}");

// ── AC 4: Prompt template dialect rules enforce SQLite (LIMIT, not TOP) ────
var promptText = Prompt.Generate(schema, "Show top 5 orders");
var ac4Passed = promptText.Contains("Use LIMIT, never TOP")
             && promptText.Contains("Use date(), never GETDATE()");
Console.WriteLine($"[AC 4] Prompt rules reinforce SQLite dialect (no TOP/GETDATE): {ac4Passed}");

// ── AC 5: SqlGuard applies to repaired SQL (destructive repair is blocked) ─
var client5 = new ScriptedChatClient(
    "SELECT * FROM non_existent_table", // Attempt 1 (fails EXPLAIN)
    "DROP TABLE orders"                 // Attempt 2 (destructive repair)
);
var engine5 = new NlToSqlEngine(client5, executor, schema);
var res5 = await engine5.GenerateAsync("Clear orders");

var ac5Passed = res5.Outcome == SqlOutcome.Blocked
             && res5.ModelCalls == 2
             && !string.IsNullOrWhiteSpace(res5.Error);
Console.WriteLine($"[AC 5] SqlGuard blocks destructive repaired SQL: {ac5Passed}");

// ── Summary ─────────────────────────────────────────────────────────────────
if (ac1Passed && ac2Passed && ac3Passed && ac4Passed && ac5Passed)
{
    Console.WriteLine("\n[PASS] All EDGE-102.3 Acceptance Criteria verified successfully!");
}
else
{
    Console.Error.WriteLine("\n[FAIL] One or more Acceptance Criteria failed.");
}

// ── Test Double Helper ───────────────────────────────────────────────────────
file sealed class ScriptedChatClient(params string[] responses) : IChatClient
{
    private readonly Queue<string> _queue = new(responses);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var text = _queue.Count > 0 ? _queue.Dequeue() : string.Empty;
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
        return Task.FromResult(response);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}