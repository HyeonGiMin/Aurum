using PrismOne.Db.Core;
using Xunit;

namespace PrismOne.Db.Core.Tests;

public class SchemaCacheTests
{
    private static readonly TableInfo Study = new("prismone", "study", false);
    private static readonly TableInfo Series = new("prismone", "series", false);

    private static SchemaSnapshot Snapshot() => new(
        [Study, Series],
        new Dictionary<string, List<ColumnInfo>>(StringComparer.Ordinal)
        {
            ["prismone.study"] = [new ColumnInfo(1, "study_key", "bigint", "no", "P1", "")],
            ["prismone.series"] = [new ColumnInfo(1, "series_key", "bigint", "no", "P1", "")],
        });

    /// <summary>가짜 로더 — DB 없이 캐시 동작만 본다.</summary>
    private static SchemaCache Cache() => new(_ => Task.FromResult(Snapshot()));

    [Fact]
    public async Task LoadsOnFirstUseOnly()
    {
        var cache = Cache();

        await cache.GetTablesAsync();
        await cache.GetTablesAsync();
        await cache.GetColumnsAsync(Study);

        Assert.Equal(1, cache.LoadCount);
    }

    [Fact]
    public async Task ServesColumnsFromTheSnapshot()
    {
        var cache = Cache();

        var columns = await cache.GetColumnsAsync(Series);

        Assert.Single(columns);
        Assert.Equal("series_key", columns[0].Name);
    }

    [Fact]
    public async Task UnknownTableGivesEmptyInsteadOfThrowing()
    {
        var cache = Cache();

        var columns = await cache.GetColumnsAsync(new TableInfo("prismone", "nope", false));

        Assert.Empty(columns);
    }

    [Fact]
    public async Task NotLoadedBeforeFirstUse()
    {
        var cache = Cache();

        Assert.False(cache.IsLoaded);
        Assert.Empty(cache.LoadedTables);

        await cache.GetTablesAsync();

        Assert.True(cache.IsLoaded);
        Assert.Equal(2, cache.LoadedTables.Count);
    }

    [Fact]
    public async Task InvalidateForcesAReload()
    {
        var cache = Cache();
        await cache.GetTablesAsync();

        cache.Invalidate();
        Assert.False(cache.IsLoaded);
        await cache.GetTablesAsync();

        Assert.Equal(2, cache.LoadCount);
    }

    [Fact]
    public async Task RefreshReloadsAndReturnsFreshSnapshot()
    {
        var cache = Cache();
        await cache.GetTablesAsync();

        var snapshot = await cache.RefreshAsync();

        Assert.Equal(2, cache.LoadCount);
        Assert.Equal(2, snapshot.Tables.Count);
    }

    [Fact]
    public async Task ConcurrentCallersLoadOnlyOnce()
    {
        var started = 0;
        var release = new TaskCompletionSource();
        var cache = new SchemaCache(async _ =>
        {
            Interlocked.Increment(ref started);
            await release.Task;
            return Snapshot();
        });

        var callers = Enumerable.Range(0, 8).Select(_ => cache.GetTablesAsync()).ToArray();
        release.SetResult();
        await Task.WhenAll(callers);

        Assert.Equal(1, started);
        Assert.Equal(1, cache.LoadCount);
    }
}
