using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;

namespace PrismOne.Db.Core.Mongo;

/// <summary>
/// Mongo <c>explain()</c> — explain 커맨드를 만들고, 결과를 PG 와 같은
/// <see cref="PlanResult"/> 트리로 옮긴다. 그래서 Explain 버튼·플랜 트리·self 막대
/// UI 가 **그대로 재사용**된다 (⚡ᴱ = queryPlanner, ⚡ᴬ = executionStats).
/// </summary>
public static class MongoExplain
{
    /// <summary>실행할 explain 커맨드 문서. 읽기 연산(find/aggregate/count/distinct)만 가능하다.</summary>
    public static BsonDocument BuildCommand(MongoCommand command, string verbosity)
    {
        var inner = command.Operation switch
        {
            MongoOperation.Find => BuildFind(command),
            MongoOperation.Aggregate => new BsonDocument
            {
                ["aggregate"] = command.Collection,
                ["pipeline"] = command.Pipeline ?? [],
                ["cursor"] = new BsonDocument(),
            },
            MongoOperation.CountDocuments => new BsonDocument
            {
                ["count"] = command.Collection,
                ["query"] = command.Filter ?? new BsonDocument(),
            },
            MongoOperation.Distinct => new BsonDocument
            {
                ["distinct"] = command.Collection,
                ["key"] = command.DistinctField,
                ["query"] = command.Filter ?? new BsonDocument(),
            },
            _ => throw new MongoQueryException(
                "explain 은 find / aggregate / countDocuments / distinct 에만 쓸 수 있습니다."),
        };
        return new BsonDocument { ["explain"] = inner, ["verbosity"] = verbosity };
    }

    private static BsonDocument BuildFind(MongoCommand command)
    {
        var find = new BsonDocument
        {
            ["find"] = command.Collection,
            ["filter"] = command.Filter ?? new BsonDocument(),
        };
        if (command.Projection is not null) find["projection"] = command.Projection;
        if (command.Sort is not null) find["sort"] = command.Sort;
        if (command.Skip is { } skip) find["skip"] = skip;
        if (command.Limit is { } limit) find["limit"] = limit;
        return find;
    }

    /// <summary>
    /// explain 결과 → 플랜 트리. executionStats 가 있으면 스테이지별 실측
    /// (시간·반환/검사 문서 수)을, 없으면 queryPlanner 의 winningPlan 만 담는다.
    /// 모양을 모르는 결과는 버리지 않고 한 노드에 원문(JSON)을 싣는다.
    /// </summary>
    public static PlanResult Parse(BsonDocument explain)
    {
        PlanNode root;
        double? executionMs = null;

        if (explain.TryGetValue("stages", out var stages) && stages is BsonArray stageArray)
        {
            // aggregate — 밀어 넣지 못한 파이프라인은 스테이지 배열로 온다
            root = new PlanNode { Title = "Aggregation Pipeline", Detail = $"{stageArray.Count} stage(s)" };
            foreach (var stage in stageArray.OfType<BsonDocument>())
                root.Children.Add(ParsePipelineStage(stage, ref executionMs));
        }
        else if (Exec(explain) is { } exec &&
                 exec.TryGetValue("executionStages", out var execStages) && execStages is BsonDocument execRoot)
        {
            root = ParseStage(execRoot);
            executionMs = Number(exec, "executionTimeMillis");
        }
        else if (explain.TryGetValue("queryPlanner", out var planner) && planner is BsonDocument p &&
                 p.TryGetValue("winningPlan", out var winning) && winning is BsonDocument w)
        {
            root = ParseStage(w);
        }
        else
        {
            root = new PlanNode { Title = "explain", Detail = "", Extra = Pretty(explain) };
        }

        PlanParser.ComputeSelf(root);
        return new PlanResult { Root = root, ExecutionMs = executionMs, PlanningMs = null };
    }

    private static BsonDocument? Exec(BsonDocument explain) =>
        explain.TryGetValue("executionStats", out var v) && v is BsonDocument d ? d : null;

    /// <summary>파이프라인 스테이지 하나 — <c>$cursor</c> 는 안에 find 플랜을 통째로 품는다.</summary>
    private static PlanNode ParsePipelineStage(BsonDocument stage, ref double? executionMs)
    {
        var name = stage.Names.FirstOrDefault(n => n.StartsWith('$')) ?? "stage";
        if (name == "$cursor" && stage[name] is BsonDocument cursor)
        {
            var inner = Parse(cursor);
            executionMs ??= inner.ExecutionMs;
            var node = new PlanNode { Title = "$cursor", Detail = "" };
            node.Children.Add(inner.Root);
            return node;
        }

        var detailParts = new List<string>();
        if (Number(stage, "executionTimeMillisEstimate") is { } ms) detailParts.Add($"actual {ms:0.###} ms");
        if (Number(stage, "nReturned") is { } returned) detailParts.Add($"returned={returned:0}");
        return new PlanNode
        {
            Title = name,
            Detail = string.Join(" · ", detailParts),
            TotalMs = Number(stage, "executionTimeMillisEstimate"),
            Extra = stage[name] is BsonDocument body ? Pretty(body) : null,
        };
    }

    /// <summary>winningPlan / executionStages 공용 — 필드가 있으면 싣고 없으면 넘어간다.</summary>
    private static PlanNode ParseStage(BsonDocument stage)
    {
        var title = stage.TryGetValue("stage", out var s) ? s.AsString : "?";
        if (stage.TryGetValue("indexName", out var index))
            title += $" ({index.AsString})";

        var parts = new List<string>();
        if (Number(stage, "executionTimeMillisEstimate") is { } ms) parts.Add($"actual {ms:0.###} ms");
        if (Number(stage, "nReturned") is { } returned) parts.Add($"returned={returned:0}");
        if (Number(stage, "docsExamined") is { } docs) parts.Add($"docsExamined={docs:0}");
        if (Number(stage, "keysExamined") is { } keys) parts.Add($"keysExamined={keys:0}");
        if (stage.TryGetValue("direction", out var dir)) parts.Add(dir.AsString);
        if (stage.TryGetValue("keyPattern", out var pattern)) parts.Add(pattern.ToString()!);

        var extras = new List<string>();
        if (stage.TryGetValue("filter", out var filter)) extras.Add($"filter: {filter}");
        if (stage.TryGetValue("indexBounds", out var bounds)) extras.Add($"indexBounds: {bounds}");

        var node = new PlanNode
        {
            Title = title,
            Detail = string.Join(" · ", parts),
            TotalMs = Number(stage, "executionTimeMillisEstimate"),
            Extra = extras.Count > 0 ? string.Join("\n", extras) : null,
        };

        if (stage.TryGetValue("inputStage", out var input) && input is BsonDocument single)
            node.Children.Add(ParseStage(single));
        if (stage.TryGetValue("inputStages", out var inputs) && inputs is BsonArray many)
            foreach (var child in many.OfType<BsonDocument>())
                node.Children.Add(ParseStage(child));
        // COUNT/shards 류가 쓰는 다른 이름들
        if (stage.TryGetValue("queryPlan", out var qp) && qp is BsonDocument queryPlan)
            node.Children.Add(ParseStage(queryPlan));
        return node;
    }

    private static double? Number(BsonDocument doc, string name) =>
        doc.TryGetValue(name, out var v) && v.IsNumeric ? v.ToDouble() : null;

    private static string Pretty(BsonDocument doc) =>
        doc.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { Indent = true });
}
