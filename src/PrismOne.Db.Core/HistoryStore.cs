using System.Text.Json;

namespace PrismOne.Db.Core;

/// <summary>히스토리 한 건 — jsonl 라인의 형태 그대로 (Sql/At 이름 바꾸지 말 것).</summary>
public sealed record HistoryEntry(string Sql, DateTime At);

/// <summary>
/// 실행한 문장의 히스토리 (Golden 의 ◀ ▶ 순환 + History 조회 창).
/// 메모리 + ~/.prismone-studio/history.jsonl 에 보존, 최근 500개 유지.
/// </summary>
public static class HistoryStore
{
    private const int MaxEntries = 500;
    private static readonly object Gate = new();
    private static List<HistoryEntry>? _items;

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".prismone-studio");

    private static string FilePath => Path.Combine(Dir, "history.jsonl");

    /// <summary>◀ ▶ 순환용 — SQL 만.</summary>
    public static IReadOnlyList<string> Items
    {
        get { lock (Gate) { return (_items ??= Load()).Select(e => e.Sql).ToList(); } }
    }

    /// <summary>History 조회 창용 — 실행 시각 포함, 오래된 것부터.</summary>
    public static IReadOnlyList<HistoryEntry> Entries
    {
        get { lock (Gate) { return (_items ??= Load()).AsReadOnly(); } }
    }

    public static void Add(string sql)
    {
        sql = sql.Trim();
        if (sql.Length == 0) return;
        lock (Gate)
        {
            _items ??= Load();
            if (_items.Count > 0 && _items[^1].Sql == sql)
                return;   // 연속 중복 제거
            var entry = new HistoryEntry(sql, DateTime.Now);
            _items.Add(entry);
            if (_items.Count > MaxEntries)
                _items.RemoveAt(0);
            try
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(FilePath, JsonSerializer.Serialize(entry) + "\n");
            }
            catch { /* 히스토리 저장 실패는 치명적이지 않다 */ }
        }
    }

    private static List<HistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var items = new List<HistoryEntry>();
            foreach (var line in File.ReadLines(FilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<HistoryEntry>(line) is { } e)
                        items.Add(e);
                }
                catch { /* 손상 라인 스킵 */ }
            }
            return items.Count > MaxEntries ? items[^MaxEntries..] : items;
        }
        catch
        {
            return [];
        }
    }
}
