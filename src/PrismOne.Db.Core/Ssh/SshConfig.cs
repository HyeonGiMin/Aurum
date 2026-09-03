using System.Text.RegularExpressions;

namespace PrismOne.Db.Core.Ssh;

/// <summary>
/// <c>~/.ssh/config</c> 에서 별칭 하나를 풀어낸 결과.
/// 값이 없으면 null·빈 목록이고, 그때는 호출부가 사용자가 적은 값을 쓴다.
/// </summary>
/// <param name="Alias">사용자가 적은 이름 (<c>Host</c> 별칭이거나 그냥 호스트명).</param>
/// <param name="HostName">실제로 붙을 주소 (<c>HostName</c>). 없으면 별칭 그대로.</param>
/// <param name="ProxyJump">경유할 호스트들 — OpenSSH <c>-J</c> 표기 그대로.</param>
public sealed record SshConfigHost(
    string Alias,
    string HostName,
    string? User,
    int? Port,
    IReadOnlyList<string> IdentityFiles,
    string? ProxyJump)
{
    /// <summary>설정 파일에 이 별칭에 대한 내용이 하나라도 있었는지.</summary>
    public bool HasSettings =>
        HostName != Alias || User is not null || Port is not null
        || IdentityFiles.Count > 0 || ProxyJump is not null;

    /// <summary>설정 창에 보여줄 요약 — 무엇이 자동으로 채워졌는지 알려준다.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (HostName != Alias) parts.Add(HostName);
            if (User is not null) parts.Add($"user {User}");
            if (Port is not null) parts.Add($"port {Port}");
            if (IdentityFiles.Count > 0) parts.Add($"key {Path.GetFileName(IdentityFiles[0])}");
            if (ProxyJump is not null) parts.Add($"via {ProxyJump}");
            return parts.Count == 0 ? "설정 없음" : string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// OpenSSH <c>~/.ssh/config</c> 읽기 (DataGrip 의 "OpenSSH config" 대응).
///
/// 규칙은 OpenSSH 와 같다: <b>먼저 나온 값이 이긴다</b>. 여러 <c>Host</c> 블록이 같은
/// 별칭에 걸리면 앞 블록의 값이 남고 뒤는 무시된다 — 사람들이 파일 맨 위에 구체적인
/// 항목을, 아래에 <c>Host *</c> 를 두는 관례가 그래서 통한다.
///
/// 다루는 키워드는 접속에 실제로 필요한 것만이다 — <c>HostName · User · Port ·
/// IdentityFile · ProxyJump</c>, 그리고 <c>Include</c>. <c>Match</c> 블록은 조건이
/// 실행 환경에 달려 있어 <b>건너뛴다</b>(잘못 적용하느니 안 하는 게 낫다).
/// </summary>
public static class SshConfig
{
    /// <summary>테스트가 진짜 ~/.ssh 를 건드리지 않게 하는 자리. 앱은 건드리지 않는다.</summary>
    internal static string? HomeOverride { get; set; }

    private static string Home =>
        HomeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string SshDir => Path.Combine(Home, ".ssh");

    /// <summary>읽는 파일. 쓰지는 않는다 — 사용자 설정은 우리가 고칠 것이 아니다.</summary>
    public static string FilePath => Path.Combine(SshDir, "config");

    /// <summary>설정 파일이 있는지 — 없으면 UI 에서 이 방식을 못 고르게 한다.</summary>
    public static bool Exists => File.Exists(FilePath);

    /// <summary><c>Include</c> 가 서로를 부르는 사고를 막는 깊이 상한.</summary>
    private const int MaxIncludeDepth = 8;

    /// <summary>
    /// 별칭 하나를 푼다. 설정이 없거나 걸리는 블록이 없으면 별칭을 그대로 호스트명으로 쓴다
    /// (그게 OpenSSH 동작이다 — <c>ssh foo</c> 는 설정이 없으면 그냥 foo 에 붙는다).
    /// </summary>
    public static SshConfigHost Resolve(string alias)
    {
        var entries = Load();

        string? hostName = null, user = null, proxyJump = null;
        int? port = null;
        var identityFiles = new List<string>();

        foreach (var entry in entries)
        {
            if (!Matches(entry.Patterns, alias)) continue;

            // 먼저 나온 값이 이긴다 — 이미 정해진 건 덮지 않는다.
            hostName ??= entry.Get("hostname");
            user ??= entry.Get("user");
            proxyJump ??= entry.Get("proxyjump");
            if (port is null && entry.Get("port") is { } portText
                && int.TryParse(portText, out var parsed) && parsed is > 0 and <= 65535)
                port = parsed;

            // IdentityFile 만은 쌓인다 (OpenSSH 도 여러 개를 순서대로 시도한다).
            foreach (var file in entry.GetAll("identityfile"))
                identityFiles.Add(ExpandPath(file));
        }

        var effectiveHost = string.IsNullOrWhiteSpace(hostName) ? alias : ExpandTokens(hostName, alias);
        // ProxyJump 을 none 으로 끄는 관례를 존중한다.
        if (string.Equals(proxyJump, "none", StringComparison.OrdinalIgnoreCase)) proxyJump = null;

        return new SshConfigHost(alias, effectiveHost, user, port, identityFiles, proxyJump);
    }

    /// <summary>설정에 적힌 <c>Host</c> 별칭들 (와일드카드 항목은 뺀다) — 설정 창의 자동완성용.</summary>
    public static IReadOnlyList<string> Aliases() =>
        Load()
            .SelectMany(e => e.Patterns)
            .Where(p => !p.Contains('*') && !p.Contains('?') && !p.StartsWith('!'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    // ---------- 파싱 ----------

    private sealed class Entry(List<string> patterns)
    {
        public List<string> Patterns { get; } = patterns;

        /// <summary>키워드는 소문자로 정규화해 담는다 (OpenSSH 는 대소문자를 안 가린다).</summary>
        private readonly Dictionary<string, List<string>> _values = [];

        public void Add(string keyword, string value)
        {
            if (!_values.TryGetValue(keyword, out var list))
                _values[keyword] = list = [];
            list.Add(value);
        }

        public string? Get(string keyword) =>
            _values.TryGetValue(keyword, out var list) && list.Count > 0 ? list[0] : null;

        public IEnumerable<string> GetAll(string keyword) =>
            _values.TryGetValue(keyword, out var list) ? list : [];
    }

    private static List<Entry> Load()
    {
        var entries = new List<Entry>();
        ReadInto(entries, FilePath, depth: 0);
        return entries;
    }

    private static void ReadInto(List<Entry> entries, string path, int depth)
    {
        if (depth > MaxIncludeDepth) return;

        string[] lines;
        try
        {
            if (!File.Exists(path)) return;
            lines = File.ReadAllLines(path);
        }
        catch
        {
            return;   // 읽을 수 없으면 설정이 없는 셈 친다
        }

        // Host 블록 앞의 설정은 모든 호스트에 걸린다 (OpenSSH 와 같다).
        var current = new Entry(["*"]);
        entries.Add(current);

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var (keyword, value) = SplitDirective(line);
            if (keyword is null || value is null) continue;

            switch (keyword)
            {
                case "host":
                    current = new Entry(SplitPatterns(value));
                    entries.Add(current);
                    break;

                case "match":
                    // 조건이 실행 환경(exec·user 등)에 달려 있어 우리가 판정할 수 없다.
                    // 뒤따르는 설정이 엉뚱한 호스트에 붙지 않도록 아무에게도 안 걸리게 막는다.
                    current = new Entry([]);
                    entries.Add(current);
                    break;

                case "include":
                    foreach (var included in ResolveIncludes(value))
                        ReadInto(entries, included, depth + 1);
                    break;

                default:
                    current.Add(keyword, value);
                    break;
            }
        }
    }

    /// <summary>OpenSSH 는 <c>Key Value</c> 와 <c>Key=Value</c> 를 모두 받는다.</summary>
    private static (string? Keyword, string? Value) SplitDirective(string line)
    {
        var separator = line.IndexOfAny([' ', '\t', '=']);
        if (separator <= 0) return (null, null);
        var keyword = line[..separator].ToLowerInvariant();
        var value = line[(separator + 1)..].Trim().TrimStart('=').Trim();
        return value.Length == 0 ? (null, null) : (keyword, Unquote(value));
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    private static List<string> SplitPatterns(string value) =>
        [.. value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(Unquote)];

    /// <summary><c>Include</c> 의 상대 경로는 ~/.ssh 기준이고 와일드카드를 쓸 수 있다.</summary>
    private static IEnumerable<string> ResolveIncludes(string value)
    {
        foreach (var token in value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var pattern = ExpandPath(Unquote(token));
            if (!Path.IsPathRooted(pattern)) pattern = Path.Combine(SshDir, pattern);

            var dir = Path.GetDirectoryName(pattern);
            var name = Path.GetFileName(pattern);
            if (dir is null || name.Length == 0) continue;

            if (!name.Contains('*') && !name.Contains('?'))
            {
                yield return pattern;
                continue;
            }
            string[] matches;
            try { matches = Directory.Exists(dir) ? Directory.GetFiles(dir, name) : []; }
            catch { matches = []; }
            Array.Sort(matches, StringComparer.Ordinal);
            foreach (var match in matches) yield return match;
        }
    }

    // ---------- 패턴·토큰 ----------

    /// <summary>OpenSSH 패턴 목록: 하나라도 걸리고 부정(<c>!</c>)에 안 걸리면 적용된다.</summary>
    private static bool Matches(List<string> patterns, string alias)
    {
        var hit = false;
        foreach (var pattern in patterns)
        {
            if (pattern.StartsWith('!'))
            {
                if (Glob(pattern[1..], alias)) return false;
            }
            else if (Glob(pattern, alias))
            {
                hit = true;
            }
        }
        return hit;
    }

    private static bool Glob(string pattern, string value)
    {
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);

        var regex = "^" + string.Concat(pattern.Select(c => c switch
        {
            '*' => ".*",
            '?' => ".",
            _ => Regex.Escape(c.ToString()),
        })) + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
    }

    /// <summary><c>~</c> 를 홈으로 편다. 키 경로가 거의 항상 <c>~/.ssh/id_...</c> 라서 필요하다.</summary>
    private static string ExpandPath(string path)
    {
        var value = Unquote(path.Trim());
        if (value.StartsWith("~/", StringComparison.Ordinal) || value == "~")
        {
            // 설정 파일은 언제나 '/' 를 쓴다 — 윈도우에서도 경로가 성립하도록 바꿔 준다.
            var rest = value.Length > 1 ? value[2..].Replace('/', Path.DirectorySeparatorChar) : "";
            return Path.Combine(Home, rest);
        }
        return value;
    }

    /// <summary>HostName 에 흔히 쓰는 <c>%h</c>(별칭) 만 편다.</summary>
    private static string ExpandTokens(string value, string alias) => value.Replace("%h", alias);
}
