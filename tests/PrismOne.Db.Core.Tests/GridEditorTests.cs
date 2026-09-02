using PrismOne.Db.Core;
using PrismOne.Db.Core.Providers;
using Xunit;

namespace PrismOne.Db.Core.Tests;

public class GridEditorPrepareTests
{
    private static readonly IDbProvider Pg = new PostgresProvider();
    private static readonly IDbProvider Oracle = new OracleProvider();
    private static readonly IDbProvider Sqlite = new SqliteProvider();
    private static readonly IDbProvider Mongo = new MongoProvider();

    [Fact]
    public void AddsCtidColumnForSimpleSelect()
    {
        var prepared = GridEditor.Prepare("select study_key, modality from prismone.study", Pg);

        Assert.NotNull(prepared);
        Assert.Equal("prismone.study", prepared!.Table);
        Assert.Equal(
            $"select prismone.study.ctid::text as \"{GridEditor.RowIdColumn}\", study_key, modality from prismone.study",
            prepared.Sql);
    }

    [Fact]
    public void QualifiesCtidWithTableAlias()
    {
        var prepared = GridEditor.Prepare("select s.* from prismone.study s where s.modality = 'CT'", Pg);

        Assert.NotNull(prepared);
        Assert.Contains($"s.ctid::text as \"{GridEditor.RowIdColumn}\"", prepared!.Sql);
        Assert.Equal("prismone.study", prepared.Table);
    }

    [Fact]
    public void TreatsClauseKeywordAfterTableAsClauseNotAlias()
    {
        var prepared = GridEditor.Prepare("select * from study where study_key = 1", Pg);

        Assert.NotNull(prepared);
        Assert.Contains("study.ctid::text", prepared!.Sql);
    }

    [Fact]
    public void AddsRowidForOracle()
    {
        var prepared = GridEditor.Prepare("select * from scott.emp e where e.deptno = 10", Oracle);

        Assert.NotNull(prepared);
        Assert.Equal("scott.emp", prepared!.Table);
        Assert.Contains($"ROWIDTOCHAR(e.ROWID) as \"{GridEditor.RowIdColumn}\"", prepared.Sql);
    }

    [Fact]
    public void QualifiesBareStarSoOracleAcceptsTheExtraColumn()
    {
        // Oracle 은 SELECT expr, * FROM t 를 거부한다 (ORA-00936) — t.* 로 한정해야 한다
        var prepared = GridEditor.Prepare("select * from emp", Oracle);

        Assert.NotNull(prepared);
        Assert.EndsWith("emp.* from emp", prepared!.Sql);
    }

    [Fact]
    public void AddsRowidForSqlite()
    {
        var prepared = GridEditor.Prepare("select * from study", Sqlite);

        Assert.NotNull(prepared);
        Assert.Contains($"CAST(study.rowid AS TEXT) as \"{GridEditor.RowIdColumn}\"", prepared!.Sql);
    }

    [Fact]
    public void RefusesProviderWithoutGridEditing()
        => Assert.Null(GridEditor.Prepare("select * from study", Mongo));

    [Theory]
    [InlineData("select distinct modality from study")]
    [InlineData("select a.*, b.* from a join b on a.id = b.id")]
    [InlineData("select modality, count(*) from study group by modality")]
    [InlineData("select * from a, b")]
    [InlineData("select * from (select 1) t")]
    [InlineData("select 1")]
    [InlineData("update study set modality = 'CT'")]
    [InlineData("select * from a union select * from b")]
    [InlineData("select * from a; select * from b;")]
    public void RefusesQueriesWhoseRowsCannotBeIdentified(string sql)
        => Assert.Null(GridEditor.Prepare(sql, Pg));

    [Theory]
    [InlineData("select * from a minus select * from b")]
    [InlineData("select * from emp start with mgr is null connect by prior empno = mgr")]
    public void RefusesOracleSetAndHierarchicalQueries(string sql)
        => Assert.Null(GridEditor.Prepare(sql, Oracle));
}

public class GridEditorPasteTests
{
    // 편집 모드의 컬럼 배치: 0번은 행 식별자 자리라 비워 둔다
    private static readonly string[] Columns = [GridEditor.RowIdColumn, "study_id", "modality"];

    [Fact]
    public void ParsesTabSeparatedLinesIntoRows()
    {
        var rows = GridEditor.ParsePaste("ST-1\tCT\nST-2\tMR\n", Columns, offset: 1);

        Assert.Equal(2, rows.Count);
        Assert.Equal<string?[]>([null, "ST-1", "CT"], rows[0]);
        Assert.Equal<string?[]>([null, "ST-2", "MR"], rows[1]);
    }

    [Fact]
    public void SkipsHeaderLineThatMatchesColumnNames()
    {
        var rows = GridEditor.ParsePaste("study_id\tmodality\nST-1\tCT", Columns, offset: 1);

        Assert.Single(rows);
        Assert.Equal("ST-1", rows[0][1]);
    }

    [Fact]
    public void KeepsFirstLineWhenItIsData()
    {
        var rows = GridEditor.ParsePaste("ST-1\tCT", Columns, offset: 1);

        Assert.Single(rows);
    }

