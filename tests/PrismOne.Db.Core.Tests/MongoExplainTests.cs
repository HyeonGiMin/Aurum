using MongoDB.Bson;
using PrismOne.Db.Core;
using PrismOne.Db.Core.Mongo;
using Xunit;

namespace PrismOne.Db.Core.Tests;

/// <summary>
/// Mongo explain — 커맨드 생성과 결과 → 플랜 트리 매핑은 서버 없이 검증한다
/// (실서버 왕복은 MongoSessionLiveTests 쪽).
/// </summary>
public class MongoExplainTests
{
    // ---------- .explain() 파싱 ----------

    [Fact]
    public void ExplainChainDefaultsToQueryPlanner()
        => Assert.Equal("queryPlanner",
            MongoQueryParser.Parse("db.people.find({}).explain()").ExplainVerbosity);

    [Fact]
    public void ExplainChainAcceptsVerbosity()
        => Assert.Equal("executionStats",
            MongoQueryParser.Parse("db.people.find({}).explain('executionStats')").ExplainVerbosity);

    [Fact]
    public void ExplainChainRejectsUnknownVerbosity()
        => Assert.Throws<MongoQueryException>(
            () => MongoQueryParser.Parse("db.people.find({}).explain('fast')"));

    // ---------- explain 커맨드 생성 ----------

    [Fact]
    public void BuildsFindExplainWithChain()
    {
        var command = MongoQueryParser.Parse(
            "db.people.find({ age: 1 }, { name: 1 }).sort({ name: 1 }).skip(2).limit(5)");

        var explain = MongoExplain.BuildCommand(command, "executionStats");

        Assert.Equal("executionStats", explain["verbosity"].AsString);
        var find = explain["explain"].AsBsonDocument;
        Assert.Equal("people", find["find"].AsString);
        Assert.Equal(1, find["filter"]["age"].AsInt32);
        Assert.Equal(1, find["projection"]["name"].AsInt32);
        Assert.Equal(1, find["sort"]["name"].AsInt32);
        Assert.Equal(2, find["skip"].AsInt32);
        Assert.Equal(5, find["limit"].AsInt32);
    }

    [Fact]
    public void BuildsAggregateExplainWithCursor()
    {
        var command = MongoQueryParser.Parse("db.people.aggregate([{ $match: { a: 1 } }])");

        var explain = MongoExplain.BuildCommand(command, "queryPlanner");
        var aggregate = explain["explain"].AsBsonDocument;

        Assert.Equal("people", aggregate["aggregate"].AsString);
        Assert.Single(aggregate["pipeline"].AsBsonArray);
        Assert.True(aggregate.Contains("cursor"));   // 없으면 서버가 거부한다
    }

    [Fact]
    public void UseAndShowCannotBeExplained()
    {
        var use = MongoQueryParser.Parse("use mydb");

        Assert.Throws<MongoQueryException>(() => MongoExplain.BuildCommand(use, "queryPlanner"));
    }

    // ---------- 결과 → 플랜 트리 ----------

    [Fact]
    public void ParsesWinningPlanTree()
    {
        var explain = BsonDocument.Parse("""
            { "queryPlanner": { "winningPlan": {
                "stage": "FETCH",
                "inputStage": { "stage": "IXSCAN", "indexName": "age_1",
                                "keyPattern": { "age": 1 }, "direction": "forward" }
            } } }
            """);

        var plan = MongoExplain.Parse(explain);

        Assert.Equal("FETCH", plan.Root.Title);
        Assert.Null(plan.ExecutionMs);   // queryPlanner 는 실행 안 함
        var child = Assert.Single(plan.Root.Children);
        Assert.Equal("IXSCAN (age_1)", child.Title);
        Assert.Contains("forward", child.Detail);
    }

    [Fact]
    public void ParsesExecutionStatsWithSelfTime()
    {
        var explain = BsonDocument.Parse("""
            { "queryPlanner": { "winningPlan": { "stage": "COLLSCAN" } },
              "executionStats": {
                "executionTimeMillis": 12,
                "executionStages": {
                    "stage": "FETCH", "nReturned": 10, "docsExamined": 10,
                    "executionTimeMillisEstimate": 12,
                    "inputStage": { "stage": "IXSCAN", "indexName": "a_1",
                                    "nReturned": 10, "keysExamined": 10,
                                    "executionTimeMillisEstimate": 4 }
                } } }
            """);

        var plan = MongoExplain.Parse(explain);

        Assert.Equal(12, plan.ExecutionMs);
        Assert.Equal("FETCH", plan.Root.Title);
        Assert.Contains("docsExamined=10", plan.Root.Detail);
        // 누적 12ms 중 자식(IXSCAN) 4ms 를 뺀 8ms 가 자기 몫 — PG 와 같은 계산
        Assert.Equal(8, plan.Root.SelfMs);
        Assert.Equal(4, plan.Root.Children[0].SelfMs);
    }

    [Fact]
    public void ParsesAggregatePipelineStages()
    {
        var explain = BsonDocument.Parse("""
            { "stages": [
                { "$cursor": { "queryPlanner": { "winningPlan": { "stage": "COLLSCAN" } } } },
                { "$group": { "_id": "$a" }, "nReturned": 3, "executionTimeMillisEstimate": 5 }
            ] }
            """);

        var plan = MongoExplain.Parse(explain);

        Assert.Equal("Aggregation Pipeline", plan.Root.Title);
        Assert.Equal(2, plan.Root.Children.Count);
        Assert.Equal("$cursor", plan.Root.Children[0].Title);
        Assert.Equal("COLLSCAN", plan.Root.Children[0].Children[0].Title);
        Assert.Equal("$group", plan.Root.Children[1].Title);
        Assert.Contains("returned=3", plan.Root.Children[1].Detail);
    }

    [Fact]
    public void UnknownShapeFallsBackToRawJson()
    {
        var plan = MongoExplain.Parse(BsonDocument.Parse("""{ "something": "else" }"""));

        Assert.Equal("explain", plan.Root.Title);
        Assert.Contains("something", plan.Root.Extra);
    }
}
