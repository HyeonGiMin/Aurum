using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PrismOne.Db.Core;

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
        UsernameBox.Text = initial.Username;
        PasswordBox.Text = initial.Password;
        DatabaseCombo.Text = FormatDatabase(initial.Host, initial.Port, initial.Database);
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
        SavedGrid.ItemsSource = _saved;
        DatabaseCombo.ItemsSource = _saved.Select(c => c.DisplayDatabase).Distinct().ToList();
        DeleteButton.IsEnabled = _saved.Count > 0;
    }

    private void OnSavedSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (SavedGrid.SelectedItem is SavedConnection c)
        {
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
        SavePasswordBox.IsChecked = true;
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

        if (ParseDatabase(DatabaseCombo.Text) is not var (host, port, database))
        {
            ShowError("Database must be host[:port]/database (e.g. stg-ihp5022/prismone).");
            return;
        }

        var profile = new ConnectionProfile(
            host, port, database,
            UsernameBox.Text?.Trim() ?? "",
            PasswordBox.Text ?? "",
            ReadOnly: ReadOnlyBox.IsChecked == true);

        // Golden: 비밀번호 미저장 항목은 비밀번호만 채우면 바로 로그인
        if (string.IsNullOrEmpty(profile.Password))
        {
            ShowError($"Enter password for {profile.Username}:");
            PasswordBox.Focus();
            return;
        }

        ConnectButton.IsEnabled = false;
        try
        {
            // 접속 검증만 하고 닫는다. 실제 세션은 MainWindow/탭이 연다.
            await using var conn = await profile.OpenAsync();
            ConnectionStore.Remember(profile, SavePasswordBox.IsChecked == true);
            Result = profile;
            Close();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    /// <summary>"host[:port]/db" → (host, port, db). "db" 만 쓰면 localhost:5432/db.</summary>
    private static (string Host, int Port, string Database)? ParseDatabase(string? text)
    {
        var s = text?.Trim() ?? "";
        if (s.Length == 0) return null;

        var slash = s.IndexOf('/');
        if (slash < 0)
            return ("localhost", 5432, s);
        var left = s[..slash].Trim();
        var db = s[(slash + 1)..].Trim();
        if (left.Length == 0 || db.Length == 0) return null;

        var colon = left.LastIndexOf(':');
        if (colon < 0)
            return (left, 5432, db);
        if (!int.TryParse(left[(colon + 1)..], out var port) || port is <= 0 or > 65535)
            return null;
        return (left[..colon], port, db);
    }

    private static string FormatDatabase(string host, int port, string database) =>
        port == 5432 ? $"{host}/{database}" : $"{host}:{port}/{database}";

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
