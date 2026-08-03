using PrismOne.Db.Core;
using Xunit;

namespace PrismOne.Db.Core.Tests;

public class TransactionTests
{
    [Theory]
    [InlineData("select * from t", true)]
    [InlineData("  SELECT 1", true)]
    [InlineData("EXPLAIN select 1", true)]
    [InlineData("show search_path", true)]
    [InlineData("insert into t values (1)", false)]
    [InlineData("UPDATE t SET a=1", false)]
    [InlineData("delete from t", false)]
    [InlineData("create table x(a int)", false)]
    [InlineData("with x as (select 1) insert into t select * from x", false)]
    public void IsReadOnlyStatement_ClassifiesCorrectly(string sql, bool readOnly)
        => Assert.Equal(readOnly, QuerySession.IsReadOnlyStatement(sql));
}

public class TransactionIsolationTests
{
    // 표기는 DataGrip 2024.2 의 DatabaseBundle(transaction.mode.*) 와 맞춘다
    [Theory]
    [InlineData(TransactionIsolation.ReadUncommitted, "READ UNCOMMITTED", "Read Uncommitted")]
    [InlineData(TransactionIsolation.ReadCommitted, "READ COMMITTED", "Read Committed")]
    [InlineData(TransactionIsolation.RepeatableRead, "REPEATABLE READ", "Repeatable Read")]
    [InlineData(TransactionIsolation.Serializable, "SERIALIZABLE", "Serializable")]
    public void MapsToSqlAndDisplayText(TransactionIsolation level, string sql, string display)
    {
        Assert.Equal(sql, level.ToSql());
        Assert.Equal(display, level.Display());
    }

    [Fact]
    public void SessionSqlSetsLevelAndResetsForDatabaseDefault()
    {
        Assert.Equal(
            "SET SESSION CHARACTERISTICS AS TRANSACTION ISOLATION LEVEL SERIALIZABLE",
            TransactionIsolation.Serializable.ToSessionSql());
        Assert.Equal(
            "RESET default_transaction_isolation",
            TransactionIsolation.DatabaseDefault.ToSessionSql());
    }

    [Fact]
    public void DatabaseDefaultIsDisplayedLikeDataGrip()
        => Assert.Equal("Database Default", TransactionIsolation.DatabaseDefault.Display());

    [Fact]
    public void OptionsKeepIsolationAsReadableText()
    {
        var options = new AppOptions { Isolation = TransactionIsolation.Serializable };

        var json = System.Text.Json.JsonSerializer.Serialize(options);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppOptions>(json)!;

        Assert.Equal(TransactionIsolation.DatabaseDefault, new AppOptions().Isolation);
        Assert.Contains("\"Serializable\"", json);   // 숫자가 아닌 이름으로 저장돼야 읽기 쉽다
        Assert.Equal(TransactionIsolation.Serializable, restored.Isolation);
    }

    [Fact]
    public void OptionsWithoutIsolationKeyFallBackToDatabaseDefault()
    {
        var legacy = """{ "FetchBatch": 100, "AutoCommit": false }""";

        var options = System.Text.Json.JsonSerializer.Deserialize<AppOptions>(legacy)!;

        Assert.Equal(TransactionIsolation.DatabaseDefault, options.Isolation);
    }
}
