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
    public List<PlanNode> Children { get; } = [];
}

public sealed class PlanResult
{
    public required PlanNode Root { get; init; }
    public double? PlanningMs { get; init; }
    public double? ExecutionMs { get; init; }
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
            return new PlanResult
            {
                Root = ParseNode(plan),
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
        if (GetDouble(e, "Actual Total Time") is { } actual)
        {
            var loops = GetDouble(e, "Actual Loops") ?? 1;
            var actualRows = GetDouble(e, "Actual Rows") ?? 0;
            totalMs = actual * loops;
            detail += $"   |   actual {totalMs:0.###} ms · rows={actualRows:0} · loops={loops:0}";
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
        };
        if (e.TryGetProperty("Plans", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
                node.Children.Add(ParseNode(child));
        }
        return node;
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? GetDouble(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
