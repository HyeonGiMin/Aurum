using PrismOne.Db.Core.Ssh;
using Xunit;

namespace PrismOne.Db.Core.Tests;

/// <summary>
/// <c>~/.ssh/config</c> 읽기. <see cref="SshConfig.HomeOverride"/> 로 임시 폴더를 보게 해
/// 사용자의 진짜 설정은 건드리지 않는다.
/// </summary>
public class SshConfigTests : IDisposable
{
    private readonly string _home;

    public SshConfigTests()
    {
        _home = Path.Combine(Path.GetTempPath(), $"aurum-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_home, ".ssh"));
        SshConfig.HomeOverride = _home;
    }

    public void Dispose()
    {
        SshConfig.HomeOverride = null;
        try { Directory.Delete(_home, recursive: true); } catch { /* 임시 폴더 */ }
        GC.SuppressFinalize(this);
    }

    private void Write(string text) => File.WriteAllText(SshConfig.FilePath, text);

    private void WriteInclude(string name, string text) =>
        File.WriteAllText(Path.Combine(_home, ".ssh", name), text);

    [Fact]
    public void MissingFile_ResolvesToTheAliasItself()
    {
        var resolved = SshConfig.Resolve("db.internal");
        Assert.Equal("db.internal", resolved.HostName);
        Assert.Null(resolved.User);
        Assert.Null(resolved.Port);
        Assert.False(resolved.HasSettings);
    }

    [Fact]
    public void ResolvesHostNameUserPortAndKey()
    {
        Write("""
            Host prod-db
                HostName 10.0.0.9
                User deploy
                Port 2222
                IdentityFile ~/.ssh/id_prod
            """);

        var resolved = SshConfig.Resolve("prod-db");
        Assert.Equal("10.0.0.9", resolved.HostName);
        Assert.Equal("deploy", resolved.User);
        Assert.Equal(2222, resolved.Port);
        Assert.Equal(Path.Combine(_home, ".ssh", "id_prod"), Assert.Single(resolved.IdentityFiles));
        Assert.True(resolved.HasSettings);
    }

    /// <summary>OpenSSH 규칙: 먼저 나온 값이 이긴다. 그래서 Host * 를 맨 아래 두는 관례가 통한다.</summary>
    [Fact]
    public void FirstMatchWins_SoWildcardBlocksBelongAtTheBottom()
    {
        Write("""
            Host prod-db
                User deploy

            Host *
                User fallback
                Port 2200
            """);

        var resolved = SshConfig.Resolve("prod-db");
        Assert.Equal("deploy", resolved.User);   // 앞 블록이 이긴다
        Assert.Equal(2200, resolved.Port);       // 앞에서 안 정한 것은 뒤에서 채운다
    }

    [Fact]
    public void WildcardAndNegationPatterns()
    {
        Write("""
            Host *.internal !secret.internal
                User ops
            """);

        Assert.Equal("ops", SshConfig.Resolve("db.internal").User);
        Assert.Null(SshConfig.Resolve("secret.internal").User);
        Assert.Null(SshConfig.Resolve("db.example.com").User);
    }

    [Fact]
    public void ReadsProxyJump_AndTreatsNoneAsAbsent()
    {
        Write("""
            Host prod-db
                ProxyJump ops@bastion.example.com:2222

            Host direct-db
                ProxyJump none
            """);

        Assert.Equal("ops@bastion.example.com:2222", SshConfig.Resolve("prod-db").ProxyJump);
        Assert.Null(SshConfig.Resolve("direct-db").ProxyJump);
    }

    [Fact]
    public void AcceptsEqualsSyntaxAndComments()
    {
        Write("""
            # 주석
            Host=prod-db
                HostName=10.0.0.9
                User = deploy
            """);

        var resolved = SshConfig.Resolve("prod-db");
        Assert.Equal("10.0.0.9", resolved.HostName);
        Assert.Equal("deploy", resolved.User);
    }

    [Fact]
    public void FollowsIncludeDirectives()
    {
        WriteInclude("extra.conf", """
            Host prod-db
                HostName 10.0.0.9
                User deploy
            """);
        Write("Include extra.conf\n");

        var resolved = SshConfig.Resolve("prod-db");
        Assert.Equal("10.0.0.9", resolved.HostName);
        Assert.Equal("deploy", resolved.User);
    }

    /// <summary>Match 는 조건을 우리가 판정할 수 없다 — 뒤따르는 설정이 새면 안 된다.</summary>
    [Fact]
    public void MatchBlocks_AreIgnoredEntirely()
    {
        Write("""
            Match host prod-db
                User wrong

            Host prod-db
                HostName 10.0.0.9
            """);

        var resolved = SshConfig.Resolve("prod-db");
        Assert.Equal("10.0.0.9", resolved.HostName);
        Assert.Null(resolved.User);   // Match 블록의 값이 새 나오면 안 된다
    }

    /// <summary>Host 블록 앞에 적은 설정은 모든 호스트에 걸린다 (OpenSSH 와 같다).</summary>
    [Fact]
    public void PreambleAppliesToEveryHost()
    {
        Write("""
            User global

            Host prod-db
                HostName 10.0.0.9
            """);

        Assert.Equal("global", SshConfig.Resolve("prod-db").User);
        Assert.Equal("global", SshConfig.Resolve("anything-else").User);
    }

    [Fact]
    public void Aliases_ListsConcreteHostsOnly()
    {
        Write("""
            Host prod-db staging-db
                User ops

            Host *
                Port 22
            """);

        Assert.Equal(new[] { "prod-db", "staging-db" }, SshConfig.Aliases());
    }
}

/// <summary>ProxyJump 를 실제로 거쳐 갈 홉 목록으로 펴는 부분.</summary>
public class SshHopsTests : IDisposable
{
    private readonly string _home;

