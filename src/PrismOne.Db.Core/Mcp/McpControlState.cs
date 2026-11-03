namespace PrismOne.Db.Core.Mcp;

/// <summary>
/// "Claude 가 Aurum 을 조작 중" 상태 — Chrome 확장이 브라우저를 제어할 때처럼
/// 화면에 표시하고 사용자 입력을 잠글지 정한다. UI 와 분리해 시계를 주입받는다 (테스트용).
///
/// <list type="bullet">
/// <item>도구 호출 중에는 잠근다. 호출이 끝나도 <c>linger</c> 동안 유지한다 —
///   Claude 는 생각하며 여러 도구를 연달아 부르므로 사이사이 잠금이 깜빡이면 사용자 입력이 섞인다.</item>
/// <item><see cref="Pause"/>(사용자가 [중지]) 하면 즉시 풀고, <see cref="Resume"/> 전까지 새 호출을 거절한다.</item>
/// </list>
/// UI 스레드에서만 쓴다 (MCP 도구는 UI 스레드에서 하나씩 돈다) — 잠금이 필요 없다.
/// </summary>
public sealed class McpControlState(TimeSpan linger, Func<DateTimeOffset> clock)
{
    private int _activeCalls;
    private DateTimeOffset? _lastEnd;

    public bool IsPaused { get; private set; }

    /// <summary>지금 돌고 있는 도구 이름 (없으면 null).</summary>
    public string? CurrentTool { get; private set; }

    /// <summary>표시·입력 잠금 여부.</summary>
    public bool IsLocked =>
        !IsPaused && (_activeCalls > 0 || (_lastEnd is { } end && clock() - end < linger));

    /// <summary>잠금이 풀리는 시각 (호출 중이거나 잠겨 있지 않으면 null) — UI 가 타이머를 맞춘다.</summary>
    public DateTimeOffset? UnlocksAt =>
        IsLocked && _activeCalls == 0 && _lastEnd is { } end ? end + linger : null;

    /// <summary>상태가 바뀔 때마다 (UI 다시 그리기).</summary>
    public event Action? Changed;

    /// <summary>호출 시작. 일시정지 중이면 false — 호출을 거절해야 한다.</summary>
    public bool TryBegin(string tool)
    {
        if (IsPaused)
            return false;
        _activeCalls++;
        CurrentTool = tool;
        Changed?.Invoke();
        return true;
    }

    public void End()
    {
        if (_activeCalls == 0)
            return;
        _activeCalls--;
        if (_activeCalls == 0)
        {
            CurrentTool = null;
            _lastEnd = clock();
        }
        Changed?.Invoke();
    }

    /// <summary>사용자가 제어를 되찾는다 — 잠금을 바로 풀고 새 호출을 막는다.</summary>
    public void Pause()
    {
        IsPaused = true;
        _lastEnd = null;
        Changed?.Invoke();
    }

    public void Resume()
    {
        IsPaused = false;
        _lastEnd = null;
        Changed?.Invoke();
    }
}
