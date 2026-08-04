namespace PrismOne.Db.Core;

/// <summary>박스·글자 치수. 레이아웃과 렌더가 같은 값을 써야 선이 박스에 정확히 붙는다.</summary>
public static class ErdMetrics
{
    public const double FontSize = 12;
    public const double TitleFontSize = 12.5;
    /// <summary>비례폭 글꼴의 평균 글자폭 근사 — Core 는 UI 에 의존하지 않으므로 상수로 잡는다.</summary>
    public const double CharWidth = 6.9;
    public const double RowHeight = 18;
    public const double HeaderHeight = 26;
    public const double Padding = 10;
    public const double MinBoxWidth = 150;
    public const double MaxBoxWidth = 340;
    /// <summary>다이어그램 바깥 여백.</summary>
    public const double Margin = 36;
    /// <summary>자기참조 루프가 박스 오른쪽으로 삐져나가는 폭.</summary>
    public const double SelfLoopWidth = 26;
    /// <summary>주제영역 테두리와 안쪽 박스 사이 여백.</summary>
    public const double GroupPadding = 16;
    /// <summary>주제영역 제목줄 높이.</summary>
    public const double GroupTitleHeight = 22;
}

/// <summary>테이블을 어떤 기준으로 묶어 주제영역(subject area)을 만들지.</summary>
public enum ErdGrouping
{
    /// <summary>묶지 않는다 — 테두리 없이 배치만.</summary>
    None,

    /// <summary>FK 로 이어진 덩어리(연결 요소)끼리. 가장 자동적이고 대개 의미가 맞는다.</summary>
    Component,

    /// <summary>테이블 이름 접두어(`ihp_request_...`)끼리. 명명 규칙이 뚜렷한 스키마에 잘 맞는다.</summary>
    Prefix,
}

public sealed record ErdLayoutOptions(
    bool KeyColumnsOnly = true,
    int MaxColumnsPerTable = 14,
    double HorizontalGap = 56,
    double VerticalGap = 72,
    double PackWidth = 2600,
    ErdGrouping Grouping = ErdGrouping.Component)
{
    public static ErdLayoutOptions Default { get; } = new();
}

public readonly record struct ErdPoint(double X, double Y);

/// <summary>주제영역 테두리 하나. <c>ColorIndex</c> 로 렌더가 팔레트 색을 고른다.</summary>
public sealed record ErdGroup(
    string Name,
    int ColorIndex,
    int TableCount,
    double X,
    double Y,
    double Width,
    double Height);

