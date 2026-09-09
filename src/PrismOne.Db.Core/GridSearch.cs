namespace PrismOne.Db.Core;

/// <summary>찾은 자리 (행·열 인덱스). 둘 다 0부터.</summary>
public readonly record struct GridHit(int Row, int Column);

/// <summary>
/// 결과 그리드 안에서 찾기 (Golden 의 "Find in Results…").
/// 에디터 Find 와 달리 <b>이미 가져온 행</b>만 뒤진다 — 점진 fetch 로 아직 안 받은 행은
/// 서버에 있지 UI 에 없다. 그래서 못 찾으면 "받은 N행 안에는 없다"고 알려야 한다.
///
/// 자리를 돌려주기만 하고 선택·스크롤은 UI 몫이다 (그래야 테스트할 수 있다).
/// </summary>
public static class GridSearch
{
    /// <summary>
    /// <paramref name="fromRow"/>·<paramref name="fromColumn"/> <b>다음</b> 칸부터 찾는다.
    /// 끝에 닿으면 처음으로 돌아와 시작 자리까지 훑는다 (한 바퀴, 무한 반복 없음).
    /// </summary>
    /// <param name="fromRow">-1 이면 맨 처음부터.</param>
    public static GridHit? FindNext(
        IReadOnlyList<string?[]> rows,
        string term,
        int fromRow = -1,
        int fromColumn = -1,
        bool matchCase = false,
        bool wholeCell = false,
        bool backwards = false)
    {
        if (rows.Count == 0 || string.IsNullOrEmpty(term))
            return null;

        var width = rows.Max(r => r.Length);
        if (width == 0)
            return null;

        var total = (long)rows.Count * width;
        var start = Position(fromRow, fromColumn, width, rows.Count, backwards);
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        for (long step = 1; step <= total; step++)
        {
            var at = backwards
                ? ((start - step) % total + total) % total
                : (start + step) % total;
            var row = (int)(at / width);
            var column = (int)(at % width);
            if (column >= rows[row].Length)
                continue;
            if (Matches(rows[row][column], term, comparison, wholeCell))
                return new GridHit(row, column);
        }
        return null;
    }

    /// <summary>받은 행 전체에서 몇 칸이 걸리는지 (상태 표시용).</summary>
    public static int Count(
        IReadOnlyList<string?[]> rows, string term, bool matchCase = false, bool wholeCell = false)
    {
        if (string.IsNullOrEmpty(term))
            return 0;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return rows.Sum(row => row.Count(cell => Matches(cell, term, comparison, wholeCell)));
    }

    private static bool Matches(string? cell, string term, StringComparison comparison, bool wholeCell)
    {
        if (cell is null)
            return false;
        return wholeCell
            ? string.Equals(cell, term, comparison)
            : cell.Contains(term, comparison);
    }

    /// <summary>시작 자리를 1차원 위치로. 범위를 벗어나면 처음(뒤로 찾기면 끝)으로 본다.</summary>
    private static long Position(int row, int column, int width, int rowCount, bool backwards)
    {
        if (row < 0 || row >= rowCount || column < 0 || column >= width)
            return backwards ? (long)rowCount * width : -1;
        return (long)row * width + column;
    }
}
