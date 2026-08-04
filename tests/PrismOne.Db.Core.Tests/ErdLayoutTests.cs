using PrismOne.Db.Core;
using Xunit;

namespace PrismOne.Db.Core.Tests;

public class ErdLayoutTests
{
    private static ErdTable Table(string name, params string[] pkColumns)
    {
        var columns = pkColumns
            .Select(c => new ErdColumn(c, "bigint", true, IsPk: true, IsFk: false))
            .ToList();
        return new ErdTable("public", name, IsView: false, columns);
    }

    private static ErdTable Child(string name, string fkColumn)
    {
        var columns = new List<ErdColumn>
        {
            new($"{name}_key", "bigint", true, IsPk: true, IsFk: false),
            new(fkColumn, "bigint", true, IsPk: false, IsFk: true),
        };
        return new ErdTable("public", name, IsView: false, columns);
    }

    private static ErdRelation Fk(string child, string parent, string column, bool unique = false, bool optional = false)
        => new($"fk_{child}_{parent}", $"public.{child}", [column], $"public.{parent}", [column], unique, optional);

    /// <summary>study ← series ← image 사슬.</summary>
    private static ErdGraph Chain() => new(
        [Table("study", "study_key"), Child("series", "study_key"), Child("image", "series_key")],
        [Fk("series", "study", "study_key"), Fk("image", "series", "series_key")]);

    [Fact]
    public void EmptyGraphProducesEmptyDiagram()
        => Assert.Same(ErdDiagram.Empty, ErdLayout.Compute(ErdGraph.Empty));

    [Fact]
    public void PlacesEveryTableExactlyOnce()
    {
        var diagram = ErdLayout.Compute(Chain());

        Assert.Equal(3, diagram.Boxes.Count);
        Assert.Equal(3, diagram.Boxes.Select(b => b.Table.Key).Distinct().Count());
    }

    [Fact]
    public void ParentSitsAboveChild()
    {
        var diagram = ErdLayout.Compute(Chain());
        var box = diagram.Boxes.ToDictionary(b => b.Table.Name);

        Assert.True(box["study"].Bottom <= box["series"].Y);
        Assert.True(box["series"].Bottom <= box["image"].Y);
    }

    [Fact]
    public void BoxesDoNotOverlap()
    {
        var diagram = ErdLayout.Compute(Chain());

        foreach (var a in diagram.Boxes)
            foreach (var b in diagram.Boxes)
            {
                if (ReferenceEquals(a, b)) continue;
                var separated = a.Right <= b.X || b.Right <= a.X || a.Bottom <= b.Y || b.Bottom <= a.Y;
                Assert.True(separated, $"{a.Table.Key} 와 {b.Table.Key} 가 겹친다");
            }
    }

    [Fact]
    public void LayoutIsDeterministic()
    {
        var first = ErdLayout.Compute(Chain());
        var second = ErdLayout.Compute(Chain());

        Assert.Equal(
            first.Boxes.Select(b => (b.Table.Key, b.X, b.Y)),
            second.Boxes.Select(b => (b.Table.Key, b.X, b.Y)));
    }

    [Fact]
    public void EveryRelationGetsAnEdgeStartingAtChild()
    {
        var diagram = ErdLayout.Compute(Chain());
        var box = diagram.Boxes.ToDictionary(b => b.Table.Key);

        Assert.Equal(2, diagram.Edges.Count);
        foreach (var edge in diagram.Edges)
        {
            var child = box[edge.Relation.ChildKey];
            var start = edge.Points[0];
            Assert.InRange(start.X, child.X, child.Right);
            Assert.InRange(start.Y, child.Y, child.Bottom);
        }
    }

    [Fact]
    public void CyclicReferenceStillTerminates()
    {
        var graph = new ErdGraph(
            [Child("a", "b_key"), Child("b", "a_key")],
            [Fk("a", "b", "b_key"), Fk("b", "a", "a_key")]);

        var diagram = ErdLayout.Compute(graph);

        Assert.Equal(2, diagram.Boxes.Count);
        Assert.Equal(2, diagram.Edges.Count);
    }

    [Fact]
    public void SelfReferenceLoopsOutsideTheBox()
    {
        var graph = new ErdGraph([Child("node", "parent_key")], [Fk("node", "node", "parent_key")]);

        var diagram = ErdLayout.Compute(graph);
        var box = diagram.Boxes.Single();
        var edge = diagram.Edges.Single();

        Assert.True(edge.Relation.IsSelfReference);
        Assert.Contains(edge.Points, p => p.X > box.Right);
        Assert.True(diagram.Width >= box.Right + ErdMetrics.SelfLoopWidth);
    }

    [Fact]
    public void DisconnectedTablesArePackedSideBySide()
    {
        var graph = new ErdGraph([Table("alpha", "a_key"), Table("beta", "b_key")], []);

        var diagram = ErdLayout.Compute(graph);
        var box = diagram.Boxes.ToDictionary(b => b.Table.Name);

        Assert.True(box["alpha"].Right <= box["beta"].X || box["beta"].Right <= box["alpha"].X);
    }

    [Fact]
    public void KeyColumnsOnlyHidesPlainColumns()
    {
        var table = new ErdTable("public", "study", false,
        [
            new ErdColumn("study_key", "bigint", true, IsPk: true, IsFk: false),
            new ErdColumn("patient_name", "text", false, IsPk: false, IsFk: false),
        ]);
        var graph = new ErdGraph([table], []);

        var keysOnly = ErdLayout.Compute(graph).Boxes.Single();
        var all = ErdLayout.Compute(graph, ErdLayoutOptions.Default with { KeyColumnsOnly = false }).Boxes.Single();

        Assert.Equal(["study_key"], keysOnly.VisibleColumns.Select(c => c.Name));
        Assert.Equal(["study_key", "patient_name"], all.VisibleColumns.Select(c => c.Name));
    }

