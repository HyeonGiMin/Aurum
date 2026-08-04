using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;

namespace PrismOne.Studio;

/// <summary>
/// 완료 알림 토스트 (UI_POLISH P2-4). 상태바 한 줄은 놓치기 쉽다 —
/// 파일 저장·import 완료처럼 "됐다"를 확인하고 싶은 곳에만 쓴다 (실패는 기존
/// 오류 표시 경로 그대로). 창 우하단에 잠깐 떴다 사라진다.
/// </summary>
public static class Toast
{
    private static readonly Dictionary<Window, WindowNotificationManager> Managers = new();

    public static void Show(Window owner, string title, string? message = null)
    {
        if (!Managers.TryGetValue(owner, out var manager))
        {
            manager = new WindowNotificationManager(owner)
            {
                Position = NotificationPosition.BottomRight,
                MaxItems = 3,
            };
            Managers[owner] = manager;
            owner.Closed += (_, _) => Managers.Remove(owner);
        }
        manager.Show(new Notification(title, message, NotificationType.Success));
    }
}
