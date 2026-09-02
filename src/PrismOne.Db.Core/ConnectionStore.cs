using System.Text.Json;
using PrismOne.Db.Core.Providers;
using PrismOne.Db.Core.Ssh;

namespace PrismOne.Db.Core;

/// <summary>
/// 저장된 접속 항목. Password 는 "Save password" 를 켠 경우에만 남는다.
///
/// <see cref="Kind"/> 와 <see cref="Ssh"/> 는 **맨 뒤에 기본값과 함께** 두었다 — 이 필드가 없는
/// 기존 connections.json 이 PostgreSQL·직접 접속으로 읽히게 하기 위해서다(하위 호환).
///
/// <see cref="Ssh"/> 안의 비밀번호·passphrase 도 <see cref="Password"/> 와 똑같이
/// <see cref="PasswordCipher"/> 로 암호화되어 디스크에 남는다.
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
    DbKind Kind = DbKind.PostgreSql,
    SshOptions? Ssh = null)
{
    /// <summary>Login List 의 Type 컬럼 표기. JSON 에는 저장되지 않는 계산 값.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string TypeLabel =>
        DbProviders.IsSupported(Kind) ? DbProviders.For(Kind).DisplayName : Kind.ToString();

    /// <summary>파일 DB(SQLite)는 host/port/user 가 없어 경로만 쓴다.</summary>
    private bool IsFileDb => Kind == DbKind.Sqlite;

    /// <summary>DB 이름 부분 — Mongo 는 비어 있을 수 있고, 그때는 슬래시도 안 붙인다.</summary>
    private string DbSuffix => Database.Length == 0 ? "" : $"/{Database}";

    /// <summary>
    /// 항목의 신원 키이기도 하다 (UpdateMeta·Remove 가 이걸로 찾는다). 그래서 점프 호스트를
    /// 함께 담는다 — 같은 <c>localhost:5432/app</c> 를 서로 다른 서버로 거치는 두 항목이
    /// 하나로 뭉개져 서로를 지우면 안 된다.
    /// </summary>
    public string DisplayName => (IsFileDb
        ? Database
        : $"{Username}@{Host}:{Port}{DbSuffix}") + (Ssh is null ? "" : $" (ssh {Ssh.Label})");

    /// <summary>Golden 의 Database 표기: host[:port]/db (기본 포트는 생략).</summary>
    public string DisplayDatabase => IsFileDb
        ? Database
        : (Port == DefaultPort(Kind) ? Host : $"{Host}:{Port}") + DbSuffix;

    /// <summary>SSH 터널을 쓰는 항목인지 — Login List 가 표식을 띄우는 데 쓴다.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasSsh => Ssh is not null;

    /// <summary>터널 표식의 툴팁. SSH 를 안 쓰면 null.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SshLabel => Ssh is null ? null : $"SSH 터널: {Ssh.Label}";

    /// <summary>
    /// 종류가 다르면 같은 호스트·DB 라도 별개 항목이다.
    /// Mongo 는 Database 를 비교에서 뺀다 — 한 서버에 매번 다른 DB 로(또는 아예 안 적고)
    /// 접속해도 같은 로그인 항목으로 취급해, 저장할 때마다 중복이 쌓이지 않게 한다.
    ///
    /// 점프 호스트도 대상의 일부다 — <c>localhost:5432/app</c> 는 어느 서버를 거치느냐에
    /// 따라 완전히 다른 DB 다. 이걸 빼면 서로 다른 접속이 하나로 덮어써진다.
    /// </summary>
    public bool SameTarget(ConnectionProfile p) =>
        Kind == p.Kind && Host == p.Host && Port == p.Port && Username == p.Username
        && (Kind == DbKind.MongoDb || Database == p.Database)
        && SameSshTarget(p.Ssh);

    /// <summary>비밀은 빼고 "어느 서버를 어느 계정으로 거치는가" 만 본다 —
    /// 비밀번호를 바꿨다고 로그인 항목이 둘로 갈라지면 안 된다.</summary>
    private bool SameSshTarget(SshOptions? other)
    {
        if (Ssh is null || other is null) return Ssh is null && other is null;
        // 경유 경로도 대상의 일부다 — 같은 bastion 이라도 그 앞을 다르게 거치면 다른 접속이다.
        return Ssh.Host == other.Host && Ssh.Port == other.Port && Ssh.Username == other.Username
               && Ssh.ProxyJump == other.ProxyJump;
    }

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
            // 이름 붙은 SSH 설정을 채워 넣으려면 프로필 모음이 필요하다. 항목마다 파일을
            // 다시 읽지 않도록 한 번만 읽어 넘긴다.
            var profiles = SshProfileStore.Load();
            // 디스크에는 암호화되어 있다 (예전 평문 저장분은 그대로 통과 → 다음 저장 때 암호화)
            return list.Select(c => c with
            {
                Password = PasswordCipher.Unprotect(c.Password),
                Ssh = UnprotectSsh(c.Ssh, profiles),
            }).ToList();
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
            .Select(c => c with
            {
                Password = c.Password is null ? null : PasswordCipher.Protect(c.Password),
                Ssh = ProtectSsh(c.Ssh),
            })
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
            profile.Kind,
            profile.Ssh));
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

    /// <summary>
    /// 이름 붙은 설정은 <b>이름만</b> 남긴다 — 실제 값은 <see cref="SshProfileStore"/> 에 한 벌만
    /// 있고, 그래야 점프 호스트를 한 곳에서 고칠 수 있다. 이름이 없는(이 접속 전용) 설정만
    /// 여기 통째로 들어간다.
    /// </summary>
    private static SshOptions? ProtectSsh(SshOptions? ssh) => ssh switch
    {
        null => null,
        { IsNamed: true } => SshOptions.Empty with { Name = ssh.Name },
        _ => SshSecrets.Protect(ssh),
    };

    /// <summary>
    /// 이름만 저장된 항목은 프로필 모음에서 실제 값을 채워 넣는다. 그 이름이 지워졌으면
    /// 이름만 남은 채로 둔다 — <see cref="SshOptions.Validate"/> 가 그 상황을 정확히 알린다
    /// (조용히 직접 접속으로 바꿔 버리면 엉뚱한 곳에 붙는다).
    /// </summary>
    private static SshOptions? UnprotectSsh(SshOptions? ssh, List<SshOptions> profiles) => ssh switch
    {
        null => null,
        { IsNamed: true } => profiles.Find(
            p => string.Equals(p.Name, ssh.Name, StringComparison.OrdinalIgnoreCase)) ?? ssh,
        _ => SshSecrets.Unprotect(ssh),
    };
}
