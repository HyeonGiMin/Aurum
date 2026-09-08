using PrismOne.Db.Core;

namespace PrismOne.Studio.Tests;

/// <summary>
/// 자동완성이 "지금 커서 자리에 무엇을 제안할지" 고르는 판단. 화면 없이 도는 부분이라
/// 여기서 실물로 검증한다 — 전에는 Db.Core.Tests 가 정규식 사본을 검사해서
/// 진짜 코드가 바뀌어도 테스트가 안 깨졌다.
/// </summary>
public class SqlCompletionTests
{
    private static readonly List<TableInfo> Tables =
    [
        new("prismone", "study", false),
        new("prismone", "patient", false),
        new("prismone", "v_study_summary", true),
        new("public", "study", false),
    ];

    /// <summary>커서를 문장 끝에 둔다고 보고 단어 시작 위치를 구한다 (뷰가 하는 계산과 같다).</summary>
    private static int WordStart(string sql)
    {
        var at = sql.Length;
        while (at > 0 && (char.IsLetterOrDigit(sql[at - 1]) || sql[at - 1] == '_'))
            at--;
        return at;
    }

    // ---------- 별칭 해석 ----------

    [Fact]
    public void ExtractAliasesMapsAliasToItsTable()
    {
        var aliases = SqlCompletion.ExtractAliases(
            "select * from prismone.study s join prismone.patient p on 1=1");

        Assert.Equal(("prismone", "study"), aliases["s"]);
        Assert.Equal(("prismone", "patient"), aliases["p"]);
    }

    [Fact]
    public void AsKeywordFormIsUnderstood()
    {
        var aliases = SqlCompletion.ExtractAliases("select * from prismone.study as s");

        Assert.Equal(("prismone", "study"), aliases["s"]);
    }

    [Fact]
    public void TableWithoutAliasStillResolvesByItsOwnName()
    {
        var table = SqlCompletion.ResolveTable(Tables, "study", "select * from prismone.study");

        Assert.NotNull(table);
        Assert.Equal("study", table!.Name);
    }

    [Fact]
    public void AliasResolvesToTheTableItStandsFor()
    {
        var table = SqlCompletion.ResolveTable(Tables, "p", "select * from prismone.patient p");

        Assert.Equal("patient", table!.Name);
    }

    [Fact]
    public void ClauseKeywordAfterTableIsNotMistakenForAnAlias()
    {
        // "from prismone.study where ..." 에서 where 가 별칭으로 잡히면 안 된다
        var aliases = SqlCompletion.ExtractAliases("select * from prismone.study where x = 1");

        Assert.False(aliases.ContainsKey("where"));
    }

    // ---------- 위치 판정 ----------

    [Theory]
    [InlineData("select * from ")]
    [InlineData("select * from prismone.study s join ")]
    [InlineData("insert into ")]
    [InlineData("update ")]
    public void TablePositionIsRecognisedAfterFromJoinIntoUpdate(string sql)
        => Assert.True(SqlCompletion.IsTablePosition(sql, WordStart(sql)));

    [Theory]
    [InlineData("select ")]
    [InlineData("select * from study where ")]
    public void OtherPlacesAreNotTablePositions(string sql)
        => Assert.False(SqlCompletion.IsTablePosition(sql, WordStart(sql)));

    [Theory]
    [InlineData("select * from study where ")]
    [InlineData("select * from study where a = 1 and ")]
    [InlineData("select * from a join b on ")]
    public void ColumnPositionIsRecognisedAfterWhereAndOn(string sql)
        => Assert.True(SqlCompletion.IsColumnPosition(sql, WordStart(sql)));

    // ---------- 후보 목록 ----------

    [Fact]
    public void TablesOnlyListsTablesAndViewsWithoutKeywords()
    {
        var items = SqlCompletion.TablesOnly(Tables);

        Assert.All(items, i => Assert.NotEqual(SqlCompletionKind.Keyword, i.Kind));
        Assert.Contains(items, i => i.Text == "study");
        Assert.Contains(items, i => i.Kind == SqlCompletionKind.View);
    }

    [Fact]
    public void GeneralListIncludesKeywords()
        => Assert.Contains(SqlCompletion.General(Tables), i => i.Kind == SqlCompletionKind.Keyword);

    [Fact]
    public void SchemaQualifierNarrowsToThatSchema()
    {
        var items = SqlCompletion.SchemaTables(Tables, "prismone");

        Assert.NotNull(items);
        Assert.Contains(items!, i => i.Text == "patient");
        Assert.All(items!, i => Assert.NotEqual(SqlCompletionKind.Keyword, i.Kind));
    }

    [Fact]
    public void UnknownQualifierIsNotASchemaSoColumnsAreTriedInstead()
        => Assert.Null(SqlCompletion.SchemaTables(Tables, "notaschema"));

    [Fact]
    public void ReferencedTablesFindsWhatTheStatementActuallyUses()
    {
        var used = SqlCompletion.ReferencedTables(
            Tables, "select * from prismone.study s join prismone.patient p on 1=1");

        Assert.Contains(used, t => t.Name == "study");
        Assert.Contains(used, t => t.Name == "patient");
    }
}
