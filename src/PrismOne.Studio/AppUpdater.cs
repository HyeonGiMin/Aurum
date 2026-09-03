using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PrismOne.Db.Core.Ssh;
using Velopack;
using Velopack.Sources;

namespace PrismOne.Studio;

/// <summary>
/// GitHub Releases 기반 자동 업데이트 (Velopack).
///
/// 흐름: 시작 시(또는 Help > Check for Updates) 최신 릴리즈를 확인 → 새 버전이 있으면
/// 팝업 → Update 를 누르면 내려받고 앱을 다시 시작한다. 배포는
/// <c>.github/workflows/release.yml</c> 이 태그(vX.Y.Z)마다 만든다.
///
/// zip 이나 <c>dotnet run</c> 으로 띄운 경우엔 Update.exe 가 없어 <see cref="IsInstalled"/> 가
/// false — 이때는 조용히 건너뛴다 (수동 확인이면 안내만).
/// </summary>
public static class AppUpdater
{
    public const string RepoUrl = "https://github.com/HyeonGiMin/Aurum";

    private static UpdateManager Create() => new(new GithubSource(RepoUrl, accessToken: null, prerelease: false));

    /// <summary>Setup 으로 설치된 본인가 — 아니면 자동 업데이트를 쓸 수 없다.</summary>
    public static bool IsInstalled
    {
        get
        {
            try { return Create().IsInstalled; }
            catch { return false; }
        }
    }

    /// <summary>About 창에 보일 버전. 설치본은 Velopack 메타데이터, 아니면 어셈블리 버전.</summary>
    public static string CurrentVersion
    {
        get
        {
            try
            {
                if (Create().CurrentVersion is { } installed)
                    return installed.ToString();
            }
            catch { /* 설치본이 아니면 아래로 */ }

            var info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return info?.Split('+')[0] ?? "dev";
        }
    }

    /// <param name="manual">true 면 Help 메뉴에서 부른 것 — 결과가 무엇이든 알린다.
    /// false 면 시작 시 확인 — 새 버전이 있을 때만 팝업, 실패는 조용히 로그만.</param>
    public static async Task CheckAsync(Window owner, bool manual)
    {
        UpdateManager manager;
        try
        {
            manager = Create();
        }
        catch (Exception ex)
        {
            if (manual)
                await ErrorDialog.ShowAsync(owner, "Check for Updates", "업데이트 기능을 초기화하지 못했습니다", ex);
            return;
        }

        if (!manager.IsInstalled)
        {
            if (manual)
                await ErrorDialog.ShowAsync(owner, "Check for Updates", "자동 업데이트를 쓸 수 없는 실행 방식입니다",
                    $"zip 이나 소스에서 직접 띄운 버전입니다. 자동 업데이트는 Setup 으로 설치한 본에서만 됩니다.\n최신 설치본: {RepoUrl}/releases/latest");
            return;
        }

        UpdateInfo? update;
        try
        {
            update = await manager.CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            if (manual)
                await ErrorDialog.ShowAsync(owner, "Check for Updates", "업데이트를 확인하지 못했습니다", ex);
            else
                Trace.WriteLine($"[AppUpdater] check failed: {ex.Message}");
            return;
        }

        if (update is null)
        {
            if (manual)
                Toast.Show(owner, "최신 버전입니다", $"Aurum {manager.CurrentVersion}");
            return;
        }

        await ShowUpdateDialogAsync(owner, manager, update);
    }

    /// <summary>
    /// "새 버전 있음" 팝업. Update 를 누르면 같은 창에서 진행률을 보이며 내려받고,
    /// 끝나면 앱을 다시 시작한다 (열린 SSH 터널은 먼저 닫는다).
    /// </summary>
    private static async Task ShowUpdateDialogAsync(Window owner, UpdateManager manager, UpdateInfo update)
    {
        var newVersion = update.TargetFullRelease.Version.ToString();
        var current = manager.CurrentVersion?.ToString() ?? CurrentVersion;

        var progress = new ProgressBar { Minimum = 0, Maximum = 100, IsVisible = false, Height = 6 };
        var status = new TextBlock { FontSize = 12, Opacity = 0.7, IsVisible = false };
        var later = new Button { Content = "Later", MinWidth = 84, MinHeight = 30, IsCancel = true };
        var install = new Button { Content = "Update Now", MinWidth = 110, MinHeight = 30, IsDefault = true };

        var dialog = new Window
        {
            Title = "Aurum Update",
            Icon = AppIcon.Shared,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"새 버전 {newVersion} 이 있습니다 (현재 {current}).",
                        FontSize = 13.5,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = "지금 업데이트하면 내려받은 뒤 Aurum 이 다시 시작됩니다.\n" +
                               "커밋하지 않은 트랜잭션과 저장하지 않은 편집은 사라집니다.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    progress,
                    status,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { later, install },
                    },
                },
            },
        };

        later.Click += (_, _) => dialog.Close();
        install.Click += async (_, _) =>
        {
            install.IsEnabled = false;
            later.IsEnabled = false;
            progress.IsVisible = true;
            status.IsVisible = true;
            status.Text = "내려받는 중…";
            try
            {
                await manager.DownloadUpdatesAsync(update,
                    p => Dispatcher.UIThread.Post(() => progress.Value = p));
            }
            catch (Exception ex)
            {
                dialog.Close();
                await ErrorDialog.ShowAsync(owner, "Aurum Update", "업데이트를 내려받지 못했습니다", ex);
                return;
            }

            status.Text = "다시 시작합니다…";
            // Update.exe 가 이 프로세스를 끝내므로 Program.Main 의 finally 가 돌지 않는다 — 여기서 닫는다.
            SshTunnelPool.CloseAll();
            manager.ApplyUpdatesAndRestart(update);
        };

        await dialog.ShowDialog(owner);
    }
}
