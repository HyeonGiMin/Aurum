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
}
