namespace PrismOne.Db.Core.Ssh;

/// <summary>
/// SSH 인증 방식. 저장된 접속 항목(connections.json)에 숫자로 남으므로
/// <b>기존 이름의 값을 바꾸지 말 것</b> — 새 방식은 뒤에 붙인다.
/// </summary>
public enum SshAuthMode
{
    Password = 0,
    PrivateKey = 1,

    /// <summary>ssh-agent · OpenSSH Authentication Agent 가 든 키로 인증. 비밀을 저장하지 않는다.</summary>
    Agent = 2,

    /// <summary>
    /// <c>~/.ssh/config</c> 의 <c>Host</c> 별칭을 그대로 쓴다 — HostName·User·Port·IdentityFile·
    /// ProxyJump 를 설정에서 읽고, 인증은 그 IdentityFile 과 agent 로 한다.
    /// DataGrip 의 "OpenSSH config and authentication agent" 에 해당한다.
    /// </summary>
    OpenSshConfig = 3,
}

/// <summary>
/// DataGrip 의 "SSH/SSL &gt; SSH tunnel" 설정에 해당하는 점프 호스트 정보.
///
/// 이 값이 붙은 <see cref="ConnectionProfile"/> 은 DB 에 직접 붙지 않고,
/// 여기 적힌 서버로 SSH 접속한 뒤 로컬 포트 포워딩을 통해 붙는다
/// (<see cref="SshTunnelPool"/>). DB 쪽 host/port 는 <b>SSH 서버에서 본</b> 주소다 —
/// 흔히 <c>localhost:5432</c> 처럼 점프 호스트 자신을 가리킨다.
///
/// <see cref="Name"/> 이 있으면 <see cref="SshProfileStore"/> 에 한 벌만 저장되고, 접속 항목은
/// 이름만 들고 있다 — 같은 bastion 을 쓰는 접속이 여럿일 때 한 곳만 고치면 된다.
/// 이름이 없으면 "이 접속에만" 쓰는 설정이라 접속 항목 안에 그대로 남는다.
///
/// <see cref="Password"/>·<see cref="Passphrase"/> 는 DB 비밀번호와 똑같이
/// <see cref="PasswordCipher"/> 로 암호화되어 저장되지만, <see cref="SavePassword"/> 를 끄면
/// 아예 디스크에 남지 않고 접속할 때마다 물어본다 (pgAdmin 의 "Prompt for password?" 대응).
/// </summary>
public sealed record SshOptions(
    string Host,
    int Port,
    string Username,
    SshAuthMode AuthMode = SshAuthMode.Password,
    string? Password = null,
    string? PrivateKeyPath = null,
    string? Passphrase = null,
    bool SavePassword = true,
    string? ProxyJump = null,
    string? Name = null)
{
    public const int DefaultPort = 22;

    public static SshOptions Empty { get; } = new("", DefaultPort, "");

    /// <summary>
    /// 상태바·Login List 표기. 비밀 정보는 담지 않는다.
    /// OpenSSH config 모드는 사용자 이름을 설정에서 가져오므로 비어 있을 수 있다.
    /// </summary>
    public string Describe
    {
        get
        {
            var target = Port == DefaultPort ? Host : $"{Host}:{Port}";
            var withUser = string.IsNullOrWhiteSpace(Username) ? target : $"{Username}@{target}";
            // 경유가 있으면 그것까지 보여야 한다 — 최종 주소만으로는 경로를 알 수 없다.
            return string.IsNullOrWhiteSpace(ProxyJump) ? withUser : $"{withUser} via {ProxyJump}";
        }
    }

    /// <summary>
    /// 쓸 수 있는 설정인지 확인한다. 문제가 없으면 null, 있으면 사람이 읽을 이유를 돌려준다
    /// (다이얼로그가 그대로 띄운다).
    /// </summary>
    public string? Validate()
    {
        // 이름만 남고 내용이 비었다면, 접속 항목이 가리키던 저장된 설정이 지워진 것이다.
        // "호스트를 입력하세요" 로 뭉뚱그리면 사용자가 원인을 찾지 못한다.
        if (Name is { Length: > 0 } name && string.IsNullOrWhiteSpace(Host))
            return $"저장된 SSH 설정 '{name}' 을 찾을 수 없습니다 — 지워졌거나 이름이 바뀌었습니다.";

        if (string.IsNullOrWhiteSpace(Host))
            return AuthMode == SshAuthMode.OpenSshConfig
                ? "~/.ssh/config 의 Host 별칭을 입력하세요."
                : "SSH 호스트를 입력하세요.";
        if (Port is <= 0 or > 65535)
            return "SSH 포트는 1~65535 사이여야 합니다.";

        // OpenSSH config 모드는 User 를 설정에서 가져올 수 있어 비어 있어도 된다.
        // 정말 없으면 접속 직전(홉 확장 뒤)에 걸린다.
        if (AuthMode != SshAuthMode.OpenSshConfig && string.IsNullOrWhiteSpace(Username))
            return "SSH 사용자 이름을 입력하세요.";

        switch (AuthMode)
        {
            case SshAuthMode.PrivateKey:
                if (string.IsNullOrWhiteSpace(PrivateKeyPath))
                    return "개인키 파일 경로를 입력하세요.";
                if (!File.Exists(PrivateKeyPath))
                    return $"개인키 파일을 찾을 수 없습니다: {PrivateKeyPath}";
                break;

            case SshAuthMode.Password:
                if (string.IsNullOrEmpty(Password))
                    return "SSH 비밀번호를 입력하세요.";
                break;

            case SshAuthMode.OpenSshConfig:
                if (!SshConfig.Exists)
                    return $"{SshConfig.FilePath} 가 없습니다.";
                break;

            // Agent 는 확인할 입력이 없다 — agent 가 실제로 붙는지는 접속할 때 알 수 있다.
        }

        return null;
    }

    /// <summary>비밀을 뺀 복사본 — 로그·오류 메시지에 실어도 되는 형태이자, 저장할 때
    /// <see cref="SavePassword"/> 가 꺼져 있으면 디스크에 남기는 형태.</summary>
    public SshOptions WithoutSecrets() =>
        this with { Password = null, Passphrase = null };

    /// <summary>
    /// 이 방식이 요구하는 비밀이 지금 채워져 있는지. 저장을 껐다면 접속할 때 비어 있고,
    /// 그때는 물어봐야 한다.
    ///
    /// 개인키는 passphrase 가 없는 키가 정상이므로 <b>비어 있어도 부족한 게 아니다</b> —
    /// 그건 키를 실제로 읽어 봐야 알 수 있고, 실패하면 드라이버가 알려준다.
    /// </summary>
    public bool NeedsPassword => AuthMode == SshAuthMode.Password && string.IsNullOrEmpty(Password);

    /// <summary>비밀을 아예 안 다루는 방식인지 — 설정 창이 저장 체크를 숨기는 데 쓴다.</summary>
    public bool UsesStoredSecret => AuthMode is SshAuthMode.Password or SshAuthMode.PrivateKey;

    /// <summary>이름 붙은(공유) 설정인지 — 저장 방식이 갈린다.</summary>
    public bool IsNamed => !string.IsNullOrWhiteSpace(Name);

    /// <summary>
    /// 짧은 표기. 이름을 붙였으면 이름이 낫다 — 사용자가 고른 것이 그 이름이고,
    /// 같은 이름을 쓰는 접속들이 한 덩어리로 보인다.
    /// </summary>
    public string Label => IsNamed ? Name! : Describe;

    /// <summary>표기용 인증 방식 이름.</summary>
    public string AuthLabel => AuthMode switch
    {
        SshAuthMode.PrivateKey => "개인키",
        SshAuthMode.Agent => "ssh-agent",
        SshAuthMode.OpenSshConfig => "OpenSSH config",
        _ => "비밀번호",
    };
}
