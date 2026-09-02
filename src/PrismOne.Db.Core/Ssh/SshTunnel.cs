using System.Net;
using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace PrismOne.Db.Core.Ssh;

/// <summary>
/// 터널을 세우지 못했을 때. DB 접속 실패와 구분하려고 따로 둔다 —
/// "DB 가 안 뜬다" 와 "점프 호스트에 못 붙는다" 는 사용자가 할 일이 전혀 다르다.
/// </summary>
public sealed class SshTunnelException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// SSH 세션 사슬 + 그 끝의 로컬 포트 포워딩. 경유가 없으면 사슬 길이는 1 이다.
///
/// 포워딩은 언제나 127.0.0.1 의 임의 포트로만 listen 한다 — 다른 장비에서 이 포트로
/// 들어와 우리 터널을 타고 DB 에 붙는 일이 없게 하기 위해서다(ssh -L 의 기본과 같다).
///
/// 수명은 <see cref="SshTunnelPool"/> 이 관리한다. 직접 만들지 말 것.
/// </summary>
internal sealed class SshTunnel : IDisposable
{
    private const string LoopbackHost = "127.0.0.1";

    /// <summary>로컬 포트를 못 잡았을 때 다시 시도할 횟수 (고른 포트를 남이 먼저 채갈 수 있다).</summary>
    private const int BindAttempts = 5;

    /// <summary>거쳐 가는 SSH 세션들. <c>[0]</c> 이 첫 점프 호스트, 마지막이 최종 SSH 서버다.</summary>
    private readonly List<SshClient> _clients;

    /// <summary>홉마다 하나씩. 마지막이 DB 로 가는 포워딩이고, 앞의 것들은 다음 홉으로 가는 통로다.</summary>
    private readonly List<ForwardedPortLocal> _forwards;

    private bool _disposed;

    private SshTunnel(List<SshClient> clients, List<ForwardedPortLocal> forwards)
    {
        _clients = clients;
        _forwards = forwards;
        LocalPort = (int)forwards[^1].BoundPort;
    }

    public string LocalHost => LoopbackHost;

    /// <summary>DB 드라이버가 실제로 붙을 로컬 포트.</summary>
    public int LocalPort { get; }

    /// <summary>
    /// SSH 세션과 포워딩이 아직 살아 있는지. 네트워크가 끊기거나 sshd 가 재시작되면
    /// false 가 되고, 풀이 이 터널을 버리고 새로 세운다.
    /// </summary>
    public bool IsHealthy
    {
        get
        {
            if (_disposed) return false;
            try { return _clients.All(c => c.IsConnected) && _forwards.All(f => f.IsStarted); }
            catch { return false; }
        }
    }

    /// <summary>
    /// 홉들을 차례로 거쳐 <paramref name="remoteHost"/>:<paramref name="remotePort"/> 로 가는
    /// 로컬 포워딩을 연다. 실패는 전부 <see cref="SshTunnelException"/> 으로 감싼다.
    ///
    /// 다단 경유(ProxyJump)는 <b>포워딩을 사슬로 잇는</b> 방식이다: 첫 홉에 붙어 두 번째
    /// 홉의 22번으로 가는 로컬 포워딩을 열고, 그 로컬 포트에 두 번째 SSH 세션을 붙인다.
    /// 이렇게 반복하다 마지막 홉에서 DB 로 포워딩한다.
    ///
    /// 중간 홉은 <c>127.0.0.1:임의포트</c> 로 붙지만 <b>호스트 키는 진짜 이름으로 확인한다</b> —
    /// 127.0.0.1 로 대조하면 모든 점프 호스트가 같은 이름이 되어 검증이 무의미해진다.
    /// </summary>
    public static async Task<SshTunnel> ConnectAsync(
        IReadOnlyList<SshOptions> hops, string remoteHost, int remotePort,
        TimeSpan timeout, HostKeyPromptHandler? hostKeyPrompt, CancellationToken ct = default)
    {
        if (hops.Count == 0) throw new SshTunnelException("SSH 홉이 하나도 없습니다.");
        foreach (var hop in hops)
            if (hop.Validate() is { } invalid)
                throw new SshTunnelException(invalid);

        var clients = new List<SshClient>(hops.Count);
        var forwards = new List<ForwardedPortLocal>(hops.Count);
        try
        {
            // 첫 홉은 진짜 주소로 붙고, 그 다음부터는 앞 홉이 열어 준 로컬 포트로 붙는다.
            var dialHost = hops[0].Host;
            var dialPort = hops[0].Port;

            for (var i = 0; i < hops.Count; i++)
            {
                var client = await ConnectClientAsync(hops[i], dialHost, dialPort, timeout, hostKeyPrompt, ct);
                clients.Add(client);

                // 이 홉에서 갈 곳: 다음 홉의 SSH 포트, 마지막이면 DB.
                var (nextHost, nextPort) = i + 1 < hops.Count
                    ? (hops[i + 1].Host, hops[i + 1].Port)
                    : (remoteHost, remotePort);

                var forward = StartForward(client, nextHost, nextPort);
                forwards.Add(forward);

                dialHost = LoopbackHost;
                dialPort = (int)forward.BoundPort;
            }

            return new SshTunnel(clients, forwards);
        }
        catch
        {
            // 반쯤 세운 사슬은 역순으로 정리한다 — 안쪽 세션부터 닫아야 깔끔하다.
            CloseChain(clients, forwards);
            throw;
        }
    }

