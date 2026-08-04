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

    // ---------- 시각화용 self 지표 (누적 → 자기 몫) ----------

    [Fact]
    public void SelfCostSubtractsChildren()
    {
        var result = PlanParser.Parse(SampleAnalyze)!;

        // 220.75 - (100 + 80) = 40.75, 리프는 자기 값 그대로
        Assert.Equal(40.75, result.Root.SelfCost, 2);
        Assert.Equal(100, result.Root.Children[0].SelfCost, 2);
        Assert.Equal(80, result.Root.Children[1].SelfCost, 2);
        Assert.Equal(220.75, result.SelfCostTotal, 2);
    }

    [Fact]
    public void SelfMsSubtractsChildrenWithLoops()
    {
        var result = PlanParser.Parse(SampleAnalyze)!;

        // 루트 12.5 - (4.0 + 0.5×2loops) = 7.5
        Assert.Equal(7.5, result.Root.SelfMs!.Value, 3);
        Assert.Equal(12.5, result.SelfMsTotal, 3);
    }

    [Fact]
    public void SelfCostClampsAtZeroWhenAccountingIsOff()
    {
        // InitPlan/CTE 는 자식 합이 부모 누적치를 넘을 수 있다 — 음수 막대는 안 된다
        var result = PlanParser.Parse("""
            [{"Plan":{"Node Type":"Result","Total Cost":10,
              "Plans":[{"Node Type":"Seq Scan","Relation Name":"t","Total Cost":25}]}}]
            """)!;

        Assert.Equal(0, result.Root.SelfCost);
    }

    [Fact]
    public void RowsEstimateErrorComparesPerLoopValues()
    {
        var result = PlanParser.Parse("""
            [{"Plan":{"Node Type":"Seq Scan","Relation Name":"t",
              "Total Cost":10,"Plan Rows":100,
              "Actual Total Time":1.0,"Actual Rows":10000,"Actual Loops":3}}]
            """)!;

        // 100 예측 vs 10000 실제 (루프당) = 100배. loops 를 곱해 눈금을 흐트리지 않는다
        Assert.Equal(100, result.Root.RowsEstimateError!.Value, 2);
    }

    [Fact]
    public void RowsEstimateErrorNeedsAnalyze()
    {
        var result = PlanParser.Parse(
            """[{"Plan":{"Node Type":"Seq Scan","Total Cost":10,"Plan Rows":5}}]""")!;

        Assert.Null(result.Root.RowsEstimateError);
    }

    [Fact]
    public void ZeroActualRowsDoesNotDivideByZero()
    {
        var result = PlanParser.Parse("""
            [{"Plan":{"Node Type":"Seq Scan","Total Cost":10,"Plan Rows":50,
              "Actual Total Time":0.1,"Actual Rows":0,"Actual Loops":1}}]
            """)!;

        Assert.Equal(50, result.Root.RowsEstimateError!.Value, 2);
    }
}
