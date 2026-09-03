using System;
using System.Collections;
using System.Globalization;

namespace PrismOne.Studio;

/// <summary>
/// 결과 그리드 컬럼 정렬 (Golden 의 컬럼 헤더 정렬).
///
/// 컬럼 바인딩이 <c>Cells[i]</c> 인덱서라 DataGrid 의 기본(경로 반사) 정렬이 먹지 않아
/// 컬럼마다 이 비교자를 붙인다.
///
/// 셀은 전부 문자열이지만 숫자로 읽히면 숫자로 비교한다 — 그러지 않으면
/// "1000" 이 "999" 앞에 온다. NULL 은 항상 앞.
/// </summary>
public sealed class CellComparer(int index) : IComparer
{
    /// <summary>비교하는 셀 인덱스 — 편집 모드 진입 시 정렬을 옮겨 적용할 때 쓴다.</summary>
    public int Index => index;

    public int Compare(object? x, object? y)
    {
        var left = Cell(x);
        var right = Cell(y);

        if (left is null && right is null) return 0;
        if (left is null) return -1;
        if (right is null) return 1;

        if (double.TryParse(left, NumberStyles.Any, CultureInfo.InvariantCulture, out var a) &&
            double.TryParse(right, NumberStyles.Any, CultureInfo.InvariantCulture, out var b))
            return a.CompareTo(b);

        return string.Compare(left, right, StringComparison.CurrentCultureIgnoreCase);
    }

    private string? Cell(object? row) =>
        row is RowItem item && index >= 0 && index < item.Cells.Length ? item.Cells[index] : null;
}