    /// <summary>
    /// 홉 하나에 붙는다. <paramref name="dialHost"/>/<paramref name="dialPort"/> 는 실제로
    /// TCP 를 여는 주소이고(중간 홉이면 루프백), 호스트 키 대조와 오류 메시지는
    /// <paramref name="hop"/> 의 진짜 이름으로 한다.
    /// </summary>
    private static async Task<SshClient> ConnectClientAsync(
        SshOptions hop, string dialHost, int dialPort,
        TimeSpan timeout, HostKeyPromptHandler? hostKeyPrompt, CancellationToken ct)
    {
        var client = new SshClient(BuildConnectionInfo(hop, dialHost, dialPort, timeout));
        // 키를 거부한 이유. SSH.NET 은 CanTrust=false 를 "Key exchange negotiation failed"
        // 라는 엉뚱한 메시지로만 알려주므로, 진짜 이유를 여기 담아 두고 아래에서 바꿔 던진다.
        string? rejection = null;
        try
        {
            // **호스트 키 검증.** SSH.NET 은 이 이벤트를 구독하지 않으면 어떤 키든 그냥
            // 신뢰한다 — 그러면 중간자가 bastion 인 척하고 DB 비밀번호를 그대로 받아간다.
            client.HostKeyReceived += (_, e) =>
            {
                e.CanTrust = Approve(hop, e, hostKeyPrompt, out var reason);
                if (reason is not null) rejection = reason;
            };

            // NAT·방화벽이 유휴 SSH 세션을 조용히 끊는 걸 막는다 (탭을 열어만 둔 상태가 흔하다).
            client.KeepAliveInterval = TimeSpan.FromSeconds(30);
            await client.ConnectAsync(ct);
            return client;
        }
        catch (Exception ex)
        {
            client.Dispose();
            // 취소는 그대로 올려보낸다 — 호출부가 "실패" 와 "그만둠" 을 구분해야 한다.
            if (ex is OperationCanceledException) throw;
            if (rejection is { } reason) throw new SshTunnelException(reason, ex);
            throw Wrap(ex, hop);
        }
    }

    /// <summary>
    /// 서버가 내민 키를 받아들일지 정한다 (pgAdmin 과 같은 규칙).
    /// 아는 키면 조용히 통과, 취소된 키면 무조건 거부, 처음 보거나 다르면 사용자에게 묻는다.
    /// 승인하면 그 키를 기억해 다음부터는 안 묻는다.
    /// </summary>
    private static bool Approve(
        SshOptions ssh, HostKeyEventArgs e, HostKeyPromptHandler? prompt, out string? rejection)
    {
        rejection = null;
        var info = new HostKeyInfo(ssh.Host, ssh.Port, e.HostKeyName, Convert.ToBase64String(e.HostKey));
        var verdict = KnownHosts.Verify(info);

        switch (verdict.Trust)
        {
            case HostKeyTrust.Trusted:
                return true;

            case HostKeyTrust.Revoked:
                rejection = $"{ssh.Describe} 의 호스트 키가 known_hosts 에서 취소(@revoked)된 키입니다. "
                            + $"접속을 중단했습니다 ({info.Fingerprint}).";
                return false;
        }

        if (prompt is null)
        {
            // 물어볼 UI 가 없다 — 조용히 믿는 대신 막는다.
            rejection = verdict.Trust == HostKeyTrust.Mismatch
                ? $"{ssh.Describe} 의 호스트 키가 알려진 것과 다릅니다 ({info.Fingerprint})."
                : $"{ssh.Describe} 는 처음 보는 호스트입니다 ({info.Fingerprint}). "
                  + "known_hosts 에 등록한 뒤 다시 시도하세요.";
            return false;
        }

        if (!prompt(new HostKeyRequest(info, verdict)))
        {
            rejection = $"{ssh.Describe} 의 호스트 키를 거부해 접속을 중단했습니다 ({info.Fingerprint}).";
            return false;
        }

        // 기억은 승인한 뒤에만. 불일치는 옛 항목을 밀어내야 다음에 또 안 묻는다.
        try
        {
            if (verdict.Trust == HostKeyTrust.Mismatch) KnownHosts.Replace(info);
            else KnownHosts.Trust(info);
        }
        catch (Exception ex)
        {
            // 기록에 실패해도 이번 접속은 사용자가 승인한 것이라 그대로 진행한다
            // (다음에 또 물어볼 뿐이다). 조용히 넘기지는 않는다.
            System.Diagnostics.Debug.WriteLine($"known_hosts 기록 실패: {ex.Message}");
        }
        return true;
    }

