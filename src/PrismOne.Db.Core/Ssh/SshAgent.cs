using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace PrismOne.Db.Core.Ssh;

/// <summary>
/// ssh-agent 클라이언트 (OpenSSH agent 프로토콜, RFC draft-miller-ssh-agent).
///
/// SSH.NET 에는 agent 지원이 없다. 대신 <see cref="HostAlgorithm"/> 이 딱 맞는 훅이다 —
/// 공개키 blob(<see cref="HostAlgorithm.Data"/>)과 서명(<see cref="HostAlgorithm.Sign"/>)만
/// 내주면 되고, agent 는 바로 그 둘을 해준다. **개인키는 우리 프로세스에 들어오지 않는다** —
/// 그게 agent 를 쓰는 이유다.
///
/// 전송 경로:
/// <list type="bullet">
/// <item>Linux·macOS — <c>$SSH_AUTH_SOCK</c> 유닉스 소켓.</item>
/// <item>Windows — OpenSSH agent 의 명명 파이프(<c>\\.\pipe\openssh-ssh-agent</c>).
///       <c>SSH_AUTH_SOCK</c> 이 파이프 경로를 가리키면 그쪽을 쓴다.</item>
/// </list>
/// PuTTY Pageant 의 WM_COPYDATA 공유메모리 방식은 <b>구현하지 않았다</b> —
/// Pageant 를 OpenSSH 명명 파이프로 노출하도록 켜면(PuTTY 0.77+) 그대로 붙는다.
/// </summary>
public static class SshAgent
{
    private const byte RequestIdentities = 11;
    private const byte IdentitiesAnswer = 12;
    private const byte SignRequest = 13;
    private const byte SignResponse = 14;

    /// <summary>RSA 키에 rsa-sha2-* 서명을 요청하는 플래그 (구식 SHA-1 서명을 피한다).</summary>
    private const uint FlagRsaSha2_256 = 2;
    private const uint FlagRsaSha2_512 = 4;

    /// <summary>agent 응답 상한 — 망가진 소켓이 우리 메모리를 먹지 않게.</summary>
    private const int MaxMessage = 256 * 1024;

    private const string WindowsPipePrefix = @"\\.\pipe\";
    private const string DefaultWindowsPipe = "openssh-ssh-agent";

    /// <summary>agent 를 쓸 수 있는지 (설정 창의 안내 문구용).</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                using var stream = Open();
                return stream is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>agent 에 물어 볼 수 없을 때 사용자에게 보일 이유.</summary>
    public static string UnavailableReason => OperatingSystem.IsWindows()
        ? @"ssh-agent 에 연결할 수 없습니다. OpenSSH 인증 에이전트 서비스가 실행 중인지 "
          + @"확인하세요 (services.msc 의 'OpenSSH Authentication Agent'). "
          + "Pageant 는 OpenSSH 명명 파이프 노출을 켠 경우에만 인식합니다."
        : "ssh-agent 에 연결할 수 없습니다. SSH_AUTH_SOCK 이 설정되어 있는지 확인하세요 "
          + "(ssh-add -l 로 확인).";

    /// <summary>
    /// agent 가 들고 있는 신원들을 SSH.NET 이 쓸 수 있는 형태로 돌려준다.
    /// 하나도 없으면 빈 목록 — 호출부가 "키를 안 넣었다" 고 알려야 한다.
    /// </summary>
    public static IReadOnlyList<HostAlgorithm> LoadIdentities()
    {
        var result = new List<HostAlgorithm>();
        foreach (var (blob, comment) in ListKeys())
        {
            var keyType = ReadKeyType(blob);
            if (keyType is null) continue;   // 우리가 못 읽는 blob 은 건너뛴다

            if (keyType == "ssh-rsa")
            {
                // 요즘 서버는 SHA-1 서명(ssh-rsa)을 거부한다. 선호도 순으로 셋 다 올린다 —
                // 서버가 받아주는 것으로 붙는다.
                result.Add(new AgentHostAlgorithm("rsa-sha2-512", blob, comment, FlagRsaSha2_512));
                result.Add(new AgentHostAlgorithm("rsa-sha2-256", blob, comment, FlagRsaSha2_256));
                result.Add(new AgentHostAlgorithm("ssh-rsa", blob, comment, 0));
            }
            else
            {
                result.Add(new AgentHostAlgorithm(keyType, blob, comment, 0));
            }
        }
        return result;
    }

    /// <summary>agent 가 든 키들 — (공개키 blob, 주석).</summary>
    private static List<(byte[] Blob, string Comment)> ListKeys()
    {
        using var stream = Open()
            ?? throw new SshTunnelException(UnavailableReason);

        var response = Query(stream, [RequestIdentities]);
        var reader = new Reader(response);
        if (reader.ReadByte() != IdentitiesAnswer)
            throw new SshTunnelException("ssh-agent 가 키 목록 요청에 응답하지 않았습니다.");

        var count = reader.ReadUInt32();
        if (count > 1024) throw new SshTunnelException("ssh-agent 응답이 올바르지 않습니다.");

        var keys = new List<(byte[], string)>((int)count);
        for (var i = 0; i < count; i++)
        {
            var blob = reader.ReadBytes();
            var comment = Encoding.UTF8.GetString(reader.ReadBytes());
            keys.Add((blob, comment));
        }
        return keys;
    }

