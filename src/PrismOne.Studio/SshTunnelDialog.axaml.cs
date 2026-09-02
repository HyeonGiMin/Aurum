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

    /// <summary>순서가 <see cref="SshAuthMode"/> 값 순서와 같아야 한다 (인덱스로 변환한다).</summary>
    private static readonly List<string> AuthLabels =
        ["Password", "Private key", "ssh-agent", "OpenSSH config (~/.ssh/config)"];

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
        AuthCombo.SelectedIndex = (int)ssh.AuthMode;
        PasswordBox.Text = ssh.Password ?? "";
        KeyPathBox.Text = ssh.PrivateKeyPath ?? "";
        PassphraseBox.Text = ssh.Passphrase ?? "";
        SavePasswordBox.IsChecked = ssh.SavePassword;
        ProxyJumpBox.Text = ssh.ProxyJump ?? "";
        // OpenSSH config 모드에서 Host 칸에 별칭을 자동완성해 준다 (~/.ssh/config 의 Host 들).
        HostBox.ItemsSource = SshConfig.Exists ? SshConfig.Aliases() : null;

        TargetHint.Text = $"DB 주소 {dbHost}:{dbPort} 는 이 서버에서 본 주소로 해석됩니다.";
        ClearButton.IsEnabled = initial is not null;

        ApplyAuthMode();
        // Host 칸을 고치면 config 요약을 다시 계산한다. AutoCompleteBox 의 TextChanged 는
        // 버전마다 시그니처가 달라, ConnectDialog 처럼 PropertyChanged 로 붙인다.
        HostBox.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == "Text") UpdateAuthHint();
        };
        HostBox.Focus();
    }

    private SshAuthMode SelectedAuth =>
        AuthCombo.SelectedIndex >= 0 && AuthCombo.SelectedIndex < AuthLabels.Count
            ? (SshAuthMode)AuthCombo.SelectedIndex
            : SshAuthMode.Password;

    private void OnAuthModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PasswordBox is null) return;   // InitializeComponent 중
        ApplyAuthMode();
    }



    /// <summary>안 쓰는 칸은 비활성 — "적었는데 무시되는" 상태를 만들지 않는다.</summary>
    private void ApplyAuthMode()
    {
        var mode = SelectedAuth;
        var usesPassword = mode == SshAuthMode.Password;
        // OpenSSH config 는 설정이 짚어 준 키를 쓰지만, 사용자가 직접 덮어쓸 수도 있게 열어 둔다.
        var usesKey = mode is SshAuthMode.PrivateKey or SshAuthMode.OpenSshConfig;
        var fromConfig = mode == SshAuthMode.OpenSshConfig;

        PasswordBox.IsEnabled = usesPassword;
        PasswordLabel.Opacity = usesPassword ? 1 : 0.4;

        KeyPathBox.IsEnabled = usesKey;
        BrowseButton.IsEnabled = usesKey;
        PassphraseBox.IsEnabled = usesKey;
        KeyLabel.Opacity = usesKey ? 1 : 0.4;
        PassphraseLabel.Opacity = usesKey ? 1 : 0.4;

        // 비밀을 안 다루는 방식이면 저장 체크가 의미 없다.
        SavePasswordBox.IsVisible = mode is SshAuthMode.Password or SshAuthMode.PrivateKey;

        HostLabel.Text = fromConfig ? "Host alias:" : "SSH host:";
        HostBox.Watermark = fromConfig ? "prod-db (~/.ssh/config 의 Host)" : "jump.example.com";
        KeyLabel.Text = fromConfig ? "Key (덮어쓰기):" : "Private key:";

        UpdateAuthHint();
    }

    /// <summary>
    /// 고른 방식이 실제로 될 상태인지 미리 알려준다 — agent 가 안 떠 있거나 config 에
    /// 그 별칭이 없으면 Login 을 눌러 봐야 알게 되는 게 최악이다.
    /// </summary>
    private void UpdateAuthHint()
    {
        string? hint = SelectedAuth switch
        {
            SshAuthMode.Agent => SshAgent.IsAvailable
                ? "ssh-agent 의 키로 인증합니다. 비밀은 저장하지 않습니다."
                : "⚠ " + SshAgent.UnavailableReason,

            SshAuthMode.OpenSshConfig when !SshConfig.Exists =>
                $"⚠ {SshConfig.FilePath} 가 없습니다.",

            SshAuthMode.OpenSshConfig => DescribeConfig(),

            _ => null,
        };

        AuthHint.Text = hint ?? "";
        AuthHint.IsVisible = hint is not null;
    }

    /// <summary>별칭을 실제로 풀어 보여준다 — 무엇이 자동으로 채워지는지 눈에 보이게.</summary>
    private string DescribeConfig()
    {
        var alias = (HostBox.Text ?? "").Trim();
        if (alias.Length == 0) return "~/.ssh/config 의 Host 별칭을 입력하세요.";
        var resolved = SshConfig.Resolve(alias);
        return resolved.HasSettings
            ? $"{alias} → {resolved.Summary}"
            : $"⚠ ~/.ssh/config 에 '{alias}' 항목이 없습니다 — 호스트 이름으로 그대로 붙습니다.";
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

        var usesKey = SelectedAuth is SshAuthMode.PrivateKey or SshAuthMode.OpenSshConfig;
        var keyPath = usesKey ? (KeyPathBox.Text ?? "").Trim() : null;
        var proxyJump = (ProxyJumpBox.Text ?? "").Trim();

        var options = new SshOptions(
            (HostBox.Text ?? "").Trim(),
            port,
            (UserBox.Text ?? "").Trim(),
            SelectedAuth,
            SelectedAuth == SshAuthMode.Password ? PasswordBox.Text ?? "" : null,
            string.IsNullOrEmpty(keyPath) ? null : keyPath,
            usesKey ? PassphraseBox.Text ?? "" : null,
            SavePasswordBox.IsChecked == true,
            proxyJump.Length == 0 ? null : proxyJump);

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

    /// <summary>
    /// 저장을 꺼 둔 접속으로 로그인할 때 — "비밀번호만 채워 주세요" 상태로 연다.
    /// 로그온 창의 DB 비밀번호 재입력과 같은 흐름이다.
    /// </summary>
    public void AskForPassword()
    {
        ShowStatus("이 접속은 SSH 비밀번호를 저장하지 않습니다. 비밀번호를 입력하세요.", error: false);
        PasswordBox.Focus();
    }

    private void ShowStatus(string message, bool error)
    {
        StatusText.Text = message;
        StatusText.Foreground = error ? Brushes.OrangeRed : Brushes.SeaGreen;
        StatusText.IsVisible = true;
    }
}