    /// <summary>
    /// 로컬 포트를 먼저 잡아 두고 그 번호로 포워딩을 연다. 포트 0 을 넘겨 드라이버가
    /// 정하게 두지 않는 이유는, 실제로 열린 번호를 확실히 알기 위해서다.
    /// </summary>
    private static ForwardedPortLocal StartForward(SshClient client, string remoteHost, int remotePort)
    {
        SocketException? last = null;
        for (var attempt = 0; attempt < BindAttempts; attempt++)
        {
            var forward = new ForwardedPortLocal(
                LoopbackHost, (uint)PickFreeLoopbackPort(), remoteHost, (uint)remotePort);
            try
            {
                client.AddForwardedPort(forward);
                forward.Start();
                return forward;
            }
            catch (SocketException ex)
            {
                // 고른 포트를 그 사이에 남이 채갔다 — 다른 번호로 다시.
                last = ex;
                try { client.RemoveForwardedPort(forward); } catch { /* 이미 빠졌을 수 있다 */ }
                forward.Dispose();
            }
        }
        throw new SshTunnelException(
            $"로컬 포워딩 포트를 {BindAttempts}번 시도했지만 열지 못했습니다.", last);
    }

    /// <summary>비어 있는 루프백 포트 하나. 잡았다 놓는 사이의 경합은 호출부가 재시도로 흡수한다.</summary>
    private static int PickFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    /// <summary>
    /// 인증 방법을 조립한다. <paramref name="dialHost"/>/<paramref name="dialPort"/> 는
    /// 실제 TCP 대상이라 중간 홉에서는 루프백이다 — <c>ssh.Host</c> 와 다를 수 있다.
    /// </summary>
    private static ConnectionInfo BuildConnectionInfo(
        SshOptions ssh, string dialHost, int dialPort, TimeSpan timeout)
    {
        var username = string.IsNullOrWhiteSpace(ssh.Username) ? Environment.UserName : ssh.Username;
        // 배열로 타입을 못박는다 — 컬렉션 식([...])은 대상 타입이 있어야 한다.
        AuthenticationMethod[] methods = ssh.AuthMode switch
        {
            SshAuthMode.PrivateKey => [KeyMethod(username, ssh.PrivateKeyPath!, ssh.Passphrase)],
            SshAuthMode.Agent => [AgentMethod(username)],
            // OpenSSH config 는 설정이 짚어 준 키가 있으면 그걸 먼저, 없으면 agent 로.
            // 둘 다 올려 두면 서버가 받아주는 쪽으로 붙는다 (ssh 의 동작과 같다).
            SshAuthMode.OpenSshConfig => ConfigMethods(username, ssh),
            _ => PasswordMethods(username, ssh.Password ?? ""),
        };

        return new ConnectionInfo(dialHost, dialPort, username, methods) { Timeout = timeout };
    }

