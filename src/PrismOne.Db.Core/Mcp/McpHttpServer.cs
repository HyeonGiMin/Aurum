using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace PrismOne.Db.Core.Mcp;

/// <summary>
/// MCP Streamable HTTP 전송의 최소 구현 — <c>POST /mcp</c> 에 JSON-RPC 를 받아 JSON 으로 답한다.
/// 서버발 스트림(SSE)은 쓰지 않으므로 GET 은 405 다 (규약상 허용).
///
/// 외부 라이브러리 없이 <see cref="HttpListener"/> 를 쓴다 (ASP.NET Core 를 끌어오면 배포본이 커진다).
/// 보안: localhost 에만 바인딩, Bearer 토큰 필수, 외부 Origin 거부(DNS rebinding 방지).
///
/// 종료: 처리 중인 도구 호출에 취소를 알리고(<see cref="ShutdownGrace"/> 까지) 끝나기를 기다린다 —
/// 승인 창이나 실행 중인 쿼리가 닫힌 세션을 건드리지 않게.
/// </summary>
public sealed class McpHttpServer(int port, string token, McpDispatcher dispatcher) : IAsyncDisposable
{
    /// <summary>요청 본문 상한 — 도구 인자는 SQL 한 덩어리 수준이다.</summary>
    public const int MaxBodyBytes = 4 * 1024 * 1024;

    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromMilliseconds(200);
    private const int MaxConsecutiveAcceptFailures = 50;

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<long, Task> _inFlight = new();
    private long _nextRequestId;
    private Task? _loop;

    public string Url => $"http://localhost:{port}/mcp";

    public bool IsRunning => _listener.IsListening;

    /// <summary>바인딩. 포트가 쓰이고 있으면 <see cref="HttpListenerException"/>.</summary>
    public void Start()
    {
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
                failures = 0;
            }
            catch (Exception) when (ct.IsCancellationRequested || !_listener.IsListening)
            {
                return;   // Stop() 으로 깨어난 경우
            }
            catch (HttpListenerException ex)
            {
                // 일시적 오류는 잠깐 쉬고 다시 — 계속 실패하면 헛돌지 말고 접는다
                Trace.TraceWarning($"MCP accept failed: {ex.Message}");
                if (++failures >= MaxConsecutiveAcceptFailures)
                    return;
                try { await Task.Delay(AcceptRetryDelay, ct); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            Track(context, ct);
        }
    }

    /// <summary>요청마다 따로 돌린다 — 오래 걸리는 도구(쿼리 실행)가 ping 을 막지 않게. 종료 때 기다리려고 기록한다.</summary>
    private void Track(HttpListenerContext context, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var task = Task.Run(() => ServeAsync(context, ct));
        _inFlight[id] = task;
        _ = task.ContinueWith(_ => _inFlight.TryRemove(id, out var _), TaskScheduler.Default);
    }

    private async Task ServeAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            var path = request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (!string.Equals(path, "/mcp", StringComparison.OrdinalIgnoreCase))
            {
                await WriteAsync(response, HttpStatusCode.NotFound, "not found");
                return;
            }
            if (!IsAllowedOrigin(request.Headers["Origin"]))
            {
                await WriteAsync(response, HttpStatusCode.Forbidden, "origin not allowed");
                return;
            }
            if (!IsAuthorized(request.Headers["Authorization"], token))
            {
                await WriteAsync(response, HttpStatusCode.Unauthorized, "missing or invalid bearer token");
                return;
            }
            if (request.HttpMethod != "POST")
            {
                response.AddHeader("Allow", "POST");
                await WriteAsync(response, HttpStatusCode.MethodNotAllowed, "only POST is supported");
                return;
            }
            if (request.ContentLength64 > MaxBodyBytes
                || await ReadBodyAsync(request.InputStream, ct) is not { } body)
            {
                await WriteAsync(response, HttpStatusCode.RequestEntityTooLarge, "request too large");
                return;
            }

            var reply = await dispatcher.HandleAsync(body, ct);
            if (reply is null)
            {
                response.StatusCode = (int)HttpStatusCode.Accepted;
                response.Close();
                return;
            }
            await WriteAsync(response, HttpStatusCode.OK, reply, "application/json");
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            response.Abort();
        }
        catch (Exception ex)
        {
            // 내부 사정(경로·호스트명)은 응답에 싣지 않는다
            Trace.TraceError($"MCP request failed: {ex}");
            try { await WriteAsync(response, HttpStatusCode.InternalServerError, "internal error"); }
            catch (Exception) { response.Abort(); }
        }
    }

    /// <summary>
    /// 본문을 상한까지만 읽는다. Content-Length 없는 청크 전송도 여기서 막힌다. 넘으면 null.
    /// </summary>
    internal static async Task<string?> ReadBodyAsync(Stream input, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await input.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static async Task WriteAsync(
        HttpListenerResponse response, HttpStatusCode status, string text, string contentType = "text/plain")
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.StatusCode = (int)status;
        response.ContentType = $"{contentType}; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    /// <summary>
    /// Origin 이 없거나(비브라우저 클라이언트) 루프백일 때만 허용한다.
    /// 브라우저의 악성 페이지가 localhost 서버를 두드리는 것(DNS rebinding)을 막는다.
    /// </summary>
    public static bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin))
            return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;
        return uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><c>Authorization: Bearer &lt;token&gt;</c> 을 상수 시간으로 비교한다.</summary>
    public static bool IsAuthorized(string? header, string expectedToken)
    {
        const string scheme = "Bearer ";
        if (string.IsNullOrEmpty(header) || expectedToken.Length == 0
            || !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return false;
        var presented = Encoding.UTF8.GetBytes(header[scheme.Length..].Trim());
        var expected = Encoding.UTF8.GetBytes(expectedToken);
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }

    public async ValueTask DisposeAsync()
    {
        // 1) 처리 중인 도구에 취소를 알린다 (승인 창은 닫히고 쿼리는 Cancel 된다)
        _cts.Cancel();

        // 2) 끝나기를 기다린다 — 응답 스트림이 살아 있는 동안 기다려야 Abort 가 깔끔하다
        var pending = _inFlight.Values.ToArray();
        if (pending.Length > 0)
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(ShutdownGrace));

        // 3) 새 요청을 막고 accept 루프를 정리한다
        try
        {
            if (_listener.IsListening)
                _listener.Stop();
        }
        catch (ObjectDisposedException) { /* 이미 닫힘 */ }
        if (_loop is not null)
        {
            try { await _loop; }
            catch (Exception ex) { Trace.TraceWarning($"MCP accept loop ended with: {ex.Message}"); }
        }
        _listener.Close();
        // _cts 는 Dispose 하지 않는다 — 유예 시간을 넘긴 핸들러가 아직 토큰을 볼 수 있다
    }
}
