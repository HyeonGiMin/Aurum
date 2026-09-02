using System.Security.Cryptography;
using System.Text;

namespace PrismOne.Db.Core.Ssh;

/// <summary>호스트 키를 대조한 결과.</summary>
public enum HostKeyTrust
{
    /// <summary>알려진 호스트이고 키도 같다 — 바로 붙는다.</summary>
    Trusted,

    /// <summary>처음 보는 호스트 — 사용자에게 지문을 보이고 물어야 한다.</summary>
    Unknown,

    /// <summary>알려진 호스트인데 키가 다르다 — MITM 이거나 서버를 다시 깐 것이다.</summary>
    Mismatch,

    /// <summary>known_hosts 에 <c>@revoked</c> 로 표시된 키다 — 무조건 거부.</summary>
    Revoked,
}

/// <summary>
/// 서버가 내민 호스트 키 하나. 지문은 <c>ssh</c> 명령과 같은 표기다
/// (<c>SHA256:</c> + 패딩 없는 base64).
/// </summary>
/// <param name="Host">우리가 붙으려 한 호스트 (known_hosts 조회 키).</param>
/// <param name="Port">SSH 포트. 22 가 아니면 known_hosts 에 <c>[host]:port</c> 로 적힌다.</param>
/// <param name="KeyType">키 종류 (<c>ssh-ed25519</c> 등).</param>
/// <param name="KeyBase64">키 blob 의 base64 — known_hosts 에 그대로 들어가는 값.</param>
public sealed record HostKeyInfo(string Host, int Port, string KeyType, string KeyBase64)
{
    /// <summary><c>SHA256:ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og</c> 형태.</summary>
    public string Fingerprint => FingerprintOf(KeyBase64);

    public static string FingerprintOf(string keyBase64)
    {
        try
        {
            var hash = SHA256.HashData(Convert.FromBase64String(keyBase64));
            return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
        }
        catch (FormatException)
        {
            return "SHA256:?";   // 손상된 항목 — 표시용이라 죽지 않게만 한다
        }
    }
}

/// <summary>
/// 사용자에게 물어볼 내용 — 서버가 내민 키와 그 판정.
/// UI 가 이걸 받아 지문을 보여주고 accept/reject 를 받는다.
/// </summary>
public sealed record HostKeyRequest(HostKeyInfo Key, HostKeyVerdict Verdict);

/// <summary>
/// 호스트 키 승인 물음. true 면 접속을 계속하고 키를 기억한다.
///
/// **동기다** — SSH.NET 이 핸드셰이크 도중 동기 이벤트로 묻기 때문이다. 구현부는
/// UI 스레드로 넘겨 기다려도 된다: 터널 접속은 <see cref="SshTunnelPool"/> 이
/// 스레드 풀에서 돌리므로 UI 스레드를 막지 않는다.
///
/// 핸들러가 없으면(콘솔·테스트) 처음 보는 키는 <b>거부</b>한다 — 물어볼 사람이 없을 때
/// 조용히 신뢰하는 것이 바로 이 기능이 막으려는 상황이다.
/// </summary>
public delegate bool HostKeyPromptHandler(HostKeyRequest request);

/// <summary>대조 결과와, 불일치일 때 우리가 알고 있던 키들.</summary>
/// <param name="Trust">판정.</param>
/// <param name="KnownFingerprints">이 호스트로 알고 있던 키의 지문들 (불일치 경고에 나란히 보인다).</param>
public sealed record HostKeyVerdict(HostKeyTrust Trust, IReadOnlyList<string> KnownFingerprints);

