using PrismOne.Db.Core.Mcp;

namespace PrismOne.Db.Core.Tests;

public class McpControlStateTests
{
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(3);
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private McpControlState Create() => new(Linger, () => _now);

    [Fact]
    public void Idle_IsNotLocked()
    {
        var state = Create();

        Assert.False(state.IsLocked);
        Assert.Null(state.CurrentTool);
    }

    [Fact]
    public void DuringCall_IsLockedAndNamesTheTool()
    {
        var state = Create();

        Assert.True(state.TryBegin("run_query"));

        Assert.True(state.IsLocked);
        Assert.Equal("run_query", state.CurrentTool);
    }

    [Fact]
    public void AfterCall_StaysLockedUntilLingerElapses()
    {
        // Arrange — 연속 호출 사이에 잠금이 깜빡이지 않게 잠깐 유지한다
        var state = Create();
        state.TryBegin("set_editor");

        // Act
        state.End();
        var justAfter = state.IsLocked;
        _now += Linger - TimeSpan.FromMilliseconds(1);
        var nearEnd = state.IsLocked;
        _now += TimeSpan.FromMilliseconds(2);
        var afterLinger = state.IsLocked;

        // Assert
        Assert.True(justAfter);
        Assert.True(nearEnd);
        Assert.False(afterLinger);
        Assert.Null(state.CurrentTool);
    }

    [Fact]
    public void Pause_UnlocksImmediatelyAndRejectsNewCalls()
    {
        var state = Create();
        state.TryBegin("run_query");

        state.Pause();

        Assert.True(state.IsPaused);
        Assert.False(state.IsLocked);
        Assert.False(state.TryBegin("aurum_status"));
    }

    [Fact]
    public void Resume_AllowsCallsAgain()
    {
        var state = Create();
        state.Pause();

        state.Resume();

        Assert.False(state.IsPaused);
        Assert.True(state.TryBegin("run_query"));
    }

    [Fact]
    public void EndAfterPause_DoesNotGoNegativeOrRelock()
    {
        // 중지를 누른 뒤 진행 중이던 호출이 마저 끝나는 경우
        var state = Create();
        state.TryBegin("run_query");
        state.Pause();

        state.End();
        state.Resume();

        Assert.False(state.IsLocked);
        Assert.True(state.TryBegin("x"));
        state.End();
        state.End();   // 짝이 안 맞는 End 는 무시
        _now += Linger + TimeSpan.FromSeconds(1);
        Assert.False(state.IsLocked);
    }

    [Fact]
    public void Changed_FiresOnEveryTransition()
    {
        var state = Create();
        var count = 0;
        state.Changed += () => count++;

        state.TryBegin("a");
        state.End();
        state.Pause();
        state.Resume();

        Assert.Equal(4, count);
    }
}