    private static AuthenticationMethod[] PasswordMethods(string username, string password)
    {
        // 서버가 password 를 끄고 keyboard-interactive 만 켠 경우가 흔하다.
        // 둘 다 등록해 두면 서버가 받아주는 쪽으로 붙는다 (DataGrip 도 같은 방식).
        var interactive = new KeyboardInteractiveAuthenticationMethod(username);
        interactive.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
                prompt.Response = password;
        };
        return [new PasswordAuthenticationMethod(username, password), interactive];
    }

    private static AuthenticationMethod KeyMethod(string username, string path, string? passphrase)
    {
        PrivateKeyFile key;
        try
        {
            key = string.IsNullOrEmpty(passphrase)
                ? new PrivateKeyFile(path)
                : new PrivateKeyFile(path, passphrase);
        }
        catch (SshPassPhraseNullOrEmptyException ex)
        {
            throw new SshTunnelException("개인키에 passphrase 가 걸려 있습니다. passphrase 를 입력하세요.", ex);
        }
        catch (Exception ex)
        {
            throw new SshTunnelException($"개인키를 읽지 못했습니다 ({path}): {ex.Message}", ex);
        }
        return new PrivateKeyAuthenticationMethod(username, key);
    }

    /// <summary>agent 가 든 신원 전부를 한 번에 올린다 — 서버가 받아주는 키로 붙는다.</summary>
    private static AuthenticationMethod AgentMethod(string username)
    {
        var identities = SshAgent.LoadIdentities();
        if (identities.Count == 0)
            throw new SshTunnelException(
                SshAgent.IsAvailable
                    ? "ssh-agent 에 키가 들어 있지 않습니다. ssh-add 로 키를 넣으세요."
                    : SshAgent.UnavailableReason);
        return new PrivateKeyAuthenticationMethod(username, new AgentKeySource(identities));
    }

    /// <summary>OpenSSH config 모드 — 설정이 짚은 키와 agent 를 함께 시도한다.</summary>
    private static AuthenticationMethod[] ConfigMethods(string username, SshOptions ssh)
    {
        var methods = new List<AuthenticationMethod>();

        if (!string.IsNullOrWhiteSpace(ssh.PrivateKeyPath) && File.Exists(ssh.PrivateKeyPath))
        {
            // 키가 passphrase 로 잠겨 있고 우리가 그걸 모르면, 이 키는 건너뛰고 agent 에 맡긴다
            // (agent 에 이미 풀어서 넣어 둔 경우가 대부분이다).
            try { methods.Add(KeyMethod(username, ssh.PrivateKeyPath, ssh.Passphrase)); }
            catch (SshTunnelException) { /* 아래 agent 로 넘어간다 */ }
        }

        var identities = SshAgent.LoadIdentities();
        if (identities.Count > 0)
            methods.Add(new PrivateKeyAuthenticationMethod(username, new AgentKeySource(identities)));

        if (methods.Count == 0)
            throw new SshTunnelException(
                $"{ssh.Host} 로 인증할 방법을 찾지 못했습니다 — ~/.ssh/config 의 IdentityFile 을 "
                + $"읽을 수 없고 ssh-agent 에도 키가 없습니다. ({SshAgent.UnavailableReason})");
        return [.. methods];
    }

    /// <summary>드라이버 예외를 사람이 읽을 이유로 바꾼다. 비밀번호는 절대 싣지 않는다.</summary>
    private static SshTunnelException Wrap(Exception ex, SshOptions ssh)
    {
        if (ex is SshTunnelException tunnel) return tunnel;
        var target = ssh.Describe;
        return ex switch
        {
            SshAuthenticationException =>
                new SshTunnelException($"SSH 인증에 실패했습니다 ({target}): {ex.Message}", ex),
            SshOperationTimeoutException =>
                new SshTunnelException($"SSH 접속이 시간 초과되었습니다 ({target}).", ex),
            SocketException =>
                new SshTunnelException($"SSH 서버에 연결하지 못했습니다 ({target}): {ex.Message}", ex),
            _ => new SshTunnelException($"SSH 터널을 세우지 못했습니다 ({target}): {ex.Message}", ex),
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseChain(_clients, _forwards);
    }

    /// <summary>
    /// 포워딩·세션을 <b>역순으로</b> 닫는다 — 안쪽(최종 서버) 세션이 바깥 홉의 포워딩 위에
    /// 얹혀 있으므로, 바깥부터 닫으면 안쪽이 먼저 끊기며 예외가 쏟아진다.
    /// </summary>
    private static void CloseChain(List<SshClient> clients, List<ForwardedPortLocal> forwards)
    {
        for (var i = forwards.Count - 1; i >= 0; i--)
        {
            var forward = forwards[i];
            try { if (forward.IsStarted) forward.Stop(); } catch { /* 이미 끊긴 터널 */ }
            if (i < clients.Count)
                try { clients[i].RemoveForwardedPort(forward); } catch { /* 위와 같음 */ }
            try { forward.Dispose(); } catch { /* 위와 같음 */ }
        }
        for (var i = clients.Count - 1; i >= 0; i--)
        {
            try { clients[i].Disconnect(); } catch { /* 위와 같음 */ }
            try { clients[i].Dispose(); } catch { /* 위와 같음 */ }
        }
        forwards.Clear();
        clients.Clear();
    }
}
