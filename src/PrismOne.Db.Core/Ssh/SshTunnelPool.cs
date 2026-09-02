namespace PrismOne.Db.Core.Ssh;

/// <summary>
/// SSH 터널을 대상별로 하나씩만 세우고 공유하는 풀.
///
/// 풀이 필요한 이유는 <b>짧은 접속이 많기 때문</b>이다 — 자동완성 캐시(SchemaCache),
/// ERD 카탈로그, Session Monitor 는 접속을 열고 바로 닫는다. 그때마다 SSH 핸드셰이크를
/// 하면(1~2초) 도구를 못 쓴다. 그래서 (SSH 대상 + DB 대상) 조합마다 터널 하나를 두고
/// 재사용한다.
///
/// 수명은 두 가지로 관리한다:
/// <list type="bullet">
/// <item>쿼리 탭처럼 오래 붙잡는 쪽은 <see cref="LeaseAsync"/> 로 <b>참조를 건다</b> —
///       참조가 남아 있는 동안에는 절대 닫히지 않는다.</item>
/// <item>짧은 접속만 다녀간 터널은 마지막 사용으로부터 <see cref="IdleTimeout"/> 뒤에 닫힌다.</item>
/// </list>
///
/// 잠금 규칙: <see cref="Gate"/> 안에서는 <b>절대 터널을 닫지 않는다</b>. 닫기는 SSH 세션
/// 종료(네트워크 I/O)라 시간이 걸리고, 그 사이 다른 접속이 전부 멈춘다.
/// </summary>
public static class SshTunnelPool
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<TunnelKey, Entry> Entries = [];

    /// <summary>SSH 접속·인증에 허용할 시간.</summary>
    public static TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>붙잡은 쪽이 하나도 없는 터널을 닫기까지 기다리는 시간.</summary>
    public static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>진단·테스트용 — 지금 살아 있는 터널 수.</summary>
    public static int ActiveTunnelCount { get { lock (Gate) return Entries.Count; } }

    /// <summary>
    /// 터널 하나를 특정하는 값. DB 대상까지 넣는 이유는 포워딩이 목적지 하나에 고정되기
    /// 때문이다 — 같은 점프 호스트라도 DB 가 다르면 포워딩이 따로 필요하다.
    /// </summary>
    private readonly record struct TunnelKey(SshOptions Ssh, string DbHost, int DbPort);

    private sealed class Entry
    {
        public Task<SshTunnel> Connecting = null!;

        /// <summary>접속이 끝난 뒤 채워진다. null 이면 아직 붙는 중이거나 실패했다.</summary>
        public SshTunnel? Tunnel;

        public int Leases;
        public Timer? IdleTimer;
    }

    // ---------- 공개 진입점 ----------

    /// <summary>
    /// SSH 설정이 붙은 프로필을 <b>로컬 포워딩 주소로 바꿔</b> 돌려준다.
    /// SSH 설정이 없으면 받은 그대로 돌려준다(무비용) — 호출부가 분기할 필요 없다.
    ///
    /// 돌려주는 프로필은 <c>Ssh</c> 가 비어 있고 <c>ViaTunnel</c> 이 켜져 있어,
    /// 드라이버 쪽 코드가 다시 터널을 세우려 하지 않는다.
    /// </summary>
    public static async Task<ConnectionProfile> ResolveAsync(
        ConnectionProfile profile, CancellationToken ct = default)
    {
        if (profile.Ssh is not { } ssh) return profile;
        var (tunnel, _) = await AcquireAsync(ssh, profile.Host, profile.Port, takeLease: false, ct);
        return profile.ThroughTunnel(tunnel.LocalHost, tunnel.LocalPort);
    }

    /// <summary>
    /// <see cref="ResolveAsync"/> 의 동기판. Mongo 드라이버 감싸개처럼 동기 진입점밖에
    /// 없는 자리에서만 쓴다 — UI 스레드에서 불려도 교착하지 않도록 스레드 풀에서 기다린다.
    /// </summary>
    public static ConnectionProfile Resolve(ConnectionProfile profile)
    {
        if (profile.Ssh is null) return profile;
        return Task.Run(() => ResolveAsync(profile)).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 터널에 참조를 걸고, 로컬 포워딩 주소로 바뀐 프로필을 돌려준다.
    /// 반환된 <see cref="IDisposable"/> 을 버릴 때까지 터널이 유지된다 —
    /// 쿼리 탭(<see cref="QuerySession"/>)처럼 접속을 오래 붙잡는 쪽이 쓴다.
    ///
    /// SSH 설정이 없으면 아무 일도 하지 않는 참조를 돌려준다.
    /// </summary>
    public static async Task<(ConnectionProfile Profile, IDisposable Lease)> LeaseAsync(
        ConnectionProfile profile, CancellationToken ct = default)
    {
        if (profile.Ssh is not { } ssh) return (profile, NullLease.Instance);
        var key = new TunnelKey(ssh, profile.Host, profile.Port);
        var (tunnel, entry) = await AcquireAsync(ssh, profile.Host, profile.Port, takeLease: true, ct);
        return (profile.ThroughTunnel(tunnel.LocalHost, tunnel.LocalPort), new Lease(key, entry));
    }

    /// <summary>앱 종료 시 호출 — 열려 있는 SSH 세션을 정리한다.</summary>
    public static void CloseAll()
    {
        List<Entry> closing;
        lock (Gate)
        {
            closing = [.. Entries.Values];
            Entries.Clear();
            foreach (var entry in closing) CancelIdleTimer(entry);
        }
        foreach (var entry in closing) Close(entry);
    }

    // ---------- 내부 ----------

    /// <summary>
    /// 터널과 그 항목을 함께 돌려준다 — 참조를 놓을 때 <b>키가 아니라 항목</b>을 찾아가야
    /// 한다. 그 사이 터널이 끊겨 새 항목으로 교체되면, 키로 찾으면 엉뚱한(새) 터널의
    /// 참조를 깎게 된다.
    /// </summary>
    private static async Task<(SshTunnel Tunnel, Entry Entry)> AcquireAsync(
        SshOptions ssh, string dbHost, int dbPort, bool takeLease, CancellationToken ct)
    {
        var key = new TunnelKey(ssh, dbHost, dbPort);
        Entry entry;
        Entry? discarded = null;

        lock (Gate)
        {
            if (Entries.TryGetValue(key, out var existing) && IsReusable(existing))
            {
                entry = existing;
            }
            else
            {
                // 끊긴 터널은 버린다 (네트워크 단절·sshd 재시작). 붙잡은 쪽이 있어도
                // 이미 죽은 것이라 붙들고 있어 봐야 소용없다.
                if (existing is not null)
                {
                    Entries.Remove(key);
                    CancelIdleTimer(existing);
                    discarded = existing;   // 닫기는 락 밖에서
                }
                // 락 안에서 시작만 하고 기다리지는 않는다 — 핸드셰이크(수 초) 동안
                // 락을 쥐고 있으면 같은 시각의 다른 접속이 전부 멈춘다.
                entry = new Entry
                {
                    Connecting = SshTunnel.ConnectAsync(ssh, dbHost, dbPort, ConnectTimeout, ct),
                };
                Entries[key] = entry;
            }

            if (takeLease) entry.Leases++;
            CancelIdleTimer(entry);
        }

        if (discarded is not null) Close(discarded);

        try
        {
            var tunnel = await entry.Connecting;
            lock (Gate)
            {
                entry.Tunnel = tunnel;
                RefreshIdleTimer(key, entry);
            }
            return (tunnel, entry);
        }
        catch
        {
            lock (Gate)
            {
                if (takeLease && entry.Leases > 0) entry.Leases--;
                // 실패한 시도는 캐시에 남기지 않는다 — 다음 시도가 다시 붙을 수 있게.
                if (Entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                    Entries.Remove(key);
                CancelIdleTimer(entry);
            }
            throw;
        }
    }

    /// <summary>
    /// 재사용할 수 있는 항목인지. 접속이 실패·취소로 끝난 항목은 붙잡아 봐야 그 예외를
    /// 그대로 다시 받을 뿐이라 버린다. 아직 붙는 중이면 같이 기다린다.
    /// </summary>
    private static bool IsReusable(Entry entry)
    {
        if (entry.Connecting.IsFaulted || entry.Connecting.IsCanceled) return false;
        return entry.Tunnel is null || entry.Tunnel.IsHealthy;
    }

    /// <summary>
    /// 항목의 터널을 닫는다. <b>Gate 를 쥐지 않은 상태에서만</b> 부를 것.
    /// 아직 붙는 중이면 접속이 끝난 뒤에 닫는다 — 반쯤 열린 SSH 세션을 서버에 남기지 않는다.
    /// </summary>
    private static void Close(Entry entry)
    {
        if (entry.Tunnel is { } tunnel)
        {
            tunnel.Dispose();
            return;
        }
        _ = entry.Connecting.ContinueWith(
            t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); },
            TaskScheduler.Default);
    }

    private static void CancelIdleTimer(Entry entry)
    {
        entry.IdleTimer?.Dispose();
        entry.IdleTimer = null;
    }

    /// <summary>붙잡은 쪽이 없을 때만 유휴 타이머를 건다. (호출부가 Gate 를 쥐고 있어야 한다)</summary>
    private static void RefreshIdleTimer(TunnelKey key, Entry entry)
    {
        CancelIdleTimer(entry);
        if (entry.Leases > 0) return;
        entry.IdleTimer = new Timer(
            _ => ReapIfIdle(key, entry), null, IdleTimeout, Timeout.InfiniteTimeSpan);
    }

    private static void ReapIfIdle(TunnelKey key, Entry entry)
    {
        lock (Gate)
        {
            if (entry.Leases > 0) return;                       // 그 사이 누가 붙잡았다
            if (!Entries.TryGetValue(key, out var current) || !ReferenceEquals(current, entry))
                return;                                        // 이미 교체·정리되었다
            Entries.Remove(key);
            CancelIdleTimer(entry);
        }
        Close(entry);
    }

    private static void ReleaseLease(TunnelKey key, Entry entry)
    {
        var closeNow = false;
        lock (Gate)
        {
            if (entry.Leases > 0) entry.Leases--;
            if (Entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
            {
                // 아직 풀에 있는 항목 — 바로 닫지 않고 유휴 시간을 준다 (짧은 접속이 이어질 수 있다).
                RefreshIdleTimer(key, entry);
            }
            else if (entry.Leases == 0)
            {
                // 이미 풀에서 빠진(교체된) 항목이라 유휴 타이머를 걸 대상이 아니다 — 바로 닫는다.
                CancelIdleTimer(entry);
                closeNow = true;
            }
        }
        if (closeNow) Close(entry);
    }

    /// <summary>참조 하나. 두 번 버려도 한 번만 센다.</summary>
    private sealed class Lease(TunnelKey key, Entry entry) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            ReleaseLease(key, entry);
        }
    }

    /// <summary>SSH 를 안 쓰는 프로필용 — 호출부가 null 검사를 하지 않게.</summary>
    private sealed class NullLease : IDisposable
    {
        public static readonly NullLease Instance = new();
        public void Dispose() { }
    }
}
