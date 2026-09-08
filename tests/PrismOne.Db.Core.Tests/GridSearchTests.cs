using PrismOne.Db.Core;
using Xunit;

namespace PrismOne.Db.Core.Tests;

/// <summary>
/// 결과 그리드 안에서 찾기. 셀을 훑는 순서(행 → 열)와 한 바퀴 돌고 멈추는지가 핵심이다.
/// </summary>
public class GridSearchTests
{
    private static readonly List<string?[]> Rows =
    [
        ["1", "CT",  "seoul"],
        ["2", "MR",  null],
        ["3", "ct",  "busan"],
    ];

    [Fact]
    public void FindsTheFirstMatchScanningRowsThenColumns()
    {
        var hit = GridSearch.FindNext(Rows, "ct");

        Assert.Equal(new GridHit(0, 1), hit);
    }

    [Fact]
    public void ContinuesFromTheGivenCellSoRepeatedCallsWalkForward()
    {
        var first = GridSearch.FindNext(Rows, "ct")!.Value;
        var second = GridSearch.FindNext(Rows, "ct", first.Row, first.Column);

        Assert.Equal(new GridHit(2, 1), second);
    }

    [Fact]
    public void WrapsAroundToTheTopAfterTheLastMatch()
    {
        var last = new GridHit(2, 1);

        var next = GridSearch.FindNext(Rows, "ct", last.Row, last.Column);

        Assert.Equal(new GridHit(0, 1), next);
    }

    [Fact]
    public void StopsAfterOneLapWhenNothingMatches()
        => Assert.Null(GridSearch.FindNext(Rows, "없는값"));

    [Fact]
    public void CaseSensitiveSearchSkipsTheOtherCasing()
    {
        var hit = GridSearch.FindNext(Rows, "ct", matchCase: true);

        Assert.Equal(new GridHit(2, 1), hit);   // 대문자 "CT" 는 건너뛴다
    }

    [Fact]
    public void WholeCellRequiresTheEntireValueToMatch()
    {
        Assert.Null(GridSearch.FindNext(Rows, "seou", wholeCell: true));
        Assert.Equal(new GridHit(0, 2), GridSearch.FindNext(Rows, "seoul", wholeCell: true));
    }

    [Fact]
    public void BackwardsWalksTheOtherWay()
    {
        var hit = GridSearch.FindNext(Rows, "ct", fromRow: 2, fromColumn: 2, backwards: true);

        Assert.Equal(new GridHit(2, 1), hit);
    }

    [Fact]
    public void RaggedRowsDoNotWalkOffTheEnd()
    {
        List<string?[]> ragged = [["a"], ["b", "target"]];

        Assert.Equal(new GridHit(1, 1), GridSearch.FindNext(ragged, "target"));
    }

    [Fact]
    public void NullCellsNeverMatch()
    {
        // 2행 3열이 null — "" 로 오인해 걸리면 안 된다
        Assert.Equal(1, GridSearch.Count(Rows, "busan"));
        Assert.Equal(0, GridSearch.Count(Rows, "null"));
    }

    [Fact]
    public void EmptyGridAndEmptyTermFindNothing()
    {
        Assert.Null(GridSearch.FindNext([], "x"));
        Assert.Null(GridSearch.FindNext(Rows, ""));
        Assert.Equal(0, GridSearch.Count(Rows, ""));
    }

    [Fact]
    public void CountReportsEveryMatchingCell()
    {
        Assert.Equal(2, GridSearch.Count(Rows, "ct"));
        Assert.Equal(1, GridSearch.Count(Rows, "ct", matchCase: true));
        // 셀 전체 일치라도 대소문자를 안 가리면 "CT" 와 "ct" 가 둘 다 걸린다
        Assert.Equal(2, GridSearch.Count(Rows, "CT", wholeCell: true));
        Assert.Equal(1, GridSearch.Count(Rows, "CT", wholeCell: true, matchCase: true));
    }
}
