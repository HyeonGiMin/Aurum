namespace PrismOne.Db.Core;

/// <summary>ERD 박스 안에 그려지는 컬럼 한 줄. DB 중립.</summary>
public sealed record ErdColumn(string Name, string Type, bool NotNull, bool IsPk, bool IsFk);

/// <summary>ERD 박스 하나 = 테이블/뷰. DB 중립 (PG 의 schema = Oracle 의 owner).</summary>
public sealed record ErdTable(string Schema, string Name, bool IsView, IReadOnlyList<ErdColumn> Columns)
{
    /// <summary>그래프 안에서 테이블을 식별하는 키.</summary>
    public string Key => $"{Schema}.{Name}";
}

/// <summary>
/// FK 관계 하나. <c>ChildUnique</c> 는 자식 FK 컬럼 집합이 자식 쪽 PK/UNIQUE 로 덮이는지 —
/// 덮이면 1:1, 아니면 1:N. <c>ChildOptional</c> 은 자식 컬럼 중 nullable 이 하나라도
/// 있는지 (0..N 표기용).
/// </summary>
public sealed record ErdRelation(
    string Name,
    string ChildKey,
    IReadOnlyList<string> ChildColumns,
    string ParentKey,
    IReadOnlyList<string> ParentColumns,
    bool ChildUnique,
    bool ChildOptional)
{
    public bool IsSelfReference => ChildKey == ParentKey;

    /// <summary>툴팁용: child(a, b) → parent(x, y)</summary>
    public string Describe =>
        $"{ChildKey}({string.Join(", ", ChildColumns)}) → {ParentKey}({string.Join(", ", ParentColumns)})";
}

/// <summary>테이블 + FK 관계의 집합. 카탈로그가 만들고 레이아웃이 소비한다.</summary>
public sealed record ErdGraph(IReadOnlyList<ErdTable> Tables, IReadOnlyList<ErdRelation> Relations)
{
    public static ErdGraph Empty { get; } = new([], []);

    /// <summary>
    /// 씨앗 테이블에서 <paramref name="hops"/> 홉 안에 닿는 부분만 잘라낸다.
    /// 전체 스키마는 한 화면에 읽을 수 없으니 SQL Developer 처럼 "선택 + 이웃"이 기본 시야다.
    /// hops 가 음수면 전체를 그대로 돌려준다.
    /// </summary>
    public ErdGraph Focus(IEnumerable<string> seedKeys, int hops)
    {
        if (hops < 0) return this;

        var known = Tables.Select(t => t.Key).ToHashSet();
        var keep = seedKeys.Where(known.Contains).ToHashSet();
        if (keep.Count == 0) return Empty;

        var frontier = new HashSet<string>(keep);
        for (var hop = 0; hop < hops && frontier.Count > 0; hop++)
        {
            var next = new HashSet<string>();
            foreach (var rel in Relations)
            {
                if (frontier.Contains(rel.ChildKey) && keep.Add(rel.ParentKey)) next.Add(rel.ParentKey);
                if (frontier.Contains(rel.ParentKey) && keep.Add(rel.ChildKey)) next.Add(rel.ChildKey);
            }
            frontier = next;
        }

        return Restrict(keep);
    }

    /// <summary>이름으로 걸러낸다(부분 일치). 걸린 테이블끼리의 관계만 남는다.</summary>
    public ErdGraph Filter(string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return this;
        var keep = Tables
            .Where(t => t.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Key)
            .ToHashSet();
        return Restrict(keep);
    }

    private ErdGraph Restrict(IReadOnlySet<string> keep) => new(
        Tables.Where(t => keep.Contains(t.Key)).ToList(),
        Relations.Where(r => keep.Contains(r.ChildKey) && keep.Contains(r.ParentKey)).ToList());
}
