using PrismOne.Db.Core;
using Xunit;

namespace PrismOne.Db.Core.Tests;

public class GridEditorPrepareTests
{
    [Fact]
    public void AddsCtidColumnForSimpleSelect()
    {
        var prepared = GridEditor.Prepare("select study_key, modality from prismone.study");

        Assert.NotNull(prepared);
        Assert.Equal("prismone.study", prepared!.Table);
        Assert.Equal(
            $"select prismone.study.ctid::text as \"{GridEditor.RowIdColumn}\", study_key, modality from prismone.study",
            prepared.Sql);
    }

    [Fact]
    public void QualifiesCtidWithTableAlias()
    {
        var prepared = GridEditor.Prepare("select s.* from prismone.study s where s.modality = 'CT'");

        Assert.NotNull(prepared);
        Assert.Contains($"s.ctid::text as \"{GridEditor.RowIdColumn}\"", prepared!.Sql);
        Assert.Equal("prismone.study", prepared.Table);
    }

    [Fact]
    public void TreatsClauseKeywordAfterTableAsClauseNotAlias()
    {
        var prepared = GridEditor.Prepare("select * from study where study_key = 1");

        Assert.NotNull(prepared);
        Assert.Contains("study.ctid::text", prepared!.Sql);
    }

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
        => Assert.Null(GridEditor.Prepare(sql));
}

public class GridEditorBuildTests
{
    private const string Table = "prismone.study";

    [Fact]
    public void UpdateSetsChangedCellsAndKeysByCtid()
    {
        var change = new GridChange.Update("(0,7)", [("modality", "MR"), ("note", null)]);

        var statement = Assert.Single(GridEditor.Build(Table, [change]));

        Assert.Equal(
            "UPDATE prismone.study SET \"modality\" = $1, \"note\" = $2 WHERE ctid = $3::tid",
            statement.Sql);
        Assert.Equal(["MR", null, "(0,7)"], statement.Parameters);
    }

    [Fact]
    public void DeleteKeysByCtid()
    {
        var statement = Assert.Single(GridEditor.Build(Table, [new GridChange.Delete("(3,2)")]));

        Assert.Equal("DELETE FROM prismone.study WHERE ctid = $1::tid", statement.Sql);
        Assert.Equal(["(3,2)"], statement.Parameters);
    }

    [Fact]
    public void InsertListsOnlyFilledColumns()
    {
        var change = new GridChange.Insert([("study_id", "ST-0007"), ("modality", "CT")]);

        var statement = Assert.Single(GridEditor.Build(Table, [change]));

        Assert.Equal(
            "INSERT INTO prismone.study (\"study_id\", \"modality\") VALUES ($1, $2)",
            statement.Sql);
        Assert.Equal(["ST-0007", "CT"], statement.Parameters);
    }

    [Fact]
    public void QuotesIdentifiersSoReservedOrMixedCaseColumnsSurvive()
    {
        var change = new GridChange.Update("(0,1)", [("Order", "1")]);

        var statement = Assert.Single(GridEditor.Build(Table, [change]));

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

        var statements = GridEditor.Build(Table, changes);

        Assert.StartsWith("UPDATE", statements[0].Sql);
        Assert.StartsWith("DELETE", statements[1].Sql);
        Assert.StartsWith("INSERT", statements[2].Sql);
    }

    [Fact]
    public void RejectsEmptyChangeSets()
    {
        Assert.Throws<ArgumentException>(() => GridEditor.Build(Table, [new GridChange.Update("(0,1)", [])]));
        Assert.Throws<ArgumentException>(() => GridEditor.Build(Table, [new GridChange.Insert([])]));
    }
}
