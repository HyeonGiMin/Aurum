using System.Text.Encodings.Web;
using System.Text.Json;

namespace PrismOne.Db.Core.Mcp;

/// <summary>
/// MCP 응답 직렬화 옵션. 기본 인코더는 한글·따옴표를 \uXXXX 로 바꾸는데, 도구 결과는
/// JSON 안의 JSON 문자열이라 AI 가 그 이스케이프를 글자 그대로 읽게 된다 — 한글 데이터가 깨진다.
/// HTML 에 넣는 출력이 아니므로 완화된 인코더가 안전하다.
/// </summary>
public static class McpJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
