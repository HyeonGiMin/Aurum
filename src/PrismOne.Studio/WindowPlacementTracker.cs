using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using PrismOne.Db.Core;

namespace PrismOne.Studio;

/// <summary>
/// 창 위치·크기 기억 (UI_POLISH P1-3). 닫을 때 저장하고 다음에 그 자리로 연다.
///
/// - 저장 시 options.json 을 **새로 읽어 placement 만 갱신** — 다른 창이나 옵션
///   다이얼로그가 바꾼 필드를 덮어쓰지 않기 위해서다.
/// - 화면 밖(모니터 해제 등)이면 적용하지 않는다.
/// - 스크린샷 하니스에서는 동작하지 않는다 — 회귀 화면 크기가 흔들리면 안 된다.
/// </summary>
public sealed class WindowPlacementTracker
{
    private readonly Window _window;
    private readonly string _key;
    private PixelPoint _normalPosition;
    private Size _normalSize;

    public static void Attach(Window window, string key)
    {
        if (Environment.GetEnvironmentVariable("IAPDM_SHOT_DIR") is { Length: > 0 })
            return;
        _ = new WindowPlacementTracker(window, key);
    }

    private WindowPlacementTracker(Window window, string key)
    {
        _window = window;
        _key = key;
        Apply(AppOptions.Load().WindowPlacements?.GetValueOrDefault(key));

        _normalPosition = window.Position;
        _normalSize = new Size(window.Width, window.Height);
        // Maximized 상태의 Bounds 는 복원 크기가 아니다 — normal 일 때의 값만 기억해 둔다
        window.PositionChanged += (_, e) =>
        {
            if (window.WindowState == WindowState.Normal)
                _normalPosition = e.Point;
        };
        window.SizeChanged += (_, e) =>
        {
            if (window.WindowState == WindowState.Normal)
                _normalSize = e.NewSize;
        };
        window.Closing += (_, _) => Save();
    }

    private void Apply(WindowRect? saved)
    {
        if (saved is null) return;
        var rect = new PixelRect(saved.X, saved.Y, Math.Max(saved.Width, 200), Math.Max(saved.Height, 150));
        // 어느 모니터에도 걸치지 않으면(모니터 구성 변경) 기본 위치 유지
        if (!_window.Screens.All.Any(s => s.Bounds.Intersects(rect)))
            return;
        _window.WindowStartupLocation = WindowStartupLocation.Manual;
        _window.Position = rect.Position;
        _window.Width = rect.Width;
        _window.Height = rect.Height;
        if (saved.Maximized)
            _window.WindowState = WindowState.Maximized;
    }

    private void Save()
    {
        try
        {
            var options = AppOptions.Load();
            options.WindowPlacements ??= new Dictionary<string, WindowRect>();
            options.WindowPlacements[_key] = new WindowRect(
                _normalPosition.X, _normalPosition.Y,
                (int)_normalSize.Width, (int)_normalSize.Height,
                _window.WindowState == WindowState.Maximized);
            options.Save();
        }
        catch { /* 창 위치 저장 실패는 치명적이지 않다 */ }
    }
}
