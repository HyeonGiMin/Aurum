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
        string? alias = null,
        IReadOnlyList<QueryJoin>? joins = null,
        IReadOnlyList<QueryAggregate>? aggregates = null,
        bool distinct = false,
        SqlDialect dialect = SqlDialect.Standard) =>
        new("prismone.study", columns ?? [], conditions ?? [], orders ?? [], limit, alias,
            joins, aggregates, distinct, dialect);

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

    // ---------- 조인 ----------

    private static QueryJoin PatientJoin(JoinKind kind = JoinKind.Inner) =>
        new("prismone.patient", "p", kind, [new JoinOn("s.patient_key", "p.patient_key")]);

    [Fact]
    public void JoinRendersOnClauseWithQualifiedColumns()
    {
        var sql = SqlBuilder.Build(Spec(
            columns: ["s.study_id", "p.patient_id"], alias: "s", joins: [PatientJoin()]));

        Assert.Equal(
            "select s.study_id, p.patient_id\n"
            + "  from prismone.study s\n"
            + "  join prismone.patient p\n"
            + "    on s.patient_key = p.patient_key;",
            sql);
    }

    [Theory]
    [InlineData(JoinKind.Inner, "join")]
    [InlineData(JoinKind.Left, "left join")]
    [InlineData(JoinKind.Right, "right join")]
    [InlineData(JoinKind.Full, "full join")]
    public void JoinKindPicksTheKeyword(JoinKind kind, string keyword)
    {
        var sql = SqlBuilder.Build(Spec(alias: "s", joins: [PatientJoin(kind)]));

        Assert.Contains($"  {keyword} prismone.patient p", sql);
    }

    [Fact]
    public void MultipleOnPairsAreAndedTogether()
    {
        var join = new QueryJoin("prismone.exam", "e", JoinKind.Left,
            [new JoinOn("s.study_key", "e.study_key"), new JoinOn("s.site", "e.site")]);

        var sql = SqlBuilder.Build(Spec(alias: "s", joins: [join]));

        Assert.Contains("    on s.study_key = e.study_key\n   and s.site = e.site", sql);
    }

    [Fact]
    public void JoinWithoutOnPairsBecomesAPlainCrossJoin()
    {
        var join = new QueryJoin("prismone.patient", "p", JoinKind.Inner, []);

        var sql = SqlBuilder.Build(Spec(alias: "s", joins: [join]));

        Assert.Contains("join prismone.patient p", sql);
        Assert.DoesNotContain(" on ", sql);
    }

    [Fact]
    public void StarIsNotAliasQualifiedOnceAJoinExists()
    {
        // s.* 로 좁히면 조인해 온 컬럼이 사라진다 — 아무것도 안 골랐으면 전체를 준다
        var sql = SqlBuilder.Build(Spec(alias: "s", joins: [PatientJoin()]));

        Assert.StartsWith("select *\n", sql);
    }

    [Fact]
    public void QualifiedNamesSurviveInWhereAndOrderBy()
    {
        var sql = SqlBuilder.Build(Spec(
            alias: "s",
            joins: [PatientJoin()],
            conditions: [new QueryCondition("p.patient_id", "=", "P0001")],
            orders: [new QueryOrder("p.patient_id", true)]));

        Assert.Contains("where p.patient_id = 'P0001'", sql);
        Assert.Contains("order by p.patient_id desc", sql);
    }

    // ---------- 집계 · distinct · 방언 ----------

    [Fact]
    public void CountWithoutColumnBecomesCountStar()
    {
        var sql = SqlBuilder.Build(Spec(aggregates: [new QueryAggregate("count", null, null)]));

        Assert.Equal("select count(*)\n  from prismone.study;", sql);
    }

    [Fact]
    public void AggregateAliasIsEmitted()
    {
        var sql = SqlBuilder.Build(Spec(
            alias: "s", aggregates: [new QueryAggregate("sum", "s.amount", "total")]));

        Assert.Contains("select sum(s.amount) as total", sql);
    }

    [Fact]
    public void ChosenColumnsBecomeGroupByWhenAggregatesExist()
    {
        var sql = SqlBuilder.Build(Spec(
            columns: ["modality"], alias: "s",
            aggregates: [new QueryAggregate("count", null, "cnt")]));

        Assert.Equal(
            "select s.modality, count(*) as cnt\n  from prismone.study s\n group by s.modality;",
            sql);
    }

    [Fact]
    public void NoGroupByWithoutAggregates()
        => Assert.DoesNotContain("group by", SqlBuilder.Build(Spec(columns: ["modality"])));

    [Fact]
    public void RejectsUnknownAggregateAndColumnlessSum()
    {
        Assert.Throws<ArgumentException>(() =>
            SqlBuilder.Build(Spec(aggregates: [new QueryAggregate("drop", "x", null)])));
        Assert.Throws<ArgumentException>(() =>
            SqlBuilder.Build(Spec(aggregates: [new QueryAggregate("sum", null, null)])));
    }

    [Fact]
    public void DistinctGoesRightAfterSelect()
        => Assert.StartsWith("select distinct s.modality",
            SqlBuilder.Build(Spec(columns: ["modality"], alias: "s", distinct: true)));

    [Fact]
    public void OracleUsesFetchFirstInsteadOfLimit()
    {
        Assert.Contains("\n fetch first 50 rows only;",
            SqlBuilder.Build(Spec(limit: 50, dialect: SqlDialect.Oracle)));
        Assert.Contains("\n limit 50;", SqlBuilder.Build(Spec(limit: 50)));
    }

    [Fact]
    public void SpacesAroundTheDotAreNotPartOfTheIdentifier()
    {
        // ON 은 손으로 적을 수 있다 — "s . patient_key" 가 "s "·" patient_key" 로 굳으면 안 된다
        var join = new QueryJoin("prismone.patient", "p", JoinKind.Inner,
            [new JoinOn("s . patient_key", "p . patient_key")]);

        var sql = SqlBuilder.Build(Spec(alias: "s", joins: [join]));

        Assert.Contains("on s.patient_key = p.patient_key", sql);
    }

    [Fact]
    public void MixedCaseIdentifiersStayQuotedOnBothSidesOfADot()
    {
        var sql = SqlBuilder.Build(Spec(columns: ["S.Study Key"], alias: "s"));

        Assert.Contains("select \"S\".\"Study Key\"", sql);
    }
}
