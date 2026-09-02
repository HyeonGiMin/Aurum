using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace PrismOne.Db.Core.Mongo;

/// <summary>
/// Mongo 세션 모니터 — <c>currentOp</c> 를 pg_stat_activity 와 같은
/// <see cref="ActivityRow"/> 로 옮겨 기존 Session Monitor 창을 그대로 재사용한다.
/// 취소는 <c>killOp</c> — Mongo 에는 PG 의 "쿼리만 취소 vs 세션 종료" 구분이 없어
/// Cancel/Terminate 둘 다 killOp 다.
/// </summary>
public static class MongoSessionMonitor
{
    public static async Task<List<ActivityRow>> GetActivityAsync(
        ConnectionProfile profile, CancellationToken ct = default)
    {
        using var client = CreateClient(profile);
        var result = await client.GetDatabase("admin")
            .RunCommandAsync<BsonDocument>(new BsonDocument("currentOp", 1), cancellationToken: ct);

        var rows = new List<ActivityRow>();
        if (result.TryGetValue("inprog", out var inprog) && inprog is BsonArray operations)
            foreach (var op in operations.OfType<BsonDocument>())
                rows.Add(ToRow(op));
        return rows
            .OrderByDescending(r => r.Elapsed)
            .ToList();
    }

    private static ActivityRow ToRow(BsonDocument op)
    {
        var opid = op.TryGetValue("opid", out var id) && id.IsNumeric ? (int)id.ToInt64() : 0;
        var active = op.TryGetValue("active", out var a) && a.AsBoolean;
        var kind = op.TryGetValue("op", out var o) ? o.AsString : "";
        var ns = op.TryGetValue("ns", out var n) ? n.AsString : "";
        var command = op.TryGetValue("command", out var c) ? c.ToString() ?? "" : "";
        var query = $"{ns} {command}".Trim();

        var user = op.TryGetValue("effectiveUsers", out var users) && users is BsonArray { Count: > 0 } list
                   && list[0] is BsonDocument first && first.TryGetValue("user", out var u)
            ? u.AsString
            : "";

        return new ActivityRow(
            Pid: opid,
            User: user,
            App: op.TryGetValue("appName", out var app) ? app.AsString : "",
            Client: op.TryGetValue("client", out var client) ? client.AsString : "",
            State: active ? kind : "idle",
            Elapsed: op.TryGetValue("secs_running", out var secs) && secs.IsNumeric
                ? TimeSpan.FromSeconds(secs.ToDouble()).ToString(@"hh\:mm\:ss")
                : "",
            Wait: op.TryGetValue("waitingForLock", out var wait) && wait.AsBoolean ? "lock" : "",
            Query: query.Length > 300 ? query[..300] : query);
    }

    /// <summary><c>killOp</c> — 진행 중인 연산 하나를 중단시킨다.</summary>
    public static async Task<bool> KillOpAsync(
        ConnectionProfile profile, int opid, CancellationToken ct = default)
    {
        using var client = CreateClient(profile);
        var result = await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(
            new BsonDocument { ["killOp"] = 1, ["op"] = opid }, cancellationToken: ct);
        return result.TryGetValue("ok", out var ok) && ok.ToDouble() >= 1;
    }

    /// <summary>SSH 를 쓰는 프로필이면 터널을 통과한 주소로 붙는다 (MongoSession.Open 과 같은 이유).</summary>
    private static MongoClient CreateClient(ConnectionProfile rawProfile)
    {
        var profile = PrismOne.Db.Core.Ssh.SshTunnelPool.Resolve(rawProfile);
        var settings = MongoClientSettings.FromConnectionString(MongoSession.BuildConnectionString(profile));
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        settings.ConnectTimeout = TimeSpan.FromSeconds(5);
        return new MongoClient(settings);
    }
}
