using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PrismOne.Db.Core.Mcp;

namespace PrismOne.Db.Core.Tests;

public class McpHttpServerTests
{
    private const string Token = "test-token-0123456789";

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("http://localhost:3000", true)]
    [InlineData("http://127.0.0.1", true)]
    [InlineData("http://[::1]:8080", true)]
    [InlineData("https://evil.example.com", false)]
    [InlineData("http://localhost.evil.com", false)]
    [InlineData("null", false)]
    public void IsAllowedOrigin_OnlyAcceptsLoopback(string? origin, bool expected) =>
        Assert.Equal(expected, McpHttpServer.IsAllowedOrigin(origin));

    [Theory]
    [InlineData("Bearer " + Token, true)]
    [InlineData("bearer " + Token, true)]
    [InlineData("Bearer wrong", false)]
    [InlineData(Token, false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsAuthorized_RequiresMatchingBearerToken(string? header, bool expected) =>
        Assert.Equal(expected, McpHttpServer.IsAuthorized(header, Token));

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static McpDispatcher Dispatcher() => new("aurum", "1.0", "", [
        new McpTool("hello", "", new JsonObject { ["type"] = "object" },
            (_, _) => Task.FromResult(McpToolResult.Ok("hi"))),
    ]);

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Server_EnforcesAuthAndSpeaksJsonRpcOverPost()
    {
        // Arrange
        var port = FreePort();
        await using var server = new McpHttpServer(port, Token, Dispatcher());
        server.Start();
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        const string call = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"hello"}}""";

        // Act — 토큰 없음
        var anonymous = await client.PostAsync("", Json(call));

        // Act — 토큰 있음
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var ok = await client.PostAsync("", Json(call));
        var notification = await client.PostAsync("", Json("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        var get = await client.GetAsync("");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("application/json", ok.Content.Headers.ContentType?.MediaType);
        var body = JsonDocument.Parse(await ok.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("hi", body.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(HttpStatusCode.Accepted, notification.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
    }

    [Fact]
    public async Task Server_RejectsForeignOrigin()
    {
        var port = FreePort();
        await using var server = new McpHttpServer(port, Token, Dispatcher());
        server.Start();
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Url)
        {
            Content = Json("""{"jsonrpc":"2.0","id":1,"method":"ping"}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        request.Headers.Add("Origin", "https://evil.example.com");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Server_ChunkedBodyOverLimit_IsRejected()
    {
        // Content-Length 없이(청크) 보내도 상한이 걸려야 한다
        var port = FreePort();
        await using var server = new McpHttpServer(port, Token, Dispatcher());
        server.Start();
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Url)
        {
            Content = new StreamContent(new MemoryStream(new byte[McpHttpServer.MaxBodyBytes + 1])),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        request.Headers.TransferEncodingChunked = true;

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Dispose_CancelsAndAwaitsInFlightToolCalls()
    {
        // Arrange — 취소될 때까지 끝나지 않는 도구 (승인 창을 기다리는 run_query 같은 것)
        var started = new TaskCompletionSource();
        var observedCancel = new TaskCompletionSource<bool>();
        var dispatcher = new McpDispatcher("aurum", "1.0", "", [
            new McpTool("wait", "", new JsonObject { ["type"] = "object" }, async (_, ct) =>
            {
                started.SetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { observedCancel.SetResult(true); throw; }
                return McpToolResult.Ok("never");
            }),
        ]);
        var port = FreePort();
        var server = new McpHttpServer(port, Token, dispatcher);
        server.Start();
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var call = client.PostAsync(server.Url,
            Json("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"wait"}}"""));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Act
        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        // Assert — 도구가 취소 신호를 받았고, 서버는 그 응답을 마저 보낸 뒤에 닫혔다
        // (연결을 끊는 대신 "Cancelled" 도구 오류로 깔끔하게 끝난다)
        Assert.True(observedCancel.Task.IsCompleted);
        var response = await call.WaitAsync(TimeSpan.FromSeconds(5));
        var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal("Cancelled", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Server_UnknownPath_IsNotFound()
    {
        var port = FreePort();
        await using var server = new McpHttpServer(port, Token, Dispatcher());
        server.Start();
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var response = await client.PostAsync($"http://localhost:{port}/other", Json("{}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
