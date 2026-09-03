using Avalonia;
using System;

namespace PrismOne.Studio;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack 훅 — 설치/제거/업데이트 직후 실행되는 경우를 처리하고 바로 종료한다.
        // 반드시 다른 어떤 것보다 먼저 불러야 한다 (AppUpdater.cs 참고).
        Velopack.VelopackApp.Build().Run();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // 열려 있는 SSH 터널을 닫는다 — 안 닫으면 서버에 유령 세션이 남는다.
            PrismOne.Db.Core.Ssh.SshTunnelPool.CloseAll();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
