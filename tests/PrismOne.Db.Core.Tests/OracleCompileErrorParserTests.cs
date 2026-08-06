using PrismOne.Db.Core;
using Xunit;

namespace PrismOne.Db.Core.Tests;

public class OracleCompileErrorParserTests
{
    [Fact]
    public void ParseObjectHeader_CreateProcedure()
    {
        var header = OracleCompileErrorParser.ParseObjectHeader("create procedure p1 as\nbegin\n  null;\nend;");
        Assert.Equal(("p1", "PROCEDURE"), header);
    }

    [Fact]
    public void ParseObjectHeader_CreateOrReplaceFunction()
    {
        var header = OracleCompileErrorParser.ParseObjectHeader(
            "create or replace function f1(x number) return number as\nbegin\n  return x;\nend;");
        Assert.Equal(("f1", "FUNCTION"), header);
    }

    [Fact]
    public void ParseObjectHeader_PackageBody_UsesTwoWordType()
    {
        var header = OracleCompileErrorParser.ParseObjectHeader(
            "create or replace package body pkg is\nend pkg;");
        Assert.Equal(("pkg", "PACKAGE BODY"), header);
    }

    [Fact]
    public void ParseObjectHeader_PackageSpec_UsesOneWordType()
    {
        var header = OracleCompileErrorParser.ParseObjectHeader("create or replace package pkg is\nend pkg;");
        Assert.Equal(("pkg", "PACKAGE"), header);
    }

    [Fact]
    public void ParseObjectHeader_Trigger()
    {
        var header = OracleCompileErrorParser.ParseObjectHeader(
            "create or replace trigger trg1 before insert on t1\nbegin\n  null;\nend;");
        Assert.Equal(("trg1", "TRIGGER"), header);
    }

    [Fact]
    public void ParseObjectHeader_TypeBody()
    {
        var header = OracleCompileErrorParser.ParseObjectHeader("create or replace type body ty1 is\nend;");
        Assert.Equal(("ty1", "TYPE BODY"), header);
    }

    [Fact]
    public void ParseObjectHeader_PlainSelect_ReturnsNull()
    {
        Assert.Null(OracleCompileErrorParser.ParseObjectHeader("select 1 from dual"));
    }

    [Fact]
    public void ParseObjectHeader_CreateTable_ReturnsNull()
    {
        // USER_ERRORS 는 저장 프로시저류만 다룬다 — TABLE 은 컴파일 오류 대상이 아니다
        Assert.Null(OracleCompileErrorParser.ParseObjectHeader("create table t1 (id number)"));
    }

    [Fact]
    public void ToSqlIssue_MapsLineAndPositionIntoStatementOffset()
    {
        var stmt = "create procedure p1 as\nbegin\n  bogus_call();\nend;";
        var error = new OracleCompileError(Line: 3, Position: 3, Text: "PLS-00201: identifier 'BOGUS_CALL' must be declared");

        var issue = error.ToSqlIssue(stmt, stmtStart: 100);

        // line 3 은 "  bogus_call();" — USER_ERRORS.POSITION 은 1-based 라 position 3 은
        // 그 줄의 3번째 문자("b", 0-based index 2)를 가리킨다
        var line3Start = stmt.IndexOf("  bogus_call();");
        Assert.Equal(100 + line3Start + 2, issue.Start);
        Assert.Equal("PLS-00201: identifier 'BOGUS_CALL' must be declared", issue.Message);
        Assert.True(issue.Length > 0);
    }

    [Fact]
    public void ToSqlIssue_OffsetPastEndOfText_ClampsToLength()
    {
        var stmt = "create procedure p1 as\nbegin\nend;";
        var error = new OracleCompileError(Line: 99, Position: 0, Text: "some error");

        var issue = error.ToSqlIssue(stmt, stmtStart: 0);

        Assert.Equal(stmt.Length, issue.Start);
    }
}