/// <summary>
/// OpenSSH <c>known_hosts</c> 대조·기록.
///
/// **읽기**는 사용자의 <c>~/.ssh/known_hosts</c> 와 우리 것(<c>~/.prismone-studio/known_hosts</c>)을
/// 모두 본다 — 터미널에서 이미 그 bastion 에 붙어 본 사람은 아무것도 안 물어보게 하기 위해서다.
/// **쓰기**는 우리 파일에만 한다. 사용자의 <c>~/.ssh/known_hosts</c> 는 건드리지 않는다 —
/// 다른 도구가 쓰는 파일을 GUI 가 말없이 고치면 안 된다.
///
/// 형식은 OpenSSH 그대로라 사람이 열어 보고 지울 수 있다. 해시된 항목
/// (<c>|1|salt|hash</c>, <c>ssh-keyscan -H</c>·<c>HashKnownHosts yes</c>)도 대조한다.
/// </summary>
public static class KnownHosts
{
    /// <summary>
    /// 테스트가 진짜 홈 디렉터리(와 사용자의 ~/.ssh)를 건드리지 않도록 갈아끼우는 자리.
    /// 앱은 절대 건드리지 않는다 — null 이면 실제 홈을 쓴다.
    ///
    /// 환경변수(HOME)를 바꾸는 방식은 쓰지 않는다: 프로세스 전역이라 같이 도는 다른
    /// 테스트(PasswordCipher·Favorites 등 같은 폴더를 쓰는 것들)까지 끌려간다.
    /// </summary>
    internal static string? HomeOverride { get; set; }

    private static string Home =>
        HomeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string Dir => Path.Combine(Home, ".prismone-studio");

    /// <summary>우리가 승인분을 쌓는 파일. 여기에만 쓴다.</summary>
    public static string OurPath => Path.Combine(Dir, "known_hosts");

    /// <summary>사용자의 OpenSSH 파일. 읽기만 한다.</summary>
    public static string OpenSshPath => Path.Combine(Home, ".ssh", "known_hosts");

    /// <summary>
    /// 서버가 내민 키를 알려진 키들과 대조한다.
    ///
    /// 판정 순서가 중요하다: <b>취소(@revoked)가 최우선</b>이고, 그 다음이 일치,
    /// 그 다음이 "호스트는 아는데 키가 다름"(불일치), 마지막이 처음 보는 호스트다.
    /// </summary>
    public static HostKeyVerdict Verify(HostKeyInfo key)
    {
        var known = new List<string>();
        var matched = false;

        foreach (var entry in LoadAll())
        {
            if (!entry.Matches(key.Host, key.Port)) continue;

            var sameKey = entry.KeyBase64 == key.KeyBase64;
            if (entry.Revoked)
            {
                // 취소된 키는 다른 줄에서 허용하고 있어도 거부한다.
                if (sameKey) return new HostKeyVerdict(HostKeyTrust.Revoked, [key.Fingerprint]);
                continue;   // 이 호스트의 '취소된 다른 키' 는 비교 대상이 아니다
            }

            known.Add(HostKeyInfo.FingerprintOf(entry.KeyBase64));
            if (sameKey && entry.KeyType == key.KeyType) matched = true;
        }

        if (matched) return new HostKeyVerdict(HostKeyTrust.Trusted, known);
        return known.Count > 0
            ? new HostKeyVerdict(HostKeyTrust.Mismatch, known)
            : new HostKeyVerdict(HostKeyTrust.Unknown, known);
    }

