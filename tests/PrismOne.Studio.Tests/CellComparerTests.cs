namespace PrismOne.Studio.Tests;

/// <summary>
/// 결과 그리드 컬럼 정렬. 셀이 전부 문자열이라 그냥 비교하면 "1000" 이 "999" 앞에 온다 —
/// 숫자로 읽히면 숫자로 비교하는지가 핵심이다.
/// </summary>
public class CellComparerTests
{
    private static RowItem Row(params string?[] cells) => new(1, cells);

    [Fact]
    public void NumbersCompareAsNumbersNotText()
    {
        var comparer = new CellComparer(0);

        Assert.True(comparer.Compare(Row("999"), Row("1000")) < 0);
        Assert.True(comparer.Compare(Row("-5"), Row("3")) < 0);
        Assert.True(comparer.Compare(Row("2.5"), Row("10")) < 0);
    }

    [Fact]
    public void TextComparesCaseInsensitively()
    {
        var comparer = new CellComparer(0);

        Assert.Equal(0, comparer.Compare(Row("CT"), Row("ct")));
        Assert.True(comparer.Compare(Row("apple"), Row("banana")) < 0);
    }

    [Fact]
    public void NullsSortFirstAndTieWithEachOther()
    {
        var comparer = new CellComparer(0);

        Assert.True(comparer.Compare(Row((string?)null), Row("a")) < 0);
        Assert.True(comparer.Compare(Row("a"), Row((string?)null)) > 0);
        Assert.Equal(0, comparer.Compare(Row((string?)null), Row((string?)null)));
    }

    [Fact]
    public void ComparesTheColumnItWasBuiltFor()
    {
        var second = new CellComparer(1);

        // 0번은 같고 1번만 다르다 — 1번 기준으로 갈려야 한다
        Assert.True(second.Compare(Row("x", "1"), Row("x", "2")) < 0);
    }

    [Fact]
    public void OutOfRangeIndexIsTreatedAsNullInsteadOfThrowing()
    {
        var far = new CellComparer(9);

        Assert.Equal(0, far.Compare(Row("a"), Row("b")));
    }

    [Fact]
    public void NonRowItemsAreTreatedAsNull()
    {
        var comparer = new CellComparer(0);

        Assert.Equal(0, comparer.Compare("not a row", 42));
    }

    [Fact]
    public void IndexIsExposedSoEditModeCanShiftIt()
        => Assert.Equal(3, new CellComparer(3).Index);
}
