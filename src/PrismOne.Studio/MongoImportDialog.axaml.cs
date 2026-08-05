using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using PrismOne.Db.Core;
using PrismOne.Db.Core.Mongo;
using PrismOne.Db.Core.Providers;

namespace PrismOne.Studio;

/// <summary>
/// Tools &gt; Import JSON (Mongo) — 파일을 컬렉션으로 (Studio3T 의 Import 대응).
/// 스키마가 없으므로 CSV Import 와 달리 컬럼 매핑이 필요 없다 — 파싱한 문서를
/// 그대로 InsertMany 한다. 실행은 탭과 무관한 전용 접속에서 한다.
/// </summary>
public partial class MongoImportDialog : Window
{
    private readonly ConnectionProfile _profile;
    private IReadOnlyList<BsonDocument> _documents = [];

    public MongoImportDialog() : this(ConnectionProfile.Default with { Kind = DbKind.MongoDb }) { }

    public MongoImportDialog(ConnectionProfile profile)
    {
        InitializeComponent();
        _profile = profile;
        Title = $"Import JSON - {profile.DisplayName}";
        if (!string.IsNullOrEmpty(profile.Database))
            DatabaseBox.Text = profile.Database;
    }

    private async void OnPickFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "JSON 파일 선택",
            FileTypeFilter =
            [
                new FilePickerFileType("JSON") { Patterns = ["*.json", "*.jsonl", "*.txt"] },
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
            SummaryText.Text = $"파일을 읽지 못했습니다: {ex.Message}";
        }
    }

    /// <summary>파일 내용 적재 — 스크린샷 하니스도 이 경로로 들어온다.</summary>
    internal void LoadText(string fileName, string text)
    {
        FileLabel.Text = fileName;
        try
        {
            _documents = MongoJsonImport.Parse(text);
        }
        catch (MongoQueryException ex)
        {
            _documents = [];
            SummaryText.Text = $"파싱 실패: {ex.Message}";
            PreviewList.ItemsSource = null;
            UpdateImportEnabled();
            return;
        }
        RebuildPreview();
    }

    private void RebuildPreview()
    {
        var settings = new JsonWriterSettings { OutputMode = JsonOutputMode.RelaxedExtendedJson };
        var items = _documents.Take(15)
            .Select(d => (Control)new TextBlock { Text = d.ToJson(settings) })
            .ToList();
        if (_documents.Count > 15)
            items.Add(new TextBlock { Text = $"… 외 {_documents.Count - 15:N0}개", Opacity = 0.6 });
        PreviewList.ItemsSource = items;

        SummaryText.Text = _documents.Count == 0
            ? "문서를 하나도 못 읽었습니다."
            : $"{_documents.Count:N0}개 문서를 읽었습니다.";
        UpdateImportEnabled();
    }

    private void OnTargetChanged(object? sender, RoutedEventArgs e) => UpdateImportEnabled();

    private void UpdateImportEnabled() =>
        ImportButton.IsEnabled = _documents.Count > 0
            && !string.IsNullOrWhiteSpace(DatabaseBox.Text)
            && !string.IsNullOrWhiteSpace(CollectionBox.Text);

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        UpdateImportEnabled();
        if (!ImportButton.IsEnabled) return;
        var database = DatabaseBox.Text!.Trim();
        var collection = CollectionBox.Text!.Trim();

        ImportButton.IsEnabled = false;
        ImportProgress.IsIndeterminate = true;
        ImportProgress.IsVisible = true;
        try
        {
            StatusText.Text = "Import 중…";
            // 공유 세션(탭)을 건드리지 않게 전용 접속을 새로 연다
            var targetProfile = _profile with { Database = database };
            await using var connection =
                (MongoDbConnection)await DbProviders.For(DbKind.MongoDb).OpenAsync(targetProfile);
            var inserted = await connection.InsertManyDocumentsAsync(database, collection, _documents);

            StatusText.Text = $"완료 — {inserted:N0}개 문서를 {database}.{collection} 에 넣었습니다.";
            Toast.Show(this, "Import 완료", $"{database}.{collection} — {inserted:N0}개");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"실패: {ex.Message}";
        }
        finally
        {
            ImportButton.IsEnabled = true;
            ImportProgress.IsVisible = false;
            ImportProgress.IsIndeterminate = false;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
