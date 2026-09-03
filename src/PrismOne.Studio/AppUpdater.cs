using System;
using System.Diagnostics;
using System.Linq;
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
/// 시작 시엔 **로그온 창보다 먼저** 확인한다 — 접속해서 일을 시작한 뒤에 "업데이트하고
/// 다시 시작하라"고 하면 하던 것을 버려야 하기 때문이다. 대신 네트워크가 느릴 때
/// 로그온이 묶이지 않게 <see cref="StartupTimeout"/> 만 기다리고, 늦게 온 답은
/// 로그온 뒤에 알린다.
///
/// zip 이나 <c>dotnet run</c> 으로 띄운 경우엔 Update.exe 가 없어 <see cref="IsInstalled"/> 가
/// false — 이때는 조용히 건너뛴다 (수동 확인이면 안내만).
/// </summary>
public static class AppUpdater
{
    public const string RepoUrl = "https://github.com/HyeonGiMin/Aurum";

    /// <summary>시작 시 확인이 로그온 창을 붙잡을 수 있는 최대 시간.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(4);

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

    /// <summary>
    /// 시작 시 확인 — 새 버전이 있으면 로그온 전에 알린다. 실패는 조용히 로그만 남긴다
    /// (업데이트 서버가 죽었다고 앱을 못 쓰게 만들 이유가 없다).
    /// </summary>
    public static async Task CheckAtStartupAsync(Window owner)
    {
        UpdateManager manager;
        try { manager = Create(); }
        catch (Exception ex) { Trace.WriteLine($"[AppUpdater] init failed: {ex.Message}"); return; }

        if (!manager.IsInstalled)
            return;

        Task<UpdateInfo?> check;
        try { check = manager.CheckForUpdatesAsync(); }
        catch (Exception ex) { Trace.WriteLine($"[AppUpdater] check failed: {ex.Message}"); return; }

        // 제한 시간 안에 답이 없으면 로그온을 먼저 띄우고, 답이 오면 그때 알린다
        if (await Task.WhenAny(check, Task.Delay(StartupTimeout)) != check)
        {
            _ = ShowWhenReadyAsync(owner, manager, check);
            return;
        }

        UpdateInfo? update;
        try { update = await check; }
        catch (Exception ex) { Trace.WriteLine($"[AppUpdater] check failed: {ex.Message}"); return; }

        if (update is not null)
            await ShowUpdateDialogAsync(owner, manager, update);
    }

