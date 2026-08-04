using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;

namespace PrismOne.Studio;

/// <summary>
/// 코드에서 그리는 색은 여기로 — 현재 테마의 리소스를 읽는다.
/// (XAML 은 DynamicResource 로 즉시 따라오지만, 코드가 만든 요소는
/// 만들 때의 테마 색을 갖는다 — 플랜 트리·diff 트리는 재실행 시 다시 그려진다.)
/// </summary>
public static class ThemeBrushes
{
    public static bool IsDark =>
        Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    public static IBrush Get(string key, string fallbackHex)
    {
        var app = Application.Current;
        if (app is not null &&
            app.TryGetResource(key, app.ActualThemeVariant, out var value) &&
            value is IBrush brush)
            return brush;
        return new SolidColorBrush(Color.Parse(fallbackHex));
    }
}

/// <summary>
/// 코드로 만드는 창의 타이틀바 아이콘 (Windows 는 창마다 아이콘이 따로다).
/// XAML 창은 <c>Icon="/Assets/icon.png"</c> 속성으로 같은 그림을 쓴다.
/// </summary>
public static class AppIcon
{
    private static WindowIcon? _shared;

    public static WindowIcon Shared => _shared ??=
        new WindowIcon(AssetLoader.Open(new Uri("avares://Aurum/Assets/icon.png")));
}
