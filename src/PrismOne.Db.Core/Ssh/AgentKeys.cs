using Renci.SshNet;

namespace PrismOne.Db.Core.Ssh;

/// <summary>
/// 키 에이전트가 들고 있는 신원들과, 그것을 어디서 받았는지.
/// </summary>
/// <param name="Keys">SSH.NET 공개키 인증에 그대로 넘길 수 있는 신원들.</param>
/// <param name="Transport">"ssh-agent" 또는 "Pageant" — 설정 창이 사용자에게 보여준다.</param>
public sealed record AgentIdentities(IReadOnlyList<IPrivateKeySource> Keys, string Transport)
{
    public string Describe => $"{Transport} — 키 {Keys.Count}개";
}

/// <summary>
/// ssh-agent · PuTTY Pageant 에서 신원을 가져온다 (DataGrip 의 "authentication agent").
///
/// 실제 프로토콜은 <c>SshNet.Agent</c> 패키지가 처리한다. 직접 짜다가 갈아탄 이유:
/// <list type="bullet">
/// <item><b>Pageant</b> — 0.77+ 는 명명 파이프를, 그 이전은 WM_COPYDATA 공유메모리를 쓴다.
///       후자는 Win32 상호운용이라 직접 쓰면 검증이 어렵다.</item>
/// <item><b>FIDO 보안키</b>(<c>sk-ecdsa-*</c>·<c>sk-ssh-ed25519</c>)와 <b>OpenSSH 인증서</b>
///       (<c>*-cert-v01@openssh.com</c>) — 직접 짠 판은 이런 신원을 조용히 건너뛰었다.
///       "agent 에 키가 있는데 Aurum 만 못 본다" 는 진단하기 아주 나쁜 증상이다.</item>
/// <item>PKCS#11 스마트카드, agent 잠금/해제, rsa-sha2 우선순위도 함께 딸려온다.</item>
/// </list>
///
/// <b>개인키는 이 프로세스에 들어오지 않는다</b> — 서명은 agent 안에서 일어난다.
/// 그래서 이 방식에는 저장할 비밀이 없다.
/// </summary>
public static class AgentKeys
{
    /// <summary>agent 가 안 떠 있을 때 오래 매달리지 않도록.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 쓸 수 있는 agent 를 찾는다. OpenSSH agent 를 먼저 보고, Windows 면 Pageant 도 본다 —
    /// 사용자가 둘 중 어느 것을 쓰는지 우리가 물어볼 이유가 없다.
    /// </summary>
    /// <exception cref="SshTunnelException">쓸 수 있는 agent 가 없을 때, 이유를 담아서.</exception>
    public static AgentIdentities Load() =>
        TryLoad(out var reason) ?? throw new SshTunnelException(reason);

    /// <summary>설정 창의 안내 문구용 — 던지지 않는 판.</summary>
    public static AgentIdentities? TryLoad() => TryLoad(out _);

    private static AgentIdentities? TryLoad(out string reason)
    {
        var problems = new List<string>();

        if (Probe(() => new SshNet.Agent.SshAgent(Timeout), "ssh-agent", problems) is { } openSsh)
        {
            reason = "";
            return openSsh;
        }

        // Pageant 는 Windows 전용이다. 다른 OS 에서 시도하면 무의미한 오류만 쌓인다.
        if (OperatingSystem.IsWindows()
            && Probe(() => new SshNet.Agent.Pageant(Timeout), "Pageant", problems) is { } pageant)
        {
            reason = "";
            return pageant;
        }

        reason = Describe(problems);
        return null;
    }

    /// <summary>
    /// agent 하나를 두드려 본다. 키가 <b>하나도 없으면 실패로 친다</b> — 그래야 Windows 에서
    /// OpenSSH agent 가 비어 있어도 Pageant 로 넘어간다.
    /// </summary>
    private static AgentIdentities? Probe(
        Func<SshNet.Agent.SshAgent> connect, string transport, List<string> problems)
    {
        try
        {
            var keys = connect().RequestIdentities();
            if (keys.Length > 0)
                return new AgentIdentities(keys, transport);
            problems.Add($"{transport}: 키가 들어 있지 않습니다");
        }
        catch (Exception ex)
        {
            problems.Add($"{transport}: {ex.Message}");
        }
        return null;
    }

    /// <summary>왜 못 썼는지 — 사용자가 다음에 할 일이 보이게.</summary>
    private static string Describe(List<string> problems)
    {
        var detail = problems.Count > 0 ? " (" + string.Join(" / ", problems) + ")" : "";
        var howTo = OperatingSystem.IsWindows()
            ? "OpenSSH 인증 에이전트 서비스(services.msc 의 'OpenSSH Authentication Agent')가 "
              + "실행 중인지, 또는 Pageant 가 떠 있는지 확인하고 ssh-add 로 키를 넣으세요."
            : "SSH_AUTH_SOCK 이 설정되어 있는지 확인하고 ssh-add 로 키를 넣으세요 (ssh-add -l 로 확인).";
        return $"쓸 수 있는 키 에이전트를 찾지 못했습니다. {howTo}{detail}";
    }
}
