using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PrismOne.Db.Core;

namespace PrismOne.Studio;

/// <summary>Golden 의 Options 다이얼로그 — fetch 크기·행수 상한·NULL 표시·timeout.</summary>
public partial class OptionsDialog : Window
{
    public AppOptions? Result { get; private set; }

    public OptionsDialog() : this(new AppOptions()) { }

    public OptionsDialog(AppOptions current)
    {
        InitializeComponent();
        FetchBatchBox.Text = current.FetchBatch.ToString(CultureInfo.InvariantCulture);
        LimitBox.Text = current.RecordsetLimit.ToString(CultureInfo.InvariantCulture);
        NullTextBox.Text = current.NullText;
        TimeoutBox.Text = current.StatementTimeoutMs.ToString(CultureInfo.InvariantCulture);
        AutoCommitOption.IsChecked = current.AutoCommit;
        AllowNonSelectFavoritesOption.IsChecked = current.AllowNonSelectFavorites;
        FetchAllOnExecuteOption.IsChecked = current.FetchAllOnExecute;
        CountTotalRecordsOption.IsChecked = current.CountTotalRecords;
        ThemeCombo.SelectedIndex = current.Theme switch { "Dark" => 1, "System" => 2, _ => 0 };
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var options = new AppOptions
        {
            FetchBatch = ParseInt(FetchBatchBox.Text, 100, min: 10, max: 100_000),
            RecordsetLimit = ParseInt(LimitBox.Text, -1, min: -1, max: 10_000_000),
            NullText = NullTextBox.Text ?? "",
            StatementTimeoutMs = ParseInt(TimeoutBox.Text, 0, min: 0, max: 86_400_000),
            AutoCommit = AutoCommitOption.IsChecked == true,
            AllowNonSelectFavorites = AllowNonSelectFavoritesOption.IsChecked == true,
            CountTotalRecords = CountTotalRecordsOption.IsChecked == true,
            FetchAllOnExecute = FetchAllOnExecuteOption.IsChecked == true,
            Theme = ThemeCombo.SelectedIndex switch { 1 => "Dark", 2 => "System", _ => "Light" },
        };
        options.Save();
        App.ApplyTheme(options.Theme);
        Result = options;
        Close();
    }

    private static int ParseInt(string? text, int fallback, int min, int max) =>
        int.TryParse(text, out var value) && value >= min && value <= max ? value : fallback;
}
