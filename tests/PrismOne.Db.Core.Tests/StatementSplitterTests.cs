using PrismOne.Db.Core;
using Xunit;

namespace PrismOne.Db.Core.Tests;

public class StatementSplitterTests
{
    [Fact]
    public void Split_TwoStatements()
    {
        var stmts = StatementSplitter.Split("SELECT 1; SELECT 2;");
        Assert.Equal(2, stmts.Count);
        Assert.Equal("SELECT 1", stmts[0].Text);
        Assert.Equal("SELECT 2", stmts[1].Text);
    }

    [Fact]
    public void Split_LastStatementWithoutSemicolon()
    {
        var stmts = StatementSplitter.Split("SELECT 1;\nSELECT 2");
        Assert.Equal(2, stmts.Count);
        Assert.Equal("SELECT 2", stmts[1].Text);
    }

    [Fact]
    public void Split_SemicolonInsideSingleQuotes()
    {
        var stmts = StatementSplitter.Split("SELECT 'a;b'; SELECT 2;");
        Assert.Equal(2, stmts.Count);
        Assert.Equal("SELECT 'a;b'", stmts[0].Text);
    }

    [Fact]
    public void Split_EscapedQuoteByDoubling()
    {
        var stmts = StatementSplitter.Split("SELECT 'it''s; fine'; SELECT 2;");
        Assert.Equal(2, stmts.Count);
        Assert.Equal("SELECT 'it''s; fine'", stmts[0].Text);
    }

    [Fact]
    public void Split_EStringBackslashEscape()
    {
        var stmts = StatementSplitter.Split(@"SELECT E'a\';b'; SELECT 2;");
        Assert.Equal(2, stmts.Count);
        Assert.Equal(@"SELECT E'a\';b'", stmts[0].Text);
    }

    [Fact]
    public void Split_SemicolonInsideDollarQuote()
    {
        var sql = """
            CREATE FUNCTION f() RETURNS void AS $$
            BEGIN
              PERFORM 1;
            END;
            $$ LANGUAGE plpgsql;
            SELECT 2;
            """;
        var stmts = StatementSplitter.Split(sql);
        Assert.Equal(2, stmts.Count);
        Assert.StartsWith("CREATE FUNCTION", stmts[0].Text);
        Assert.Contains("PERFORM 1;", stmts[0].Text);
        Assert.Equal("SELECT 2", stmts[1].Text);
    }

    [Fact]
    public void Split_TaggedDollarQuote()
    {
        var stmts = StatementSplitter.Split("SELECT $body$x; $$ y$body$; SELECT 2;");
        Assert.Equal(2, stmts.Count);
        Assert.Contains("$body$x; $$ y$body$", stmts[0].Text);
    }

    [Fact]
    public void Split_LineCommentWithSemicolon()
    {
        var stmts = StatementSplitter.Split("SELECT 1 -- comment; not a split\n+ 2; SELECT 3;");
        Assert.Equal(2, stmts.Count);
        Assert.Equal("SELECT 3", stmts[1].Text);
    }

    [Fact]
    public void Split_NestedBlockComment()
    {
        var stmts = StatementSplitter.Split("SELECT 1 /* outer ; /* inner ; */ still; */; SELECT 2;");
        Assert.Equal(2, stmts.Count);
        Assert.Equal("SELECT 2", stmts[1].Text);
    }

    [Fact]
    public void Split_DollarParameterIsNotDollarQuote()
    {
        // $1 파라미터는 달러쿼트가 아니다
        var stmts = StatementSplitter.Split("SELECT $1; SELECT 2;");
        Assert.Equal(2, stmts.Count);
    }

    [Fact]
    public void Split_EmptyAndCommentOnlyInputs()
    {
        Assert.Empty(StatementSplitter.Split(""));
        Assert.Empty(StatementSplitter.Split("   \n  "));
        Assert.Empty(StatementSplitter.Split("-- only a comment\n"));
        Assert.Empty(StatementSplitter.Split(";;;"));
    }

    [Fact]
    public void StatementAt_CaretInsideStatement()
    {
        var sql = "SELECT 1;\nSELECT 22;\nSELECT 333;";
        var stmt = StatementSplitter.StatementAt(sql, sql.IndexOf("22"));
        Assert.NotNull(stmt);
        Assert.Equal("SELECT 22", stmt!.Text);
    }

    [Fact]
    public void StatementAt_CaretRightAfterSemicolon()
    {
        var sql = "SELECT 1;\nSELECT 2;";
        var stmt = StatementSplitter.StatementAt(sql, sql.IndexOf(';') + 1);
        Assert.Equal("SELECT 1", stmt!.Text);
    }

    [Fact]
    public void StatementAt_CaretInGapPicksPrevious()
    {
        var sql = "SELECT 1;\n\n\nSELECT 2;";
        var stmt = StatementSplitter.StatementAt(sql, sql.IndexOf("1;") + 3);
        Assert.Equal("SELECT 1", stmt!.Text);
    }

    [Fact]
    public void StatementAt_EmptyText()
    {
        Assert.Null(StatementSplitter.StatementAt("", 0));
    }

