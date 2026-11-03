using System.Text.Json;
using System.Text.Json.Nodes;

namespace PrismOne.Db.Core.Mcp;

/// <summary>MCP 도구 하나 — 이름·설명·입력 JSON Schema·실행 함수.</summary>
public sealed record McpTool(
    string Name,
    string Description,
    JsonObject InputSchema,
    Func<JsonElement, CancellationToken, Task<McpToolResult>> Handler);

/// <summary>도구 실행 결과. 실패도 JSON-RPC 오류가 아니라 isError 결과로 돌려준다 (MCP 규약).</summary>
public sealed record McpToolResult(string Text, bool IsError = false)
{
    public static McpToolResult Ok(string text) => new(text);
    public static McpToolResult Error(string text) => new(text, IsError: true);
}

/// <summary>
/// MCP 의 JSON-RPC 2.0 부분만 구현한 디스패처 — initialize · ping · tools/list · tools/call.
/// 전송(HTTP)과 분리돼 있어 문자열만으로 테스트한다.
/// 리소스·프롬프트·샘플링은 쓰지 않으므로 광고하지 않는다.
/// </summary>
public sealed class McpDispatcher(
    string serverName,
    string serverVersion,
    string instructions,
    IReadOnlyList<McpTool> tools)
{
    /// <summary>클라이언트가 버전을 안 밝혔을 때 쓰는 값.</summary>
    public const string DefaultProtocolVersion = "2025-06-18";

    private const int ParseError = -32700;
    private const int InvalidRequest = -32600;
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;

    private readonly Dictionary<string, McpTool> _tools =
        tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement;

    /// <summary>요청 본문을 처리해 응답 JSON 을 돌려준다. 알림뿐이면 null (HTTP 202).</summary>
    public async Task<string?> HandleAsync(string body, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return ErrorResponse(null, ParseError, "Parse error").ToJsonString(McpJson.Options);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
                return (await HandleOneAsync(root, ct))?.ToJsonString(McpJson.Options);
            // JSON-RPC 2.0: 빈 배치는 Invalid Request 하나로 답한다
            if (root.GetArrayLength() == 0)
                return ErrorResponse(null, InvalidRequest, "Invalid request: empty batch").ToJsonString(McpJson.Options);

            var responses = new JsonArray();
            foreach (var item in root.EnumerateArray())
            {
                if (await HandleOneAsync(item, ct) is { } response)
                    responses.Add(response);
            }
            return responses.Count == 0 ? null : responses.ToJsonString(McpJson.Options);
        }
    }

    private async Task<JsonObject?> HandleOneAsync(JsonElement request, CancellationToken ct)
    {
        if (request.ValueKind != JsonValueKind.Object
            || !request.TryGetProperty("method", out var methodElement)
            || methodElement.ValueKind != JsonValueKind.String)
        {
            return ErrorResponse(IdOf(request), InvalidRequest, "Invalid request");
        }

        // id 가 없으면 알림 — 응답하지 않는다
        if (!request.TryGetProperty("id", out var idElement))
            return null;

        var id = JsonNode.Parse(idElement.GetRawText());
        var parameters = request.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object
            ? p
            : EmptyObject;

        return methodElement.GetString() switch
        {
            "initialize" => ResultResponse(id, Initialize(parameters)),
            "ping" => ResultResponse(id, new JsonObject()),
            "tools/list" => ResultResponse(id, ListTools()),
            "tools/call" => await CallToolAsync(id, parameters, ct),
            var method => ErrorResponse(id, MethodNotFound, $"Method not found: {method}"),
        };
    }

    private JsonObject Initialize(JsonElement parameters)
    {
        // 요청한 버전을 그대로 돌려준다 — tools 부분은 버전 간 형태가 같다
        var version = parameters.TryGetProperty("protocolVersion", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : DefaultProtocolVersion;
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = serverName,
                ["version"] = serverVersion,
            },
            ["instructions"] = instructions,
        };
    }

    private JsonObject ListTools()
    {
        var list = new JsonArray();
        foreach (var tool in _tools.Values)
        {
            list.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
            });
        }
        return new JsonObject { ["tools"] = list };
    }

    private async Task<JsonObject> CallToolAsync(JsonNode? id, JsonElement parameters, CancellationToken ct)
    {
        var name = parameters.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString()!
            : "";
        if (!_tools.TryGetValue(name, out var tool))
            return ErrorResponse(id, InvalidParams, $"Unknown tool: {name}");

        var arguments = parameters.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
            ? a
            : EmptyObject;

        McpToolResult result;
        try
        {
            result = await tool.Handler(arguments, ct);
        }
        catch (OperationCanceledException)
        {
            result = McpToolResult.Error("Cancelled");
        }
        catch (Exception ex)
        {
            result = McpToolResult.Error($"{tool.Name} failed: {ex.Message}");
        }

        return ResultResponse(id, new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = result.Text,
            }),
            ["isError"] = result.IsError,
        });
    }

    private static JsonNode? IdOf(JsonElement request) =>
        request.ValueKind == JsonValueKind.Object && request.TryGetProperty("id", out var id)
            ? JsonNode.Parse(id.GetRawText())
            : null;

    private static JsonObject ResultResponse(JsonNode? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    private static JsonObject ErrorResponse(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
