using System.Globalization;
using System.Text;

namespace PrismOne.Db.Core;

/// <summary>DB 값 → 그리드/CSV 공용 표시 문자열. NULL 은 null 유지.
/// 거대한 값(JSONB DICOM Data Set 등)은 그리드 렌더링을 마비시키므로 표시용으로 자른다.</summary>
public static class ValueFormatter
{
    /// <summary>셀 표시 상한 (Golden 도 그리드엔 잘라 보여주고 상세는 cell detail 로).</summary>
    public const int MaxDisplayChars = 500;

    public static string? Format(object v) => v switch
    {
        null or DBNull => null,
        string s => Truncate(s),
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToString(dt.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss.FFF", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.FFFzzz", CultureInfo.InvariantCulture),
        byte[] bytes => FormatBytes(bytes),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        Array a => FormatArray(a),
        _ => v.ToString(),
    };

    private const int MaxByteaDisplay = 64;

    private static string FormatBytes(byte[] bytes)
    {
        var shown = bytes.Length > MaxByteaDisplay ? bytes.AsSpan(0, MaxByteaDisplay) : bytes.AsSpan();
        var sb = new StringBuilder(@"\x", shown.Length * 2 + 16);
        foreach (var b in shown) sb.Append(b.ToString("x2"));
        if (bytes.Length > MaxByteaDisplay) sb.Append($"… ({bytes.Length} bytes)");
        return sb.ToString();
    }

    private static string FormatArray(Array a)
    {
        var parts = new List<string?>(a.Length);
        foreach (var item in a) parts.Add(Format(item!) ?? "NULL");
        return Truncate("{" + string.Join(",", parts) + "}");
    }

    private static string Truncate(string s) =>
        s.Length <= MaxDisplayChars ? s : s[..MaxDisplayChars] + $"… (+{s.Length - MaxDisplayChars:N0} chars)";
}
