// ============================================================================
// EDGE-102 — Edge Natural Language-to-SQL Generator.
//
// WIRING ONLY. Model load, database seed, REPL, rendering. Every decision that
// could be wrong — sanitising, the read-only guard, EXPLAIN validation, the
// repair budget — lives in SqlGuard.cs, SqlExecutor.cs and NlToSqlEngine.cs,
// where it is unit-tested (EDGE-102.5).
//
// Usage:
//   nl2sql [--provider dml|cpu]
//
// REPL commands:
//   \schema   print the CREATE TABLE statements sent to the model
//   \sql      print the SQL generated for the last question
//   \q        quit
// ============================================================================

using Edge.NlToSql;
using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntimeGenAI;
using System.Diagnostics;
using System.Runtime.InteropServices;

using var oga = new OgaHandle();

var provider = ArgValue("--provider") ?? Environment.GetEnvironmentVariable("EDGE_PROVIDER") ?? "dml";

var modelPath = Environment.GetEnvironmentVariable("PHI_MODEL_PATH");
if (string.IsNullOrWhiteSpace(modelPath) || !Directory.Exists(modelPath))
{
    Console.Error.WriteLine("PHI_MODEL_PATH is not set or does not exist. See EDGE-100.2.");
    return 69;
}

// ── Database ────────────────────────────────────────────────────────────────
using var db = AnalyticsDatabase.CreateSeeded();
var schema = db.DumpSchema();
var executor = new SqlExecutor(db.Connection);
Console.WriteLine("Database seeded (customers, orders, order_items).");

// ── Model ───────────────────────────────────────────────────────────────────
Console.WriteLine($"Loading model ({provider})...");
var loadTimer = Stopwatch.StartNew();

using var config = new Config(modelPath);

// MANDATORY. genai_config.json ships "provider_options": [] even for GPU
// builds — the provider is chosen at runtime. Skip this and GenAI silently
// falls back to CPU at roughly a fifth of the speed.
if (!provider.Equals("cpu", StringComparison.OrdinalIgnoreCase))
{
    config.ClearProviders();
    config.AppendProvider(provider);
}

using var model = new Model(config);
loadTimer.Stop();

// ONE client, reused for every question in the session. In a REPL this matters
// more than anywhere else — if the EDGE-101.1 spike found the chat-client
// overhead to be per-instance, this is what keeps it off every question.
IChatClient chat = new OnnxRuntimeGenAIChatClient(model);
var engine = new NlToSqlEngine(chat, executor, schema);

Console.WriteLine($"Model ready in {loadTimer.Elapsed.TotalSeconds:F1}s.");
Console.WriteLine(@"Ask a question, or \schema, \sql, \q." + "\n");

// ── REPL ────────────────────────────────────────────────────────────────────
var lastSql = "(none yet)";

while (true)
{
    Console.Write("ask> ");
    var question = Console.ReadLine();

    if (question is null or "\\q") break;
    if (string.IsNullOrWhiteSpace(question)) continue;

    if (question == "\\schema") { Console.WriteLine($"\n{schema}\n"); continue; }
    if (question == "\\sql") { Console.WriteLine($"\n{lastSql}\n"); continue; }

    var sw = Stopwatch.StartNew();
    var generation = await engine.GenerateAsync(question);
    sw.Stop();

    lastSql = generation.Sql;

    switch (generation.Outcome)
    {
        case SqlOutcome.Blocked:
            Console.WriteLine($"\n[blocked] {generation.Error}");
            Console.WriteLine($"          {generation.Sql}\n");
            continue;

        case SqlOutcome.Failed:
            Console.WriteLine($"\n[failed after repair] {generation.Error}");
            Console.WriteLine($"          {generation.Sql}\n");
            continue;

        case SqlOutcome.Repaired:
            Console.WriteLine($"\n[repaired] first attempt failed: {generation.Error}");
            break;
    }

    // Echo the SQL before the rows. The user should always be able to audit
    // what actually ran against the database.
    Console.WriteLine($"\n\u001b[90m{generation.Sql}\u001b[0m\n");

    try
    {
        var result = executor.Run(generation.Sql);
        Console.WriteLine(result.ToAsciiTable());
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[execution error] {ex.Message}");
    }

    Console.WriteLine(
        $"\n\u001b[90m{sw.Elapsed.TotalSeconds:F1}s, {generation.ModelCalls} model call(s)\u001b[0m\n");
}

(chat as IDisposable)?.Dispose();
return 0;

string? ArgValue(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}