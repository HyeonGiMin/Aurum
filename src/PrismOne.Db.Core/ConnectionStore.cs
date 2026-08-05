using System.Text.Json;
using PrismOne.Db.Core.Providers;

namespace PrismOne.Db.Core;

/// <summary>
/// 저장된 접속 항목. Password 는 "Save password" 를 켠 경우에만 남는다.
///
/// <see cref="Kind"/> 는 **맨 뒤에 기본값과 함께** 두었다 — 이 필드가 없는 기존
/// connections.json 이 PostgreSQL 로 읽히게 하기 위해서다(하위 호환).
/// </summary>
public sealed record SavedConnection(
    string Host,
    int Port,
    string Database,
    string Username,
    string? Password,
    string? Name = null,
    string? Category = null,
    string? Comment = null,
    DbKind Kind = DbKind.PostgreSql)
{
    /// <summary>Login List 의 Type 컬럼 표기. JSON 에는 저장되지 않는 계산 값.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string TypeLabel =>
        DbProviders.IsSupported(Kind) ? DbProviders.For(Kind).DisplayName : Kind.ToString();

    /// <summary>파일 DB(SQLite)는 host/port/user 가 없어 경로만 쓴다.</summary>
    private bool IsFileDb => Kind == DbKind.Sqlite;

    /// <summary>DB 이름 부분 — Mongo 는 비어 있을 수 있고, 그때는 슬래시도 안 붙인다.</summary>
    private string DbSuffix => Database.Length == 0 ? "" : $"/{Database}";

    public string DisplayName => IsFileDb
        ? Database
        : $"{Username}@{Host}:{Port}{DbSuffix}";

    /// <summary>Golden 의 Database 표기: host[:port]/db (기본 포트는 생략).</summary>
    public string DisplayDatabase => IsFileDb
        ? Database
        : (Port == DefaultPort(Kind) ? Host : $"{Host}:{Port}") + DbSuffix;

    /// <summary>
    /// 종류가 다르면 같은 호스트·DB 라도 별개 항목이다.
    /// Mongo 는 Database 를 비교에서 뺀다 — 한 서버에 매번 다른 DB 로(또는 아예 안 적고)
    /// 접속해도 같은 로그인 항목으로 취급해, 저장할 때마다 중복이 쌓이지 않게 한다.
    /// </summary>
    public bool SameTarget(ConnectionProfile p) =>
        Kind == p.Kind && Host == p.Host && Port == p.Port && Username == p.Username
        && (Kind == DbKind.MongoDb || Database == p.Database);

    public static int DefaultPort(DbKind kind) => kind switch
    {
        DbKind.Oracle => 1521,
        DbKind.MongoDb => 27017,
        DbKind.Sqlite => 0,
        _ => 5432,
    };
}

/// <summary>
/// Golden 로그온 다이얼로그의 저장된 접속 목록 (~/.prismone-studio/connections.json).
/// 최근 사용 순서를 유지하고 최대 20개까지 보관한다.
/// </summary>
public static class ConnectionStore
{
    private const int MaxEntries = 20;

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".prismone-studio");

    private static string FilePath => Path.Combine(Dir, "connections.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static List<SavedConnection> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var list = JsonSerializer.Deserialize<List<SavedConnection>>(File.ReadAllText(FilePath)) ?? [];
            // 디스크에는 암호화되어 있다 (예전 평문 저장분은 그대로 통과 → 다음 저장 때 암호화)
            return list.Select(c => c with { Password = PasswordCipher.Unprotect(c.Password) }).ToList();
        }
        catch
        {
            return [];   // 손상된 파일은 빈 목록으로 시작
        }
    }

    public static void Save(List<SavedConnection> connections)
    {
        Directory.CreateDirectory(Dir);
        var encrypted = connections
            .Select(c => c with { Password = c.Password is null ? null : PasswordCipher.Protect(c.Password) })
            .ToList();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(encrypted, JsonOptions));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>접속 성공 시 호출 — 같은 대상을 갱신하고 맨 앞(최근 사용)으로 올린다.
    /// 기존 항목의 Name/Category/Comment 메타는 보존한다.</summary>
    public static List<SavedConnection> Remember(ConnectionProfile profile, bool savePassword)
    {
        var list = Load();
        var prior = list.Find(c => c.SameTarget(profile));
        list.RemoveAll(c => c.SameTarget(profile));
        // Name 은 기본적으로 Database 이름을 쓴다. Mongo 는 Database 가 비어 있을 수 있어
        // 그때는 대신 Host 를 쓴다 — Login List 에 빈 이름 줄이 뜨지 않게.
        var defaultName = profile.Database.Length > 0 ? profile.Database : profile.Host;
        list.Insert(0, new SavedConnection(
            profile.Host, profile.Port, profile.Database, profile.Username,
            savePassword ? profile.Password : null,
            prior?.Name ?? defaultName,
            prior?.Category,
            prior?.Comment,
            profile.Kind));
        if (list.Count > MaxEntries)
            list.RemoveRange(MaxEntries, list.Count - MaxEntries);
        Save(list);
        return list;
    }

    /// <summary>항목의 메타(Name/Category/Comment)를 갱신한다 — Golden 의 "Editing existing Login Item".</summary>
    public static List<SavedConnection> UpdateMeta(
        SavedConnection target, string? name, string? category, string? comment)
    {
        var list = Load();
        var i = list.FindIndex(c => c.DisplayName == target.DisplayName);
        if (i >= 0)
            list[i] = list[i] with
            {
                Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim(),
                Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
            };
        Save(list);
        return list;
    }

    public static List<SavedConnection> Remove(SavedConnection connection)
    {
        var list = Load();
        list.RemoveAll(c => c.DisplayName == connection.DisplayName);
        Save(list);
        return list;
    }
}
