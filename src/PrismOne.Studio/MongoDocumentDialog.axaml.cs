using Avalonia.Controls;
using Avalonia.Interactivity;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace PrismOne.Studio;

/// <summary>
/// Mongo 의 Edit Document (Studio3T 대응) — 문서 하나를 JSON 으로 펴서 고치고 저장한다.
/// 저장 자체(<c>ReplaceOneAsync</c>)는 호출자가 한다 — 이 창은 텍스트를 받아
/// <see cref="BsonDocument"/> 로 파싱하고 <c>_id</c> 를 안 바꿨는지만 확인한다.
/// </summary>
public partial class MongoDocumentDialog : Window
{
    /// <summary>편집 모드에서만 채워진다 — Add 모드는 비교할 원본이 없어 null(검사 안 함).</summary>
    private readonly BsonValue? _originalId;

    /// <summary>Save 를 눌러 파싱까지 성공한 경우에만 값, 취소·오류면 null.</summary>
    public BsonDocument? Result { get; private set; }

    public MongoDocumentDialog() : this(new BsonDocument("_id", 0), isNew: false) { }

    /// <summary>기존 문서 편집 — Save 시 <c>_id</c> 를 안 바꿨는지 확인한다.</summary>
    public MongoDocumentDialog(BsonDocument document) : this(document, isNew: false) { }

    /// <summary>새 문서 추가 — 원본이 없으므로 <c>_id</c> 불변 검사를 하지 않는다.</summary>
    public static MongoDocumentDialog ForNewDocument() => new(new BsonDocument(), isNew: true);

    private MongoDocumentDialog(BsonDocument document, bool isNew)
    {
        InitializeComponent();
        Title = isNew ? "Add Document" : "Edit Document";
        _originalId = isNew ? null : (document.Contains("_id") ? document["_id"] : BsonNull.Value);

        var settings = new JsonWriterSettings { Indent = true, OutputMode = JsonOutputMode.RelaxedExtendedJson };
        JsonBox.Text = isNew ? "{\n  \n}" : document.ToJson(settings);
        HeaderText.Text = isNew
            ? "새 문서를 JSON 으로 작성한 뒤 Save 를 누르세요. _id 를 안 적으면 자동 생성됩니다."
            : "JSON 을 고친 뒤 Save 를 누르세요. _id 는 바꿀 수 없습니다.";
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        BsonDocument parsed;
        try
        {
            parsed = BsonDocument.Parse(JsonBox.Text ?? "");
        }
        catch (System.Exception ex)
        {
            ShowError($"JSON 을 읽지 못했습니다: {ex.Message}");
            return;
        }

        // Mongo 는 replace 시 _id 가 바뀌면 거부한다 — DB 왕복 없이 여기서 먼저 알린다.
        // Add 모드는 비교할 원본이 없으니 이 검사를 건너뛴다.
        if (_originalId is { } originalId)
        {
            var newId = parsed.Contains("_id") ? parsed["_id"] : BsonNull.Value;
            if (!newId.Equals(originalId))
            {
                ShowError("_id 는 바꿀 수 없습니다 — 원래 값으로 되돌리세요.");
                return;
            }
        }

        Result = parsed;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