/// <summary>배치가 끝난 테이블 박스.</summary>
public sealed record ErdBox(
    ErdTable Table,
    IReadOnlyList<ErdColumn> VisibleColumns,
    int HiddenColumnCount,
    int GroupIndex,
    double X,
    double Y,
    double Width,
    double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

/// <summary>관계선 하나. Points[0] 이 자식(FK) 쪽 끝, 마지막이 부모(PK) 쪽 끝.</summary>
public sealed record ErdEdge(ErdRelation Relation, IReadOnlyList<ErdPoint> Points);

public sealed record ErdDiagram(
    IReadOnlyList<ErdBox> Boxes,
    IReadOnlyList<ErdEdge> Edges,
    IReadOnlyList<ErdGroup> Groups,
    double Width,
    double Height)
{
    public static ErdDiagram Empty { get; } = new([], [], [], 0, 0);
}

/// <summary>
/// ErdGraph → 좌표. UI 의존 없는 순수 함수라 단위 테스트가 붙는다.
/// Sugiyama 풀구현 대신: 주제영역 분리 → 참조 깊이 레이어 → 바리센터 정렬 →
/// 영역 패킹 → 직교 엣지 라우팅. 같은 입력이면 항상 같은 출력(결정적).
/// </summary>
public static class ErdLayout
{
    /// <summary>접두어 한 토막이 이 비율을 넘게 먹으면 토막을 하나 더 써서 쪼갠다.</summary>
    private const double PrefixDominanceLimit = 0.6;

    public static ErdDiagram Compute(ErdGraph graph, ErdLayoutOptions? options = null)
    {
        var opt = options ?? ErdLayoutOptions.Default;
        if (graph.Tables.Count == 0) return ErdDiagram.Empty;

        var sized = graph.Tables
            .OrderBy(t => t.Key, StringComparer.Ordinal)
            .Select(t => Measure(t, opt))
            .ToDictionary(b => b.Table.Key);

        var groups = BuildGroups(sized, graph.Relations, opt.Grouping);
        var framed = opt.Grouping != ErdGrouping.None;
        var insetX = framed ? ErdMetrics.GroupPadding : 0;
        var insetY = framed ? ErdMetrics.GroupPadding + ErdMetrics.GroupTitleHeight : 0;

        var placed = new Dictionary<string, ErdBox>();
        var frames = new List<ErdGroup>();
        double rowX = 0, rowY = 0, rowHeight = 0;

        for (var index = 0; index < groups.Count; index++)
        {
            var (name, keys) = groups[index];
            var local = LayoutComponent(keys, graph.Relations, sized, opt);
            if (local.Count == 0) continue;

            // 자기참조 고리는 박스 오른쪽으로 삐져나가므로 영역 폭에 미리 반영한다.
            var members = keys.ToHashSet();
            var hasSelfLoop = graph.Relations.Any(r => r.IsSelfReference && members.Contains(r.ChildKey));
            var innerWidth = local.Values.Max(b => b.Right) + (hasSelfLoop ? ErdMetrics.SelfLoopWidth : 0);
            var innerHeight = local.Values.Max(b => b.Bottom);
            var frameWidth = innerWidth + insetX * 2;
            var frameHeight = innerHeight + insetY + (framed ? ErdMetrics.GroupPadding : 0);
            // 제목이 테두리보다 길면 테두리를 넓힌다.
            if (framed)
                frameWidth = Math.Max(frameWidth, name.Length * ErdMetrics.CharWidth + ErdMetrics.GroupPadding * 2);

            if (rowX > 0 && rowX + frameWidth > opt.PackWidth)
            {
                rowY += rowHeight + opt.VerticalGap;
                rowX = 0;
                rowHeight = 0;
            }

            var originX = rowX + ErdMetrics.Margin;
            var originY = rowY + ErdMetrics.Margin;
            foreach (var (key, box) in local)
                placed[key] = box with
                {
                    GroupIndex = index,
                    X = box.X + originX + insetX,
                    Y = box.Y + originY + insetY,
                };

            if (framed)
                frames.Add(new ErdGroup(name, index, local.Count, originX, originY, frameWidth, frameHeight));

            rowHeight = Math.Max(rowHeight, frameHeight);
            rowX += frameWidth + opt.HorizontalGap;
        }

        var boxes = placed.Values.OrderBy(b => b.Table.Key, StringComparer.Ordinal).ToList();
        var edges = graph.Relations
            .Select(r => Route(r, placed))
            .OfType<ErdEdge>()
            .ToList();

        var width = Math.Max(
            boxes.Max(b => b.Right) + ErdMetrics.SelfLoopWidth,
            frames.Count == 0 ? 0 : frames.Max(f => f.X + f.Width)) + ErdMetrics.Margin;
        var height = Math.Max(
            boxes.Max(b => b.Bottom),
            frames.Count == 0 ? 0 : frames.Max(f => f.Y + f.Height)) + ErdMetrics.Margin;
        return new ErdDiagram(boxes, edges, frames, width, height);
    }

    // ---------- 박스 치수 ----------

    private static ErdBox Measure(ErdTable table, ErdLayoutOptions opt)
    {
        var candidates = opt.KeyColumnsOnly
            ? table.Columns.Where(c => c.IsPk || c.IsFk).ToList()
            : table.Columns.ToList();
        // 키만 보기인데 키가 하나도 없으면 빈 박스가 되므로 앞쪽 컬럼이라도 보여준다.
        if (candidates.Count == 0)
            candidates = table.Columns.Take(3).ToList();

        var hidden = Math.Max(0, candidates.Count - opt.MaxColumnsPerTable);
        var visible = candidates.Take(opt.MaxColumnsPerTable).ToList();

        var widest = visible
            .Select(c => $"{c.Name}  {c.Type}".Length)
            .Append(table.Name.Length + 4)
            .Max();
        var width = Math.Clamp(
            widest * ErdMetrics.CharWidth + ErdMetrics.Padding * 2,
            ErdMetrics.MinBoxWidth,
            ErdMetrics.MaxBoxWidth);

        var rows = visible.Count + (hidden > 0 ? 1 : 0);
        var height = ErdMetrics.HeaderHeight + Math.Max(1, rows) * ErdMetrics.RowHeight + ErdMetrics.Padding;
        return new ErdBox(table, visible, hidden, 0, 0, 0, Math.Round(width), Math.Round(height));
    }

    // ---------- 주제영역 ----------

    private static List<(string Name, List<string> Keys)> BuildGroups(
        Dictionary<string, ErdBox> sized, IReadOnlyList<ErdRelation> relations, ErdGrouping grouping) =>
        grouping switch
        {
            ErdGrouping.None => [("", sized.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())],
            ErdGrouping.Prefix => PrefixGroups(sized),
            _ => ComponentGroups(sized.Keys, relations),
        };

    /// <summary>FK 로 이어진 덩어리. 이름은 연결이 가장 많은 테이블(허브)에서 딴다.</summary>
    private static List<(string Name, List<string> Keys)> ComponentGroups(
        IEnumerable<string> keys, IReadOnlyList<ErdRelation> relations)
    {
        var parent = keys.ToDictionary(k => k, k => k);

        string Find(string k)
        {
            while (parent[k] != k) k = parent[k] = parent[parent[k]];
            return k;
        }

        var degree = parent.Keys.ToDictionary(k => k, _ => 0);
        foreach (var rel in relations)
        {
            if (!parent.ContainsKey(rel.ChildKey) || !parent.ContainsKey(rel.ParentKey)) continue;
            degree[rel.ChildKey]++;
            degree[rel.ParentKey]++;
            var (a, b) = (Find(rel.ChildKey), Find(rel.ParentKey));
            if (a != b) parent[a] = b;
        }

        return parent.Keys
            .GroupBy(Find)
            .Select(g =>
            {
                var members = g.OrderBy(k => k, StringComparer.Ordinal).ToList();
                var hub = members
                    .OrderByDescending(k => degree[k])
                    .ThenBy(k => k, StringComparer.Ordinal)
                    .First();
                return (Name: NameOf(hub), Keys: members);
            })
            // 큰 덩어리를 먼저 놓아야 패킹이 덜 들쭉날쭉하다. 동률은 이름으로 확정.
            .OrderByDescending(g => g.Keys.Count)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>이름 접두어로 묶는다. 한 토막이 거의 전부를 먹으면 토막을 하나 더 쓴다.</summary>
    private static List<(string Name, List<string> Keys)> PrefixGroups(Dictionary<string, ErdBox> sized)
    {
        var names = sized.Keys.ToDictionary(k => k, k => NameOf(k));
        var groups = Group(1);
        if (groups.Count > 0 && sized.Count >= 8)
        {
            var biggest = groups.Max(g => g.Keys.Count);
            if (biggest > sized.Count * PrefixDominanceLimit)
                groups = Group(2);
        }
        return groups;

        List<(string Name, List<string> Keys)> Group(int depth) => names
            .GroupBy(p => Prefix(p.Value, depth))
            .Select(g => (
                Name: g.Key,
                Keys: g.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToList()))
            .OrderByDescending(g => g.Keys.Count)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static string Prefix(string tableName, int depth)
    {
        var parts = tableName.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= depth ? tableName : string.Join('_', parts.Take(depth));
    }

    /// <summary>"schema.table" 에서 테이블 이름만.</summary>
    private static string NameOf(string key)
    {
        var dot = key.IndexOf('.');
        return dot < 0 ? key : key[(dot + 1)..];
    }

    // ---------- 한 영역 배치 ----------

    private static Dictionary<string, ErdBox> LayoutComponent(
        List<string> keys,
        IReadOnlyList<ErdRelation> allRelations,
        Dictionary<string, ErdBox> sized,
        ErdLayoutOptions opt)
    {
        var member = keys.ToHashSet();
        var relations = allRelations
            .Where(r => member.Contains(r.ChildKey) && member.Contains(r.ParentKey) && !r.IsSelfReference)
            .ToList();

        var depth = AssignDepths(keys, relations);
        var layers = keys
            .GroupBy(k => depth[k])
            .OrderBy(g => g.Key)
            .Select(g => g.OrderBy(k => k, StringComparer.Ordinal).ToList())
            .ToList();

        ReduceCrossings(layers, relations);
        WrapWideLayers(layers, sized, opt);

        var layerWidths = layers
            .Select(l => l.Sum(k => sized[k].Width) + Math.Max(0, l.Count - 1) * opt.HorizontalGap)
            .ToList();
        var componentWidth = layerWidths.Count == 0 ? 0 : layerWidths.Max();

        var result = new Dictionary<string, ErdBox>();
        double y = 0;
        for (var i = 0; i < layers.Count; i++)
        {
            var x = (componentWidth - layerWidths[i]) / 2;
            double tallest = 0;
            foreach (var key in layers[i])
            {
                var box = sized[key];
                result[key] = box with { X = Math.Round(x), Y = Math.Round(y) };
                x += box.Width + opt.HorizontalGap;
                tallest = Math.Max(tallest, box.Height);
            }
            y += tallest + opt.VerticalGap;
        }
        return result;
    }

    /// <summary>
    /// FK 없는 테이블이 몰리면 한 줄이 끝없이 길어진다 — PackWidth 를 넘는 레이어는
    /// 여러 줄로 접는다.
    /// </summary>
    private static void WrapWideLayers(
        List<List<string>> layers, Dictionary<string, ErdBox> sized, ErdLayoutOptions opt)
    {
        for (var i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            var width = layer.Sum(k => sized[k].Width) + Math.Max(0, layer.Count - 1) * opt.HorizontalGap;
            if (width <= opt.PackWidth || layer.Count < 2) continue;

            var rows = (int)Math.Ceiling(width / opt.PackWidth);
            var perRow = (int)Math.Ceiling(layer.Count / (double)rows);
            var chunks = layer.Chunk(perRow).Select(c => c.ToList()).ToList();
            layers.RemoveAt(i);
            layers.InsertRange(i, chunks);
            i += chunks.Count - 1;
        }
    }

    /// <summary>부모(참조되는 쪽)가 위로 가도록 깊이를 준다. 순환은 반복 횟수 상한으로 끊는다.</summary>
    private static Dictionary<string, int> AssignDepths(List<string> keys, List<ErdRelation> relations)
    {
        var depth = keys.ToDictionary(k => k, _ => 0);
        for (var pass = 0; pass < keys.Count; pass++)
        {
            var changed = false;
            foreach (var rel in relations)
            {
                var want = depth[rel.ParentKey] + 1;
                if (depth[rel.ChildKey] < want)
                {
                    depth[rel.ChildKey] = want;
                    changed = true;
                }
            }
            if (!changed) break;
        }
        return depth;
    }

    /// <summary>이웃의 평균 위치(바리센터)로 레이어 안 순서를 정리한다. 아래로 1회, 위로 1회.</summary>
    private static void ReduceCrossings(List<List<string>> layers, List<ErdRelation> relations)
    {
        if (layers.Count < 2) return;

        var parents = relations.ToLookup(r => r.ChildKey, r => r.ParentKey);
        var children = relations.ToLookup(r => r.ParentKey, r => r.ChildKey);

        for (var i = 1; i < layers.Count; i++)
            Sweep(layers[i], layers[i - 1], parents);
        for (var i = layers.Count - 2; i >= 0; i--)
            Sweep(layers[i], layers[i + 1], children);

        static void Sweep(List<string> layer, List<string> reference, ILookup<string, string> neighbors)
        {
            var index = reference
                .Select((k, i) => (k, i))
                .ToDictionary(p => p.k, p => (double)p.i);
            var current = layer
                .Select((k, i) => (k, i))
                .ToDictionary(p => p.k, p => (double)p.i);

            var ordered = layer
                .Select(k =>
                {
                    var near = neighbors[k].Where(index.ContainsKey).Select(n => index[n]).ToList();
                    return (Key: k, Bary: near.Count > 0 ? near.Average() : current[k]);
                })
                .OrderBy(p => p.Bary)
                .ThenBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key)
                .ToList();

            layer.Clear();
            layer.AddRange(ordered);
        }
    }

    // ---------- 엣지 라우팅 ----------

    private static ErdEdge? Route(ErdRelation rel, Dictionary<string, ErdBox> boxes)
    {
        if (!boxes.TryGetValue(rel.ChildKey, out var child) ||
            !boxes.TryGetValue(rel.ParentKey, out var parent))
            return null;

        if (rel.IsSelfReference)
        {
            var top = child.Y + child.Height * 0.35;
            var bottom = child.Y + child.Height * 0.7;
            var outer = child.Right + ErdMetrics.SelfLoopWidth;
            return new ErdEdge(rel, [
                new ErdPoint(child.Right, top),
                new ErdPoint(outer, top),
                new ErdPoint(outer, bottom),
                new ErdPoint(child.Right, bottom),
            ]);
        }

        // 부모가 위 — 자식 위쪽 변에서 올라간다 (기본 형태)
        if (parent.Bottom <= child.Y)
        {
            var mid = (child.Y + parent.Bottom) / 2;
            return new ErdEdge(rel, [
                new ErdPoint(child.CenterX, child.Y),
                new ErdPoint(child.CenterX, mid),
                new ErdPoint(parent.CenterX, mid),
                new ErdPoint(parent.CenterX, parent.Bottom),
            ]);
        }

        // 부모가 아래 (순환을 끊은 경우 등)
        if (child.Bottom <= parent.Y)
        {
            var mid = (child.Bottom + parent.Y) / 2;
            return new ErdEdge(rel, [
                new ErdPoint(child.CenterX, child.Bottom),
                new ErdPoint(child.CenterX, mid),
                new ErdPoint(parent.CenterX, mid),
                new ErdPoint(parent.CenterX, parent.Y),
            ]);
        }

        // 같은 레이어 — 옆구리끼리 잇는다
        var rightward = parent.CenterX >= child.CenterX;
        var sx = rightward ? child.Right : child.X;
        var ex = rightward ? parent.X : parent.Right;
        var midX = (sx + ex) / 2;
        return new ErdEdge(rel, [
            new ErdPoint(sx, child.CenterY),
            new ErdPoint(midX, child.CenterY),
            new ErdPoint(midX, parent.CenterY),
            new ErdPoint(ex, parent.CenterY),
        ]);
    }
}
