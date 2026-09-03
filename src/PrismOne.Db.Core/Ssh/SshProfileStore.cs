using System.Text.Json;

namespace PrismOne.Db.Core.Ssh;

/// <summary>
/// SSH 설정의 비밀 암·복호화. 저장하는 곳이 둘(<see cref="ConnectionStore"/> 의 인라인 설정과
/// <see cref="SshProfileStore"/> 의 이름 붙은 설정)이라 규칙을 한 곳에 둔다.
/// </summary>
internal static class SshSecrets
{
    /// <summary>
    /// 디스크에 남길 형태로. <see cref="SshOptions.SavePassword"/> 를 껐으면
    /// <b>암호문으로도 남기지 않는다</b> — 그 선택의 뜻은 "잘 숨겨라" 가 아니라 "두지 마라" 다.
    /// </summary>
    public static SshOptions Protect(SshOptions ssh)
    {
        if (!ssh.SavePassword) return ssh.WithoutSecrets();
        return ssh with
        {
            Password = ssh.Password is null ? null : PasswordCipher.Protect(ssh.Password),
            Passphrase = ssh.Passphrase is null ? null : PasswordCipher.Protect(ssh.Passphrase),
        };
    }

    public static SshOptions Unprotect(SshOptions ssh) => ssh with
    {
        Password = PasswordCipher.Unprotect(ssh.Password),
        Passphrase = PasswordCipher.Unprotect(ssh.Passphrase),
    };
}

/// <summary>
/// 이름 붙인 SSH 설정 모음 (<c>~/.prismone-studio/ssh-profiles.json</c>) —
/// DataGrip 의 재사용 가능한 SSH configuration 에 해당한다.
///
/// **왜 따로 두나:** 같은 bastion 을 거치는 접속이 열 개면 설정도 열 벌이었다.
/// 점프 호스트의 포트가 바뀌거나 키를 갈면 열 군데를 고쳐야 했다. 이제 접속 항목은
/// <see cref="SshOptions.Name"/> 만 들고 있고, 실제 값은 여기 한 벌만 있다.
///
/// 이름이 없는(<c>Name == null</c>) 설정은 "이 접속에만" 쓰는 것이라 여기 오지 않고
/// 접속 항목 안에 그대로 남는다 — 한 번 쓰고 말 bastion 때문에 목록이 지저분해지지 않게.
/// </summary>
public static class SshProfileStore
{
    /// <summary>테스트가 진짜 홈 디렉터리를 건드리지 않게 하는 자리. 앱은 건드리지 않는다.</summary>
    internal static string? HomeOverride { get; set; }

    private static string Home =>
        HomeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string Dir => Path.Combine(Home, ".prismone-studio");

    public static string FilePath => Path.Combine(Dir, "ssh-profiles.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>이름 순으로 정렬해 돌려준다 (드롭다운 순서가 매번 바뀌지 않게).</summary>
    public static List<SshOptions> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var list = JsonSerializer.Deserialize<List<SshOptions>>(File.ReadAllText(FilePath)) ?? [];
            return list
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .Select(SshSecrets.Unprotect)
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];   // 손상된 파일은 빈 목록으로 시작 (ConnectionStore 와 같은 방침)
        }
    }

    public static IReadOnlyList<string> Names() => [.. Load().Select(p => p.Name!)];

    public static SshOptions? Find(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : Load().Find(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>같은 이름이 있으면 덮어쓴다. 이름이 없으면 저장할 수 없다.</summary>
    public static List<SshOptions> Upsert(SshOptions profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("이름이 있어야 저장할 수 있습니다.", nameof(profile));

        var list = Load();
        list.RemoveAll(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
        list.Add(profile);
        Save(list);
        return Load();
    }

    public static List<SshOptions> Remove(string name)
    {
        var list = Load();
        list.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        Save(list);
        return Load();
    }

    private static void Save(List<SshOptions> profiles)
    {
        Directory.CreateDirectory(Dir);
        var encrypted = profiles.Select(SshSecrets.Protect).ToList();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(encrypted, JsonOptions));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
