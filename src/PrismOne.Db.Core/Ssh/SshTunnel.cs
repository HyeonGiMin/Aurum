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
/// SSH 접속 하나 + 그 위의 로컬 포트 포워딩 하나.
///
/// 127.0.0.1 의 임의 포트로만 listen 한다 — 다른 장비에서 이 포트로 들어와
/// 우리 터널을 타고 DB 에 붙는 일이 없게 하기 위해서다(ssh -L 의 기본 동작과 같다).
///
/// 수명은 <see cref="SshTunnelPool"/> 이 관리한다. 직접 만들지 말 것.
/// </summary>
internal sealed class SshTunnel : IDisposable
{
    private const string LoopbackHost = "127.0.0.1";

    /// <summary>로컬 포트를 못 잡았을 때 다시 시도할 횟수 (고른 포트를 남이 먼저 채갈 수 있다).</summary>
    private const int BindAttempts = 5;

    private readonly SshClient _client;
    private readonly ForwardedPortLocal _forward;
    private bool _disposed;

    private SshTunnel(SshClient client, ForwardedPortLocal forward)
    {
        _client = client;
        _forward = forward;
        LocalPort = (int)forward.BoundPort;
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
            try { return _client.IsConnected && _forward.IsStarted; }
            catch { return false; }
        }
    }

    /// <summary>
    /// SSH 로 붙고 <paramref name="remoteHost"/>:<paramref name="remotePort"/> 로 가는
    /// 로컬 포워딩을 연다. 실패는 전부 <see cref="SshTunnelException"/> 으로 감싼다.
    /// </summary>
    public static async Task<SshTunnel> ConnectAsync(
        SshOptions ssh, string remoteHost, int remotePort,
        TimeSpan timeout, CancellationToken ct = default)
    {
        if (ssh.Validate() is { } invalid)
            throw new SshTunnelException(invalid);

        var client = new SshClient(BuildConnectionInfo(ssh, timeout));
        try
        {
            // NAT·방화벽이 유휴 SSH 세션을 조용히 끊는 걸 막는다 (탭을 열어만 둔 상태가 흔하다).
            client.KeepAliveInterval = TimeSpan.FromSeconds(30);
            await client.ConnectAsync(ct);
            var forward = StartForward(client, remoteHost, remotePort);
            return new SshTunnel(client, forward);
        }
        catch (Exception ex)
        {
            client.Dispose();
            // 취소는 그대로 올려보낸다 — 호출부가 "실패" 와 "그만둠" 을 구분해야 한다.
            if (ex is OperationCanceledException) throw;
            throw Wrap(ex, ssh);
        }
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

    private static ConnectionInfo BuildConnectionInfo(SshOptions ssh, TimeSpan timeout)
    {
        AuthenticationMethod[] methods;
        if (ssh.AuthMode == SshAuthMode.PrivateKey)
        {
            PrivateKeyFile key;
            try
            {
                key = string.IsNullOrEmpty(ssh.Passphrase)
                    ? new PrivateKeyFile(ssh.PrivateKeyPath!)
                    : new PrivateKeyFile(ssh.PrivateKeyPath!, ssh.Passphrase);
            }
            catch (SshPassPhraseNullOrEmptyException ex)
            {
                throw new SshTunnelException("개인키에 passphrase 가 걸려 있습니다. passphrase 를 입력하세요.", ex);
            }
            catch (Exception ex)
            {
                throw new SshTunnelException($"개인키를 읽지 못했습니다: {ex.Message}", ex);
            }
            methods = [new PrivateKeyAuthenticationMethod(ssh.Username, key)];
        }
        else
        {
            // 서버가 password 를 끄고 keyboard-interactive 만 켠 경우가 흔하다.
            // 둘 다 등록해 두면 서버가 받아주는 쪽으로 붙는다 (DataGrip 도 같은 방식).
            var interactive = new KeyboardInteractiveAuthenticationMethod(ssh.Username);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                    prompt.Response = ssh.Password ?? "";
            };
            methods =
            [
                new PasswordAuthenticationMethod(ssh.Username, ssh.Password ?? ""),
                interactive,
            ];
        }

        return new ConnectionInfo(ssh.Host, ssh.Port, ssh.Username, methods) { Timeout = timeout };
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
        try { if (_forward.IsStarted) _forward.Stop(); } catch { /* 이미 끊긴 터널 */ }
        try { _client.RemoveForwardedPort(_forward); } catch { /* 위와 같음 */ }
        try { _forward.Dispose(); } catch { /* 위와 같음 */ }
        try { _client.Disconnect(); } catch { /* 위와 같음 */ }
        try { _client.Dispose(); } catch { /* 위와 같음 */ }
    }
}