    [Fact]
    public void IgnoresExtraValuesAndBlankLines()
    {
        var rows = GridEditor.ParsePaste("ST-1\tCT\textra\n\nST-2\t\n", Columns, offset: 1);

        Assert.Equal(2, rows.Count);
        Assert.Equal("", rows[1][2]);   // 빈 셀은 빈 문자열 → INSERT 에서 제외되어 기본값/NULL
    }

    [Fact]
    public void EmptyClipboardYieldsNoRows()
        => Assert.Empty(GridEditor.ParsePaste("", Columns, offset: 1));
}

public class GridEditorBuildTests
{
    private const string Table = "prismone.study";
    private static readonly IDbProvider Pg = new PostgresProvider();
    private static readonly IDbProvider Oracle = new OracleProvider();
    private static readonly IDbProvider Sqlite = new SqliteProvider();

    [Fact]
    public void UpdateSetsChangedCellsAndKeysByCtid()
    {
        var change = new GridChange.Update("(0,7)", [("modality", "MR"), ("note", null)]);

        var statement = Assert.Single(GridEditor.Build(Table, [change], Pg));

        Assert.Equal(
            "UPDATE prismone.study SET \"modality\" = $1, \"note\" = $2 WHERE ctid = $3::tid",
            statement.Sql);
        Assert.Equal(["MR", null, "(0,7)"], statement.Parameters);
    }

    [Fact]
    public void DeleteKeysByCtid()
    {
        var statement = Assert.Single(GridEditor.Build(Table, [new GridChange.Delete("(3,2)")], Pg));

        Assert.Equal("DELETE FROM prismone.study WHERE ctid = $1::tid", statement.Sql);
        Assert.Equal(["(3,2)"], statement.Parameters);
    }

    [Fact]
    public void InsertListsOnlyFilledColumns()
    {
        var change = new GridChange.Insert([("study_id", "ST-0007"), ("modality", "CT")]);

        var statement = Assert.Single(GridEditor.Build(Table, [change], Pg));

        Assert.Equal(
            "INSERT INTO prismone.study (\"study_id\", \"modality\") VALUES ($1, $2)",
            statement.Sql);
        Assert.Equal(["ST-0007", "CT"], statement.Parameters);
    }

    [Fact]
    public void OracleUpdateKeysByRowid()
    {
        var change = new GridChange.Update("AAAB12AAJAAAAcVAAA", [("ENAME", "KIM")]);

        var statement = Assert.Single(GridEditor.Build("scott.emp", [change], Oracle));

        Assert.Equal(
            "UPDATE scott.emp SET \"ENAME\" = :p1 WHERE ROWID = CHARTOROWID(:p2)",
            statement.Sql);
        Assert.Equal(["KIM", "AAAB12AAJAAAAcVAAA"], statement.Parameters);
    }

    [Fact]
    public void OracleDeleteAndInsertUseNamedPlaceholders()
    {
        GridChange[] changes =
        [
            new GridChange.Delete("AAAB12AAJAAAAcVAAB"),
            new GridChange.Insert([("EMPNO", "7999"), ("ENAME", "LEE")]),
        ];

        var statements = GridEditor.Build("scott.emp", changes, Oracle);

        Assert.Equal("DELETE FROM scott.emp WHERE ROWID = CHARTOROWID(:p1)", statements[0].Sql);
        Assert.Equal("INSERT INTO scott.emp (\"EMPNO\", \"ENAME\") VALUES (:p1, :p2)", statements[1].Sql);
    }

    [Fact]
    public void SqliteUpdateKeysByRowid()
    {
        var change = new GridChange.Update("42", [("modality", "MR")]);

        var statement = Assert.Single(GridEditor.Build("study", [change], Sqlite));

        Assert.Equal("UPDATE study SET \"modality\" = @p1 WHERE rowid = @p2", statement.Sql);
    }

    [Fact]
    public void QuotesIdentifiersSoReservedOrMixedCaseColumnsSurvive()
    {
        var change = new GridChange.Update("(0,1)", [("Order", "1")]);

        var statement = Assert.Single(GridEditor.Build(Table, [change], Pg));

        Assert.Contains("\"Order\" = $1", statement.Sql);
    }

    [Fact]
    public void OrdersUpdatesThenDeletesThenInserts()
    {
        GridChange[] changes =
        [
            new GridChange.Insert([("study_id", "ST-1")]),
            new GridChange.Delete("(0,1)"),
            new GridChange.Update("(0,2)", [("modality", "CT")]),
        ];

        var statements = GridEditor.Build(Table, changes, Pg);

        Assert.StartsWith("UPDATE", statements[0].Sql);
        Assert.StartsWith("DELETE", statements[1].Sql);
        Assert.StartsWith("INSERT", statements[2].Sql);
    }

    [Fact]
    public void RejectsEmptyChangeSets()
    {
        Assert.Throws<ArgumentException>(() => GridEditor.Build(Table, [new GridChange.Update("(0,1)", [])], Pg));
        Assert.Throws<ArgumentException>(() => GridEditor.Build(Table, [new GridChange.Insert([])], Pg));
    }
}
