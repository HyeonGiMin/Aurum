using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrismOne.Db.Core.Mcp;

/// <summary>
/// MCP 서버 설정 (~/.prismone-studio/mcp.json). Options 다이얼로그는 저장할 때 AppOptions 를
/// 새로 만들어 모르는 필드를 버리므로 따로 둔다. 토큰은 접속 비밀번호와 같은 방식으로 암호화한다.
/// </summary>
public sealed record McpSettings
{
    public const int DefaultPort = 47900;

    /// <summary>AI 에게 한 번에 넘기는 결과 행 상한 (도구 인자로 더 달라고 해도 이 이상은 안 준다).</summary>
    public const int DefaultMaxRows = 200;

    /// <summary>켜 두면 앱 시작 때 서버를 연다. 기본은 끔 — 사용자가 명시적으로 켜야 한다.</summary>
    public bool Enabled { get; init; }

    public int Port { get; init; } = DefaultPort;

    public int MaxRows { get; init; } = DefaultMaxRows;

    /// <summary>PasswordCipher 로 암호화한 Bearer 토큰.</summary>
    public string? ProtectedToken { get; init; }

    [JsonIgnore]
    public string? Token => PasswordCipher.Unprotect(ProtectedToken);

    public string Url => $"http://localhost:{Port}/mcp";

    /// <summary>새 무작위 토큰 (256bit, base64url).</summary>
    public McpSettings WithNewToken()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return this with { ProtectedToken = PasswordCipher.Protect(token) };
    }

    /// <summary>토큰이 없으면 만든다.</summary>
    public McpSettings EnsureToken() => string.IsNullOrEmpty(Token) ? WithNewToken() : this;

    /// <summary>
    /// Claude Code 에 등록하는 명령 (사용자가 복사해 터미널에 붙인다).
    /// <c>--scope user</c> — 명령을 실행한 폴더가 아니라 어느 폴더에서 연 Claude Code 에서도 보이게.
    /// </summary>
    public string ClaudeAddCommand(string token) =>
        $"claude mcp add --transport http --scope user aurum {Url} --header \"Authorization: Bearer {token}\"";

    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".prismone-studio", "mcp.json");

    public static McpSettings Load(string? path = null)
    {
        var file = path ?? DefaultPath;
        try
        {
            return File.Exists(file)
                ? JsonSerializer.Deserialize<McpSettings>(File.ReadAllText(file)) ?? new McpSettings()
                : new McpSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new McpSettings();
        }
    }

    public void Save(string? path = null)
    {
        var file = path ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
