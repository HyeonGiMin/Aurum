using System.Text.Json;

namespace PrismOne.Db.Core;

/// <summary>저장된 접속 항목. Password 는 "Save password" 를 켠 경우에만 남는다.</summary>
public sealed record SavedConnection(
    string Host,
    int Port,
    string Database,
    string Username,
    string? Password,
    string? Name = null,
    string? Category = null,
    string? Comment = null)
{
    public string DisplayName => $"{Username}@{Host}:{Port}/{Database}";

    /// <summary>Golden 의 Database 표기: host[:port]/db (5432 는 생략).</summary>
    public string DisplayDatabase =>
        Port == 5432 ? $"{Host}/{Database}" : $"{Host}:{Port}/{Database}";

    public bool SameTarget(ConnectionProfile p) =>
        Host == p.Host && Port == p.Port && Database == p.Database && Username == p.Username;
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
        list.Insert(0, new SavedConnection(
            profile.Host, profile.Port, profile.Database, profile.Username,
            savePassword ? profile.Password : null,
            prior?.Name ?? profile.Database,
            prior?.Category,
            prior?.Comment));
        if (list.Count > MaxEntries)
            list.RemoveRange(MaxEntries, list.Count - MaxEntries);
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
