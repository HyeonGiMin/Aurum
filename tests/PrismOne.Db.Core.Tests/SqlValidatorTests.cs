using PrismOne.Db.Core;
using Xunit;

namespace PrismOne.Db.Core.Tests;

/// <summary>
/// SQL 검증 (DATAGRIP_GAP §2) — 원칙은 "확신할 때만 표시".
/// 오탐(멀쩡한 SQL 에 밑줄)이 하나라도 나면 기능을 끄게 되므로,
/// 놓침(미탐)을 허용하는 쪽 테스트가 절반이다.
/// </summary>
public sealed class SqlValidatorTests
{
    private static readonly SchemaSnapshot Snapshot = new(
        [
            new TableInfo("prismone", "study", false),
            new TableInfo("prismone", "patient", false),
            new TableInfo("public", "examlist", false),
        ],
        new Dictionary<string, List<ColumnInfo>>(StringComparer.Ordinal)
        {
            ["prismone.study"] =
            [
                new(1, "study_key", "bigint", "no", "P1", ""),
                new(2, "study_dttm", "timestamp", "yes", "", ""),
                new(3, "patient_key", "bigint", "no", "", "F1"),
            ],
            ["prismone.patient"] =
            [
                new(1, "patient_key", "bigint", "no", "P1", ""),
                new(2, "patient_id", "varchar", "no", "", ""),
            ],
            ["public.examlist"] = [new(1, "exam_key", "bigint", "no", "P1", "")],
        });

    private static List<SqlIssue> Validate(string sql) => SqlValidator.Validate(sql, Snapshot);

    // ---------- 잡아야 하는 것 ----------

    [Fact]
    public void FlagsUnknownTable()
    {
        var issues = Validate("select * from prismone.stduy");

        var issue = Assert.Single(issues);
        Assert.Equal("stduy", "select * from prismone.stduy".Substring(issue.Start, issue.Length));
        Assert.Contains("stduy", issue.Message);
    }

    [Fact]
    public void FlagsUnknownColumnThroughAlias()
    {
        var sql = "select s.study_dtm from prismone.study s";
        var issues = Validate(sql);

        var issue = Assert.Single(issues);
        Assert.Equal("study_dtm", sql.Substring(issue.Start, issue.Length));
    }

    [Fact]
    public void FlagsUnknownTableInJoin()
        => Assert.Single(Validate(
            "select * from prismone.study s join prismone.patinet p on p.x = s.patient_key"));

    [Fact]
    public void FlagsBothTablesInCommaJoin()
    {
        // Golden 세대 SQL 은 옛날식 콤마 조인이 많다 — 둘 다 봐야 한다
        var issues = Validate("select * from prismone.stduy s, prismone.patinet p");

        Assert.Equal(2, issues.Count);
    }

    [Fact]
    public void FlagsUpdateAndInsertTargets()
    {
        Assert.Single(Validate("update prismone.stduy set study_dttm = null"));
        Assert.Single(Validate("insert into prismone.stduy values (1)"));
    }

    [Fact]
    public void ChecksColumnsOfEveryReferencedTable()
    {
        var issues = Validate(
            "select s.study_key, p.patient_nm from prismone.study s join prismone.patient p on p.patient_key = s.patient_key");

        var issue = Assert.Single(issues);
        Assert.Contains("patient_nm", issue.Message);
    }

    // ---------- 조용히 넘어가야 하는 것 (오탐 방지) ----------

    [Fact]
    public void ValidSqlHasNoIssues()
        => Assert.Empty(Validate("""
            select s.study_key, s.study_dttm, p.patient_id
              from prismone.study s
              join prismone.patient p on p.patient_key = s.patient_key
             where s.study_dttm >= '2026-07-01'
             order by s.study_dttm desc
            """));

    [Fact]
    public void UnqualifiedTableResolvesAcrossSchemas()
        => Assert.Empty(Validate("select e.exam_key from examlist e"));

    [Fact]
    public void CteNameIsNotATable()
        => Assert.Empty(Validate("""
            with recent as (select study_key from prismone.study)
            select r.anything from recent r
            """));

    [Fact]
    public void DerivedTableAliasIsSilent()
        => Assert.Empty(Validate("select t.whatever from (select 1 as one) t"));

    [Fact]
    public void FunctionInFromIsNotATable()
        => Assert.Empty(Validate("select * from generate_series(1, 10)"));

    [Fact]
    public void ExtractStyleFromIsIgnored()
        => Assert.Empty(Validate(
            "select extract(year from s.study_dttm) from prismone.study s"));

    [Fact]
    public void UnknownSchemaIsNotJudged()
        => Assert.Empty(Validate("select * from pg_catalog.pg_tables"));

    [Fact]
    public void BuiltinTablesAreSilent()
    {
        Assert.Empty(Validate("select sysdate from dual"));
        Assert.Empty(Validate("select * from pg_stat_activity"));
        Assert.Empty(Validate("select * from sqlite_master"));
    }

    [Fact]
    public void CommentsAndStringsAreMasked()
        => Assert.Empty(Validate("""
            -- from nowhere_table
            /* select x.bad from ghost x */
            select 'from missing_tbl', s.study_key from prismone.study s
            """));

    [Fact]
    public void QuotedIdentifiersAreNotValidated()
        => Assert.Empty(Validate("select * from \"MyCamelTable\""));

    [Fact]
    public void UnresolvedQualifierIsSilent()
        // ghost 는 FROM 에 없다 — 별칭 해석이 안 되면 컬럼도 판단하지 않는다
        => Assert.Empty(Validate("select ghost.col from prismone.study s"));

    [Fact]
    public void StarAndSchemaQualifiedColumnsAreSilent()
        => Assert.Empty(Validate(
            "select s.*, prismone.study.study_key from prismone.study s"));

    [Fact]
    public void ForUpdateIsNotAnUpdateStatement()
        => Assert.Empty(Validate("select s.study_key from prismone.study s for update"));

    [Fact]
    public void EmptySchemaMeansNoValidation()
        => Assert.Empty(SqlValidator.Validate("select * from ghost", SchemaSnapshot.Empty));

    [Fact]
    public void UnknownColumnOfUnknownTableIsNotDoubleReported()
    {
        // 테이블이 이미 밑줄이면 그 별칭의 컬럼까지 겹쳐 알리지 않는다
        var issues = Validate("select x.col from prismone.stduy x");

        var issue = Assert.Single(issues);
        Assert.Contains("stduy", issue.Message);
    }

    [Fact]
    public void MaskKeepsOffsets()
    {
        var sql = "select '한글 문자열' from prismone.stduy";
        var issues = Validate(sql);

        var issue = Assert.Single(issues);
        Assert.Equal("stduy", sql.Substring(issue.Start, issue.Length));
    }
}
