using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PrismOne.Db.Core;
using PrismOne.Db.Core.Providers;

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

    public ConnectDialog() : this(ConnectionProfile.Default) { }

    public ConnectDialog(ConnectionProfile initial)
    {
        InitializeComponent();
        DbTypeCombo.ItemsSource = DbProviders.All.Select(p => p.DisplayName).ToList();
        DbTypeCombo.SelectedIndex = Math.Max(0, DbProviders.All.ToList().FindIndex(p => p.Kind == initial.Kind));
        UsernameBox.Text = initial.Username;
        PasswordBox.Text = initial.Password;
        DatabaseCombo.Text = FormatDatabase(initial.Host, initial.Port, initial.Database, initial.Kind);
        ApplyDbTypeToFields();
        RefreshSavedList();
        if (_saved.Count > 0)
            SavedGrid.SelectedIndex = 0;   // 최근 사용 로그인 기본 선택
        UpdateHeader();

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
            // Save password 체크는 사용자가 끈 경우가 아니면 항상 켜둔다
            // (예전에 비밀번호 없이 저장된 항목을 선택해도 꺼지지 않게)
            ErrorText.IsVisible = false;
            UpdateHeader();
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
        ErrorText.IsVisible = false;
        UpdateHeader();
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

        DatabaseLabel.Text = fileDb ? "File:" : "Database:";
        DatabaseCombo.PlaceholderText = kind switch
        {
            DbKind.Sqlite => "C:\\path\\to\\file.db",
            DbKind.Oracle => "host[:1521]/service",
            _ => "host[:port]/database",
        };
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
                ShowError(kind == DbKind.Oracle
                    ? "Database must be host[:port]/service (e.g. ora-host/ORCLPDB)."
                    : "Database must be host[:port]/database (e.g. db-host/prismone).");
                return;
            }

            profile = new ConnectionProfile(
                host, port, database,
                UsernameBox.Text?.Trim() ?? "",
                PasswordBox.Text ?? "",
                ReadOnly: ReadOnlyBox.IsChecked == true,
                Kind: kind);

            // Golden: 비밀번호 미저장 항목은 비밀번호만 채우면 바로 로그인
            if (string.IsNullOrEmpty(profile.Password))
            {
                ShowError($"Enter password for {profile.Username}:");
                PasswordBox.Focus();
                return;
            }
        }

        ConnectButton.IsEnabled = false;
        try
        {
            // 접속 검증만 하고 닫는다. 실제 세션은 MainWindow/탭이 연다.
            await using var conn = await profile.OpenDbAsync();
            ConnectionStore.Remember(profile, savePassword: true);   // 항상 암호화 저장
            Result = profile;
            Close();
        }
        catch (Exception ex)
        {
            // Golden 처럼 인라인으로도 남기되, 이유를 놓치지 않게 팝업으로도 띄운다
            ShowError(ex.Message);
            await ErrorDialog.ShowAsync(this, "Connection failed",
                $"{profile.Provider.DisplayName} 접속에 실패했습니다.\n{profile.DisplayName}", ex);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
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
            return ("localhost", defaultPort, s);
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

    private static string FormatDatabase(string host, int port, string database, DbKind kind) =>
        kind == DbKind.Sqlite ? database
        : port == SavedConnection.DefaultPort(kind) ? $"{host}/{database}"
        : $"{host}:{port}/{database}";

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
