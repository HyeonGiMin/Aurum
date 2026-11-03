using System.Text.Json;
using System.Text.Json.Nodes;
using PrismOne.Db.Core.Mcp;

namespace PrismOne.Db.Core.Tests;

public class McpDispatcherTests
{
    private static McpDispatcher Create(params McpTool[] tools) =>
        new("aurum", "9.9.9", "test instructions", tools);

    private static McpTool Echo() => new(
        "echo",
        "echoes the text argument",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } },
        },
        (args, _) => Task.FromResult(McpToolResult.Ok(args.GetProperty("text").GetString() ?? "")));

    private static async Task<JsonElement> CallAsync(McpDispatcher dispatcher, string body)
    {
        var response = await dispatcher.HandleAsync(body, CancellationToken.None);
        Assert.NotNull(response);
        return JsonDocument.Parse(response).RootElement;
    }

    [Fact]
    public async Task Initialize_EchoesProtocolVersionAndAdvertisesTools()
    {
        // Arrange
        var dispatcher = Create();

        // Act
        var root = await CallAsync(dispatcher,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"claude-code","version":"1"}}}""");

        // Assert
        var result = root.GetProperty("result");
        Assert.Equal(1, root.GetProperty("id").GetInt32());
        Assert.Equal("2025-06-18", result.GetProperty("protocolVersion").GetString());
        Assert.Equal("aurum", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
        Assert.Equal("test instructions", result.GetProperty("instructions").GetString());
    }

    [Fact]
    public async Task Initialize_WithoutVersion_FallsBackToDefault()
    {
        var root = await CallAsync(Create(), """{"jsonrpc":"2.0","id":"a","method":"initialize","params":{}}""");

        Assert.Equal("a", root.GetProperty("id").GetString());
        Assert.Equal(McpDispatcher.DefaultProtocolVersion,
            root.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task Notification_ProducesNoResponse()
    {
        var response = await Create().HandleAsync(
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""", CancellationToken.None);

        Assert.Null(response);
    }

    [Fact]
    public async Task ToolsList_ReturnsNamesDescriptionsAndSchemas()
    {
        var root = await CallAsync(Create(Echo()), """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        var tool = Assert.Single(root.GetProperty("result").GetProperty("tools").EnumerateArray());
        Assert.Equal("echo", tool.GetProperty("name").GetString());
        Assert.Equal("echoes the text argument", tool.GetProperty("description").GetString());
        Assert.Equal("object", tool.GetProperty("inputSchema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ToolsCall_InvokesHandlerWithArguments()
    {
        var root = await CallAsync(Create(Echo()),
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hello"}}}""");

        var result = root.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        var content = Assert.Single(result.GetProperty("content").EnumerateArray());
        Assert.Equal("text", content.GetProperty("type").GetString());
        Assert.Equal("hello", content.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ToolsCall_ResponseDoesNotEscapeKoreanText()
    {
        var response = await Create(Echo()).HandleAsync(
            """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"echo","arguments":{"text":"테이블 \"환자\""}}}""",
            CancellationToken.None);

        Assert.Contains("테이블", response);
        Assert.DoesNotContain("\\u", response);
    }

    [Fact]
    public async Task ToolsCall_MissingArguments_PassesEmptyObject()
    {
        JsonValueKind seen = JsonValueKind.Undefined;
        var probe = new McpTool("probe", "", new JsonObject { ["type"] = "object" }, (args, _) =>
        {
            seen = args.ValueKind;
            return Task.FromResult(McpToolResult.Ok("ok"));
        });

        await CallAsync(Create(probe), """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"probe"}}""");

        Assert.Equal(JsonValueKind.Object, seen);
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_IsInvalidParams()
    {
        var root = await CallAsync(Create(Echo()),
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"nope"}}""");

        Assert.Equal(-32602, root.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ToolsCall_HandlerThrows_ReturnsToolErrorResult()
    {
        var boom = new McpTool("boom", "", new JsonObject { ["type"] = "object" },
            (_, _) => throw new InvalidOperationException("kaboom"));

        var root = await CallAsync(Create(boom),
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"boom","arguments":{}}}""");

        var result = root.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("kaboom", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task UnknownMethod_IsMethodNotFound()
    {
        var root = await CallAsync(Create(), """{"jsonrpc":"2.0","id":7,"method":"resources/list"}""");

        Assert.Equal(-32601, root.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Ping_ReturnsEmptyResult()
    {
        var root = await CallAsync(Create(), """{"jsonrpc":"2.0","id":8,"method":"ping"}""");

        Assert.Equal(JsonValueKind.Object, root.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task MalformedJson_IsParseErrorWithNullId()
    {
        var root = await CallAsync(Create(), "{not json");

        Assert.Equal(-32700, root.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task EmptyBatch_IsInvalidRequest()
    {
        var root = await CallAsync(Create(), "[]");

        Assert.Equal(-32600, root.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Batch_AnswersEachRequestAndSkipsNotifications()
    {
        var root = await CallAsync(Create(),
            """[{"jsonrpc":"2.0","id":1,"method":"ping"},{"jsonrpc":"2.0","method":"notifications/initialized"},{"jsonrpc":"2.0","id":2,"method":"ping"}]""");

        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(2, root.GetArrayLength());
    }
}
