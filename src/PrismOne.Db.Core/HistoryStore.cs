using System.Text.Json;

namespace PrismOne.Db.Core;

/// <summary>
/// 실행한 문장의 히스토리 (Golden 의 ◀ ▶ 순환).
/// 메모리 + ~/.prismone-studio/history.jsonl 에 보존, 최근 500개 유지.
/// </summary>
public static class HistoryStore
{
    private const int MaxEntries = 500;
    private static readonly object Gate = new();
    private static List<string>? _items;

    private sealed record Entry(string Sql, DateTime At);

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".prismone-studio");

    private static string FilePath => Path.Combine(Dir, "history.jsonl");

    public static IReadOnlyList<string> Items
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
            if (_items.Count > 0 && _items[^1] == sql)
                return;   // 연속 중복 제거
            _items.Add(sql);
            if (_items.Count > MaxEntries)
                _items.RemoveAt(0);
            try
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(FilePath, JsonSerializer.Serialize(new Entry(sql, DateTime.Now)) + "\n");
            }
            catch { /* 히스토리 저장 실패는 치명적이지 않다 */ }
        }
    }

    private static List<string> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var items = new List<string>();
            foreach (var line in File.ReadLines(FilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<Entry>(line) is { } e)
                        items.Add(e.Sql);
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
