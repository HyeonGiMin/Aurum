using System.Text.Json;

namespace PrismOne.Db.Core.Mcp;

/// <summary>Claude 가 실행·확정한 것 한 건. Decision: auto(SELECT 자동 실행) · approved · declined.</summary>
public sealed record McpAuditEntry(
    DateTimeOffset At,
    string Tool,
    int Tab,
    string Sql,
    string Decision,
    int? RowsReturned);

/// <summary>
/// AI 가 무엇을 실행하고 무엇을 읽어 갔는지 남기는 감사 로그 (~/.prismone-studio/mcp-audit.jsonl).
/// 운영·의료 DB 에서는 "Claude 가 어떤 데이터를 봤는가" 를 나중에 확인할 수 있어야 한다.
/// 행 데이터 자체는 남기지 않는다 — SQL 과 돌려준 행 수만.
/// </summary>
public sealed class McpAuditLog(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".prismone-studio", "mcp-audit.jsonl");

    private readonly Lock _lock = new();

    public string FilePath => _path;

    /// <summary>한 줄 추가. 디스크 오류는 호출자에게 던진다 — 감사 기록이 조용히 빠지면 안 된다.</summary>
    public void Append(McpAuditEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, McpJson.Options) + Environment.NewLine;
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, line);
        }
    }
}
