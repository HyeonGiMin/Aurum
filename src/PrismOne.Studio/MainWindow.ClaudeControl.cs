using System;
using System.Threading;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PrismOne.Db.Core.Mcp;

namespace PrismOne.Studio;

/// <summary>
/// "Claude 가 조작 중" 표시와 입력 잠금 — Chrome 확장이 브라우저를 제어할 때처럼.
///
/// <list type="bullet">
/// <item>도구가 도는 동안(+ 끝난 뒤 잠깐) 창 테두리·배너를 띄우고 마우스·키보드 입력을 막는다.
///   사용자 입력이 Claude 의 편집·실행과 섞이지 않게 하려는 것이다.</item>
/// <item>[중지] 또는 Esc — 진행 중인 호출(쿼리 포함)을 취소하고 일시정지한다. [재개] 전까지
///   Claude 의 호출은 "사용자가 중지함" 으로 거절된다 (상태 조회만 허용).</item>
/// <item>승인 창은 별도 창이라 잠금과 상관없이 답할 수 있다.</item>
/// </list>
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan ClaudeControlLinger = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan UnlockTimerSlack = TimeSpan.FromMilliseconds(50);

    private readonly McpControlState _claudeControl = new(ClaudeControlLinger, () => DateTimeOffset.Now);
    private DispatcherTimer? _claudeUnlockTimer;
    private CancellationTokenSource? _claudeCallCts;

    /// <summary>생성자에서 한 번. 텍스트 입력(IME 확정 포함)은 KeyDown 과 따로 오므로 따로 막는다.</summary>
    private void HookClaudeControl()
    {
        _claudeControl.Changed += RenderClaudeControl;
        AddHandler(TextInputEvent, OnLockedTextInput, RoutingStrategies.Tunnel);
    }

    private void RenderClaudeControl()
    {
        var locked = _claudeControl.IsLocked;
        ClaudeOverlay.IsVisible = locked;
        ClaudePausedBar.IsVisible = _claudeControl.IsPaused;
        if (locked)
        {
            ClaudeOverlayText.Text = _claudeControl.CurrentTool is { } tool
                ? $"Claude 가 Aurum 을 조작하는 중 ({tool}) — 입력이 잠겨 있습니다"
                : "Claude 가 Aurum 을 조작하는 중 — 입력이 잠겨 있습니다";
        }
        ScheduleUnlock();
    }

    /// <summary>호출이 끝난 뒤 유예 시간이 지나면 오버레이를 내린다.</summary>
    private void ScheduleUnlock()
    {
        _claudeUnlockTimer?.Stop();
        if (_claudeControl.UnlocksAt is not { } at)
            return;
        var delay = at - DateTimeOffset.Now;
        _claudeUnlockTimer = new DispatcherTimer
        {
            Interval = (delay > TimeSpan.Zero ? delay : TimeSpan.Zero) + UnlockTimerSlack,
        };
        _claudeUnlockTimer.Tick += (_, _) =>
        {
            _claudeUnlockTimer?.Stop();
            RenderClaudeControl();
        };
        _claudeUnlockTimer.Start();
    }

    /// <summary>
    /// 도구 호출 시작 (UI 스레드). 일시정지 중이면 null. 돌려준 토큰은 서버 종료와
    /// 사용자 [중지] 둘 다에 취소된다.
    /// </summary>
    private CancellationTokenSource? BeginClaudeCall(string tool, CancellationToken serverToken)
    {
        if (!_claudeControl.TryBegin(tool))
            return null;
        _claudeCallCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        return _claudeCallCts;
    }

    private void EndClaudeCall(CancellationTokenSource call)
    {
        if (ReferenceEquals(_claudeCallCts, call))
            _claudeCallCts = null;
        call.Dispose();
        _claudeControl.End();
    }

    private bool IsClaudePaused => _claudeControl.IsPaused;

    private void OnClaudeStop(object? sender, RoutedEventArgs e) => StopClaudeControl();

    private void StopClaudeControl()
    {
        _claudeCallCts?.Cancel();   // run_query 는 이 신호로 쿼리를 Cancel 하고, 승인 창은 닫힌다
        _claudeControl.Pause();
        StatusLabel.Text = "Claude 조작을 중지했습니다 — 오른쪽 아래 [재개] 로 다시 허용합니다";
    }

    private void OnClaudeResume(object? sender, RoutedEventArgs e)
    {
        _claudeControl.Resume();
        StatusLabel.Text = "Claude 조작을 다시 허용했습니다";
    }

    /// <summary>잠겨 있으면 키를 삼킨다 (Esc 는 중지). OnWindowKeyDown 맨 앞에서 부른다.</summary>
    private bool HandleLockedKey(KeyEventArgs e)
    {
        if (!_claudeControl.IsLocked)
            return false;
        if (e.Key == Key.Escape)
            StopClaudeControl();
        e.Handled = true;
        return true;
    }

    private void OnLockedTextInput(object? sender, TextInputEventArgs e)
    {
        if (_claudeControl.IsLocked)
            e.Handled = true;
    }
}
