using PrismOne.Db.Core;
using PrismOne.Db.Core.Mongo;
using PrismOne.Db.Core.Providers;
using PrismOne.Db.Core.Ssh;
using Xunit;

namespace PrismOne.Db.Core.Tests;

/// <summary>
/// SSH 터널의 <b>서버 없이 검증 가능한 부분</b> — 설정 검증, 프로필 전파, 저장 형식.
/// 실제 포워딩은 sshd 가 있어야 해서 자동 테스트로 두지 않는다
/// (OracleSessionLiveTests·MongoSessionLiveTests 와 같은 방침).
/// </summary>
public class SshOptionsTests
{
    private static SshOptions Password(string? password = "pw") =>
        new("jump.example.com", 22, "ops", SshAuthMode.Password, password);

    [Fact]
    public void Validate_AcceptsCompletePasswordOptions() => Assert.Null(Password().Validate());

    [Fact]
    public void Validate_RejectsMissingPieces()
    {
        Assert.NotNull((Password() with { Host = "  " }).Validate());
        Assert.NotNull((Password() with { Username = "" }).Validate());
        Assert.NotNull((Password() with { Port = 0 }).Validate());
        Assert.NotNull((Password() with { Port = 70000 }).Validate());
        Assert.NotNull(Password(null).Validate());
        Assert.NotNull(Password("").Validate());
    }

