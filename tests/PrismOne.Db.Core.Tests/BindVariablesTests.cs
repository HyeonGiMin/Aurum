using System.Linq;
using PrismOne.Db.Core;
using Xunit;

namespace PrismOne.Db.Core.Tests;

public class BindVariablesTests
{
    [Fact]
    public void Find_SimpleVariables()
    {
        var vars = BindVariables.Find("select * from study where study_key = :key and modality = :mod");
        Assert.Equal(["key", "mod"], vars.Select(v => v.Name));
    }

    [Fact]
    public void Find_IgnoresTypeCast()
        => Assert.Empty(BindVariables.Find("select '2026-01-01'::date, x::text from t"));

    [Fact]
    public void Find_IgnoresInsideStringsAndComments()
    {
        var sql = "select ':notvar', -- :alsonot\n /* :neither */ x from t where a = :real";
        Assert.Equal(["real"], BindVariables.Find(sql).Select(v => v.Name));
    }

    [Fact]
    public void Find_IgnoresInsideDollarQuote()
        => Assert.Empty(BindVariables.Find("do $$ begin perform :x; end $$;"));

    [Fact]
    public void Find_DeduplicatesKeepingOrder()
        => Assert.Equal(["a", "b"], BindVariables.Find("select :a, :b, :a").Select(v => v.Name));

    [Fact]
    public void Rewrite_ConvertsToNpgsqlParameters()
        => Assert.Equal("select * from t where k = @key and m = @mod",
            BindVariables.Rewrite("select * from t where k = :key and m = :mod"));

    [Fact]
    public void Rewrite_LeavesCastsAndLiterals()
        => Assert.Equal("select ':x', y::int from t where z = @v",
            BindVariables.Rewrite("select ':x', y::int from t where z = :v"));
}
