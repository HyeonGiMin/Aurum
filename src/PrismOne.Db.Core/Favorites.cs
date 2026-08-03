using System.Text.Json;
using System.Text.RegularExpressions;

namespace PrismOne.Db.Core;

/// <summary>즐겨찾기 쿼리 하나 (Golden 의 Favorites 메뉴 항목).</summary>
public sealed record FavoriteQuery(string Name, string Sql);

/// <summary>
/// 즐겨찾기 목록. 기본 저장 위치는 ~/.prismone-studio/favorites.json.
/// 목록 자체는 불변으로 다루고, 변경 연산은 새 목록으로 교체한 뒤 즉시 저장한다.
/// 저장 실패는 삼키지 않고 예외로 알린다 (사용자가 직접 만든 데이터라 유실이 곧 손실).
/// </summary>
public sealed class FavoritesStore
{
    private const int MaxNameLength = 80;

    private readonly string _path;
    private IReadOnlyList<FavoriteQuery> _items;

    private FavoritesStore(string path, IReadOnlyList<FavoriteQuery> items)
    {
        _path = path;
        _items = items;
    }

    public IReadOnlyList<FavoriteQuery> Items => _items;

    public string FilePath => _path;

    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".prismone-studio",
        "favorites.json");

    public static FavoritesStore Load() => Load(DefaultPath);

    /// <summary>읽기 실패(손상·권한)는 빈 목록으로 시작한다 — 쓰기 실패와 달리 잃을 게 없다.</summary>
    public static FavoritesStore Load(string path)
    {
        try
        {
            var items = File.Exists(path)
                ? JsonSerializer.Deserialize<List<FavoriteQuery>>(File.ReadAllText(path)) ?? []
                : [];
            return new FavoritesStore(path, Sort(items));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new FavoritesStore(path, []);
        }
    }

    /// <summary>같은 이름이면 SQL 을 덮어쓴다(upsert).</summary>
    public FavoriteQuery Add(string name, string sql)
    {
        var favorite = new FavoriteQuery(Normalize(name), sql.Trim());
        if (favorite.Name.Length == 0)
            throw new ArgumentException("즐겨찾기 이름이 비어 있습니다.", nameof(name));
        if (favorite.Sql.Length == 0)
            throw new ArgumentException("즐겨찾기 SQL 이 비어 있습니다.", nameof(sql));

        _items = Sort(_items.Where(f => !SameName(f.Name, favorite.Name)).Append(favorite));
        Save();
        return favorite;
    }

    public bool Remove(string name)
    {
        var remaining = _items.Where(f => !SameName(f.Name, name)).ToList();
        if (remaining.Count == _items.Count)
            return false;
        _items = remaining;
        Save();
        return true;
    }

    /// <summary>이름 변경 + SQL 수정. 새 이름이 다른 항목과 겹치면 그 항목을 대체한다.</summary>
    public bool Update(string originalName, string newName, string sql)
    {
        if (Find(originalName) is null)
            return false;
        var favorite = new FavoriteQuery(Normalize(newName), sql.Trim());
        if (favorite.Name.Length == 0 || favorite.Sql.Length == 0)
            throw new ArgumentException("즐겨찾기 이름과 SQL 은 비어 있을 수 없습니다.", nameof(newName));

        _items = Sort(_items
            .Where(f => !SameName(f.Name, originalName) && !SameName(f.Name, favorite.Name))
            .Append(favorite));
        Save();
        return true;
    }

    public FavoriteQuery? Find(string name) => _items.FirstOrDefault(f => SameName(f.Name, name));

    /// <summary>Golden 의 Favorites 필터 — 이름·SQL 부분일치.</summary>
    public static IReadOnlyList<FavoriteQuery> Filter(IReadOnlyList<FavoriteQuery> items, string? text)
    {
        var needle = text?.Trim() ?? "";
        if (needle.Length == 0)
            return items;
        return items
            .Where(f => f.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                     || f.Sql.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Normalize(string name)
    {
        var trimmed = name.Trim().ReplaceLineEndings(" ");
        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
    }

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static List<FavoriteQuery> Sort(IEnumerable<FavoriteQuery> items) =>
        items.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>
/// Golden 옵션 "Allow non-Select statements to run from the Favorites Menu." 의 판정.
/// 즐겨찾기 실행은 사용자가 SQL 을 눈으로 확인하지 않고 메뉴만 눌러 돌리는 동작이라,
/// 애매하면 쓰기로 본다(보수적).
/// QuerySession.IsReadOnlyStatement 는 트랜잭션 제어용이라 선행 주석·CTE·다중 문장을
/// 보지 않으므로 여기서 따로 판정한다.
/// </summary>
public static class FavoriteSql
{
    private static readonly Regex WritingKeyword = new(
        @"\b(insert|update|delete|merge|truncate|drop|alter|create|grant|revoke|call|do|copy|vacuum|refresh|reindex|cluster|lock|comment|set|begin|commit|rollback)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>스크립트의 모든 문장이 읽기 전용(SELECT 계열)일 때만 true.</summary>
    public static bool IsSelectOnly(string sql)
    {
        var statements = StatementSplitter.Split(sql);
        return statements.Count > 0 && statements.All(s => IsSelectStatement(s.Text));
    }

    private static bool IsSelectStatement(string text) =>
        FirstKeyword(text) switch
        {
            "select" or "values" or "table" or "show" => true,
            // WITH x AS (INSERT … RETURNING *) SELECT … 은 쓰기다
            "with" => !WritingKeyword.IsMatch(text),
            _ => false,
        };

    /// <summary>선행 주석·공백·여는 괄호를 건너뛴 첫 키워드(소문자). 없으면 빈 문자열.</summary>
    private static string FirstKeyword(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c == '(') { i++; continue; }
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                var nl = text.IndexOf('\n', i + 2);
                i = nl < 0 ? text.Length : nl + 1;
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? text.Length : close + 2;
                continue;
            }
            break;
        }
        var start = i;
        while (i < text.Length && (char.IsLetter(text[i]) || text[i] == '_'))
            i++;
        return text[start..i].ToLowerInvariant();
    }
}