    /// <summary>사용자가 승인한 키를 우리 파일에 덧붙인다 (OpenSSH 형식 그대로).</summary>
    public static void Trust(HostKeyInfo key)
    {
        Directory.CreateDirectory(Dir);
        var host = key.Port == SshOptions.DefaultPort ? key.Host : $"[{key.Host}]:{key.Port}";
        var line = $"{host} {key.KeyType} {key.KeyBase64}"
                   + $"   # Aurum {DateTime.Now:yyyy-MM-dd HH:mm}{Environment.NewLine}";
        File.AppendAllText(OurPath, line);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(OurPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>
    /// 불일치를 받아들일 때 — 그 호스트의 <b>우리 파일 항목만</b> 지우고 새 키를 넣는다.
    /// 사용자의 ~/.ssh/known_hosts 는 손대지 않으므로, 거기 옛 키가 남아 있으면
    /// 다음에도 다시 물어본다(그게 맞다 — GUI 가 남의 파일을 조용히 고치면 안 된다).
    /// </summary>
    public static void Replace(HostKeyInfo key)
    {
        if (File.Exists(OurPath))
        {
            var kept = File.ReadAllLines(OurPath)
                .Where(line => Parse(line) is not { } e || !e.Matches(key.Host, key.Port))
                .ToList();
            File.WriteAllLines(OurPath, kept);
        }
        Trust(key);
    }

    private static IEnumerable<Entry> LoadAll()
    {
        foreach (var path in new[] { OurPath, OpenSshPath })
        {
            string[] lines;
            try
            {
                if (!File.Exists(path)) continue;
                lines = File.ReadAllLines(path);
            }
            catch
            {
                continue;   // 읽을 수 없는 파일은 없는 셈 친다 (판정은 fail-closed 쪽으로 기운다)
            }
            foreach (var line in lines)
                if (Parse(line) is { } entry)
                    yield return entry;
        }
    }

    /// <summary>known_hosts 한 줄. 못 읽는 줄은 null 로 건너뛴다.</summary>
    private static Entry? Parse(string line)
    {
        var text = line.Trim();
        if (text.Length == 0 || text.StartsWith('#')) return null;

        var revoked = false;
        if (text.StartsWith('@'))
        {
            var space = text.IndexOf(' ');
            if (space < 0) return null;
            var marker = text[..space];
            // @cert-authority 는 CA 서명 방식이라 우리가 다루는 대조와 규칙이 다르다 — 건너뛴다.
            if (marker == "@cert-authority") return null;
            revoked = marker == "@revoked";
            if (!revoked) return null;
            text = text[(space + 1)..].TrimStart();
        }

        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;
        return new Entry(parts[0], parts[1], parts[2], revoked);
    }

    private sealed record Entry(string Patterns, string KeyType, string KeyBase64, bool Revoked)
    {
        public bool Matches(string host, int port)
        {
            // 기본 포트가 아니면 OpenSSH 는 [host]:port 로 적는다. 둘 다 시도한다 —
            // 사람이 손으로 넣은 항목은 포트를 안 붙였을 수 있다.
            var bracketed = $"[{host}]:{port}";
            var negated = false;
            var hit = false;

            foreach (var raw in Patterns.Split(','))
            {
                var pattern = raw.Trim();
                if (pattern.Length == 0) continue;

                var isNegation = pattern.StartsWith('!');
                if (isNegation) pattern = pattern[1..];

                var isMatch = pattern.StartsWith("|1|", StringComparison.Ordinal)
                    ? HashedMatches(pattern, host) ||
                      (port != SshOptions.DefaultPort && HashedMatches(pattern, bracketed))
                    : Glob(pattern, host) ||
                      (port != SshOptions.DefaultPort && Glob(pattern, bracketed));

                if (!isMatch) continue;
                if (isNegation) negated = true; else hit = true;
            }
            return hit && !negated;
        }

        /// <summary><c>|1|base64(salt)|base64(HMAC-SHA1(salt, host))</c>.</summary>
        private static bool HashedMatches(string pattern, string host)
        {
            var parts = pattern.Split('|');
            if (parts.Length != 4) return false;   // "", "1", salt, hash
            try
            {
                var salt = Convert.FromBase64String(parts[2]);
                var expected = Convert.FromBase64String(parts[3]);
                var actual = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(host));
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch (FormatException)
            {
                return false;   // 손상된 항목
            }
        }

        /// <summary>known_hosts 의 <c>*</c>·<c>?</c> 와일드카드. 호스트명이라 대소문자를 무시한다.</summary>
        private static bool Glob(string pattern, string host)
        {
            if (!pattern.Contains('*') && !pattern.Contains('?'))
                return string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase);

            var regex = "^" + string.Concat(pattern.Select(c => c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => System.Text.RegularExpressions.Regex.Escape(c.ToString()),
            })) + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(
                host, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
    }
}