    public SshHopsTests()
    {
        _home = Path.Combine(Path.GetTempPath(), $"aurum-hop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_home, ".ssh"));
        SshConfig.HomeOverride = _home;
    }

    public void Dispose()
    {
        SshConfig.HomeOverride = null;
        try { Directory.Delete(_home, recursive: true); } catch { /* 임시 폴더 */ }
        GC.SuppressFinalize(this);
    }

    private static SshOptions Base => new("db.internal", 22, "ops", SshAuthMode.Password, "pw");

    [Fact]
    public void NoProxyJump_IsASingleHop()
    {
        var hops = SshHops.Expand(Base);
        Assert.Equal("db.internal", Assert.Single(hops).Host);
    }

    /// <summary>순서가 중요하다 — 먼저 붙는 것부터여야 사슬을 그대로 이을 수 있다.</summary>
    [Fact]
    public void ProxyJump_PutsJumpHostsFirst()
    {
        var hops = SshHops.Expand(Base with { ProxyJump = "jump1.example.com, ops2@jump2.example.com:2222" });

        Assert.Equal(3, hops.Count);
        Assert.Equal("jump1.example.com", hops[0].Host);
        Assert.Equal(22, hops[0].Port);
        Assert.Equal("ops", hops[0].Username);              // 인증은 물려받는다

        Assert.Equal("jump2.example.com", hops[1].Host);
        Assert.Equal(2222, hops[1].Port);
        Assert.Equal("ops2", hops[1].Username);             // 적어 준 사용자 이름이 이긴다

        Assert.Equal("db.internal", hops[^1].Host);
        Assert.Null(hops[^1].ProxyJump);                    // 이미 펴 놓았으므로 비어야 한다
    }

    [Fact]
    public void JumpHosts_InheritAuthFromThePrimary()
    {
        var hops = SshHops.Expand(
            Base with { AuthMode = SshAuthMode.Agent, Password = null, ProxyJump = "bastion" });

        Assert.All(hops, h => Assert.Equal(SshAuthMode.Agent, h.AuthMode));
    }

    /// <summary>OpenSSH config 모드면 별칭이 실제 주소·사용자·포트로 풀려야 한다.</summary>
    [Fact]
    public void OpenSshConfigMode_ResolvesTheAlias()
    {
        File.WriteAllText(SshConfig.FilePath, """
            Host prod-db
                HostName 10.0.0.9
                User deploy
                Port 2222
            """);

        var hops = SshHops.Expand(new SshOptions("prod-db", 22, "", SshAuthMode.OpenSshConfig));
        var hop = Assert.Single(hops);
        Assert.Equal("10.0.0.9", hop.Host);
        Assert.Equal("deploy", hop.Username);
        Assert.Equal(2222, hop.Port);
    }

    /// <summary>설정의 ProxyJump 도 홉으로 펴져야 한다 (DataGrip 이 못 하는 부분).</summary>
    [Fact]
    public void OpenSshConfigMode_ExpandsProxyJumpFromTheFile()
    {
        File.WriteAllText(SshConfig.FilePath, """
            Host prod-db
                HostName 10.0.0.9
                User deploy
                ProxyJump bastion

            Host bastion
                HostName bastion.example.com
                User jumpuser
                Port 2200
            """);

        var hops = SshHops.Expand(new SshOptions("prod-db", 22, "", SshAuthMode.OpenSshConfig));

        Assert.Equal(2, hops.Count);
        Assert.Equal("bastion.example.com", hops[0].Host);
        Assert.Equal("jumpuser", hops[0].Username);
        Assert.Equal(2200, hops[0].Port);
        Assert.Equal("10.0.0.9", hops[1].Host);
        Assert.Equal("deploy", hops[1].Username);
    }

    /// <summary>ProxyJump 가 서로를 부르면 무한히 돌 수 있다 — 상한에서 끊어야 한다.</summary>
    [Fact]
    public void CyclicProxyJump_StopsWithAClearError()
    {
        File.WriteAllText(SshConfig.FilePath, """
            Host a
                ProxyJump b

            Host b
                ProxyJump a
            """);

        var ex = Assert.Throws<SshTunnelException>(
            () => SshHops.Expand(new SshOptions("a", 22, "u", SshAuthMode.OpenSshConfig)));
        Assert.Contains("ProxyJump", ex.Message);
    }

    [Fact]
    public void Describe_ShowsTheJumpPath()
    {
        Assert.Equal("ops@db.internal", Base.Describe);
        Assert.Equal("ops@db.internal via bastion",
            (Base with { ProxyJump = "bastion" }).Describe);
        // OpenSSH config 모드는 사용자 이름이 비어 있을 수 있다
        Assert.Equal("prod-db", new SshOptions("prod-db", 22, "", SshAuthMode.OpenSshConfig).Describe);
    }

    [Fact]
    public void AuthLabel_NamesEachMode()
    {
        Assert.Equal("비밀번호", Base.AuthLabel);
        Assert.Equal("개인키", (Base with { AuthMode = SshAuthMode.PrivateKey }).AuthLabel);
        Assert.Equal("ssh-agent", (Base with { AuthMode = SshAuthMode.Agent }).AuthLabel);
        Assert.Equal("OpenSSH config", (Base with { AuthMode = SshAuthMode.OpenSshConfig }).AuthLabel);
    }

    /// <summary>agent·config 방식은 저장할 비밀이 없다 — 설정 창이 체크를 숨기는 근거.</summary>
    [Fact]
    public void UsesStoredSecret_OnlyForPasswordAndKey()
    {
        Assert.True(Base.UsesStoredSecret);
        Assert.True((Base with { AuthMode = SshAuthMode.PrivateKey }).UsesStoredSecret);
        Assert.False((Base with { AuthMode = SshAuthMode.Agent }).UsesStoredSecret);
        Assert.False((Base with { AuthMode = SshAuthMode.OpenSshConfig }).UsesStoredSecret);
    }
}

/// <summary>
/// 키 에이전트 어댑터. agent 가 떠 있는지는 환경마다 달라서(CI 에는 없다) 동작 자체는
/// 고정할 수 없다 — 대신 <b>어느 쪽으로 끝나든 호출부가 감당할 수 있는 형태</b>인지를 본다.
/// </summary>
public class AgentKeysTests
{
    /// <summary>
    /// 실패하더라도 소켓·파이프 예외가 그대로 새어 나오면 안 된다 —
    /// 호출부는 SshTunnelException 만 잡아 사용자에게 보여준다.
    /// </summary>
    [Fact]
    public void Load_EitherReturnsIdentitiesOrThrowsATunnelException()
    {
        try
        {
            var identities = AgentKeys.Load();
            // agent 가 있는 개발 장비 — 키가 있어야만 성공으로 돌아온다.
            Assert.NotEmpty(identities.Keys);
            Assert.False(string.IsNullOrWhiteSpace(identities.Transport));
        }
        catch (SshTunnelException ex)
        {
            // agent 가 없는 환경(CI) — 다음에 뭘 하라는 안내가 들어 있어야 한다.
            Assert.Contains("ssh-add", ex.Message);
        }
    }

    /// <summary>TryLoad 는 던지지 않는다 (설정 창이 매 입력마다 부른다).</summary>
    [Fact]
    public void TryLoad_NeverThrows()
    {
        var identities = AgentKeys.TryLoad();
        if (identities is not null) Assert.NotEmpty(identities.Keys);
    }

    [Fact]
    public void Describe_NamesTheTransportAndCount() =>
        Assert.Equal("ssh-agent — 키 0개", new AgentIdentities([], "ssh-agent").Describe);
}
