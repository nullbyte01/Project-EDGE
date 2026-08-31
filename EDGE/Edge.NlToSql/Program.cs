using Edge.NlToSql;

Console.WriteLine("--- Verifying EDGE-102.1 ACs ---");

// AC 1: DumpSchema returns 3 CREATE TABLE statements and no internal sqlite_% tables
using var db1 = AnalyticsDatabase.CreateSeeded("analytics_test_1");
var schema = db1.DumpSchema();

Console.WriteLine($"Schema is : {schema}");

var hasCustomers = schema.Contains("CREATE TABLE customers", StringComparison.OrdinalIgnoreCase);
var hasOrders = schema.Contains("CREATE TABLE orders", StringComparison.OrdinalIgnoreCase);
var hasOrderItems = schema.Contains("CREATE TABLE order_items", StringComparison.OrdinalIgnoreCase);
var hasNoSqliteInternals = !schema.Contains("sqlite_", StringComparison.OrdinalIgnoreCase);

Console.WriteLine($"[AC 1] Schema contains 3 tables: {hasCustomers && hasOrders && hasOrderItems}");
Console.WriteLine($"[AC 1] No sqlite_% tables: {hasNoSqliteInternals}");

// AC 2: A three-table join returns rows
using var joinCmd = db1.Connection.CreateCommand();
joinCmd.CommandText = """
    SELECT c.name, o.id, oi.product, oi.unit_price
    FROM customers c
    JOIN orders o ON o.customer_id = c.id
    JOIN order_items oi ON oi.order_id = o.id;
""";

using var reader = joinCmd.ExecuteReader();
var rowCount = 0;
while (reader.Read())
{
    rowCount++;
}
Console.WriteLine($"[AC 2] 3-Table Join returned rows: {rowCount > 0} ({rowCount} rows found)");

// AC 3: Two instances with different names do not see each other's data
using var db2 = AnalyticsDatabase.CreateSeeded("analytics_test_2");

// Insert an extra customer into db2 only
using var insertCmd = db2.Connection.CreateCommand();
insertCmd.CommandText = "INSERT INTO customers VALUES (99, 'Test Isolation', 'Surat', '2026-08-31');";
insertCmd.ExecuteNonQuery();

// Check if db1 sees the new customer from db2
using var checkDb1Cmd = db1.Connection.CreateCommand();
checkDb1Cmd.CommandText = "SELECT COUNT(*) FROM customers WHERE id = 99;";
var db1Count = Convert.ToInt32(checkDb1Cmd.ExecuteScalar());

using var checkDb2Cmd = db2.Connection.CreateCommand();
checkDb2Cmd.CommandText = "SELECT COUNT(*) FROM customers WHERE id = 99;";
var db2Count = Convert.ToInt32(checkDb2Cmd.ExecuteScalar());

var isIsolated = (db1Count == 0) && (db2Count == 1);
Console.WriteLine($"[AC 3] Databases are isolated by name: {isIsolated}");

if (hasCustomers && hasOrders && hasOrderItems && hasNoSqliteInternals && (rowCount > 0) && isIsolated)
{
    Console.WriteLine("\n[PASS] All EDGE-102.1 Acceptance Criteria verified!");
}
else
{
    Console.Error.WriteLine("\n[FAIL] One or more criteria failed.");
}