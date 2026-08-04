using System.Text.Json;

namespace PrismOne.Db.Core;

/// <summary>EXPLAIN (FORMAT JSON) 플랜 트리의 노드 하나.</summary>
public sealed class PlanNode
{
    public required string Title { get; init; }        // "Seq Scan on study s" / "Hash Join (Inner)"
    public required string Detail { get; init; }       // cost/rows, analyze 면 actual time/rows/loops
    public string? Extra { get; init; }                // Filter/Index Cond 등 (툴팁용)
    /// <summary>이 노드가 소비한 총 시간(ms, per-loop×loops). ANALYZE 일 때만.</summary>
    public double? TotalMs { get; init; }
    public double? TotalCost { get; init; }
    public double? PlanRows { get; init; }
    /// <summary>실제 행 수(loops 곱). ANALYZE 일 때만.</summary>
    public double? ActualRows { get; init; }
    public List<PlanNode> Children { get; } = [];

    /// <summary>
    /// 이 노드 **자신의** 비용 — PG 의 Total Cost 는 자식 포함 누적이라
    /// 그대로 비교하면 루트가 항상 제일 비싸 보인다. 자식 몫을 뺀 값으로 강조를 정한다.
    /// </summary>
    public double SelfCost { get; internal set; }

    /// <summary>이 노드 자신의 시간(ms). ANALYZE 일 때만.</summary>
    public double? SelfMs { get; internal set; }

    /// <summary>
    /// 예측 행수 대비 실제 행수의 배율(큰 쪽/작은 쪽, ≥1). 크게 어긋난 노드가
    /// 플랜이 틀어진 원인일 때가 많다 — 10배 이상이면 UI 가 배지를 붙인다.
    /// </summary>
    public double? RowsEstimateError =>
        PlanRows is { } plan && ActualRows is { } actual
            ? Math.Max(plan, 1) is var p && Math.Max(actual, 1) is var a
                ? Math.Max(p, a) / Math.Min(p, a)
                : null
            : null;
}

public sealed class PlanResult
{
    public required PlanNode Root { get; init; }
    public double? PlanningMs { get; init; }
    public double? ExecutionMs { get; init; }

    /// <summary>트리 전체의 self 비용 합 — 노드 막대의 분모.</summary>
    public double SelfCostTotal => Walk(Root).Sum(n => n.SelfCost);

    /// <summary>트리 전체의 self 시간 합 (ANALYZE 일 때만 의미).</summary>
    public double SelfMsTotal => Walk(Root).Sum(n => n.SelfMs ?? 0);

    public static IEnumerable<PlanNode> Walk(PlanNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var n in Walk(child))
                yield return n;
    }
}

/// <summary>EXPLAIN (FORMAT JSON) 출력 파서 (pgAdmin 의 그래픽 플랜에 해당하는 트리 데이터).</summary>
public static class PlanParser
{
    public static PlanResult? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var top = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
                ? doc.RootElement[0]
                : doc.RootElement;
            if (!top.TryGetProperty("Plan", out var plan))
                return null;
            var root = ParseNode(plan);
            ComputeSelf(root);
            return new PlanResult
            {
                Root = root,
                PlanningMs = GetDouble(top, "Planning Time"),
                ExecutionMs = GetDouble(top, "Execution Time"),
            };
        }
        catch
        {
            return null;
        }
    }

    private static PlanNode ParseNode(JsonElement e)
    {
        var nodeType = GetString(e, "Node Type") ?? "?";
        var title = nodeType;
        if (GetString(e, "Relation Name") is { } rel)
        {
            title += $" on {rel}";
            if (GetString(e, "Alias") is { } alias && alias != rel)
                title += $" {alias}";
        }
        if (GetString(e, "Index Name") is { } index)
            title += $" using {index}";
        if (GetString(e, "Join Type") is { } join && join != "Inner")
            title += $" ({join})";

        var startup = GetDouble(e, "Startup Cost");
        var total = GetDouble(e, "Total Cost");
        var planRows = GetDouble(e, "Plan Rows");
        var detail = $"cost={startup:0.##}..{total:0.##}  rows≈{planRows:0}";

        double? totalMs = null;
        double? actualRowsPerLoop = null;
        if (GetDouble(e, "Actual Total Time") is { } actual)
        {
            var loops = GetDouble(e, "Actual Loops") ?? 1;
            // Plan Rows 도 Actual Rows 도 루프당 값 — 예측 오차는 같은 눈금끼리 비교한다
            actualRowsPerLoop = GetDouble(e, "Actual Rows") ?? 0;
            totalMs = actual * loops;
            detail += $"   |   actual {totalMs:0.###} ms · rows={actualRowsPerLoop:0} · loops={loops:0}";
        }

        var extras = new List<string>();
        foreach (var key in new[] { "Index Cond", "Filter", "Hash Cond", "Join Filter", "Sort Key", "Rows Removed by Filter" })
        {
            if (e.TryGetProperty(key, out var v))
                extras.Add($"{key}: {v}");
        }

        var node = new PlanNode
        {
            Title = title,
            Detail = detail,
            Extra = extras.Count > 0 ? string.Join("\n", extras) : null,
            TotalMs = totalMs,
            TotalCost = total,
            PlanRows = planRows,
            ActualRows = actualRowsPerLoop,
        };
        if (e.TryGetProperty("Plans", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
                node.Children.Add(ParseNode(child));
        }
        return node;
    }

    /// <summary>
    /// 누적치(Total Cost·actual time)에서 자식 몫을 빼 자기 몫을 만든다.
    /// InitPlan/CTE 처럼 회계가 어긋나는 노드는 0 으로 자른다 — 음수 막대는 없다.
    /// </summary>
    private static void ComputeSelf(PlanNode node)
    {
        foreach (var child in node.Children)
            ComputeSelf(child);
        node.SelfCost = Math.Max(0,
            (node.TotalCost ?? 0) - node.Children.Sum(c => c.TotalCost ?? 0));
        if (node.TotalMs is { } ms)
            node.SelfMs = Math.Max(0, ms - node.Children.Sum(c => c.TotalMs ?? 0));
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? GetDouble(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
