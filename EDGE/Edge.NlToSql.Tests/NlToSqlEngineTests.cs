using Edge.NlToSql;
using Microsoft.Extensions.AI;
using Moq;
using NUnit.Framework;
using System.Timers;

namespace Edge.NlToSql.Tests;

/// <summary>
/// The mocking lesson in this ticket is the opposite of EDGE-101's.
///
///   MOCK the model.     A real call is seconds. A mock is microseconds, and
///                       scripting the reply is the only way to test a
///                       specific path deterministically.
///
///   DO NOT mock SQLite. An in-memory database seeds in about a millisecond.
///                       A mock would be slower to write, and — more
///                       importantly — it would encode MY beliefs about what
///                       SQLite accepts rather than what it actually accepts.
///                       EXPLAIN validation is the thing under test; faking it
///                       would test nothing at all.
///
/// General rule this illustrates: mock what is slow, non-deterministic, or
/// external. Never mock something fast and truthful that you can just run.
/// </summary>
[TestFixture]
public class NlToSqlEngineTests
{
    private AnalyticsDatabase _db = null!;
    private SqlExecutor _executor = null!;
    private string _schema = null!;

    [SetUp]
    public void SetUp()
    {
        // A UNIQUE database name per test. Shared-cache in-memory databases are
        // keyed by name, so a fixed name would leak state between tests.
        _db = AnalyticsDatabase.CreateSeeded($"test_{Guid.NewGuid():N}");
        _executor = new SqlExecutor(_db.Connection);
        _schema = _db.DumpSchema();
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private static ChatResponse Reply(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text));

    private static Mock<IChatClient> ModelReturning(params string[] replies)
    {
        var mock = new Mock<IChatClient>();
        var setup = mock.SetupSequence(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()));

        foreach (var reply in replies) setup = setup.ReturnsAsync(Reply(reply));
        return mock;
    }

    private NlToSqlEngine Engine(IChatClient client) => new(client, _executor, _schema);

    // ── Happy path ──────────────────────────────────────────────────────────

    [Test]
    public async Task Generated_WhenFirstAttemptIsValid()
    {
        var model = ModelReturning("SELECT name FROM customers WHERE city = 'Pune'");

        var result = await Engine(model.Object).GenerateAsync("who is in Pune?");

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SqlOutcome.Generated));
            Assert.That(result.ModelCalls, Is.EqualTo(1));
            Assert.That(result.Error, Is.Null);
        });
    }

    [Test]
    public async Task Generated_StripsFencesBeforeValidating()
    {
        var model = ModelReturning("```sql\nSELECT COUNT(*) FROM orders\n```");

        var result = await Engine(model.Object).GenerateAsync("how many orders?");

        Assert.That(result.Outcome, Is.EqualTo(SqlOutcome.Generated));
        Assert.That(result.Sql, Is.EqualTo("SELECT COUNT(*) FROM orders"));
    }

    // ── Repair budget ───────────────────────────────────────────────────────

    [Test]
    public async Task Repaired_WhenSecondAttemptSucceeds()
    {
        // First reply references a table that does not exist. REAL SQLite
        // catches it via EXPLAIN - no mock required, and no guesswork about
        // what the error message looks like.
        var model = ModelReturning(
            "SELECT * FROM suppliers",
            "SELECT * FROM customers");

        var result = await Engine(model.Object).GenerateAsync("list suppliers");

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SqlOutcome.Repaired));
            Assert.That(result.ModelCalls, Is.EqualTo(2));
            Assert.That(result.Error, Is.Not.Null, "the first error is kept for display");
            Assert.That(result.Sql, Is.EqualTo("SELECT * FROM customers"));
        });
    }

    [Test]
    public async Task Failed_AfterExactlyOneRepairAttempt()
    {
        // Budget discipline: two calls, never three. A third round on a small
        // model just produces variations on the same wrong answer.
        var model = ModelReturning(
            "SELECT * FROM suppliers",
            "SELECT * FROM vendors");

        var result = await Engine(model.Object).GenerateAsync("list suppliers");

        Assert.That(result.Outcome, Is.EqualTo(SqlOutcome.Failed));
        Assert.That(result.ModelCalls, Is.EqualTo(2));

        model.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task Generated_MakesOnlyOneModelCall_WhenNoRepairIsNeeded()
    {
        var model = ModelReturning("SELECT 1");

        await Engine(model.Object).GenerateAsync("anything");

        model.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── The guard, in the pipeline ──────────────────────────────────────────

    [Test]
    public async Task Blocked_WhenModelEmitsADestructiveStatement()
    {
        var model = ModelReturning("DROP TABLE customers");

        var result = await Engine(model.Object).GenerateAsync("remove all customers");

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SqlOutcome.Blocked));
            Assert.That(result.ModelCalls, Is.EqualTo(1), "blocked before any repair round");
        });

        // And the table is still there. This is the assertion that matters.
        Assert.DoesNotThrow(() => _executor.Run("SELECT COUNT(*) FROM customers"));
    }

    [Test]
    public async Task Blocked_WhenTheRepairAttemptTurnsDestructive()
    {
        var model = ModelReturning(
            "SELECT * FROM suppliers",      // invalid -> triggers repair
            "DELETE FROM orders");          // repair turns nasty

        var result = await Engine(model.Object).GenerateAsync("clear the suppliers");

        Assert.That(result.Outcome, Is.EqualTo(SqlOutcome.Blocked),
            "the guard must apply to the repaired SQL too, not just the first attempt");
        Assert.DoesNotThrow(() => _executor.Run("SELECT COUNT(*) FROM orders"));
    }

    [Test]
    public void Rejects_EmptyQuestion()
    {
        Assert.ThrowsAsync<ArgumentException>(() =>
            Engine(Mock.Of<IChatClient>()).GenerateAsync("  "));
    }
}