    [Fact]
    public void TableWithoutKeysStillShowsSomeColumns()
    {
        var table = new ErdTable("public", "log", false,
            [new ErdColumn("message", "text", false, IsPk: false, IsFk: false)]);

        var box = ErdLayout.Compute(new ErdGraph([table], [])).Boxes.Single();

        Assert.Single(box.VisibleColumns);
        Assert.Equal("message", box.VisibleColumns[0].Name);
    }

    [Fact]
    public void ColumnsBeyondTheCapAreCounted()
    {
        var columns = Enumerable.Range(0, 20)
            .Select(i => new ErdColumn($"c{i}", "int", true, IsPk: true, IsFk: false))
            .ToList();
        var graph = new ErdGraph([new ErdTable("public", "wide", false, columns)], []);

        var box = ErdLayout.Compute(graph, ErdLayoutOptions.Default with { MaxColumnsPerTable = 5 }).Boxes.Single();

        Assert.Equal(5, box.VisibleColumns.Count);
        Assert.Equal(15, box.HiddenColumnCount);
    }

    // ---------- 주제영역(Subject Area) ----------

    [Fact]
    public void ComponentGroupingFramesEachConnectedChunk()
    {
        var graph = new ErdGraph(
            [.. Chain().Tables, Table("alone", "a_key")],
            Chain().Relations);

        var diagram = ErdLayout.Compute(graph);

        Assert.Equal(2, diagram.Groups.Count);
        Assert.Equal([3, 1], diagram.Groups.Select(g => g.TableCount));
    }

    [Fact]
    public void EveryBoxSitsInsideItsGroupFrame()
    {
        var diagram = ErdLayout.Compute(Chain());
        var frames = diagram.Groups.ToDictionary(g => g.ColorIndex);

        foreach (var box in diagram.Boxes)
        {
            var frame = frames[box.GroupIndex];
            Assert.True(box.X >= frame.X && box.Right <= frame.X + frame.Width,
                $"{box.Table.Key} 가 영역 밖으로 나갔다 (가로)");
            Assert.True(box.Y >= frame.Y && box.Bottom <= frame.Y + frame.Height,
                $"{box.Table.Key} 가 영역 밖으로 나갔다 (세로)");
        }
    }

    [Fact]
    public void GroupingNoneDrawsNoFrames()
    {
        var diagram = ErdLayout.Compute(Chain(), ErdLayoutOptions.Default with { Grouping = ErdGrouping.None });

        Assert.Empty(diagram.Groups);
        Assert.All(diagram.Boxes, b => Assert.Equal(0, b.GroupIndex));
    }

    [Fact]
    public void PrefixGroupingSplitsByNamePrefix()
    {
        var graph = new ErdGraph(
        [
            Table("req_order", "k"), Table("req_item", "k"),
            Table("arc_job", "k"), Table("arc_target", "k"),
        ], []);

        var diagram = ErdLayout.Compute(graph, ErdLayoutOptions.Default with { Grouping = ErdGrouping.Prefix });

        Assert.Equal(["arc", "req"], diagram.Groups.Select(g => g.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(diagram.Groups, g => Assert.Equal(2, g.TableCount));
    }

    [Fact]
    public void SelfReferenceLoopStaysInsideItsGroupFrame()
    {
        var graph = new ErdGraph([Child("node", "parent_key")], [Fk("node", "node", "parent_key")]);

        var diagram = ErdLayout.Compute(graph);
        var frame = diagram.Groups.Single();
        var rightmost = diagram.Edges.Single().Points.Max(p => p.X);

        Assert.True(rightmost <= frame.X + frame.Width, "자기참조 고리가 영역 테두리를 넘었다");
    }

    // ---------- ErdGraph ----------

    [Fact]
    public void FocusKeepsOnlyNeighboursWithinHops()
    {
        var graph = Chain();

        var oneHop = graph.Focus(["public.series"], hops: 1);

        Assert.Equal(
            ["public.image", "public.series", "public.study"],
            oneHop.Tables.Select(t => t.Key).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(2, oneHop.Relations.Count);
    }

    [Fact]
    public void FocusWithZeroHopsKeepsOnlyTheSeed()
    {
        var focused = Chain().Focus(["public.series"], hops: 0);

        Assert.Equal(["public.series"], focused.Tables.Select(t => t.Key));
        Assert.Empty(focused.Relations);
    }

    [Fact]
    public void FocusWithUnknownSeedIsEmpty()
        => Assert.Same(ErdGraph.Empty, Chain().Focus(["public.nope"], hops: 2));

    [Fact]
    public void NegativeHopsMeansWholeGraph()
    {
        var graph = Chain();

        Assert.Same(graph, graph.Focus(["public.series"], hops: -1));
    }

    [Fact]
    public void FilterDropsRelationsWhoseOtherEndIsGone()
    {
        var filtered = Chain().Filter("ser");

        Assert.Equal(["public.series"], filtered.Tables.Select(t => t.Key));
        Assert.Empty(filtered.Relations);
    }

    [Fact]
    public void BlankFilterKeepsEverything()
    {
        var graph = Chain();

        Assert.Same(graph, graph.Filter("  "));
    }
}
