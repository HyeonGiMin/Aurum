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
