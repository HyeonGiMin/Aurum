using System.Text.Json;

namespace PrismOne.Db.Core;

/// <summary>컬럼 하나가 어떻게 다른지 — 예: type: varchar(10) → varchar(20).</summary>
public sealed record ColumnChange(string Column, string Aspect, string Baseline, string Target);

/// <summary>양쪽에 다 있지만 내용이 다른 테이블.</summary>
public sealed record TableChange(
    string Key,
    IReadOnlyList<ErdColumn> MissingColumns,     // 기준에는 있는데 대상에 없음
    IReadOnlyList<ErdColumn> ExtraColumns,       // 대상에만 있음
    IReadOnlyList<ColumnChange> ChangedColumns);

/// <summary>
/// 기준(baseline) 대비 대상(target)의 차이. "빠짐"은 기준에만, "추가"는 대상에만 있다는 뜻.
/// </summary>
public sealed record SchemaDiffResult(
    IReadOnlyList<ErdTable> MissingTables,
    IReadOnlyList<ErdTable> ExtraTables,
    IReadOnlyList<TableChange> ChangedTables,
    IReadOnlyList<ErdRelation> MissingRelations,
    IReadOnlyList<ErdRelation> ExtraRelations)
{
    public bool IsEmpty =>
        MissingTables.Count == 0 && ExtraTables.Count == 0 && ChangedTables.Count == 0 &&
        MissingRelations.Count == 0 && ExtraRelations.Count == 0;

    public string Summary => IsEmpty
        ? "차이 없음 — 스키마가 일치합니다"
        : $"테이블: 빠짐 {MissingTables.Count} · 추가 {ExtraTables.Count} · 달라짐 {ChangedTables.Count}"
          + $"   |   FK: 빠짐 {MissingRelations.Count} · 추가 {ExtraRelations.Count}";
}

/// <summary>
/// 읽기 전용 스키마 diff (DATAGRIP_GAP §3a). "이 사이트 스키마가 표준과 뭐가 다른가"를
/// 즉시 보기 위한 것 — **동기화 DDL 은 만들지 않는다** (그건 iapdb 의 몫, STATUS.md §2·3).
///
/// 버전 pill 이 못 잡는 것을 잡는다: 기록상 패치는 적용됐는데 실제 스키마가 어긋난 경우.
/// </summary>
public static class SchemaDiff
{
    public static SchemaDiffResult Compare(ErdGraph baseline, ErdGraph target)
    {
        var baseTables = baseline.Tables.ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);
        var targetTables = target.Tables.ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);

        var missing = baseline.Tables.Where(t => !targetTables.ContainsKey(t.Key)).ToList();
        var extra = target.Tables.Where(t => !baseTables.ContainsKey(t.Key)).ToList();

        var changed = new List<TableChange>();
        foreach (var b in baseline.Tables)
        {
            if (!targetTables.TryGetValue(b.Key, out var t)) continue;
            if (CompareTable(b, t) is { } change)
                changed.Add(change);
        }

        // FK 는 이름이 아니라 **연결(자식 컬럼 → 부모 컬럼)로 동일성**을 정한다 —
        // 제약 이름은 사이트마다 다르게 생성돼 있어도 같은 관계면 같은 것이다
        var baseRels = baseline.Relations.ToDictionary(RelationIdentity, StringComparer.OrdinalIgnoreCase);
        var targetRels = target.Relations.ToDictionary(RelationIdentity, StringComparer.OrdinalIgnoreCase);
        var missingRels = baseline.Relations.Where(r => !targetRels.ContainsKey(RelationIdentity(r))).ToList();
        var extraRels = target.Relations.Where(r => !baseRels.ContainsKey(RelationIdentity(r))).ToList();

        return new SchemaDiffResult(missing, extra, changed, missingRels, extraRels);
    }

    private static string RelationIdentity(ErdRelation r) =>
        $"{r.ChildKey}({string.Join(",", r.ChildColumns)})→{r.ParentKey}({string.Join(",", r.ParentColumns)})";

    private static TableChange? CompareTable(ErdTable baseline, ErdTable target)
    {
        var baseCols = baseline.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var targetCols = target.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        var missing = baseline.Columns.Where(c => !targetCols.ContainsKey(c.Name)).ToList();
        var extra = target.Columns.Where(c => !baseCols.ContainsKey(c.Name)).ToList();
        var changes = new List<ColumnChange>();

        if (baseline.IsView != target.IsView)
            changes.Add(new ColumnChange("(object)", "kind",
                baseline.IsView ? "view" : "table", target.IsView ? "view" : "table"));

        foreach (var b in baseline.Columns)
        {
            if (!targetCols.TryGetValue(b.Name, out var t)) continue;
            if (!string.Equals(b.Type, t.Type, StringComparison.OrdinalIgnoreCase))
                changes.Add(new ColumnChange(b.Name, "type", b.Type, t.Type));
            if (b.NotNull != t.NotNull)
                changes.Add(new ColumnChange(b.Name, "null",
                    b.NotNull ? "NOT NULL" : "NULL", t.NotNull ? "NOT NULL" : "NULL"));
            if (b.IsPk != t.IsPk)
                changes.Add(new ColumnChange(b.Name, "pk",
                    b.IsPk ? "PK" : "-", t.IsPk ? "PK" : "-"));
            // IsFk 는 관계에서 파생 — FK diff 에서 따로 보고하므로 여기선 겹쳐 알리지 않는다
        }

        return missing.Count == 0 && extra.Count == 0 && changes.Count == 0
            ? null
            : new TableChange(baseline.Key, missing, extra, changes);
    }
}

/// <summary>스냅샷 파일 내용 — 어디서 언제 뜬 것인지와 그래프.</summary>
public sealed record SchemaSnapshotDoc(string Source, DateTime SavedAtUtc, ErdGraph Graph);

/// <summary>
/// 스키마 스냅샷을 JSON 파일로 저장/로드한다. 표준 사이트에서 한 번 떠서
/// 버전 관리에 두고, 각 사이트에서 그 파일과 비교하는 흐름 (지원팀 시나리오).
/// </summary>
public static class SchemaSnapshotFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Save(string path, SchemaSnapshotDoc doc) =>
        File.WriteAllText(path, JsonSerializer.Serialize(doc, Options));

    /// <summary>깨진 파일이면 예외 — 호출부가 사용자에게 알린다.</summary>
    public static SchemaSnapshotDoc Load(string path) =>
        JsonSerializer.Deserialize<SchemaSnapshotDoc>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"스냅샷 파일이 비어 있습니다: {path}");
}
