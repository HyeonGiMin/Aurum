using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace PrismOne.Db.Core;

/// <summary>
/// 그리드에 로드된 행을 xlsx 로 저장한다 (Golden 의 Save Grid As xlsx).
/// 외부 라이브러리 없이 OOXML(SpreadsheetML) 최소 구성을 직접 쓴다 —
/// 단일 exe 배포 크기를 지키기 위해서다. 문자열은 inline string 으로 넣고
/// (sharedStrings 불필요), 숫자만 숫자 셀로 내보낸다.
/// </summary>
public static class XlsxExporter
{
    /// <summary>Excel 시트 행 상한(헤더 포함 1,048,576). 넘치면 잘라내고 개수를 돌려준다.</summary>
    public const int MaxRows = 1_048_575;

    /// <summary>xlsx 를 스트림에 쓴다. 반환값은 실제로 쓴 데이터 행 수(상한 초과 시 잘림).</summary>
    public static int Write(
        Stream output,
        IReadOnlyList<string> columns,
        IReadOnlyList<string?[]> rows,
        string sheetName = "Result")
    {
        var written = Math.Min(rows.Count, MaxRows);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        AddEntry(zip, "[Content_Types].xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
            <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
            <Default Extension="xml" ContentType="application/xml"/>
            <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
            <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
            </Types>
            """);

        AddEntry(zip, "_rels/.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);

        AddEntry(zip, "xl/workbook.xml",
            $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
            <sheets><sheet name="{Escape(SafeSheetName(sheetName))}" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);

        AddEntry(zip, "xl/_rels/workbook.xml.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
            </Relationships>
            """);

        // 스타일: 0 = 기본, 1 = 굵게(헤더 행)
        AddEntry(zip, "xl/styles.xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
            <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>
            <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
            <borders count="1"><border/></borders>
            <cellStyleXfs count="1"><xf/></cellStyleXfs>
            <cellXfs count="2"><xf xfId="0"/><xf xfId="0" fontId="1" applyFont="1"/></cellXfs>
            </styleSheet>
            """);

        var sheet = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest);
        using var writer = new StreamWriter(sheet.Open(), new UTF8Encoding(false), bufferSize: 1 << 16);
        writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        writer.Write("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");

        writer.Write("<row>");
        foreach (var column in columns)
            WriteInlineString(writer, column, style: 1);
        writer.Write("</row>");

        for (var r = 0; r < written; r++)
        {
            writer.Write("<row>");
            var row = rows[r];
            for (var c = 0; c < columns.Count && c < row.Length; c++)
            {
                var value = row[c];
                if (value is null)
                {
                    writer.Write("<c/>");                       // NULL 은 빈 셀
                }
                else if (IsNumeric(value))
                {
                    writer.Write("<c><v>");
                    writer.Write(value);
                    writer.Write("</v></c>");
                }
                else
                {
                    WriteInlineString(writer, value, style: 0);
                }
            }
            writer.Write("</row>");
        }
        writer.Write("</sheetData></worksheet>");
        return written;
    }

    public static byte[] Build(
        IReadOnlyList<string> columns, IReadOnlyList<string?[]> rows, string sheetName = "Result")
    {
        using var ms = new MemoryStream();
        Write(ms, columns, rows, sheetName);
        return ms.ToArray();
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void WriteInlineString(StreamWriter writer, string value, int style)
    {
        writer.Write(style == 0 ? "<c t=\"inlineStr\"><is><t" : "<c t=\"inlineStr\" s=\"1\"><is><t");
        // 앞뒤 공백·개행이 있는 값은 Excel 이 지우지 않게 표시
        if (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])))
            writer.Write(" xml:space=\"preserve\"");
        writer.Write('>');
        writer.Write(Escape(value));
        writer.Write("</t></is></c>");
    }

    /// <summary>
    /// 숫자 셀로 내보내도 값이 보존되는 경우만 true — "007", "1e5", " 1 " 처럼
    /// 표기가 달라지는 값은 문자열로 남긴다 (round-trip 비교).
    /// </summary>
    private static bool IsNumeric(string value) =>
        value.Length > 0 && value.Length <= 17 &&
        decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var d) &&
        d.ToString(CultureInfo.InvariantCulture) == value;

    private static string Escape(string value)
    {
        if (value.IndexOfAny(['&', '<', '>', '"']) < 0 && !HasControlChar(value))
            return value;
        var sb = new StringBuilder(value.Length + 16);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default:
                    // XML 1.0 에서 허용되지 않는 제어문자는 자리표시자로
                    if (ch < 0x20 && ch is not ('\t' or '\n' or '\r'))
                        sb.Append('�');
                    else
                        sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    private static bool HasControlChar(string value)
    {
        foreach (var ch in value)
        {
            if (ch < 0x20 && ch is not ('\t' or '\n' or '\r'))
                return true;
        }
        return false;
    }

    /// <summary>시트 이름 규칙: 31자 이내, \ / ? * [ ] : 금지.</summary>
    private static string SafeSheetName(string name)
    {
        var cleaned = new string(name.Select(c => c is '\\' or '/' or '?' or '*' or '[' or ']' or ':' ? '_' : c).ToArray());
        if (cleaned.Length == 0) cleaned = "Result";
        return cleaned.Length <= 31 ? cleaned : cleaned[..31];
    }
}