/// <summary>
/// Real database throughout. These assert what SQLite does, which is precisely
/// the thing a mock could not tell us.
/// </summary>
[TestFixture]
public class SqlExecutorTests
{
    private AnalyticsDatabase _db = null!;
    private SqlExecutor _executor = null!;

    [SetUp]
    public void SetUp()
    {
        _db = AnalyticsDatabase.CreateSeeded($"exec_{Guid.NewGuid():N}");
        _executor = new SqlExecutor(_db.Connection);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    [Test]
    public void TryValidate_Accepts_AThreeTableJoin()
    {
        const string sql = """
            SELECT c.name, SUM(oi.qty * oi.unit_price) AS total
            FROM customers c
            JOIN orders o ON o.customer_id = c.id
            JOIN order_items oi ON oi.order_id = o.id
            GROUP BY c.name
            """;

        Assert.That(_executor.TryValidate(sql, out _), Is.True);
    }

    [TestCase("SELECT * FROM suppliers", "unknown table")]
    [TestCase("SELECT nonexistent FROM customers", "unknown column")]
    [TestCase("SELCT * FROM customers", "syntax error")]
    public void TryValidate_Rejects_BadSql(string sql, string why)
    {
        Assert.That(_executor.TryValidate(sql, out var error), Is.False, why);
        Assert.That(error, Is.Not.Empty);
    }

    [Test]
    public void Run_ReturnsColumnsAndRows()
    {
        var result = _executor.Run("SELECT name, city FROM customers ORDER BY id");

        Assert.Multiple(() =>
        {
            Assert.That(result.Columns, Is.EqualTo(new[] { "name", "city" }));
            Assert.That(result.Rows, Has.Count.EqualTo(5));
            Assert.That(result.Rows[0][0], Is.EqualTo("Asha Menon"));
        });
    }

    [Test]
    public void Run_TruncatesAtMaxRows()
    {
        var result = _executor.Run("SELECT * FROM customers", maxRows: 2);

        Assert.Multiple(() =>
        {
            Assert.That(result.Rows, Has.Count.EqualTo(2));
            Assert.That(result.Truncated, Is.True);
        });
    }

    [Test]
    public void Run_EnforcesTheGuard_EvenWhenCalledDirectly()
    {
        // Last line of defence. Run() re-checks rather than trusting the caller.
        Assert.Throws<InvalidOperationException>(() => _executor.Run("DROP TABLE customers"));
    }

    [Test]
    public void ToAsciiTable_SaysSoWhenThereAreNoRows()
    {
        var result = _executor.Run("SELECT * FROM customers WHERE city = 'Atlantis'");
        Assert.That(result.ToAsciiTable(), Is.EqualTo("(no rows)"));
    }

    [Test]
    public void DumpSchema_ContainsEveryUserTable_AndNoInternalOnes()
    {
        var schema = _db.DumpSchema();

        Assert.Multiple(() =>
        {
            Assert.That(schema, Does.Contain("CREATE TABLE customers"));
            Assert.That(schema, Does.Contain("CREATE TABLE orders"));
            Assert.That(schema, Does.Contain("CREATE TABLE order_items"));
            Assert.That(schema, Does.Not.Contain("sqlite_"));
        });
    }
}