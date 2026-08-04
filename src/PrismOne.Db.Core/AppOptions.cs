using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrismOne.Db.Core;

/// <summary>앱 옵션 (~/.prismone-studio/options.json). Golden 의 Options 다이얼로그 대응.</summary>
public sealed class AppOptions
{
    /// <summary>
    /// 점진 fetch 배치 크기. Golden 은 100, DataGrip 은 500 단위로 끊어 보여준다 —
    /// 왕복이 줄어 스크롤이 덜 끊기므로 500 을 기본으로 쓴다.
    /// (전부 가져오기가 기본이라 이 값은 <see cref="FetchAllOnExecute"/> 를 껐을 때와
    /// 상한에 걸린 뒤 이어 받을 때 쓰인다.)
    /// </summary>
    public int FetchBatch { get; set; } = 500;

    /// <summary>탭별 최대 로드 행수. -1 이면 무제한.</summary>
    public int RecordsetLimit { get; set; } = -1;

    /// <summary>NULL 셀 표시 문자열 (빈 문자열이면 공백).</summary>
    public string NullText { get; set; } = "";

    /// <summary>0 이면 미설정. 세션에 SET statement_timeout 적용 (ms).</summary>
    public int StatementTimeoutMs { get; set; }

    /// <summary>true 면 PG 기본 autocommit, false 면 Golden 식 수동 커밋.</summary>
    public bool AutoCommit { get; set; }

    /// <summary>Golden: "Allow non-Select statements to run from the Favorites Menu." 기본은 차단.</summary>
    public bool AllowNonSelectFavorites { get; set; }

    /// <summary>
    /// SELECT 실행 후 <c>COUNT(*)</c> 를 따로 돌려 전체 건수를 상태바에 보인다
    /// (Golden 이 레코드 수를 별도 조회하는 방식).
    ///
    /// **기본은 끔** — 운영 DB 전제(STATUS.md §2·3)라 대용량 테이블에서 COUNT(*) 가
    /// 매우 비쌀 수 있다. 켜면 첫 배치를 보여준 뒤 백그라운드로 세고,
    /// 실패하면 조용히 건너뛴다.
    /// </summary>
    public bool CountTotalRecords { get; set; }

    /// <summary>
    /// 실행 즉시 결과를 끝까지 가져온다 (Golden 의 기본 동작 — 점진 fetch 는 Golden 8 에서
    /// 옵션으로 추가된 것이고, 체크하지 않으면 전부 가져오는 게 원래 방식이다).
    ///
    /// 켜면 로드된 행 수가 곧 전체 건수라 **스크롤바가 정확**해지고 COUNT(*) 도 필요 없다.
    ///
    /// **기본은 켬.** 대신 <see cref="RecordsetLimit"/> 이 무제한이어도
    /// <see cref="FetchAllSafetyCap"/> 까지만 가져온다 — 운영 DB 에서 수백만 행을
    /// 통째로 끌어오는 사고를 막기 위해서다. 상한에 걸리면 상태바가 알리고,
    /// 그 뒤는 스크롤·Ctrl+End 로 이어 가져온다.
    /// </summary>
    public bool FetchAllOnExecute { get; set; } = true;

    /// <summary>
    /// 풀 fetch 인데 RecordsetLimit 이 무제한일 때 적용하는 안전 상한.
    /// 옵션이 아니라 코드 상수다 — 예전 options.json 에 -1 이 저장돼 있어도 보호된다.
    /// </summary>
    public const int FetchAllSafetyCap = 50_000;

    /// <summary>DataGrip 의 Tx Isolation — 새 세션에 걸 격리 수준. 기본은 DB 설정을 따른다.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<TransactionIsolation>))]
    public TransactionIsolation Isolation { get; set; } = TransactionIsolation.DatabaseDefault;

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".prismone-studio");

    private static string FilePath => Path.Combine(Dir, "options.json");

    public static AppOptions Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppOptions>(File.ReadAllText(FilePath)) ?? new AppOptions()
                : new AppOptions();
        }
        catch
        {
            return new AppOptions();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 옵션 저장 실패는 치명적이지 않다 */ }
    }
}

/// <summary>워크스페이스: 열린 탭들의 SQL 과 이름 (Golden 의 Workspace 파일).</summary>
public sealed class WorkspaceTab
{
    public string Title { get; set; } = "";
    public string Sql { get; set; } = "";
    public bool IsPrivate { get; set; }
}

public sealed class Workspace
{
    public string? Connection { get; set; }        // user@host:port/db
    public List<WorkspaceTab> Tabs { get; set; } = [];

    public static Workspace? Load(string path)
    {
        try { return JsonSerializer.Deserialize<Workspace>(File.ReadAllText(path)); }
        catch { return null; }
    }

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}
