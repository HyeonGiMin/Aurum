using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PrismOne.Db.Core;
using PrismOne.Db.Core.Mongo;
using PrismOne.Db.Core.Providers;
using PrismOne.Db.Core.Ssh;

namespace PrismOne.Studio;

/// <summary>
/// Golden 6 "Database Login" 재현 (Ctrl+L).
/// Database 는 host[:port]/database 한 칸(콤보) — Oracle EZConnect 표기의 PG 등가물.
/// 아래 Login List 에서 선택=필드 채움, 더블클릭=바로 로그인.
/// </summary>
public partial class ConnectDialog : Window
{
    public ConnectionProfile? Result { get; private set; }

    private List<SavedConnection> _saved = [];

    /// <summary>
    /// 마지막으로 설정한 SSH 터널. 체크박스를 껐다 켜도 다시 입력하지 않도록 들고 있고,
    /// 실제로 접속에 쓰이는지는 <see cref="EffectiveSsh"/> 가 정한다.
    /// </summary>
    private SshOptions? _ssh;

    public ConnectDialog() : this(ConnectionProfile.Default) { }

    public ConnectDialog(ConnectionProfile initial)
    {
        InitializeComponent();
        DbTypeCombo.ItemsSource = DbProviders.All.Select(p => p.DisplayName).ToList();
        DbTypeCombo.SelectedIndex = Math.Max(0, DbProviders.All.ToList().FindIndex(p => p.Kind == initial.Kind));
        UsernameBox.Text = initial.Username;
        PasswordBox.Text = initial.Password;
        DatabaseCombo.Text = FormatDatabase(initial.Host, initial.Port, initial.Database, initial.Kind);
        _ssh = initial.Ssh;
        SshBox.IsChecked = initial.Ssh is not null;
        ApplyDbTypeToFields();
        RefreshSavedList();
        if (_saved.Count > 0)
            SavedGrid.SelectedIndex = 0;   // 최근 사용 로그인 기본 선택
        UpdateHeader();
        UpdateSshSummary();

        // 편집 가능한 콤보의 텍스트 변경도 헤더에 반영
        DatabaseCombo.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(ComboBox.SelectionBoxItem) || e.Property.Name == "Text")
                UpdateHeader();
        };
    }

    // ---------- Login List ----------

    private void RefreshSavedList()
    {
        _saved = ConnectionStore.Load();
        DatabaseCombo.ItemsSource = _saved.Select(c => c.DisplayDatabase).Distinct().ToList();
        ApplyFilter();
    }

    /// <summary>Golden: Login List 를 Username / Database / Category 로 필터.</summary>
    private void ApplyFilter()
    {
        IEnumerable<SavedConnection> shown = _saved;
        if (FilterRow.IsVisible)
        {
            var user = FilterUserBox.Text?.Trim() ?? "";
            var db = FilterDbBox.Text?.Trim() ?? "";
            var category = FilterCategoryBox.Text?.Trim() ?? "";
            if (user.Length > 0)
                shown = shown.Where(c => c.Username.Contains(user, StringComparison.OrdinalIgnoreCase));
            if (db.Length > 0)
                shown = shown.Where(c => c.DisplayDatabase.Contains(db, StringComparison.OrdinalIgnoreCase));
            if (category.Length > 0)
                shown = shown.Where(c => c.Category?.Contains(category, StringComparison.OrdinalIgnoreCase) == true);
        }
        var list = shown.ToList();
        SavedGrid.ItemsSource = list;
        DeleteButton.IsEnabled = list.Count > 0;
        EditButton.IsEnabled = list.Count > 0;
    }

    private void OnFilterChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

    // ---------- 컬럼 정렬 (Golden 의 헤더 클릭 정렬) ----------

    /// <summary>null 이면 저장 순서(최근 사용순) 그대로 둔다.</summary>
    private string? _sortKey;
    private bool _sortDescending;

    private IEnumerable<SavedConnection> Sort(IEnumerable<SavedConnection> items)
    {
        if (_sortKey is null) return items;

        Func<SavedConnection, string> key = _sortKey switch
        {
            "Type" => c => c.TypeLabel,
            "Username" => c => c.Username,
            "Database" => c => c.DisplayDatabase,
            "Category" => c => c.Category ?? "",
            "Comment" => c => c.Comment ?? "",
            _ => c => c.Name ?? "",
        };
        return _sortDescending
            ? items.OrderByDescending(key, StringComparer.CurrentCultureIgnoreCase)
            : items.OrderBy(key, StringComparer.CurrentCultureIgnoreCase);
    }

    /// <summary>같은 헤더를 다시 누르면 방향이 바뀐다. 세 번째면 정렬 해제(저장 순서).</summary>
    private void OnSortHeader(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;

        if (_sortKey != key) { _sortKey = key; _sortDescending = false; }
        else if (!_sortDescending) { _sortDescending = true; }
        else { _sortKey = null; _sortDescending = false; }

        UpdateSortHeaders();
        ApplyFilter();
    }

    private void UpdateSortHeaders()
    {
        foreach (var button in new[] { SortName, SortType, SortUsername, SortDatabase, SortCategory, SortComment })
        {
            var label = (string)button.Tag!;
            button.Content = _sortKey == label
                ? $"{label} {(_sortDescending ? "▾" : "▴")}"
                : label;
        }
    }

    /// <summary>자가 스크린샷 하니스 전용 — 필터 행을 연 상태로 만든다.</summary>
    public void ShowFilterForShot()
    {
        FilterRow.IsVisible = true;
        FilterButton.Content = "Filter ▴";
        ApplyFilter();
    }

    private void OnToggleFilter(object? sender, RoutedEventArgs e)
    {
        FilterRow.IsVisible = !FilterRow.IsVisible;
        FilterButton.Content = FilterRow.IsVisible ? "Filter ▴" : "Filter ▾";
        if (FilterRow.IsVisible)
            FilterUserBox.Focus();
        ApplyFilter();
    }

    /// <summary>Golden 의 "Editing existing Login Item" — Name/Category/Comment 메타 편집.</summary>
    private async void OnEditSaved(object? sender, RoutedEventArgs e)
    {
        if (SavedGrid.SelectedItem is not SavedConnection c)
        {
            ShowError("Select a login item to edit.");
            return;
        }
        var dialog = new LoginItemDialog(c);
        await dialog.ShowDialog(this);
        if (dialog.Saved)
            RefreshSavedList();
    }

    private void OnSavedSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (SavedGrid.SelectedItem is SavedConnection c)
        {
            // 종류를 먼저 맞춰야 Database 칸 해석(기본 포트·파일 경로)이 어긋나지 않는다
            var index = DbProviders.All.ToList().FindIndex(p => p.Kind == c.Kind);
            if (index >= 0) DbTypeCombo.SelectedIndex = index;
            UsernameBox.Text = c.Username;
            PasswordBox.Text = c.Password ?? "";
            // 편집형 콤보: 항목 선택과 텍스트를 모두 맞춰야 표시가 확실하다
            DatabaseCombo.SelectedItem = (DatabaseCombo.ItemsSource as IEnumerable<string>)?
                .FirstOrDefault(s => s == c.DisplayDatabase);
            DatabaseCombo.Text = c.DisplayDatabase;
            // 터널 설정도 항목에 딸려 온다 — 안 그러면 저장된 접속을 골라도 직접 접속으로 나간다
            _ssh = c.Ssh;
            SshBox.IsChecked = c.Ssh is not null;
            // Save password 체크는 사용자가 끈 경우가 아니면 항상 켜둔다
            // (예전에 비밀번호 없이 저장된 항목을 선택해도 꺼지지 않게)
            ErrorText.IsVisible = false;
            UpdateHeader();
            UpdateSshSummary();
        }
    }

    /// <summary>비밀번호에 한글이 들어오면 두벌식 자판 기준 원래 키(QWERTY)로 자동 변환한다.
    /// 예: IME 를 안 끄고 친 "암호" → "dkagh". 그 외 비ASCII 문자만 제거.</summary>
    private void OnPasswordChanged(object? sender, TextChangedEventArgs e)
    {
        var text = PasswordBox.Text ?? "";
        var converted = HangulQwerty.Convert(text);
        var filtered = new string(converted.Where(c => c >= 0x20 && c < 0x7F).ToArray());
        if (filtered != text)
        {
            PasswordBox.Text = filtered;
            PasswordBox.CaretIndex = filtered.Length;
        }
    }

    private void OnSavedDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (SavedGrid.SelectedItem is not null)
            OnConnect(sender, e);
    }

    private void OnNewLogin(object? sender, RoutedEventArgs e)
    {
        SavedGrid.SelectedItem = null;
        UsernameBox.Text = "";
        PasswordBox.Text = "";
        DatabaseCombo.Text = "";
        ReadOnlyBox.IsChecked = false;
        _ssh = null;
        SshBox.IsChecked = false;
        ErrorText.IsVisible = false;
        UpdateHeader();
        UpdateSshSummary();
        UsernameBox.Focus();
    }

    private void OnDeleteSaved(object? sender, RoutedEventArgs e)
    {
        if (SavedGrid.SelectedItem is SavedConnection c)
        {
            ConnectionStore.Remove(c);
            RefreshSavedList();
        }
    }

    // ---------- Login ----------

    private void OnLoginFieldChanged(object? sender, RoutedEventArgs e) => UpdateHeader();

    /// <summary>지금 고른 DB 종류.</summary>
    private DbKind SelectedKind =>
        DbTypeCombo?.SelectedIndex is int i && i >= 0 && i < DbProviders.All.Count
            ? DbProviders.All[i].Kind
            : DbKind.PostgreSql;

    private void OnDbTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DatabaseCombo is null) return;   // InitializeComponent 중
        ApplyDbTypeToFields();
        UpdateHeader();
    }

    /// <summary>종류마다 필요한 입력이 다르다 — SQLite 는 파일 경로 하나뿐이다.</summary>
    private void ApplyDbTypeToFields()
    {
        var kind = SelectedKind;
        var fileDb = kind == DbKind.Sqlite;

        UsernameBox.IsEnabled = !fileDb;
        PasswordBox.IsEnabled = !fileDb;
        UsernameLabel.Opacity = fileDb ? 0.4 : 1;
        PasswordLabel.Opacity = fileDb ? 0.4 : 1;

        // 파일 DB 는 네트워크로 붙는 게 아니라 터널이 의미가 없다 (DataGrip 도 SQLite 는 안 준다)
        SshBox.IsEnabled = !fileDb;
        SshConfigButton.IsEnabled = !fileDb;
        if (fileDb) SshBox.IsChecked = false;
        UpdateSshSummary();

        DatabaseLabel.Text = fileDb ? "File:" : "Database:";
        DatabaseCombo.PlaceholderText = kind switch
        {
            DbKind.Sqlite => "C:\\path\\to\\file.db",
            DbKind.Oracle => "host[:1521]/service",
            _ => "host[:port]/database",
        };
    }

    // ---------- SSH 터널 ----------

    /// <summary>체크가 켜져 있을 때만 실제로 쓰인다 — 껐다 켜도 설정은 남는다.</summary>
    private SshOptions? EffectiveSsh => SshBox.IsChecked == true ? _ssh : null;

    /// <summary>체크를 켰는데 설정이 없으면 바로 설정 창을 연다 — 빈 채로 켜두면 로그인이 실패한다.</summary>
    private async void OnSshToggled(object? sender, RoutedEventArgs e)
    {
        if (SshBox.IsChecked == true && _ssh is null)
        {
            await ConfigureSshAsync();
            // 설정 없이 창을 닫았으면 체크도 되돌린다
            if (_ssh is null) SshBox.IsChecked = false;
        }
        UpdateSshSummary();
    }

    private async void OnConfigureSsh(object? sender, RoutedEventArgs e) => await ConfigureSshAsync();

    private async Task ConfigureSshAsync(bool askPassword = false)
    {
        // 설정 창의 "Test tunnel" 이 실제로 붙어 볼 DB 주소 — 지금 입력된 값을 그대로 쓴다.
        // 아직 안 적었거나 형식이 틀렸으면 기본값으로 두고, 판정은 로그인 때 한다.
        var kind = SelectedKind;
        var parsed = ParseDatabase(DatabaseCombo.Text, kind);
        var dialog = new SshTunnelDialog(
            _ssh, parsed?.Host ?? "localhost", parsed?.Port ?? SavedConnection.DefaultPort(kind));
        if (askPassword) dialog.AskForPassword();
        await dialog.ShowDialog(this);
        if (!dialog.Confirmed) return;      // Cancel — 아무것도 바꾸지 않는다

        _ssh = dialog.Result;
        SshBox.IsChecked = _ssh is not null;
        ErrorText.IsVisible = false;
        UpdateSshSummary();
    }

    private void UpdateSshSummary()
    {
        SshSummary.Text = EffectiveSsh is { } ssh
            ? $"via {ssh.Describe} [{ssh.AuthLabel}]" + (ssh.NeedsPassword ? " · 비밀번호 필요" : "")
            : "";
        SshConfigButton.Content = _ssh is null ? "Configure…" : "Edit…";
    }

    private void UpdateHeader()
    {
        var user = UsernameBox?.Text?.Trim();
        var db = DatabaseCombo?.Text?.Trim();
        LoginGroupHeader.Text = string.IsNullOrEmpty(user) && string.IsNullOrEmpty(db)
            ? " Login "
            : $" Login: {user}@{db} ";
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private async void OnConnect(object? sender, RoutedEventArgs e)
    {
        ErrorText.IsVisible = false;

        var kind = SelectedKind;

        // 비밀번호를 저장하지 않기로 한 터널이면 지금 채워야 한다 — DB 비밀번호를
        // 다시 묻는 것과 같은 흐름이다(저장된 항목을 고르면 비어 있는 채로 온다).
        if (EffectiveSsh is { NeedsPassword: true })
        {
            await ConfigureSshAsync(askPassword: true);
            if (EffectiveSsh is not { NeedsPassword: false })
            {
                ShowError("SSH 비밀번호를 입력해야 접속할 수 있습니다.");
                return;
            }
        }

        ConnectionProfile profile;

        if (kind == DbKind.Sqlite)
        {
            var path = DatabaseCombo.Text?.Trim() ?? "";
            if (path.Length == 0)
            {
                ShowError("SQLite 는 DB 파일 경로가 필요합니다.");
                return;
            }
            profile = ConnectionProfile.ForFile(path, kind, ReadOnlyBox.IsChecked == true);
        }
        else
        {
            if (ParseDatabase(DatabaseCombo.Text, kind) is not var (host, port, database))
            {
                ShowError(kind switch
                {
                    DbKind.Oracle => "Database must be host[:port]/service (e.g. ora-host/ORCLPDB).",
                    DbKind.MongoDb => "Database must be host[:port] or host[:port]/database "
                                      + "(e.g. mongo-host:27021 — database is optional).",
                    _ => "Database must be host[:port]/database (e.g. db-host/prismone).",
                });
                return;
            }

            profile = new ConnectionProfile(
                host, port, database,
                UsernameBox.Text?.Trim() ?? "",
                PasswordBox.Text ?? "",
                ReadOnly: ReadOnlyBox.IsChecked == true,
                Kind: kind,
                Ssh: EffectiveSsh);

            // Golden: 비밀번호 미저장 항목은 비밀번호만 채우면 바로 로그인
            if (string.IsNullOrEmpty(profile.Password))
            {
                ShowError($"Enter password for {profile.Username}:");
                PasswordBox.Focus();
                return;
            }
        }

        ConnectButton.IsEnabled = false;
        ConnectButton.Content = "Connecting…";
        ConnectProgress.IsVisible = true;
        try
        {
            // 접속 검증만 하고 닫는다. 실제 세션은 MainWindow/탭이 연다.
            await using var conn = await profile.OpenDbAsync();
            ConnectionStore.Remember(profile, savePassword: true);   // 항상 암호화 저장
            Result = profile;
            Close();
        }
        catch (SshTunnelException ex)
        {
            // 터널 실패는 DB 실패와 완전히 다른 문제다 — 사람이 손볼 곳이 SSH 설정이지 DB 가 아니다
            ShowError(ex.Message);
            await ErrorDialog.ShowAsync(this, "SSH tunnel failed",
                $"SSH 터널을 세우지 못해 DB 에 붙지 못했습니다.\n{EffectiveSsh?.Describe}", ex);
        }
        catch (Exception ex)
        {
            // Golden 처럼 인라인으로도 남기되, 이유를 놓치지 않게 팝업으로도 띄운다
            ShowError(ex.Message);
            var target = profile.SshLabel is { } via
                ? $"{profile.DisplayName} ({via})"
                : profile.DisplayName;
            await ErrorDialog.ShowAsync(this, "Connection failed",
                $"{profile.Provider.DisplayName} 접속에 실패했습니다.\n{target}", ex);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            ConnectButton.Content = "Login";
            ConnectProgress.IsVisible = false;
        }
    }

    /// <summary>"host[:port]/db" → (host, port, db). "db" 만 쓰면 localhost:5432/db.</summary>
    /// <summary>
    /// host[:port]/database 파싱. 포트를 안 쓰면 종류별 기본 포트를 넣는다
    /// (PG 5432 · Oracle 1521). Oracle 은 database 자리가 **서비스 이름**이다.
    /// </summary>
    private static (string Host, int Port, string Database)? ParseDatabase(string? text, DbKind kind)
    {
        var s = text?.Trim() ?? "";
        if (s.Length == 0) return null;
        var defaultPort = SavedConnection.DefaultPort(kind);

        var slash = s.IndexOf('/');
        if (slash < 0)
        {
            if (kind == DbKind.MongoDb)
            {
                // Mongo 는 DB 를 안 적어도 되는 게 정상이다 (Studio3T·DataGrip 과 같은 관례) —
                // "/" 가 없는 입력은 통째로 host[:port] 로 본다. 호스트 이름 하나만 쳐도
                // (콜론도 없이) 여기로 오게 해야 한다 — 예전엔 이걸 "DB 이름 하나만 친 것"으로
                // 보고 localhost 에 붙어버려서, 원격 호스트를 적었는데 조용히 로컬로 가는
                // 함정이 있었다. 빈 DB 로 두면 Explorer 가 서버의 DB 를 전부 보여준다.
                var onlyColon = s.LastIndexOf(':');
                if (onlyColon > 0 &&
                    int.TryParse(s[(onlyColon + 1)..], out var onlyPort) && onlyPort is > 0 and <= 65535)
                    return (s[..onlyColon], onlyPort, "");
                return (s, defaultPort, "");
            }
            // PG/Oracle 은 DB·서비스 이름이 반드시 필요하다 — 콜론이 있으면 host:port 로,
            // 아니면 문서화된 관례대로 "이름 하나만 치면 localhost/그 이름" 이다.
            var colonOnly = s.LastIndexOf(':');
            if (colonOnly > 0 &&
                int.TryParse(s[(colonOnly + 1)..], out var portOnly) && portOnly is > 0 and <= 65535)
                return null;
            return ("localhost", defaultPort, s);
        }
        var left = s[..slash].Trim();
        var db = s[(slash + 1)..].Trim();
        if (left.Length == 0 || db.Length == 0) return null;

        var colon = left.LastIndexOf(':');
        if (colon < 0)
            return (left, defaultPort, db);
        if (!int.TryParse(left[(colon + 1)..], out var port) || port is <= 0 or > 65535)
            return null;
        return (left[..colon], port, db);
    }

    private static string FormatDatabase(string host, int port, string database, DbKind kind)
    {
        if (kind == DbKind.Sqlite) return database;
        var hostPort = port == SavedConnection.DefaultPort(kind) ? host : $"{host}:{port}";
        // Mongo 는 DB 가 빈 값일 수 있다 — 그때는 슬래시도 안 붙인다("host:port/" 로
        // 어정쩡하게 보이지 않도록).
        return database.Length == 0 ? hostPort : $"{hostPort}/{database}";
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
