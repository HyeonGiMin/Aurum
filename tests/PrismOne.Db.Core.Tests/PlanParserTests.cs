using PrismOne.Db.Core;
using Xunit;

namespace PrismOne.Db.Core.Tests;

public class PlanParserTests
{
    private const string SampleAnalyze = """
        [{
          "Plan": {
            "Node Type": "Hash Join",
            "Join Type": "Inner",
            "Startup Cost": 10.5, "Total Cost": 220.75, "Plan Rows": 1000,
            "Actual Total Time": 12.5, "Actual Rows": 980, "Actual Loops": 1,
            "Hash Cond": "(s.study_key = e.study_key)",
            "Plans": [
              { "Node Type": "Seq Scan", "Relation Name": "study", "Alias": "s",
                "Startup Cost": 0, "Total Cost": 100, "Plan Rows": 5000,
                "Actual Total Time": 4.0, "Actual Rows": 5000, "Actual Loops": 1,
                "Filter": "(study_dttm >= '2026-07-01')" },
              { "Node Type": "Index Scan", "Relation Name": "examlist", "Alias": "e",
                "Index Name": "pk_examlist",
                "Startup Cost": 0.2, "Total Cost": 80, "Plan Rows": 1200,
                "Actual Total Time": 0.5, "Actual Rows": 1200, "Actual Loops": 2 }
            ]
          },
          "Planning Time": 0.35,
          "Execution Time": 13.1
        }]
        """;

    [Fact]
    public void Parse_BuildsTreeWithTimesAndExtras()
    {
        var result = PlanParser.Parse(SampleAnalyze);
        Assert.NotNull(result);
        Assert.Equal(0.35, result!.PlanningMs);
        Assert.Equal(13.1, result.ExecutionMs);
        Assert.StartsWith("Hash Join", result.Root.Title);
        Assert.Equal(2, result.Root.Children.Count);
        Assert.Contains("Seq Scan on study s", result.Root.Children[0].Title);
        Assert.Contains("Filter", result.Root.Children[0].Extra);
        // loops=2 → per-loop 0.5ms × 2 = 1.0ms
        Assert.Equal(1.0, result.Root.Children[1].TotalMs!.Value, 3);
        Assert.Contains("using pk_examlist", result.Root.Children[1].Title);
    }

    [Fact]
    public void Parse_PlainExplainWithoutAnalyze()
    {
        var result = PlanParser.Parse("""[{"Plan":{"Node Type":"Seq Scan","Relation Name":"study","Startup Cost":0,"Total Cost":10,"Plan Rows":5}}]""");
        Assert.NotNull(result);
        Assert.Null(result!.Root.TotalMs);
        Assert.Null(result.ExecutionMs);
    }

    [Fact]
    public void Parse_GarbageReturnsNull()
        => Assert.Null(PlanParser.Parse("not json"));
}
