using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PrismOne.Db.Core.Ssh;

namespace PrismOne.Studio;

/// <summary>
/// 호스트 키 승인 창 (pgAdmin·OpenSSH 와 같은 규칙).
///
/// - **처음 보는 호스트** — 지문을 보여주고 accept/reject 를 묻는다. 기본은 거부.
/// - **키가 바뀜** — 알던 지문과 받은 지문을 나란히 보여주는 강한 경고. 기본은 거부.
///
/// 지문을 보여주는 이유는 사용자가 <b>다른 경로로</b>(서버 관리자·`ssh-keygen -lf`) 받은
/// 값과 눈으로 대조하라는 것이다. 그러라고 `ssh` 명령과 같은 표기(SHA256:…)를 쓴다.
/// </summary>
public static class HostKeyDialog
{
    /// <summary>
    /// <see cref="SshTunnelPool.HostKeyPrompt"/> 에 걸 핸들러.
    ///
    /// SSH 핸드셰이크 스레드(스레드 풀)에서 동기로 불린다 — UI 스레드로 넘겨 <b>기다린다</b>.
    /// 터널 접속 자체가 스레드 풀에서 도므로 UI 를 막지 않는다.
    /// </summary>
    public static bool Prompt(HostKeyRequest request)
    {
        // 여기서 막히면(=UI 스레드에서 불렸다면) 창을 띄우는 순간 교착이다. 조용히 멈추는
        // 대신 크게 터뜨린다 — 터널 오류로 올라가 원인이 보인다.
        if (Dispatcher.UIThread.CheckAccess())
            throw new InvalidOperationException(
                "호스트 키 물음이 UI 스레드에서 불렸습니다 — 터널 접속은 스레드 풀에서 돌아야 합니다.");

        var answered = new TaskCompletionSource<bool>();
        Dispatcher.UIThread.Post(async () =>
        {
            try { answered.TrySetResult(await ShowAsync(request)); }
            catch (Exception) { answered.TrySetResult(false); }   // 창을 못 띄우면 안전한 쪽(거부)
        });
        return answered.Task.GetAwaiter().GetResult();
    }

    /// <summary>창을 띄울 부모 — 지금 활성인 창, 없으면 메인 창.</summary>
    private static Window? Owner()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime desktop) return null;
        return desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
    }

    private static async Task<bool> ShowAsync(HostKeyRequest request)
    {
        var changed = request.Verdict.Trust == HostKeyTrust.Mismatch;
        var key = request.Key;
        var target = key.Port == SshOptions.DefaultPort ? key.Host : $"{key.Host}:{key.Port}";

        var accepted = false;

        var headline = new TextBlock
        {
            Text = changed
                ? "⚠ SSH 호스트 키가 바뀌었습니다"
                : "처음 접속하는 SSH 호스트입니다",
            FontWeight = FontWeight.SemiBold,
            FontSize = 14.5,
            // 경고일 때만 색을 준다 — 평상시엔 테마 기본색을 그대로 써야 다크에서도 읽힌다
            Foreground = changed ? Brushes.OrangeRed : (IBrush?)null,
            TextWrapping = TextWrapping.Wrap,
        };

        var body = new TextBlock
        {
            Text = changed
                ? $"{target} 의 호스트 키가 전에 알던 것과 다릅니다.\n"
                  + "서버를 다시 설치했다면 정상일 수 있지만, 누군가 중간에서 가로채고 있을 수도 있습니다. "
                  + "서버 관리자에게 지문을 확인하기 전에는 받아들이지 마세요."
                : $"{target} 의 신원을 확인할 수 없습니다.\n"
                  + "아래 지문이 서버 관리자에게 받은 값과 같은지 확인하세요 "
                  + "(서버에서 `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` 로 얻을 수 있습니다).",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Avalonia.Thickness(0, 8, 0, 10),
        };

        var detail = new StackPanel { Spacing = 4 };
        detail.Children.Add(Row("받은 키", $"{key.KeyType}  {key.Fingerprint}", emphasise: changed));
        if (changed)
            foreach (var known in request.Verdict.KnownFingerprints)
                detail.Children.Add(Row("알던 키", known, emphasise: false));

        var accept = new Button
        {
            Content = changed ? "그래도 받아들이고 갱신" : "받아들이고 기억",
            MinWidth = 150,
            MinHeight = 30,
        };
        var reject = new Button
        {
            Content = "거부 (접속 중단)",
            MinWidth = 130,
            MinHeight = 30,
            IsDefault = true,     // 기본은 언제나 거부 — Enter 를 눌러도 안전한 쪽으로 간다
            IsCancel = true,
        };
        if (!changed) accept.Classes.Add("accent");

        var dialog = new Window
        {
            Title = changed ? "SSH host key changed" : "Unknown SSH host",
            Icon = AppIcon.Shared,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        accept.Click += (_, _) => { accepted = true; dialog.Close(); };
        reject.Click += (_, _) => { accepted = false; dialog.Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 14, 0, 0),
        };
        buttons.Children.Add(accept);
        buttons.Children.Add(reject);

        var root = new StackPanel { Margin = new Avalonia.Thickness(18) };
        root.Children.Add(headline);
        root.Children.Add(body);
        root.Children.Add(detail);
        root.Children.Add(buttons);
        dialog.Content = root;

        if (Owner() is { } owner) await dialog.ShowDialog(owner);
        else { dialog.Show(); await WaitForCloseAsync(dialog); }

        return accepted;
    }

    /// <summary>부모 창이 없을 때(하니스 등) — 창이 닫힐 때까지 기다린다.</summary>
    private static Task WaitForCloseAsync(Window dialog)
    {
        var done = new TaskCompletionSource();
        dialog.Closed += (_, _) => done.TrySetResult();
        return done.Task;
    }

    private static Control Row(string label, string value, bool emphasise)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("74,*"),
        };
        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var fingerprint = new SelectableTextBlock
        {
            Text = value,
            FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace"),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = emphasise ? Brushes.OrangeRed : (IBrush?)null,
        };
        Grid.SetColumn(fingerprint, 1);
        grid.Children.Add(fingerprint);
        return grid;
    }
}
