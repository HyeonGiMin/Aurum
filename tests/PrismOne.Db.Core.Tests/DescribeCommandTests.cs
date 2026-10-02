using PrismOne.Db.Core;
using PrismOne.Db.Core.Providers;
using Xunit;

namespace PrismOne.Db.Core.Tests;

public class DescribeCommandTests
{
    [Theory]
    [InlineData("desc study", "study")]
    [InlineData("DESC STUDY;", "STUDY")]
    [InlineData("describe prismone.study", "prismone.study")]
    [InlineData("  descr  prismone . study  ;  ", "prismone.study")]
    [InlineData("desc \"Mixed Case\".\"T;x\"", "\"Mixed Case\".\"T;x\"")]
    [InlineData("desc \"a\"\"b\"", "\"a\"\"b\"")]
    [InlineData("desc mydb.public.t", "mydb.public.t")]
    public void Parse_ReadsTarget(string sql, string target)
    {
        var parsed = DescribeCommand.Parse(sql);

        Assert.NotNull(parsed);
        Assert.Equal(target, parsed.Target);
    }

    [Theory]
    [InlineData("select * from t")]
    [InlineData("des t")]                     // DESC 보다 짧은 줄임은 SQL*Plus 도 안 받는다
    [InlineData("descending t")]              // DESCRIBE 의 줄임이 아닌 단어
    [InlineData("deschool t")]
    [InlineData("")]
    public void Parse_IgnoresOtherStatements(string sql)
        => Assert.Null(DescribeCommand.Parse(sql));

    [Theory]
    [InlineData("desc")]
    [InlineData("desc;")]
    [InlineData("desc a b")]
    [InlineData("desc a.")]
    [InlineData("desc a.b.c.d")]
    [InlineData("desc \"unterminated")]
    [InlineData("desc t'; drop table x; --")]
    public void Parse_RejectsMalformedTargetWithUsage(string sql)
    {
        var ex = Assert.Throws<FormatException>(() => DescribeCommand.Parse(sql));
        Assert.Contains("DESCRIBE", ex.Message);
    }

    [Theory]
    [InlineData("desc t /* 메모 */", "t")]
    [InlineData("desc /* x */ s . /* y */ t ;", "s.t")]
    public void Parse_SkipsBlockComments(string sql, string target)
        => Assert.Equal(target, DescribeCommand.Parse(sql)!.Target);

    [Fact]
    public void BuildQuery_Postgres_BindsRawTargetForRegclass()
    {
        var query = DescribeCommand.Parse("desc prismone.\"Study\"")!.BuildQuery(DbKind.PostgreSql)!;

        // 이름 해석(대소문자 접기·따옴표·search_path)은 regclass 입력 규칙에 맡긴다
        Assert.Contains("@target::text::pg_catalog.regclass", query.Sql);
        Assert.Equal([new("target", "prismone.\"Study\"")], query.Parameters);
        Assert.False(query.EmptyMeansMissing);
    }

    [Theory]
    [InlineData(DbKind.PostgreSql)]
    [InlineData(DbKind.Oracle)]
    [InlineData(DbKind.Sqlite)]
    public void BuildQuery_TargetIsBoundNotSpliced(DbKind kind)
    {
        // standard_conforming_strings=off 에서 \' 로 리터럴을 빠져나가는 입력 — 문장에 섞이면 안 된다
        var query = DescribeCommand.Parse("desc \"\\'; drop table aurum_victim; --\"")!.BuildQuery(kind)!;

        Assert.DoesNotContain("aurum_victim", query.Sql);
        Assert.Contains(query.Parameters, p => p.Value?.Contains("aurum_victim") == true);
    }

    [Theory]
    [InlineData("desc emp", null, "EMP")]                         // Oracle 은 대문자로 접는다
    [InlineData("desc scott.emp", "SCOTT", "EMP")]
    [InlineData("desc \"Scott\".\"MixedCase\"", "Scott", "MixedCase")]
    [InlineData("desc \"a\"\"b\"", null, "a\"b")]
    [InlineData("desc emp#hist", null, "EMP#HIST")]
    public void BuildQuery_Oracle_FoldsUnquotedNamesToUpper(string sql, string? owner, string name)
    {
        var query = DescribeCommand.Parse(sql)!.BuildQuery(DbKind.Oracle)!;

        Assert.Equal([new("owner", owner), new("name", name)], query.Parameters);
        Assert.True(query.EmptyMeansMissing);
        Assert.Contains("all_synonyms", query.Sql);   // 동의어(PUBLIC 포함)까지 따라간다
    }

    [Fact]
    public void BuildQuery_Sqlite_UsesPragmaTableInfoWithOptionalSchema()
    {
        var plain = DescribeCommand.Parse("desc Study")!.BuildQuery(DbKind.Sqlite)!;
        var qualified = DescribeCommand.Parse("desc main.\"Study\"")!.BuildQuery(DbKind.Sqlite)!;

        Assert.Contains("pragma_table_info(@name)", plain.Sql);
        Assert.Equal([new("@name", "Study")], plain.Parameters);
        Assert.Contains("pragma_table_info(@name, @schema)", qualified.Sql);
        Assert.Equal([new("@name", "Study"), new("@schema", "main")], qualified.Parameters);
    }

    [Theory]
    [InlineData(DbKind.Oracle)]
    [InlineData(DbKind.Sqlite)]
    public void BuildQuery_ThreePartNameIsUsageErrorOutsidePostgres(DbKind kind)
    {
        var describe = DescribeCommand.Parse("desc a.b.c")!;

        Assert.Throws<FormatException>(() => describe.BuildQuery(kind));
    }

    [Fact]
    public void BuildQuery_Mongo_IsNotSupported()
        => Assert.Null(DescribeCommand.Parse("desc users")!.BuildQuery(DbKind.MongoDb));

    [Theory]
    [InlineData("desc t", 4)]
    [InlineData("  describe x", 10)]
    [InlineData("desc/* c */t", 4)]
    [InlineData("select 1", -1)]
    public void KeywordEnd_PointsPastCommandWord(string sql, int expected)
        => Assert.Equal(expected, DescribeCommand.KeywordEnd(sql));

    [Theory]
    [InlineData("desc t", true)]
    [InlineData("  DESCRIBE x.y", true)]
    [InlineData("descending", false)]
    [InlineData("select 1", false)]
    public void IsCommandStart_DetectsKeyword(string sql, bool expected)
        => Assert.Equal(expected, DescribeCommand.IsCommandStart(sql, sql.Length - sql.TrimStart().Length));

    [Fact]
    public void DescribeIsReadOnly_SoManualCommitDoesNotOpenTransaction()
        => Assert.True(QuerySession.IsReadOnlyStatement("desc study"));
}