    [Fact]
    public void StatementAt_CaretAtEndOfUnterminatedStatement()
    {
        var sql = "SELECT 1;\nSELECT 2";
        var stmt = StatementSplitter.StatementAt(sql, sql.Length);
        Assert.Equal("SELECT 2", stmt!.Text);
    }

    // ---- Oracle PL/SQL 블록 (oracleBlocks: true) — SQL*Plus/Golden 관례 ----

    [Fact]
    public void Split_OracleBlocks_KeepsInternalSemicolons_InBeginEndBlock()
    {
        var sql = "begin\n  dbms_output.put_line('a');\n  dbms_output.put_line('b');\nend;\n/";
        var stmts = StatementSplitter.Split(sql, oracleBlocks: true);

        Assert.Single(stmts);
        Assert.Contains("put_line('a')", stmts[0].Text);
        Assert.Contains("put_line('b')", stmts[0].Text);
        Assert.EndsWith("end;", stmts[0].Text);
    }

    [Fact]
    public void Split_OracleBlocks_DeclareBlock()
    {
        var sql = "declare\n  x number := 1;\nbegin\n  x := x + 1;\nend;\n/";
        var stmts = StatementSplitter.Split(sql, oracleBlocks: true);

        Assert.Single(stmts);
        Assert.StartsWith("declare", stmts[0].Text);
    }

    [Fact]
    public void Split_OracleBlocks_CreateOrReplaceProcedure()
    {
        var sql = """
            create or replace procedure p1 as
            begin
              null;
            end;
            /
            """;
        var stmts = StatementSplitter.Split(sql, oracleBlocks: true);

        Assert.Single(stmts);
        Assert.Contains("procedure p1", stmts[0].Text);
        Assert.Contains("null;", stmts[0].Text);
    }

    [Fact]
    public void Split_OracleBlocks_CreatePackageBody_TriggersBlockMode()
    {
        var sql = "create or replace package body pkg is\n  procedure p is begin null; end;\nend pkg;\n/";
        var stmts = StatementSplitter.Split(sql, oracleBlocks: true);

        Assert.Single(stmts);   // PACKAGE 로 이미 블록 판정 — 안의 여러 ';' 가 안 쪼갠다
    }

    [Fact]
    public void Split_OracleBlocks_MultipleBlocksInSequence()
    {
        var sql = "begin\n  null;\nend;\n/\nbegin\n  null;\nend;\n/";
        var stmts = StatementSplitter.Split(sql, oracleBlocks: true);

        Assert.Equal(2, stmts.Count);
    }

    [Fact]
    public void Split_OracleBlocks_PlainSqlStillSplitsBySemicolon()
    {
        var sql = "select 1 from dual; select 2 from dual;";
        var stmts = StatementSplitter.Split(sql, oracleBlocks: true);

        Assert.Equal(2, stmts.Count);
        Assert.Equal("select 1 from dual", stmts[0].Text);
    }

    [Fact]
    public void Split_OracleBlocks_MixedPlainSqlAndPlSqlBlock()
    {
        var sql = "select 1 from dual;\nbegin\n  dbms_output.put_line('x');\nend;\n/\nselect 2 from dual;";
        var stmts = StatementSplitter.Split(sql, oracleBlocks: true);

        Assert.Equal(3, stmts.Count);
        Assert.Equal("select 1 from dual", stmts[0].Text);
        Assert.Contains("put_line", stmts[1].Text);
        Assert.Equal("select 2 from dual", stmts[2].Text);
    }

    [Fact]
    public void Split_OracleBlocks_StraySlashAfterSemicolon_IsIgnored()
    {
        // ';' 로 이미 끝난 뒤 남는 게 없는 '/' 는 조용히 지나간다(추가 빈 문장 없음)
        var sql = "select 1 from dual;\n/";
        var stmts = StatementSplitter.Split(sql, oracleBlocks: true);

        Assert.Single(stmts);
        Assert.Equal("select 1 from dual", stmts[0].Text);
    }

    [Fact]
    public void Split_OracleBlocks_BlockWithoutClosingSlash_FallsBackToEndOfInput()
    {
        var sql = "begin\n  null;\nend;";
        var stmts = StatementSplitter.Split(sql, oracleBlocks: true);

        Assert.Single(stmts);
        Assert.Contains("null;", stmts[0].Text);
    }

    [Fact]
    public void Split_OracleBlocksFalse_TreatsSlashAsOrdinaryCharacter()
    {
        // 기본값(false)에서는 '/' 가 SQL*Plus 종결자가 아니다 — 예전 동작 그대로.
        var sql = "select 1 from dual;\n/\nselect 2 from dual;";
        var stmts = StatementSplitter.Split(sql);

        Assert.Equal(2, stmts.Count);
    }

    [Fact]
    public void Split_OracleBlocks_KeywordsAreCaseInsensitive()
    {
        var sql = "BEGIN\n  NULL;\nEND;\n/";
        var stmts = StatementSplitter.Split(sql, oracleBlocks: true);

        Assert.Single(stmts);
    }
}
