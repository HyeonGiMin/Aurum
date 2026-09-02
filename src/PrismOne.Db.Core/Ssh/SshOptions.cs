namespace PrismOne.Db.Core.Ssh;

/// <summary>
/// SSH 인증 방식. 저장된 접속 항목(connections.json)에 숫자로 남으므로
/// <b>기존 이름의 값을 바꾸지 말 것</b> — 새 방식은 뒤에 붙인다.
/// </summary>
public enum SshAuthMode
{
    Password = 0,
    PrivateKey = 1,
}

/// <summary>
/// DataGrip 의 "SSH/SSL &gt; SSH tunnel" 설정에 해당하는 점프 호스트 정보.
///
/// 이 값이 붙은 <see cref="ConnectionProfile"/> 은 DB 에 직접 붙지 않고,
/// 여기 적힌 서버로 SSH 접속한 뒤 로컬 포트 포워딩을 통해 붙는다
/// (<see cref="SshTunnelPool"/>). DB 쪽 host/port 는 <b>SSH 서버에서 본</b> 주소다 —
/// 흔히 <c>localhost:5432</c> 처럼 점프 호스트 자신을 가리킨다.
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
    bool SavePassword = true)
{
    public const int DefaultPort = 22;

    public static SshOptions Empty { get; } = new("", DefaultPort, "");

    /// <summary>상태바·Login List 표기. 비밀 정보는 담지 않는다.</summary>
    public string Describe => Port == DefaultPort ? $"{Username}@{Host}" : $"{Username}@{Host}:{Port}";

    /// <summary>
    /// 쓸 수 있는 설정인지 확인한다. 문제가 없으면 null, 있으면 사람이 읽을 이유를 돌려준다
    /// (다이얼로그가 그대로 띄운다).
    /// </summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
            return "SSH 호스트를 입력하세요.";
        if (Port is <= 0 or > 65535)
            return "SSH 포트는 1~65535 사이여야 합니다.";
        if (string.IsNullOrWhiteSpace(Username))
            return "SSH 사용자 이름을 입력하세요.";

        if (AuthMode == SshAuthMode.PrivateKey)
        {
            if (string.IsNullOrWhiteSpace(PrivateKeyPath))
                return "개인키 파일 경로를 입력하세요.";
            if (!File.Exists(PrivateKeyPath))
                return $"개인키 파일을 찾을 수 없습니다: {PrivateKeyPath}";
        }
        else if (string.IsNullOrEmpty(Password))
        {
            return "SSH 비밀번호를 입력하세요.";
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
}
