namespace PrismOne.Db.Core.Ssh;

/// <summary>
/// 하나의 접속 설정을 <b>실제로 거쳐 갈 홉 목록</b>으로 편다.
///
/// 두 가지가 홉을 늘린다:
/// <list type="number">
/// <item>설정 창에 직접 적은 <see cref="SshOptions.ProxyJump"/> (OpenSSH <c>-J</c> 표기).</item>
/// <item><c>~/.ssh/config</c> 의 <c>ProxyJump</c> 지시자 (인증 방식이
///       <see cref="SshAuthMode.OpenSshConfig"/> 일 때).</item>
/// </list>
///
/// 결과의 순서는 <b>먼저 붙는 것부터</b>다: <c>[jump1, jump2, …, 최종 SSH 서버]</c>.
/// 목록은 언제나 최소 하나(최종 서버)를 담는다.
///
/// 점프 호스트의 인증은 기본적으로 <b>원래 설정을 물려받는다</b> — 실제로 ProxyJump 를
/// 쓰는 사람은 agent 나 키 하나로 전 구간을 통과하기 때문이다. <c>~/.ssh/config</c> 가
/// 그 호스트에 User·Port·IdentityFile 을 따로 정해 두었으면 그쪽이 이긴다.
/// </summary>
public static class SshHops
{
    /// <summary>ProxyJump 이 서로를 부르는 사고를 막는 상한.</summary>
    private const int MaxHops = 8;

    public static IReadOnlyList<SshOptions> Expand(SshOptions options)
    {
        var hops = new List<SshOptions>();
        Add(hops, options, depth: 0);
        return hops;
    }

    private static void Add(List<SshOptions> hops, SshOptions options, int depth)
    {
        // 깊이로 끊어야 한다. 홉은 재귀가 풀릴 때 담기므로 목록 길이만 봐서는
        // 서로를 부르는 ProxyJump(a→b→a)를 못 잡는다.
        if (depth >= MaxHops || hops.Count >= MaxHops)
            throw new SshTunnelException(
                $"경유 호스트가 {MaxHops}개를 넘습니다 — ProxyJump 설정에 순환이 있는지 확인하세요.");

        var resolved = ApplyConfig(options);

        // 앞에 세울 점프 호스트들을 먼저 붙인다 (재귀 — 점프 호스트도 자기 ProxyJump 를 가질 수 있다).
        foreach (var spec in ParseJumpSpecs(resolved.ProxyJump))
            Add(hops, Inherit(resolved, spec), depth + 1);

        // 자기 자신은 ProxyJump 를 비운 채로 넣는다 — 이미 앞에 펼쳤다.
        hops.Add(resolved with { ProxyJump = null });
    }

    /// <summary>
    /// <c>~/.ssh/config</c> 를 반영한다. 인증 방식이 OpenSSH config 일 때만 전면 적용하고,
    /// 그 외에는 사용자가 창에 적은 값을 그대로 둔다 — 적은 값이 조용히 바뀌면 안 된다.
    /// </summary>
    private static SshOptions ApplyConfig(SshOptions options)
    {
        if (options.AuthMode != SshAuthMode.OpenSshConfig) return options;

        var config = SshConfig.Resolve(options.Host);
        return options with
        {
            Host = config.HostName,
            // 창에 적은 값이 있으면 그게 이긴다 (사용자가 일부러 덮어쓴 것).
            Username = string.IsNullOrWhiteSpace(options.Username)
                ? config.User ?? Environment.UserName
                : options.Username,
            Port = options.Port == SshOptions.DefaultPort
                ? config.Port ?? SshOptions.DefaultPort
                : options.Port,
            PrivateKeyPath = string.IsNullOrWhiteSpace(options.PrivateKeyPath)
                ? config.IdentityFiles.FirstOrDefault(File.Exists)
                : options.PrivateKeyPath,
            // 창에 직접 적은 ProxyJump 가 우선, 없으면 설정의 것.
            ProxyJump = string.IsNullOrWhiteSpace(options.ProxyJump) ? config.ProxyJump : options.ProxyJump,
        };
    }

    /// <summary>
    /// 점프 호스트 하나를 원래 설정에서 인증을 물려받아 만든다.
    ///
    /// 사용자 이름은 <c>-J</c> 표기에 적힌 것이 최우선이고, 없으면
    /// <b>OpenSSH config 모드에서는 비워 둔다</b> — 그 호스트의 <c>User</c> 를 설정에서
    /// 읽어야 하기 때문이다(옆 호스트의 계정을 끌고 오면 안 된다). 다른 방식에서는
    /// 물려받는다: 한 계정으로 전 구간을 지나는 게 보통이다.
    /// </summary>
    private static SshOptions Inherit(SshOptions parent, JumpSpec spec) => parent with
    {
        Host = spec.Host,
        Port = spec.Port ?? SshOptions.DefaultPort,
        Username = spec.User
                   ?? (parent.AuthMode == SshAuthMode.OpenSshConfig ? "" : parent.Username),
        ProxyJump = null,   // 이 홉의 ProxyJump 는 ApplyConfig 가 설정에서 다시 찾는다
    };

    /// <summary><c>[user@]host[:port]</c> 를 콤마로 나열한 OpenSSH <c>-J</c> 표기.</summary>
    private sealed record JumpSpec(string Host, string? User, int? Port);

    private static IEnumerable<JumpSpec> ParseJumpSpecs(string? proxyJump)
    {
        if (string.IsNullOrWhiteSpace(proxyJump)) yield break;

        foreach (var raw in proxyJump.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            if (token.Length == 0 || string.Equals(token, "none", StringComparison.OrdinalIgnoreCase))
                continue;

            string? user = null;
            var at = token.LastIndexOf('@');
            if (at >= 0)
            {
                user = token[..at];
                token = token[(at + 1)..];
            }

            int? port = null;
            var colon = token.LastIndexOf(':');
            if (colon > 0 && int.TryParse(token[(colon + 1)..], out var parsed) && parsed is > 0 and <= 65535)
            {
                port = parsed;
                token = token[..colon];
            }

            if (token.Length > 0)
                yield return new JumpSpec(token, string.IsNullOrWhiteSpace(user) ? null : user, port);
        }
    }
}
