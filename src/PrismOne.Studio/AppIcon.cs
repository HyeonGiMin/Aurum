using System;
using Avalonia.Controls;
using Avalonia.Platform;

namespace PrismOne.Studio;

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
