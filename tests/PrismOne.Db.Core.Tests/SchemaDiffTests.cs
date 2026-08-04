using PrismOne.Db.Core;
using Xunit;

namespace PrismOne.Db.Core.Tests;

/// <summary>
/// 읽기 전용 스키마 diff (DATAGRIP_GAP §3a) — 기준(표준) 대비 대상(사이트)의 어긋남.
/// </summary>
public sealed class SchemaDiffTests
{
    private static ErdTable Table(string name, params ErdColumn[] columns) =>
        new("prismone", name, false, columns);

    private static ErdColumn Col(string name, string type = "bigint", bool notNull = true, bool pk = false) =>
        new(name, type, notNull, pk, IsFk: false);

    private static ErdRelation Rel(string child, string parent, string column) =>
        new($"fk_{child}_{parent}", $"prismone.{child}", [column], $"prismone.{parent}", [column],
            ChildUnique: false, ChildOptional: false);

    [Fact]
    public void IdenticalSchemasAreEmpty()
    {
        var graph = new ErdGraph(
            [Table("study", Col("study_key", pk: true), Col("study_dttm", "timestamp", notNull: false))],
            [Rel("study", "patient", "patient_key")]);

        var diff = SchemaDiff.Compare(graph, graph);

        Assert.True(diff.IsEmpty);
        Assert.Contains("차이 없음", diff.Summary);
    }

    [Fact]
    public void MissingAndExtraTablesAreDirectional()
    {
        var baseline = new ErdGraph([Table("study"), Table("patient")], []);
        var target = new ErdGraph([Table("study"), Table("scratch_tmp")], []);

        var diff = SchemaDiff.Compare(baseline, target);

        // 기준에만 있으면 "빠짐"(패치 누락), 대상에만 있으면 "추가"(사이트 임의 생성)
        Assert.Equal("prismone.patient", Assert.Single(diff.MissingTables).Key);
        Assert.Equal("prismone.scratch_tmp", Assert.Single(diff.ExtraTables).Key);
    }

    [Fact]
    public void ColumnDifferencesAreItemized()
    {
        var baseline = new ErdGraph([Table("study",
            Col("study_key", pk: true),
            Col("study_dttm", "timestamp", notNull: false),
            Col("dropped_col"))], []);
        var target = new ErdGraph([Table("study",
            Col("study_key", pk: true),
            Col("study_dttm", "timestamptz", notNull: true),
            Col("added_col"))], []);

        var diff = SchemaDiff.Compare(baseline, target);

        var change = Assert.Single(diff.ChangedTables);
        Assert.Equal("dropped_col", Assert.Single(change.MissingColumns).Name);
        Assert.Equal("added_col", Assert.Single(change.ExtraColumns).Name);
        Assert.Equal(2, change.ChangedColumns.Count);   // type + null
        Assert.Contains(change.ChangedColumns, c => c.Aspect == "type" && c.Target == "timestamptz");
        Assert.Contains(change.ChangedColumns, c => c.Aspect == "null" && c.Target == "NOT NULL");
    }

    [Fact]
    public void PkChangeIsReported()
    {
        var baseline = new ErdGraph([Table("t", Col("id", pk: true))], []);
        var target = new ErdGraph([Table("t", Col("id", pk: false))], []);

        var diff = SchemaDiff.Compare(baseline, target);

        var change = Assert.Single(Assert.Single(diff.ChangedTables).ChangedColumns);
        Assert.Equal("pk", change.Aspect);
    }

    [Fact]
    public void TableBecomingViewIsReported()
    {
        var baseline = new ErdGraph([Table("t", Col("id"))], []);
        var target = new ErdGraph([new ErdTable("prismone", "t", IsView: true, [Col("id")])], []);

        var diff = SchemaDiff.Compare(baseline, target);

        var change = Assert.Single(Assert.Single(diff.ChangedTables).ChangedColumns);
        Assert.Equal(("(object)", "kind", "table", "view"),
            (change.Column, change.Aspect, change.Baseline, change.Target));
    }

    [Fact]
    public void RelationsMatchByLinkNotByName()
    {
        // 제약 이름은 사이트마다 자동 생성돼 달라도, 같은 연결이면 같은 FK 다
        var a = new ErdRelation("fk_named_one", "prismone.study", ["patient_key"],
            "prismone.patient", ["patient_key"], false, false);
        var b = new ErdRelation("sys_c0012345", "prismone.study", ["patient_key"],
            "prismone.patient", ["patient_key"], false, false);

        var diff = SchemaDiff.Compare(new ErdGraph([], [a]), new ErdGraph([], [b]));

        Assert.True(diff.IsEmpty);
    }

    [Fact]
    public void MissingRelationIsReported()
    {
        var baseline = new ErdGraph([], [Rel("study", "patient", "patient_key")]);
        var target = new ErdGraph([], []);

        var diff = SchemaDiff.Compare(baseline, target);

        Assert.Single(diff.MissingRelations);
        Assert.Empty(diff.ExtraRelations);
    }

    [Fact]
    public void CaseDifferenceIsNotADifference()
    {
        // Oracle 은 대문자, PG 는 소문자로 읽힌다 — 이름 비교는 대소문자 무시
        var baseline = new ErdGraph([new ErdTable("PRISMONE", "STUDY", false, [Col("STUDY_KEY")])], []);
        var target = new ErdGraph([new ErdTable("prismone", "study", false, [Col("study_key")])], []);

        Assert.True(SchemaDiff.Compare(baseline, target).IsEmpty);
    }

    [Fact]
    public void SnapshotRoundTripsThroughJsonFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aurum-snap-{Guid.NewGuid():N}.json");
        var graph = new ErdGraph(
            [Table("study", Col("study_key", pk: true))],
            [Rel("study", "patient", "patient_key")]);
        try
        {
            SchemaSnapshotFile.Save(path, new SchemaSnapshotDoc(
                "postgres@dev/prismone", new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc), graph));
            var loaded = SchemaSnapshotFile.Load(path);

            Assert.Equal("postgres@dev/prismone", loaded.Source);
            Assert.True(SchemaDiff.Compare(graph, loaded.Graph).IsEmpty);
            Assert.Equal("fk_study_patient", loaded.Graph.Relations[0].Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BrokenSnapshotFileThrows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aurum-snap-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "not json at all");
        try
        {
            Assert.ThrowsAny<Exception>(() => SchemaSnapshotFile.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
