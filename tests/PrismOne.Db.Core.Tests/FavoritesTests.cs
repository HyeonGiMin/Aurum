using PrismOne.Db.Core;
using Xunit;

namespace PrismOne.Db.Core.Tests;

public class FavoritesStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "prismone-tests", $"favorites-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Add_PersistsAndReloads()
    {
        // Arrange
        var store = FavoritesStore.Load(_path);

        // Act
        store.Add("recent studies", "select * from prismone.study limit 100;");

        // Assert
        var reloaded = FavoritesStore.Load(_path);
        Assert.Single(reloaded.Items);
        Assert.Equal("recent studies", reloaded.Items[0].Name);
    }

    [Fact]
    public void Add_SameNameOverwritesSql()
    {
        var store = FavoritesStore.Load(_path);
        store.Add("count", "select 1;");

        store.Add("COUNT", "select 2;");

        Assert.Single(store.Items);
        Assert.Equal("select 2;", store.Items[0].Sql);
    }

    [Fact]
    public void Add_KeepsItemsSortedByName()
    {
        var store = FavoritesStore.Load(_path);

        store.Add("zeta", "select 1;");
        store.Add("alpha", "select 2;");

        Assert.Equal(["alpha", "zeta"], store.Items.Select(f => f.Name));
    }

    [Fact]
    public void Add_ThrowsWhenNameOrSqlIsBlank()
    {
        var store = FavoritesStore.Load(_path);

        Assert.Throws<ArgumentException>(() => store.Add("  ", "select 1;"));
        Assert.Throws<ArgumentException>(() => store.Add("name", "   "));
    }

    [Fact]
    public void Update_RenamesAndRewritesSql()
    {
        var store = FavoritesStore.Load(_path);
        store.Add("old", "select 1;");

        var updated = store.Update("old", "new", "select 2;");

        Assert.True(updated);
        Assert.Null(store.Find("old"));
        Assert.Equal("select 2;", store.Find("new")!.Sql);
    }

    [Fact]
    public void Update_ReturnsFalseWhenOriginalMissing()
    {
        var store = FavoritesStore.Load(_path);

        Assert.False(store.Update("ghost", "name", "select 1;"));
    }

    [Fact]
    public void Remove_DeletesAndReportsWhetherItExisted()
    {
        var store = FavoritesStore.Load(_path);
        store.Add("temp", "select 1;");

        Assert.True(store.Remove("TEMP"));
        Assert.False(store.Remove("temp"));
        Assert.Empty(FavoritesStore.Load(_path).Items);
    }

    [Fact]
    public void Filter_MatchesNameOrSqlCaseInsensitively()
    {
        var items = new List<FavoriteQuery>
        {
            new("worklist count", "select count(*) from prismone.examlist;"),
            new("patients", "select * from prismone.patient;"),
        };

        Assert.Single(FavoritesStore.Filter(items, "WORKLIST"));
        Assert.Single(FavoritesStore.Filter(items, "patient;"));
        Assert.Equal(2, FavoritesStore.Filter(items, "  ").Count);
    }

    [Fact]
    public void Load_ReturnsEmptyWhenFileIsCorrupt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ not json");

        Assert.Empty(FavoritesStore.Load(_path).Items);
    }
}

public class FavoriteSqlTests
{
    [Theory]
    [InlineData("select * from study;")]
    [InlineData("  SELECT 1")]
    [InlineData("-- comment\nselect 1;")]
    [InlineData("/* block */ values (1), (2);")]
    [InlineData("(select 1);")]
    [InlineData("table prismone.study;")]
    [InlineData("show search_path;")]
    [InlineData("with recent as (select 1) select * from recent;")]
    [InlineData("select 1; select 2;")]
    public void IsSelectOnly_TrueForReadOnlyScripts(string sql)
        => Assert.True(FavoriteSql.IsSelectOnly(sql));

    [Theory]
    [InlineData("delete from study;")]
    [InlineData("update study set x = 1;")]
    [InlineData("with moved as (insert into a select * from b returning *) select * from moved;")]
    [InlineData("select 1; delete from study;")]
    [InlineData("-- only a comment")]
    [InlineData("")]
    [InlineData("explain analyze delete from study;")]
    public void IsSelectOnly_FalseForWritingOrEmptyScripts(string sql)
        => Assert.False(FavoriteSql.IsSelectOnly(sql));
}