    [Fact]
    public void Validate_PrivateKeyNeedsAnExistingFile()
    {
        var noPath = Password() with { AuthMode = SshAuthMode.PrivateKey, Password = null };
        Assert.NotNull(noPath.Validate());

        var missing = noPath with { PrivateKeyPath = Path.Combine(Path.GetTempPath(), "no-such-key-file") };
        Assert.NotNull(missing.Validate());

        var path = Path.Combine(Path.GetTempPath(), $"aurum-key-{Guid.NewGuid():N}");
        File.WriteAllText(path, "not a real key");
        try
        {
            // 내용은 접속할 때 드라이버가 본다 — 설정 검증은 "파일이 있는가" 까지다
            Assert.Null((noPath with { PrivateKeyPath = path }).Validate());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Describe_OmitsDefaultPort_AndNeverCarriesSecrets()
    {
        Assert.Equal("ops@jump.example.com", Password().Describe);
        Assert.Equal("ops@jump.example.com:2222", (Password() with { Port = 2222 }).Describe);
        Assert.DoesNotContain("pw", Password().Describe);
    }

    [Fact]
    public void WithoutSecrets_DropsPasswordAndPassphrase()
    {
        var stripped = (Password() with { Passphrase = "phrase" }).WithoutSecrets();
        Assert.Null(stripped.Password);
        Assert.Null(stripped.Passphrase);
        Assert.Equal("jump.example.com", stripped.Host);
    }
}

public class ConnectionProfileSshTests
{
    private static readonly SshOptions Jump =
        new("jump.example.com", 22, "ops", SshAuthMode.Password, "pw");

    /// <summary>SSH 를 안 쓰던 호출부·저장 파일이 그대로 돌아야 한다 (필드는 맨 뒤 기본값).</summary>
    [Fact]
    public void Ssh_DefaultsToNull()
    {
        Assert.Null(ConnectionProfile.Default.Ssh);
        Assert.False(ConnectionProfile.Default.ViaTunnel);
        Assert.Null(ConnectionProfile.Default.SshLabel);
        Assert.Null(ConnectionProfile.ForFile("/tmp/a.db", DbKind.Sqlite).Ssh);
    }

    [Fact]
    public void ThroughTunnel_SwapsAddress_AndClearsSshToStopRecursion()
    {
        var profile = new ConnectionProfile("db.internal", 5432, "app", "u", "p", Ssh: Jump);
        var tunneled = profile.ThroughTunnel("127.0.0.1", 54321);

        Assert.Equal("127.0.0.1", tunneled.Host);
        Assert.Equal(54321, tunneled.Port);
        Assert.Null(tunneled.Ssh);
        Assert.True(tunneled.ViaTunnel);
        // 나머지는 그대로 — 사용자·DB·종류가 바뀌면 엉뚱한 데 붙는다
        Assert.Equal("app", tunneled.Database);
        Assert.Equal("u", tunneled.Username);
        Assert.Equal(DbKind.PostgreSql, tunneled.Kind);
    }

    [Fact]
    public void DisplayName_KeepsRealTarget_NotTheLocalForwardPort()
    {
        var profile = new ConnectionProfile("db.internal", 5432, "app", "u", "p", Ssh: Jump);
        // 상태바에 127.0.0.1:임의포트 가 뜨면 어디에 붙었는지 알 수 없게 된다
        Assert.Equal("u@db.internal:5432/app", profile.DisplayName);
        Assert.Equal("ssh ops@jump.example.com", profile.SshLabel);
    }

    /// <summary>터널 프로필은 접속 문자열도 로컬 끝점을 가리켜야 한다.</summary>
    [Fact]
    public void ToConnectionString_UsesLocalEndpointOnceTunneled()
    {
        var profile = new ConnectionProfile("db.internal", 5432, "app", "u", "p", Ssh: Jump);
        Assert.Contains("Host=db.internal", profile.ToConnectionString());
        Assert.Contains("Host=127.0.0.1", profile.ThroughTunnel("127.0.0.1", 54321).ToConnectionString());
        Assert.Contains("Port=54321", profile.ThroughTunnel("127.0.0.1", 54321).ToConnectionString());
    }

    /// <summary>
    /// SSH 설정이 없으면 풀은 아무 일도 하지 않아야 한다 — 직접 접속 경로에
    /// 비용이나 부작용이 생기면 안 된다.
    /// </summary>
    [Fact]
    public async Task Resolve_IsAPassThroughWithoutSsh()
    {
        var profile = new ConnectionProfile("db", 5432, "app", "u", "p");
        Assert.Same(profile, SshTunnelPool.Resolve(profile));
        Assert.Same(profile, await SshTunnelPool.ResolveAsync(profile));
        Assert.Equal(0, SshTunnelPool.ActiveTunnelCount);
    }

    [Fact]
    public async Task LeaseAsync_WithoutSsh_ReturnsProfileUnchangedAndNoOpLease()
    {
        var profile = new ConnectionProfile("db", 5432, "app", "u", "p");
        var (resolved, lease) = await SshTunnelPool.LeaseAsync(profile);
        Assert.Same(profile, resolved);
        lease.Dispose();
        lease.Dispose();   // 두 번 버려도 안전해야 한다 (QuerySession 이 실패 경로에서 또 버린다)
        Assert.Equal(0, SshTunnelPool.ActiveTunnelCount);
    }
}

public class SavedConnectionSshTests
{
    private static readonly SshOptions Jump =
        new("jump.example.com", 22, "ops", SshAuthMode.Password, "pw");

    private static SavedConnection Saved(SshOptions? ssh) =>
        new("localhost", 5432, "app", "u", "p", Kind: DbKind.PostgreSql, Ssh: ssh);

    private static ConnectionProfile Profile(SshOptions? ssh) =>
        new("localhost", 5432, "app", "u", "p", Ssh: ssh);

    /// <summary>
    /// 같은 <c>localhost:5432/app</c> 라도 거치는 서버가 다르면 다른 DB 다 —
    /// 이걸 같은 항목으로 보면 로그인 목록에서 서로를 덮어쓴다.
    /// </summary>
    [Fact]
    public void SameTarget_DistinguishesJumpHosts()
    {
        Assert.True(Saved(Jump).SameTarget(Profile(Jump)));
        Assert.True(Saved(null).SameTarget(Profile(null)));

        Assert.False(Saved(Jump).SameTarget(Profile(null)));
        Assert.False(Saved(null).SameTarget(Profile(Jump)));
        Assert.False(Saved(Jump).SameTarget(Profile(Jump with { Host = "other.example.com" })));
        Assert.False(Saved(Jump).SameTarget(Profile(Jump with { Username = "root" })));
        Assert.False(Saved(Jump).SameTarget(Profile(Jump with { Port = 2222 })));
    }

    /// <summary>비밀번호를 바꿨다고 로그인 항목이 둘로 갈라지면 안 된다.</summary>
    [Fact]
    public void SameTarget_IgnoresSshSecrets()
    {
        Assert.True(Saved(Jump).SameTarget(Profile(Jump with { Password = "rotated" })));
        Assert.True(Saved(Jump).SameTarget(
            Profile(Jump with { AuthMode = SshAuthMode.PrivateKey, PrivateKeyPath = "/k" })));
    }

    /// <summary>DisplayName 은 UpdateMeta·Remove 의 신원 키라 터널까지 구분해야 한다.</summary>
    [Fact]
    public void DisplayName_IsUniquePerJumpHost()
    {
        Assert.Equal("u@localhost:5432/app", Saved(null).DisplayName);
        Assert.Equal("u@localhost:5432/app (ssh ops@jump.example.com)", Saved(Jump).DisplayName);
        Assert.NotEqual(Saved(null).DisplayName, Saved(Jump).DisplayName);
    }

    /// <summary>Database 칸 표기는 건드리면 안 된다 — 로그온 창이 이 문자열을 되파싱한다.</summary>
    [Fact]
    public void DisplayDatabase_IsUnaffectedByTunnel()
    {
        Assert.Equal(Saved(null).DisplayDatabase, Saved(Jump).DisplayDatabase);
        Assert.Equal("localhost/app", Saved(Jump).DisplayDatabase);
    }

    [Fact]
    public void SshMarkers_TrackTheOption()
    {
        Assert.False(Saved(null).HasSsh);
        Assert.Null(Saved(null).SshLabel);
        Assert.True(Saved(Jump).HasSsh);
        Assert.Equal("SSH 터널: ops@jump.example.com", Saved(Jump).SshLabel);
    }
}

public class MongoTunnelConnectionStringTests
{
    /// <summary>
    /// 터널을 거칠 때 <c>directConnection</c> 이 없으면, 드라이버가 replica set 토폴로지를
    /// 탐색해 <b>서버가 알려준 실제 호스트</b>로 다시 붙는다 — 그 주소는 터널 밖이다.
    /// </summary>
    [Fact]
    public void DirectConnection_IsAddedOnlyForTunneledProfiles()
    {
        var direct = new ConnectionProfile("mongo.internal", 27017, "app", "", "", Kind: DbKind.MongoDb);
        Assert.Equal("mongodb://mongo.internal:27017", MongoSession.BuildConnectionString(direct));

        var tunneled = direct.ThroughTunnel("127.0.0.1", 55001);
        Assert.Equal("mongodb://127.0.0.1:55001/?directConnection=true",
            MongoSession.BuildConnectionString(tunneled));
    }

    [Fact]
    public void DirectConnection_KeepsCredentialEscaping()
    {
        var profile = new ConnectionProfile("mongo.internal", 27017, "app", "us er", "p@ss", Kind: DbKind.MongoDb)
            .ThroughTunnel("127.0.0.1", 55001);
        Assert.Equal("mongodb://us%20er:p%40ss@127.0.0.1:55001/?directConnection=true",
            MongoSession.BuildConnectionString(profile));
    }
}

/// <summary>
/// known_hosts 대조. 파일을 실제로 만들어 검사하되, <see cref="KnownHosts.HomeOverride"/> 로
/// 임시 폴더를 보게 해 <b>사용자의 ~/.ssh 와 홈 디렉터리는 건드리지 않는다</b>.
/// </summary>
public class KnownHostsTests : IDisposable
{
    private readonly string _home;

    /// <summary>ed25519 키 blob 흉내 — 형식만 맞으면 대조 로직을 검사하기에 충분하다.</summary>
    private const string KeyA = "AAAAC3NzaC1lZDI1NTE5AAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string KeyB = "AAAAC3NzaC1lZDI1NTE5AAAAIBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string Type = "ssh-ed25519";

    public KnownHostsTests()
    {
        _home = Path.Combine(Path.GetTempPath(), $"aurum-kh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_home);
        KnownHosts.HomeOverride = _home;
    }

    public void Dispose()
    {
        KnownHosts.HomeOverride = null;
        try { Directory.Delete(_home, recursive: true); } catch { /* 임시 폴더 */ }
        GC.SuppressFinalize(this);
    }

    private static HostKeyInfo Key(string host = "jump.example.com", int port = 22, string key = KeyA) =>
        new(host, port, Type, key);

    private void WriteOurs(params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KnownHosts.OurPath)!);
        File.WriteAllLines(KnownHosts.OurPath, lines);
    }

    private void WriteOpenSsh(params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KnownHosts.OpenSshPath)!);
        File.WriteAllLines(KnownHosts.OpenSshPath, lines);
    }

    /// <summary>아무것도 모르면 Unknown — 조용히 신뢰하지 않는다.</summary>
    [Fact]
    public void EmptyStore_IsUnknown() =>
        Assert.Equal(HostKeyTrust.Unknown, KnownHosts.Verify(Key()).Trust);

    [Fact]
    public void MatchingEntry_IsTrusted()
    {
        WriteOurs($"jump.example.com {Type} {KeyA}");
        Assert.Equal(HostKeyTrust.Trusted, KnownHosts.Verify(Key()).Trust);
    }

    /// <summary>터미널에서 이미 붙어 본 사람은 아무것도 안 물어봐야 한다.</summary>
    [Fact]
    public void OpenSshFile_IsAlsoTrusted()
    {
        WriteOpenSsh($"jump.example.com {Type} {KeyA}");
        Assert.Equal(HostKeyTrust.Trusted, KnownHosts.Verify(Key()).Trust);
    }

    /// <summary>키가 다르면 불일치 — 그리고 알던 지문을 같이 돌려줘야 경고에 나란히 보인다.</summary>
    [Fact]
    public void DifferentKeyForKnownHost_IsMismatch()
    {
        WriteOurs($"jump.example.com {Type} {KeyA}");
        var verdict = KnownHosts.Verify(Key(key: KeyB));
        Assert.Equal(HostKeyTrust.Mismatch, verdict.Trust);
        Assert.Equal(new[] { HostKeyInfo.FingerprintOf(KeyA) }, verdict.KnownFingerprints);
    }

    /// <summary>@revoked 는 다른 줄이 허용해도 이긴다.</summary>
    [Fact]
    public void RevokedKey_IsRejectedEvenIfAlsoListed()
    {
        WriteOurs(
            $"jump.example.com {Type} {KeyA}",
            $"@revoked jump.example.com {Type} {KeyA}");
        Assert.Equal(HostKeyTrust.Revoked, KnownHosts.Verify(Key()).Trust);
    }

    /// <summary>기본 포트가 아니면 OpenSSH 는 [host]:port 로 적는다.</summary>
    [Fact]
    public void NonDefaultPort_MatchesBracketedForm()
    {
        WriteOurs($"[jump.example.com]:2222 {Type} {KeyA}");
        Assert.Equal(HostKeyTrust.Trusted, KnownHosts.Verify(Key(port: 2222)).Trust);
        // 22번으로 붙을 때는 그 항목이 잡히면 안 된다
        Assert.Equal(HostKeyTrust.Unknown, KnownHosts.Verify(Key(port: 22)).Trust);
    }

    /// <summary>ssh-keyscan -H · HashKnownHosts yes 로 만든 해시 항목도 대조해야 한다.</summary>
    [Fact]
    public void HashedEntry_Matches()
    {
        var salt = new byte[20];
        for (var i = 0; i < salt.Length; i++) salt[i] = (byte)i;
        var hash = System.Security.Cryptography.HMACSHA1.HashData(
            salt, System.Text.Encoding.UTF8.GetBytes("jump.example.com"));
        var pattern = $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)}";

        WriteOurs($"{pattern} {Type} {KeyA}");
        Assert.Equal(HostKeyTrust.Trusted, KnownHosts.Verify(Key()).Trust);
        Assert.Equal(HostKeyTrust.Unknown, KnownHosts.Verify(Key(host: "other.example.com")).Trust);
    }

    [Fact]
    public void Wildcards_AndComments_AreHandled()
    {
        WriteOurs(
            "# 주석은 건너뛴다",
            "",
            $"*.example.com {Type} {KeyA}");
        Assert.Equal(HostKeyTrust.Trusted, KnownHosts.Verify(Key()).Trust);
        Assert.Equal(HostKeyTrust.Unknown, KnownHosts.Verify(Key(host: "jump.other.net")).Trust);
    }

    /// <summary>승인하면 다음부터는 안 물어야 한다. 기록은 우리 파일에만 남는다.</summary>
    [Fact]
    public void Trust_PersistsToOurFileOnly()
    {
        WriteOpenSsh($"unrelated.example.com {Type} {KeyB}");
        var before = File.ReadAllText(KnownHosts.OpenSshPath);

        KnownHosts.Trust(Key());

        Assert.Equal(HostKeyTrust.Trusted, KnownHosts.Verify(Key()).Trust);
        Assert.Equal(before, File.ReadAllText(KnownHosts.OpenSshPath));   // 남의 파일은 안 건드린다
    }

    /// <summary>불일치를 받아들이면 우리 파일의 옛 항목이 새 키로 갈린다.</summary>
    [Fact]
    public void Replace_SwapsTheOldKey()
    {
        WriteOurs($"jump.example.com {Type} {KeyA}");
        KnownHosts.Replace(Key(key: KeyB));

        Assert.Equal(HostKeyTrust.Trusted, KnownHosts.Verify(Key(key: KeyB)).Trust);
        Assert.Equal(HostKeyTrust.Mismatch, KnownHosts.Verify(Key(key: KeyA)).Trust);
    }

    [Fact]
    public void Fingerprint_MatchesSshCommandFormat()
    {
        var fingerprint = HostKeyInfo.FingerprintOf(KeyA);
        Assert.StartsWith("SHA256:", fingerprint);
        Assert.DoesNotContain("=", fingerprint);            // ssh 와 같이 패딩을 뗀다
        Assert.Equal(fingerprint, Key().Fingerprint);
    }
}

