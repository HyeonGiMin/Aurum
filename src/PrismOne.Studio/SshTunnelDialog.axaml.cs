using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PrismOne.Db.Core.Ssh;

namespace PrismOne.Studio;

/// <summary>
/// DataGrip 의 "SSH/SSL &gt; SSH tunnel" 대응. 점프 호스트와 인증 방법을 받는다.
///
/// 여기서 정한 값은 로그온 창이 <c>ConnectionProfile.Ssh</c> 로 실어 보내고,
/// 실제 터널은 접속할 때 <see cref="SshTunnelPool"/> 이 세운다.
/// **Test tunnel** 은 DB 는 건드리지 않고 SSH 접속과 포워딩만 확인한다 —
/// "SSH 가 안 되는 건지 DB 가 안 되는 건지" 를 먼저 갈라 주기 위해서다.
/// </summary>
public partial class SshTunnelDialog : Window
{
    /// <summary>OK 로 닫혔을 때만 채워진다. "Don't use SSH" 로 닫으면 null 이 담긴다.</summary>
    public SshOptions? Result { get; private set; }

    /// <summary>OK 나 "Don't use SSH" 로 닫혔는지 — Cancel 과 구분한다.</summary>
    public bool Confirmed { get; private set; }

    /// <summary>"Test tunnel" 이 실제로 붙어 볼 DB 주소 (SSH 서버에서 본 주소).</summary>
    private readonly string _dbHost;
    private readonly int _dbPort;

    private static readonly List<string> AuthLabels = ["Password", "Private key"];

    public SshTunnelDialog() : this(null, "localhost", 5432) { }

    public SshTunnelDialog(SshOptions? initial, string dbHost, int dbPort)
    {
        _dbHost = dbHost;
        _dbPort = dbPort;
        InitializeComponent();

        AuthCombo.ItemsSource = AuthLabels;
        var ssh = initial ?? SshOptions.Empty;
        HostBox.Text = ssh.Host;
        PortBox.Text = ssh.Port > 0 ? ssh.Port.ToString() : SshOptions.DefaultPort.ToString();
        UserBox.Text = ssh.Username;
        AuthCombo.SelectedIndex = ssh.AuthMode == SshAuthMode.PrivateKey ? 1 : 0;
        PasswordBox.Text = ssh.Password ?? "";
        KeyPathBox.Text = ssh.PrivateKeyPath ?? "";
        PassphraseBox.Text = ssh.Passphrase ?? "";

        TargetHint.Text = $"DB 주소 {dbHost}:{dbPort} 는 이 서버에서 본 주소로 해석됩니다.";
        ClearButton.IsEnabled = initial is not null;

        ApplyAuthMode();
        HostBox.Focus();
    }

    private SshAuthMode SelectedAuth =>
        AuthCombo.SelectedIndex == 1 ? SshAuthMode.PrivateKey : SshAuthMode.Password;

    private void OnAuthModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PasswordBox is null) return;   // InitializeComponent 중
        ApplyAuthMode();
    }

    /// <summary>안 쓰는 칸은 비활성 — "적었는데 무시되는" 상태를 만들지 않는다.</summary>
    private void ApplyAuthMode()
    {
        var key = SelectedAuth == SshAuthMode.PrivateKey;

        PasswordBox.IsEnabled = !key;
        PasswordLabel.Opacity = key ? 0.4 : 1;

        KeyPathBox.IsEnabled = key;
        BrowseButton.IsEnabled = key;
        PassphraseBox.IsEnabled = key;
        KeyLabel.Opacity = key ? 1 : 0.4;
        PassphraseLabel.Opacity = key ? 1 : 0.4;
    }

    private async void OnBrowseKey(object? sender, RoutedEventArgs e)
    {
        // 개인키는 확장자가 없는 게 보통이라(id_ed25519) 필터를 걸지 않는다.
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Private key",
            AllowMultiple = false,
            SuggestedStartLocation = await DefaultSshFolderAsync(),
        });
        if (files.Count > 0)
            KeyPathBox.Text = files[0].Path.LocalPath;
    }

    /// <summary>키는 거의 항상 ~/.ssh 에 있다 — 없으면 그냥 기본 위치에서 연다.</summary>
    private async Task<IStorageFolder?> DefaultSshFolderAsync()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
            return Directory.Exists(path) ? await StorageProvider.TryGetFolderFromPathAsync(path) : null;
        }
        catch
        {
            return null;   // 접근이 막힌 환경 — 기본 위치로 둔다
        }
    }

    /// <summary>입력을 읽어 옵션으로 만든다. 형식이 틀리면 이유를 띄우고 null.</summary>
    private SshOptions? ReadOptions()
    {
        var portText = (PortBox.Text ?? "").Trim();
        if (portText.Length == 0) portText = SshOptions.DefaultPort.ToString();
        if (!int.TryParse(portText, out var port))
        {
            ShowStatus("SSH 포트는 숫자여야 합니다.", error: true);
            return null;
        }

        var options = new SshOptions(
            (HostBox.Text ?? "").Trim(),
            port,
            (UserBox.Text ?? "").Trim(),
            SelectedAuth,
            SelectedAuth == SshAuthMode.Password ? PasswordBox.Text ?? "" : null,
            SelectedAuth == SshAuthMode.PrivateKey ? (KeyPathBox.Text ?? "").Trim() : null,
            SelectedAuth == SshAuthMode.PrivateKey ? PassphraseBox.Text ?? "" : null);

        if (options.Validate() is { } problem)
        {
            ShowStatus(problem, error: true);
            return null;
        }
        return options;
    }

    /// <summary>
    /// SSH 접속과 포워딩만 확인한다 — DB 에는 붙지 않는다.
    /// 성공하면 이후 로그인은 이 터널을 그대로 재사용한다(풀이 붙잡고 있다).
    /// </summary>
    private async void OnTest(object? sender, RoutedEventArgs e)
    {
        if (ReadOptions() is not { } options) return;

        TestButton.IsEnabled = false;
        OkButton.IsEnabled = false;
        TestProgress.IsVisible = true;
        ShowStatus("SSH 접속을 확인하는 중…", error: false);
        try
        {
            var probe = new PrismOne.Db.Core.ConnectionProfile(
                _dbHost, _dbPort, "", "", "", Ssh: options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (tunneled, lease) = await SshTunnelPool.LeaseAsync(probe, cts.Token);
            lease.Dispose();   // 확인만 — 붙잡아 두지 않는다(유휴 타이머가 정리한다)
            ShowStatus($"OK — {options.Describe} 를 거쳐 {_dbHost}:{_dbPort} 로 가는 터널이 열렸습니다 "
                       + $"(로컬 {tunneled.Host}:{tunneled.Port}).", error: false);
        }
        catch (Exception ex)
        {
            ShowStatus(ex is SshTunnelException ? ex.Message : $"터널 확인 실패: {ex.Message}", error: true);
        }
        finally
        {
            TestButton.IsEnabled = true;
            OkButton.IsEnabled = true;
            TestProgress.IsVisible = false;
        }
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (ReadOptions() is not { } options) return;
        Result = options;
        Confirmed = true;
        Close();
    }

    /// <summary>SSH 를 끄고 직접 접속으로 되돌린다.</summary>
    private void OnClear(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Confirmed = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void ShowStatus(string message, bool error)
    {
        StatusText.Text = message;
        StatusText.Foreground = error ? Brushes.OrangeRed : Brushes.SeaGreen;
        StatusText.IsVisible = true;
    }
}
