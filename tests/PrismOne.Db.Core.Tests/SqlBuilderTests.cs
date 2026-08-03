using PrismOne.Db.Core;
using Xunit;

namespace PrismOne.Db.Core.Tests;

public class SqlBuilderTests
{
    private static QuerySpec Spec(
        IReadOnlyList<string>? columns = null,
        IReadOnlyList<QueryCondition>? conditions = null,
        IReadOnlyList<QueryOrder>? orders = null,
        int? limit = null,
        string? alias = null) =>
        new("prismone.study", columns ?? [], conditions ?? [], orders ?? [], limit, alias);

    [Fact]
    public void NoColumnsMeansSelectStar()
        => Assert.Equal("select *\n  from prismone.study;", SqlBuilder.Build(Spec()));

    [Fact]
    public void ListsChosenColumnsAndAliasesThem()
    {
        var sql = SqlBuilder.Build(Spec(columns: ["study_key", "modality"], alias: "s"));

        Assert.Equal("select s.study_key, s.modality\n  from prismone.study s;", sql);
    }

    [Fact]
    public void JoinsConditionsWithAnd()
    {
        var sql = SqlBuilder.Build(Spec(conditions:
        [
            new QueryCondition("modality", "=", "CT"),
            new QueryCondition("study_key", ">", "1000"),
        ]));

        Assert.Contains("\n where modality = 'CT'", sql);
        Assert.Contains("\n   and study_key > 1000", sql);
    }

    [Fact]
    public void QuotesTextButLeavesNumbersAndBindVariablesAlone()
    {
        var sql = SqlBuilder.Build(Spec(conditions:
        [
            new QueryCondition("note", "=", "it's fine"),
            new QueryCondition("study_key", "=", ":key"),
        ]));

        Assert.Contains("note = 'it''s fine'", sql);
        Assert.Contains("study_key = :key", sql);
    }

    [Fact]
    public void NullOperatorsTakeNoValue()
    {
        var sql = SqlBuilder.Build(Spec(conditions: [new QueryCondition("modality", "IS NULL", "ignored")]));

        Assert.Contains("modality is null", sql);
        Assert.DoesNotContain("ignored", sql);
    }

    [Fact]
    public void InSplitsCommaSeparatedValues()
    {
        var sql = SqlBuilder.Build(Spec(conditions: [new QueryCondition("modality", "IN", "CT, MR, 7")]));

        Assert.Contains("modality in ('CT', 'MR', 7)", sql);
    }

    [Fact]
    public void OrderByAndLimitAreAppended()
    {
        var sql = SqlBuilder.Build(Spec(
            orders: [new QueryOrder("study_dttm", true), new QueryOrder("study_key", false)],
            limit: 100));

        Assert.Contains("\n order by study_dttm desc, study_key", sql);
        Assert.EndsWith("\n limit 100;", sql);
    }

    [Fact]
    public void QuotesIdentifiersThatNeedIt()
    {
        var sql = SqlBuilder.Build(Spec(columns: ["Study Key"]));

        Assert.Contains("\"Study Key\"", sql);
    }

    [Fact]
    public void BlankColumnRowsAreIgnored()
    {
        var sql = SqlBuilder.Build(Spec(
            conditions: [new QueryCondition("  ", "=", "x")],
            orders: [new QueryOrder("", false)]));

        Assert.DoesNotContain("where", sql);
        Assert.DoesNotContain("order by", sql);
    }

    [Fact]
    public void RejectsMissingTableAndUnknownOperators()
    {
        Assert.Throws<ArgumentException>(() =>
            SqlBuilder.Build(new QuerySpec("  ", [], [], [])));
        Assert.Throws<ArgumentException>(() =>
            SqlBuilder.Build(Spec(conditions: [new QueryCondition("a", "; drop table t --", "1")])));
    }
}
