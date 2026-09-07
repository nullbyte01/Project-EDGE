using Edge.NlToSql;
using NUnit.Framework;

namespace Edge.NlToSql.Tests;

/// <summary>
/// Pure functions, so no mocks and no database. Everything here is string in,
/// verdict out — which is why it can afford to be exhaustive.
/// </summary>
[TestFixture]
public class SqlGuardTests
{
    // ── Sanitise ────────────────────────────────────────────────────────────

    [Test]
    public void Sanitise_StripsMarkdownFences()
    {
        const string raw = "```sql\nSELECT * FROM customers\n```";
        Assert.That(SqlGuard.Sanitise(raw), Is.EqualTo("SELECT * FROM customers"));
    }

    [Test]
    public void Sanitise_DropsConversationalPreamble()
    {
        const string raw = "Sure! Here is the query you asked for:\nSELECT id FROM orders";
        Assert.That(SqlGuard.Sanitise(raw), Is.EqualTo("SELECT id FROM orders"));
    }

    [Test]
    public void Sanitise_StripsTrailingSemicolon()
    {
        Assert.That(SqlGuard.Sanitise("SELECT 1;"), Is.EqualTo("SELECT 1"));
    }

    [Test]
    public void Sanitise_PreservesCteStartingWithWith()
    {
        const string raw = "WITH totals AS (SELECT 1) SELECT * FROM totals";
        Assert.That(SqlGuard.Sanitise(raw), Is.EqualTo(raw));
    }

    [Test]
    public void Sanitise_PicksWhicheverKeywordComesFirst()
    {
        // A CTE mentions SELECT after WITH; the statement must start at WITH.
        const string raw = "Here you go:\nWITH t AS (SELECT 1) SELECT * FROM t";
        Assert.That(SqlGuard.Sanitise(raw), Does.StartWith("WITH t AS"));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public void Sanitise_ReturnsEmpty_ForNothingUseful(string? raw)
    {
        Assert.That(SqlGuard.Sanitise(raw), Is.Empty);
    }

    // ── Check: the read-only guarantee ──────────────────────────────────────

    [TestCase("SELECT * FROM customers")]
    [TestCase("select name from customers where city = 'Pune'")]
    [TestCase("WITH t AS (SELECT 1 AS n) SELECT n FROM t")]
    [TestCase("SELECT COUNT(*) FROM orders WHERE status = 'created'")]   // 'created' != CREATE
    [TestCase("SELECT updated_at FROM orders")]                          // 'updated_at' != UPDATE
    public void Check_Allows_ReadOnlyStatements(string sql)
    {
        Assert.That(SqlGuard.Check(sql).Allowed, Is.True, $"should allow: {sql}");
    }

    [TestCase("DROP TABLE customers", SqlRejection.NotReadOnly)]
    [TestCase("DELETE FROM orders", SqlRejection.NotReadOnly)]
    [TestCase("UPDATE customers SET city = 'X'", SqlRejection.NotReadOnly)]
    [TestCase("INSERT INTO customers VALUES (9,'a','b','c')", SqlRejection.NotReadOnly)]
    [TestCase("PRAGMA table_info(customers)", SqlRejection.NotReadOnly)]
    public void Check_Rejects_NonSelectStatements(string sql, SqlRejection expected)
    {
        var check = SqlGuard.Check(sql);
        Assert.Multiple(() =>
        {
            Assert.That(check.Allowed, Is.False);
            Assert.That(check.Reason, Is.EqualTo(expected));
        });
    }

    [Test]
    public void Check_Rejects_StackedStatements()
    {
        // The classic smuggle: a legitimate SELECT with a second statement behind it.
        var check = SqlGuard.Check("SELECT 1; DROP TABLE orders");

        Assert.Multiple(() =>
        {
            Assert.That(check.Allowed, Is.False);
            Assert.That(check.Reason, Is.EqualTo(SqlRejection.StackedStatements));
        });
    }

    [Test]
    public void Check_Rejects_MutatingKeywordHiddenInsideASelect()
    {
        var check = SqlGuard.Check("SELECT * FROM customers UNION SELECT * FROM (DELETE FROM orders)");

        Assert.Multiple(() =>
        {
            Assert.That(check.Allowed, Is.False);
            Assert.That(check.Reason, Is.EqualTo(SqlRejection.MutatingKeyword));
        });
    }

    [Test]
    public void Check_FailsClosed_OnMutatingWordInStringLiteral()
    {
        // Documented false positive, accepted on purpose. A false rejection is
        // an annoyance; a false acceptance drops a table.
        var check = SqlGuard.Check("SELECT * FROM orders WHERE status = 'please delete'");

        Assert.That(check.Allowed, Is.False,
            "the guard fails closed - this is the intended trade-off, not a bug");
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public void Check_Rejects_Empty(string? sql)
    {
        Assert.That(SqlGuard.Check(sql).Reason, Is.EqualTo(SqlRejection.Empty));
    }

    [Test]
    public void SanitiseThenCheck_RejectsAFencedDropStatement()
    {
        // End-to-end on the pair: fences removed, statement still refused.
        var sql = SqlGuard.Sanitise("```sql\nDROP TABLE customers\n```");
        Assert.That(SqlGuard.Check(sql).Allowed, Is.False);
    }
}