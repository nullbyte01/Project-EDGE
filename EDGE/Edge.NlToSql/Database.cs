using System.Text;
using Microsoft.Data.Sqlite;

namespace Edge.NlToSql;

/// <summary>
/// A seeded in-memory SQLite database.
///
/// Two details that bite if you get them wrong:
///
/// 1. The connection is held open for the object's lifetime. A ":memory:"
///    database is destroyed when its LAST connection closes — open/close per
///    query and your tables vanish between calls.
/// 2. The name is a parameter, not a constant. Shared-cache in-memory
///    databases are keyed by name, so two instances called "analytics" are the
///    SAME database. Tests pass a unique name to stay isolated.
/// </summary>
public sealed class AnalyticsDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteConnection Connection => _connection;

    private AnalyticsDatabase(SqliteConnection connection) => _connection = connection;

    public static AnalyticsDatabase CreateSeeded(string name = "analytics")
    {
        var connection = new SqliteConnection($"Data Source={name};Mode=Memory;Cache=Shared");
        connection.Open();          // stays open - see note above

        using var cmd = connection.CreateCommand();
        cmd.CommandText = SeedScript;
        cmd.ExecuteNonQuery();

        return new AnalyticsDatabase(connection);
    }

    /// <summary>
    /// The CREATE TABLE statements, verbatim from sqlite_master. This exact
    /// string is what gets injected into the prompt — the model sees the real
    /// schema, not a hand-maintained description that can drift out of date.
    /// </summary>
    public string DumpSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";

        using var reader = cmd.ExecuteReader();
        var sb = new StringBuilder();
        while (reader.Read())
            sb.AppendLine(reader.GetString(0)).AppendLine(";").AppendLine();

        return sb.ToString().TrimEnd();
    }

    public void Dispose() => _connection.Dispose();

    private const string SeedScript = """
        CREATE TABLE customers (
            id          INTEGER PRIMARY KEY,
            name        TEXT NOT NULL,
            city        TEXT NOT NULL,
            signup_date TEXT NOT NULL
        );

        CREATE TABLE orders (
            id          INTEGER PRIMARY KEY,
            customer_id INTEGER NOT NULL,
            order_date  TEXT NOT NULL,
            status      TEXT NOT NULL,
            FOREIGN KEY (customer_id) REFERENCES customers(id)
        );

        CREATE TABLE order_items (
            id         INTEGER PRIMARY KEY,
            order_id   INTEGER NOT NULL,
            product    TEXT NOT NULL,
            qty        INTEGER NOT NULL,
            unit_price REAL NOT NULL,
            FOREIGN KEY (order_id) REFERENCES orders(id)
        );

        INSERT INTO customers VALUES
            (1, 'Asha Menon',   'Pune',      '2024-01-14'),
            (2, 'Rahul Shah',   'Ahmedabad', '2024-03-02'),
            (3, 'Meera Iyer',   'Mumbai',    '2024-06-21'),
            (4, 'Vikram Desai', 'Ahmedabad', '2025-02-08'),
            (5, 'Priya Nair',   'Pune',      '2025-05-30');

        INSERT INTO orders VALUES
            (1, 1, '2025-07-01', 'shipped'),
            (2, 2, '2025-07-14', 'pending'),
            (3, 1, '2025-08-03', 'cancelled'),
            (4, 3, '2025-08-11', 'shipped'),
            (5, 4, '2025-09-02', 'shipped'),
            (6, 5, '2025-09-19', 'pending'),
            (7, 2, '2025-10-05', 'shipped');

        INSERT INTO order_items VALUES
            (1,  1, 'Mechanical Keyboard', 1,  8999.00),
            (2,  1, 'USB-C Hub',           2,  2499.50),
            (3,  2, '27in Monitor',        1, 24999.00),
            (4,  3, 'Laptop Stand',        1,  3499.00),
            (5,  4, 'Noise Cancelling Headphones', 1, 18999.00),
            (6,  4, 'USB-C Hub',           1,  2499.50),
            (7,  5, 'Mechanical Keyboard', 2,  8999.00),
            (8,  6, '27in Monitor',        2, 24999.00),
            (9,  7, 'Webcam',              1,  5499.00),
            (10, 7, 'Laptop Stand',        1,  3499.00);
        """;
}