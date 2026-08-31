using Edge.NlToSql;

Console.WriteLine("--- Verifying EDGE-102.2 ACs ---\n");

using var db = AnalyticsDatabase.CreateSeeded($"test_guard_{Guid.NewGuid():N}");
var executor = new SqlExecutor(db.Connection);

// ── AC 1: DROP, DELETE, UPDATE, INSERT, and PRAGMA are all refused ──────────
string[] mutatingStatements =
[
    "DROP TABLE customers",
    "DELETE FROM orders WHERE id = 1",
    "UPDATE customers SET city = 'Valsad' WHERE id = 1",
    "INSERT INTO customers VALUES (10, 'Test User', 'Surat', '2026-08-31')",
    "PRAGMA table_info(customers)"
];

var ac1Passed = mutatingStatements.All(sql =>
{
    var check = SqlGuard.Check(sql);
    return !check.Allowed;
});
Console.WriteLine($"[AC 1] Mutating statements (DROP, DELETE, UPDATE, INSERT, PRAGMA) refused: {ac1Passed}");

// ── AC 2: Stacked statements are refused ─────────────────────────────────────
var stackedSql = "SELECT 1; DROP TABLE orders";
var stackedCheck = SqlGuard.Check(stackedSql);
var ac2Passed = !stackedCheck.Allowed && stackedCheck.Reason == SqlRejection.StackedStatements;
Console.WriteLine($"[AC 2] Stacked statements ('SELECT 1; DROP TABLE orders') refused: {ac2Passed}");

// ── AC 3: 'created' keyword in WHERE clause is allowed ───────────────────────
var allowedWordSql = "SELECT COUNT(*) FROM orders WHERE status = 'created'";
var allowedCheck = SqlGuard.Check(allowedWordSql);
var ac3Passed = allowedCheck.Allowed;
Console.WriteLine($"[AC 3] 'WHERE status = ''created''' allowed (word boundary check): {ac3Passed}");

// ── AC 4: EXPLAIN rejects an unknown table without executing anything ────────
var badSql = "SELECT * FROM non_existent_table";
var validationPassed = !executor.TryValidate(badSql, out var validationError)
                       && !string.IsNullOrWhiteSpace(validationError);
Console.WriteLine($"[AC 4] EXPLAIN rejects unknown table with error ('{validationError}'): {validationPassed}");

// ── AC 5: Run() re-checks the guard even when called directly ────────────────
var ac5Passed = false;
try
{
    executor.Run("DROP TABLE customers");
}
catch (InvalidOperationException ex) when (ex.Message.Contains("Blocked by guard"))
{
    ac5Passed = true;
}
Console.WriteLine($"[AC 5] Run() directly blocks mutating statements: {ac5Passed}");

// ── Summary ─────────────────────────────────────────────────────────────────
if (ac1Passed && ac2Passed && ac3Passed && validationPassed && ac5Passed)
{
    Console.WriteLine("\n[PASS] All EDGE-102.2 Acceptance Criteria verified successfully!");
}
else
{
    Console.Error.WriteLine("\n[FAIL] One or more Acceptance Criteria failed.");
}