    /// <summary>agent 에 서명을 시킨다. 돌아오는 값은 SSH 규격대로 인코딩된 서명이다.</summary>
    internal static byte[] Sign(byte[] keyBlob, byte[] data, uint flags)
    {
        using var stream = Open()
            ?? throw new SshTunnelException(UnavailableReason);

        var writer = new Writer();
        writer.WriteByte(SignRequest);
        writer.WriteBytes(keyBlob);
        writer.WriteBytes(data);
        writer.WriteUInt32(flags);

        var reader = new Reader(Query(stream, writer.ToArray()));
        if (reader.ReadByte() != SignResponse)
            throw new SshTunnelException(
                "ssh-agent 가 서명을 거부했습니다. 키가 agent 에 들어 있는지 확인하세요 (ssh-add -l).");
        return reader.ReadBytes();
    }

    /// <summary>공개키 blob 의 첫 SSH 문자열이 키 종류다 (ssh-ed25519 등).</summary>
    private static string? ReadKeyType(byte[] blob)
    {
        try
        {
            return Encoding.UTF8.GetString(new Reader(blob).ReadBytes());
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------- 전송 ----------

    private static Stream? Open()
    {
        var authSock = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");

        if (OperatingSystem.IsWindows())
        {
            var pipe = authSock is { Length: > 0 } && authSock.StartsWith(WindowsPipePrefix, StringComparison.OrdinalIgnoreCase)
                ? authSock[WindowsPipePrefix.Length..]
                : DefaultWindowsPipe;
            var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut);
            try
            {
                stream.Connect(2000);
                return stream;
            }
            catch
            {
                stream.Dispose();
                return null;
            }
        }

        if (string.IsNullOrEmpty(authSock)) return null;
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(authSock));
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            return null;
        }
    }

    /// <summary>길이(4바이트 빅엔디언) + 본문 한 번 주고받기.</summary>
    private static byte[] Query(Stream stream, byte[] payload)
    {
        var framed = new byte[4 + payload.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)payload.Length);
        payload.CopyTo(framed, 4);
        stream.Write(framed, 0, framed.Length);
        stream.Flush();

        var header = ReadExactly(stream, 4);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 or > MaxMessage)
            throw new SshTunnelException("ssh-agent 응답 길이가 올바르지 않습니다.");
        return ReadExactly(stream, (int)length);
    }

    private static byte[] ReadExactly(Stream stream, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n <= 0) throw new SshTunnelException("ssh-agent 연결이 끊겼습니다.");
            read += n;
        }
        return buffer;
    }

    // ---------- SSH 와이어 형식 ----------

    /// <summary>SSH 문자열(4바이트 길이 + 본문)을 읽는다.</summary>
    private sealed class Reader(byte[] data)
    {
        private int _at;

        public byte ReadByte()
        {
            Require(1);
            return data[_at++];
        }

        public uint ReadUInt32()
        {
            Require(4);
            var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(_at, 4));
            _at += 4;
            return value;
        }

        public byte[] ReadBytes()
        {
            var length = ReadUInt32();
            if (length > MaxMessage) throw new SshTunnelException("ssh-agent 응답이 올바르지 않습니다.");
            Require((int)length);
            var slice = data.AsSpan(_at, (int)length).ToArray();
            _at += (int)length;
            return slice;
        }

        private void Require(int count)
        {
            if (_at + count > data.Length)
                throw new SshTunnelException("ssh-agent 응답이 잘렸습니다.");
        }
    }

    private sealed class Writer
    {
        private readonly MemoryStream _buffer = new();

        public void WriteByte(byte value) => _buffer.WriteByte(value);

        public void WriteUInt32(uint value)
        {
            Span<byte> span = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(span, value);
            _buffer.Write(span);
        }

        public void WriteBytes(byte[] value)
        {
            WriteUInt32((uint)value.Length);
            _buffer.Write(value, 0, value.Length);
        }

        public byte[] ToArray() => _buffer.ToArray();
    }
}

/// <summary>
/// agent 가 들고 있는 신원 하나를 SSH.NET 의 공개키 인증에 끼우는 어댑터.
/// 서명은 agent 가 하고 우리는 blob 만 나른다 — 개인키는 이 프로세스에 없다.
/// </summary>
internal sealed class AgentHostAlgorithm(string name, byte[] keyBlob, string comment, uint signFlags)
    : HostAlgorithm(name)
{
    /// <summary>Login 실패 시 어느 키였는지 알려주기 위한 표시용 (ssh-add -l 의 주석).</summary>
    public string Comment { get; } = comment;

    public override byte[] Data => keyBlob;

    /// <summary>agent 는 SSH 규격대로 인코딩된 서명을 돌려주므로 그대로 넘긴다.</summary>
    public override byte[] Sign(byte[] data) => SshAgent.Sign(keyBlob, data, signFlags);

    /// <summary>클라이언트 인증에는 쓰이지 않는다 (서버 키 검증용 경로).</summary>
    public override bool VerifySignature(byte[] data, byte[] signature) =>
        throw new NotSupportedException("ssh-agent 신원으로는 서명을 검증하지 않습니다.");
}

/// <summary>agent 의 신원들을 <see cref="PrivateKeyAuthenticationMethod"/> 에 넘기는 통로.</summary>
internal sealed class AgentKeySource(IReadOnlyCollection<HostAlgorithm> algorithms) : IPrivateKeySource
{
    public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms => algorithms;
}
