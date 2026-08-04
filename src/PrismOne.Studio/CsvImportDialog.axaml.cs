using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PrismOne.Db.Core;

namespace PrismOne.Studio;

/// <summary>
/// Tools &gt; Import CSV/TSV — 파일을 테이블로 (DATAGRIP_GAP §5).
///
/// 헤더를 테이블 컬럼에 이름으로 매핑하고(대소문자 무시), 값은 전부 문자열로 보내
/// 서버가 타입을 정한다. **전량 성공 아니면 전량 롤백** — Run and Edit 의 원칙과 같다.
/// 실행은 탭과 무관한 전용 접속에서 하므로 진행 중인 쿼리를 방해하지 않는다.
/// </summary>
public partial class CsvImportDialog : Window
{
    private readonly ConnectionProfile _profile;
    private readonly SchemaCache _cache;
    private List<string[]> _parsed = [];
    private string _rawText = "";
    private CsvMapping? _mapping;

    private sealed record TableChoice(TableInfo Info)
    {
        public override string ToString() => Info.IsView ? $"{Info.Schema}.{Info.Name} (view)" : $"{Info.Schema}.{Info.Name}";
    }

    public CsvImportDialog() : this(ConnectionProfile.Default, new SchemaCache(_ => Task.FromResult(SchemaSnapshot.Empty))) { }

    public CsvImportDialog(ConnectionProfile profile, SchemaCache cache)
    {
        InitializeComponent();
        _profile = profile;
        _cache = cache;
        Title = $"Import CSV/TSV - {profile.DisplayName}";
        Opened += async (_, _) => await LoadTablesAsync();
    }

    private async Task LoadTablesAsync()
    {
        try
        {
            var tables = await _cache.GetTablesAsync();
            // 뷰에는 insert 할 수 없다 — 목록에서 뺀다
            TableCombo.ItemsSource = tables.Where(t => !t.IsView).Select(t => new TableChoice(t)).ToList();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"테이블 목록을 읽지 못했습니다: {ex.Message}";
        }
    }

    private async void OnPickFile(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "CSV/TSV 파일 선택",
            FileTypeFilter =
            [
                new FilePickerFileType("CSV/TSV") { Patterns = ["*.csv", "*.tsv", "*.txt"] },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count == 0) return;
        try
        {
            LoadText(files[0].Name, await System.IO.File.ReadAllTextAsync(files[0].Path.LocalPath));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"파일을 읽지 못했습니다: {ex.Message}";
        }
    }

    /// <summary>파일 내용 적재 — 스크린샷 하니스도 이 경로로 들어온다.</summary>
    internal void LoadText(string fileName, string text)
    {
        _rawText = text;
        FileLabel.Text = fileName;
        Reparse();
    }

    private char? SelectedDelimiter => DelimiterCombo.SelectedIndex switch
    {
        1 => ',',
        2 => '\t',
        3 => ';',
        _ => null,   // 자동
    };

    private void OnOptionsChanged(object? sender, SelectionChangedEventArgs e) => Reparse();
    private void OnHeaderChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Reparse();

    private void Reparse()
    {
        if (_rawText.Length == 0) return;
        var delimiter = SelectedDelimiter ?? CsvParser.DetectDelimiter(_rawText);
        _parsed = CsvParser.Parse(_rawText, delimiter);
        RebuildMapping();
        RebuildPreview(delimiter);
    }

    private IReadOnlyList<ColumnInfo> TargetColumns { get; set; } = [];

    private async void RebuildMapping()
    {
        _mapping = null;
        ImportButton.IsEnabled = false;
        if (_parsed.Count == 0 || (TableCombo.SelectedItem as TableChoice)?.Info is not { } table)
        {
            MappingText.Text = "파일과 테이블을 고르면 헤더 ↔ 컬럼 매핑을 보여줍니다.";
            return;
        }
        try
        {
            TargetColumns = await _cache.GetColumnsAsync(table);
        }
        catch (Exception ex)
        {
            MappingText.Text = $"컬럼을 읽지 못했습니다: {ex.Message}";
            return;
        }

        var useHeader = HeaderCheck.IsChecked == true;
        _mapping = useHeader
            ? CsvImporter.MapByHeader(_parsed[0], TargetColumns)
            : CsvImporter.MapByPosition(_parsed[0].Length, TargetColumns);
        var dataRows = _parsed.Count - (useHeader ? 1 : 0);

        var text = $"매핑 {_mapping.Columns.Count}/{TargetColumns.Count} 컬럼 · 데이터 {dataRows:N0}행";
        if (_mapping.UnmatchedHeaders.Count > 0)
            text += $" · 무시되는 헤더: {string.Join(", ", _mapping.UnmatchedHeaders)}";
        var notCovered = TargetColumns
            .Where(c => c.Nullable == "no" && _mapping.Columns.All(m => m.Column.Name != c.Name))
            .Select(c => c.Name).ToList();
        if (notCovered.Count > 0)
            text += $" · 파일에 없는 NOT NULL 컬럼: {string.Join(", ", notCovered)} (기본값 없으면 실패합니다)";
        MappingText.Text = text;

        ImportButton.IsEnabled = _mapping.Columns.Count > 0 && dataRows > 0;
    }

    private void RebuildPreview(char delimiter)
    {
        var items = new List<Control>();
        foreach (var (row, index) in _parsed.Take(15).Select((r, i) => (r, i)))
        {
            items.Add(new TextBlock
            {
                Text = string.Join("  |  ", row.Select(c => c.Length > 40 ? c[..40] + "…" : c)),
                FontWeight = index == 0 && HeaderCheck.IsChecked == true ? FontWeight.Bold : FontWeight.Normal,
            });
        }
        if (_parsed.Count > 15)
            items.Add(new TextBlock { Text = $"… 외 {_parsed.Count - 15:N0}행", Opacity = 0.6 });
        PreviewList.ItemsSource = items;
        StatusText.Text = $"구분자: {(delimiter == '\t' ? "탭" : delimiter.ToString())} · " +
                          "전량 성공 아니면 전량 롤백입니다.";
    }

    private async void OnImport(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_mapping is null || (TableCombo.SelectedItem as TableChoice)?.Info is not { } table)
            return;
        var rows = HeaderCheck.IsChecked == true ? _parsed.Skip(1).ToList() : _parsed;
        ImportButton.IsEnabled = false;
        try
        {
            StatusText.Text = "Import 중…";
            // 공유 세션을 건드리지 않게 전용 접속으로 실행한다
            await using var session = await QuerySession.CreateAsync(_profile);
            var progress = new Progress<int>(n => StatusText.Text = $"Import 중… {n:N0}/{rows.Count:N0}행");
            var result = await CsvImporter.RunAsync(
                session, table.Schema, table.Name, _mapping, rows,
                EmptyNullCheck.IsChecked == true, progress);

            StatusText.Text = result.Success
                ? $"완료 — {result.Inserted:N0}행을 {table.Schema}.{table.Name} 에 넣고 커밋했습니다."
                : $"실패 (전량 롤백) — {result.ErrorRow}번째 행: {result.Error}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"실패: {ex.Message}";
        }
        finally
        {
            ImportButton.IsEnabled = true;
        }
    }

    private void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