    /// <summary>시작 확인이 제한 시간을 넘겼을 때 — 답이 오면 (로그온 뒤에) 알린다.</summary>
    private static async Task ShowWhenReadyAsync(Window owner, UpdateManager manager, Task<UpdateInfo?> check)
    {
        UpdateInfo? update;
        try { update = await check; }
        catch (Exception ex) { Trace.WriteLine($"[AppUpdater] check failed: {ex.Message}"); return; }

        if (update is null)
            return;

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (owner.IsVisible)
                await ShowUpdateDialogAsync(owner, manager, update);
        });
    }

    /// <summary>Help > Check for Updates — 결과가 무엇이든 알린다.</summary>
    public static async Task CheckAsync(Window owner, bool manual)
    {
        if (!manual)
        {
            await CheckAtStartupAsync(owner);
            return;
        }

        UpdateManager manager;
        try { manager = Create(); }
        catch (Exception ex)
        {
            await ErrorDialog.ShowAsync(owner, "Check for Updates", "업데이트 기능을 초기화하지 못했습니다", ex);
            return;
        }

        if (!manager.IsInstalled)
        {
            await ErrorDialog.ShowAsync(owner, "Check for Updates", "자동 업데이트를 쓸 수 없는 실행 방식입니다",
                $"zip 이나 소스에서 직접 띄운 버전입니다. 자동 업데이트는 Setup 으로 설치한 본에서만 됩니다.\n최신 설치본: {RepoUrl}/releases/latest");
            return;
        }

        UpdateInfo? update;
        try { update = await manager.CheckForUpdatesAsync(); }
        catch (Exception ex)
        {
            await ErrorDialog.ShowAsync(owner, "Check for Updates", "업데이트를 확인하지 못했습니다", ex);
            return;
        }

        if (update is null)
        {
            Toast.Show(owner, "최신 버전입니다", $"Aurum {manager.CurrentVersion}");
            return;
        }

        await ShowUpdateDialogAsync(owner, manager, update);
    }

    // ---------- 업데이트 알림 창 ----------

    /// <summary>
    /// "새 버전 있음" 창. 무엇이 얼마나 바뀌는지(버전·용량·릴리즈 노트)를 먼저 보이고,
    /// Update Now 를 누르면 같은 창에서 진행률을 보이며 내려받은 뒤 앱을 다시 시작한다.
    /// </summary>
    private static async Task ShowUpdateDialogAsync(Window owner, UpdateManager manager, UpdateInfo update)
    {
        var target = update.TargetFullRelease;

        // 델타가 있으면 그쪽이 훨씬 작다 — 실제로 내려받을 양을 보여준다
        var deltas = update.DeltasToTarget?.ToArray() ?? [];
        var deltaSize = deltas.Sum(d => d.Size);
        var useDelta = deltas.Length > 0 && deltaSize > 0 && deltaSize < target.Size;

        var parts = BuildDialog(
            current: manager.CurrentVersion?.ToString() ?? CurrentVersion,
            newVersion: target.Version.ToString(),
            downloadBytes: useDelta ? deltaSize : target.Size,
            isDelta: useDelta,
            notes: target.NotesMarkdown);

        parts.Later.Click += (_, _) => parts.Dialog.Close();
        parts.Install.Click += async (_, _) =>
        {
            parts.Install.IsEnabled = false;
            parts.Later.IsEnabled = false;
            parts.Progress.IsVisible = true;
            parts.Status.IsVisible = true;
            parts.Status.Text = "내려받는 중… 0%";
            try
            {
                await manager.DownloadUpdatesAsync(update, p => Dispatcher.UIThread.Post(() =>
                {
                    parts.Progress.Value = p;
                    parts.Status.Text = $"내려받는 중… {p}%";
                }));
            }
            catch (Exception ex)
            {
                parts.Dialog.Close();
                await ErrorDialog.ShowAsync(owner, "Aurum Update", "업데이트를 내려받지 못했습니다", ex);
                return;
            }

            parts.Status.Text = "다시 시작합니다…";
            // Update.exe 가 이 프로세스를 끝내므로 Program.Main 의 finally 가 돌지 않는다 — 여기서 닫는다.
            SshTunnelPool.CloseAll();
            manager.ApplyUpdatesAndRestart(update);
        };

        await parts.Dialog.ShowDialog(owner);
    }

    /// <summary>창과, 뒤에서 손대야 하는 조각들 (버튼·진행률).</summary>
    private sealed record DialogParts(
        Window Dialog, Button Later, Button Install, ProgressBar Progress, TextBlock Status);

    /// <summary>
    /// 알림 창 조립. 실제 릴리즈 정보 없이도 만들 수 있게 값만 받는다
    /// (스크린샷 하니스가 <see cref="PreviewWindow"/> 로 같은 창을 찍는다).
    /// </summary>
    private static DialogParts BuildDialog(
        string current, string newVersion, long downloadBytes, bool isDelta, string? notes)
    {
        var accent = AccentBrush();
        var muted = ThemeBrushes.Get("TextMutedBrush", "#6B6B6B");
        var separator = ThemeBrushes.Get("SeparatorBrush", "#D4D4D4");
        var chrome = ThemeBrushes.Get("ChromeBgBrush", "#F3F3F3");

        // ---- 머리말: 아이콘 배지 + 제목 ----
        var badge = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new Avalonia.CornerRadius(19),
            Background = accent,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Viewbox
            {
                Width = 20,
                Height = 20,
                Child = new Canvas
                {
                    Width = 24,
                    Height = 24,
                    Children =
                    {
                        new Avalonia.Controls.Shapes.Path
                        {
                            Data = Geometry.Parse("M12,3 L12,14 M7,9.5 L12,14.5 L17,9.5 M5,19 L19,19"),
                            Stroke = Brushes.White,
                            StrokeThickness = 2.2,
                            StrokeLineCap = PenLineCap.Round,
                            StrokeJoin = PenLineJoin.Round,
                        },
                    },
                },
            },
        };

        var header = new Border
        {
            Background = chrome,
            BorderBrush = separator,
            BorderThickness = new Avalonia.Thickness(0, 0, 0, 1),
            Padding = new Avalonia.Thickness(18, 15),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 13,
                Children =
                {
                    badge,
                    new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "새 버전이 있습니다",
                                FontSize = 15,
                                FontWeight = FontWeight.SemiBold,
                            },
                            new TextBlock
                            {
                                Text = $"Aurum {newVersion}",
                                FontSize = 12.5,
                                Foreground = muted,
                            },
                        },
                    },
                },
            },
        };

        // ---- 버전 이동: 0.3.0 → 0.3.1 ----
        var versions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                VersionChip(current, emphasise: false, accent, separator, chrome, muted),
                new TextBlock
                {
                    Text = "→",
                    FontSize = 14,
                    Foreground = muted,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                VersionChip(newVersion, emphasise: true, accent, separator, chrome, muted),
            },
        };

        var sizeText = isDelta
            ? $"내려받을 용량 {Bytes(downloadBytes)} · 바뀐 부분만 받습니다"
            : $"내려받을 용량 {Bytes(downloadBytes)}";

        var body = new StackPanel
        {
            Margin = new Avalonia.Thickness(18, 14, 18, 16),
            Spacing = 11,
            Children =
            {
                versions,
                new TextBlock { Text = sizeText, FontSize = 12.5, Foreground = muted },
            },
        };

        // ---- 릴리즈 노트 (있을 때만) ----
        if (!string.IsNullOrWhiteSpace(notes))
        {
            body.Children.Add(new Border
            {
                BorderBrush = separator,
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(3),
                Padding = new Avalonia.Thickness(10, 8),
                Child = new ScrollViewer
                {
                    MaxHeight = 150,
                    Content = new SelectableTextBlock
                    {
                        Text = notes.Trim(),
                        FontSize = 12.5,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            });
        }

        body.Children.Add(new TextBlock
        {
            Text = "업데이트하면 Aurum 이 다시 시작됩니다. "
                 + "커밋하지 않은 트랜잭션과 저장하지 않은 편집은 사라집니다.",
            FontSize = 12,
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
        });

        // ---- 진행률 (내려받는 동안만 보인다) ----
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, IsVisible = false };
        var status = new TextBlock { FontSize = 12, Foreground = muted, IsVisible = false };
        body.Children.Add(progress);
        body.Children.Add(status);

        var later = new Button { Content = "Later", MinWidth = 88, MinHeight = 30, IsCancel = true };
        var install = new Button { Content = "Update Now", MinWidth = 118, MinHeight = 30, IsDefault = true };
        install.Classes.Add("accent");
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 3, 0, 0),
            Children = { later, install },
        });

        var dialog = new Window
        {
            Title = "Aurum Update",
            Icon = AppIcon.Shared,
            Width = 452,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = new StackPanel { Children = { header, body } },
        };

        return new DialogParts(dialog, later, install, progress, status);
    }

    /// <summary>
    /// 스크린샷 하니스용 — 실제 릴리즈 없이 창 모양만 찍는다
    /// (README "자가 검증": 화면 회귀는 사람 눈이 아니라 스크린샷으로 잡는다).
    /// </summary>
    internal static Window PreviewWindow() =>
        BuildDialog(
            current: "0.3.1",
            newVersion: "0.4.0",
            downloadBytes: 131_072,
            isDelta: true,
            notes: "- 그리드 편집: ✓ Post 뒤 상단 Commit/Rollback 으로 확정·취소\n"
                 + "- 편집 모드에서 헤더 정렬 유지\n"
                 + "- 셀 상세 창은 하나만 두고 내용을 갈아 끼움").Dialog;

    /// <summary>버전 표시 알약. 새 버전만 강조한다.</summary>
    private static Border VersionChip(
        string version, bool emphasise, IBrush accent, IBrush separator, IBrush chrome, IBrush muted) =>
        new()
        {
            Background = emphasise ? null : chrome,
            BorderBrush = emphasise ? accent : separator,
            BorderThickness = new Avalonia.Thickness(emphasise ? 1.4 : 1),
            CornerRadius = new Avalonia.CornerRadius(3),
            Padding = new Avalonia.Thickness(9, 3),
            Child = new TextBlock
            {
                Text = version,
                FontSize = 13,
                FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace"),
                FontWeight = emphasise ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = emphasise ? accent : muted,
            },
        };

    /// <summary>
    /// 앱 강조색 — Update Now 버튼(<c>accent</c> 클래스)과 배지·칩이 같은 색이어야
    /// 한 화면에 색이 셋씩 나오지 않는다. 테마가 강조색을 안 주면 Golden 파랑으로.
    /// </summary>
    private static IBrush AccentBrush()
    {
        var app = Avalonia.Application.Current;
        if (app is not null &&
            app.TryGetResource("SystemAccentColor", app.ActualThemeVariant, out var value) &&
            value is Color color)
            return new SolidColorBrush(color);
        return ThemeBrushes.Get("FloppyBlueBrush", "#2C6FBB");
    }

    private static string Bytes(long n) => n switch
    {
        >= 1024L * 1024 => $"{n / 1024.0 / 1024:0.#} MB",
        >= 1024 => $"{n / 1024.0:0} KB",
        _ => $"{n} B",
    };
}
