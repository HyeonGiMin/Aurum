using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace PrismOne.Studio;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// 옵션의 Theme("Light"/"Dark"/"System")을 적용한다.
    /// 색은 DynamicResource 라 즉시 반영되고, 에디터 하이라이팅 같은 코드 색은
    /// 창들이 ActualThemeVariantChanged 로 따라온다.
    /// </summary>
    public static void ApplyTheme(string theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme switch
        {
            "Dark" => ThemeVariant.Dark,
            "System" => ThemeVariant.Default,
            _ => ThemeVariant.Light,
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 스크린샷 하니스의 다크 변형: IAPDM_SHOT_THEME=dark (옵션 파일과 무관하게)
        ApplyTheme(Environment.GetEnvironmentVariable("IAPDM_SHOT_THEME") is "dark"
            ? "Dark"
            : PrismOne.Db.Core.AppOptions.Load().Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}