public class SshSavePasswordTests
{
    private static SshOptions Jump(bool save) =>
        new("jump.example.com", 22, "ops", SshAuthMode.Password, "pw", SavePassword: save);

    [Fact]
    public void SavePassword_DefaultsToOn() =>
        Assert.True(new SshOptions("h", 22, "u").SavePassword);

    /// <summary>저장을 껐으면 접속할 때 비어 있으므로 물어봐야 한다.</summary>
    [Fact]
    public void NeedsPassword_OnlyForPasswordAuthWithNoSecret()
    {
        Assert.False(Jump(save: true).NeedsPassword);
        Assert.True((Jump(save: true) with { Password = "" }).NeedsPassword);
        Assert.True((Jump(save: true) with { Password = null }).NeedsPassword);

        // 개인키는 passphrase 없는 키가 정상이라 "부족" 이 아니다
        var key = new SshOptions("h", 22, "u", SshAuthMode.PrivateKey, PrivateKeyPath: "/k");
        Assert.False(key.NeedsPassword);
    }

    /// <summary>저장을 끈 항목은 암호문으로도 디스크에 남으면 안 된다.</summary>
    [Fact]
    public void WithoutSecrets_ClearsBothSecretsButKeepsTheTarget()
    {
        var stripped = (Jump(save: false) with { Passphrase = "phrase" }).WithoutSecrets();
        Assert.Null(stripped.Password);
        Assert.Null(stripped.Passphrase);
        Assert.Equal("ops@jump.example.com", stripped.Describe);
        Assert.False(stripped.SavePassword);
    }
}
