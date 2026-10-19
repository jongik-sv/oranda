using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Oranda.Core.Transfer;

namespace Oranda.Oracle.Transfer;

/// <summary>
/// 가져오기 원본(CSV·TSV·xlsx) 행 읽기. 앞에서부터 한 행씩 흘려 읽는다(전체를 메모리에 올리지 않는다). 소유: TSK-06-07.
/// 미리보기 대화상자도 같은 읽기를 쓴다. 셀 값은 문자열(CSV·xlsx 문자·숫자 원문), xlsx 날짜 서식 셀은 <see cref="DateTime"/>, 불리언은 <see cref="bool"/>, 빈 셀은 null.
/// </summary>
public static class TransferFileReader
{
    /// <summary>원본의 모든 행. xlsx 는 첫 시트. 빈 행도 위치를 지키려고 길이 0 배열로 낸다(xlsx), CSV 의 빈 줄은 건너뛴다.</summary>
    public static IEnumerable<object?[]> ReadRows(TransferFormat format, Stream source) => format switch
    {
        TransferFormat.Csv => ReadDelimited(source, ','),
        TransferFormat.Tsv => ReadDelimited(source, '\t'),
        TransferFormat.Xlsx => ReadXlsx(source),
        _ => throw new NotSupportedException($"{format} 형식은 가져올 수 없다(CSV·TSV·xlsx 만)."),
    };

    /// <summary>앞에서 <paramref name="maxRows"/> 행만(헤더 포함) 읽는다.</summary>
    public static IReadOnlyList<object?[]> Preview(TransferFormat format, Stream source, int maxRows) =>
        ReadRows(format, source).Take(maxRows).ToList();

    // ---------------- CSV ----------------

    private static IEnumerable<object?[]> ReadDelimited(Stream source, char delimiter)
    {
        using var reader = new StreamReader(source, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, 64 * 1024, leaveOpen: true);
        var fields = new List<object?>();
        var sb = new StringBuilder();
        var inQuotes = false;
        var wasQuoted = false;
        var any = false;
        int c;
        while ((c = reader.Read()) >= 0)
        {
            var ch = (char)c;
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        sb.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(ch);
                }

                continue;
            }

            if (ch == '"' && sb.Length == 0 && !wasQuoted)
            {
                inQuotes = true;
                wasQuoted = true;
                any = true;
            }
            else if (ch == delimiter)
            {
                fields.Add(sb.ToString());
                sb.Clear();
                wasQuoted = false;
                any = true;
            }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && reader.Peek() == '\n')
                {
                    reader.Read();
                }

                if (any || sb.Length > 0)
                {
                    fields.Add(sb.ToString());
                    yield return fields.ToArray();
                }

                fields.Clear();
                sb.Clear();
                wasQuoted = false;
                any = false;
            }
            else
            {
                sb.Append(ch);
                any = true;
            }
        }

        if (any || sb.Length > 0)
        {
            fields.Add(sb.ToString());
            yield return fields.ToArray();
        }
    }

    // ---------------- XLSX ----------------

    private static IEnumerable<object?[]> ReadXlsx(Stream source)
    {
        using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        var sheetPath = FindFirstSheet(zip);
        var shared = ReadSharedStrings(zip);
        var dateStyles = ReadDateStyles(zip);
        var entry = zip.GetEntry(sheetPath) ?? throw new InvalidDataException("xlsx 에 시트가 없다.");
        using var stream = entry.Open();
        using var xml = XmlReader.Create(stream, new XmlReaderSettings { IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Prohibit });
        long lastRow = 0;
        while (xml.Read())
        {
            if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "row")
            {
                continue;
            }

            var rowNo = long.TryParse(xml.GetAttribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out var rn) ? rn : lastRow + 1;
            for (var gap = lastRow + 1; gap < rowNo; gap++)
            {
                yield return [];
            }

            lastRow = rowNo;
            var cells = new List<object?>();
            if (!xml.IsEmptyElement)
            {
                using var rowReader = xml.ReadSubtree();
                rowReader.Read();
                while (rowReader.Read())
                {
                    if (rowReader.NodeType == XmlNodeType.Element && rowReader.LocalName == "c")
                    {
                        ReadCell(rowReader, shared, dateStyles, cells);
                    }
                }
            }

            yield return cells.ToArray();
        }
    }

    private static void ReadCell(XmlReader r, List<string> shared, HashSet<int> dateStyles, List<object?> cells)
    {
        var reference = r.GetAttribute("r");
        var type = r.GetAttribute("t");
        var style = int.TryParse(r.GetAttribute("s"), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0;
        var col = reference is null ? cells.Count : ColumnIndex(reference);
        string? v = null;
        string? inline = null;
        if (!r.IsEmptyElement)
        {
            using var sub = r.ReadSubtree();
            sub.Read();
            while (sub.Read())
            {
                if (sub.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (sub.LocalName == "v")
                {
                    v = sub.ReadElementContentAsString();
                }
                else if (sub.LocalName == "is")
                {
                    inline = ReadInlineString(sub);
                }
            }
        }

        object? value = type switch
        {
            "s" when int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var idx) && idx < shared.Count => shared[idx],
            "inlineStr" => inline,
            "str" => v,
            "b" => v == "1",
            "e" => null,
            "d" when DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dd) => dd,
            _ when v is null => null,
            _ when dateStyles.Contains(style) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial)
                => FromSerial(serial),
            _ => v,
        };
        while (cells.Count < col)
        {
            cells.Add(null);
        }

        if (cells.Count == col)
        {
            cells.Add(value);
        }
    }

    private static string ReadInlineString(XmlReader r)
    {
        var sb = new StringBuilder();
        var depth = r.Depth;
        while (r.Read() && r.Depth > depth)
        {
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "t" && !r.IsEmptyElement)
            {
                sb.Append(r.ReadElementContentAsString());
            }
        }

        return sb.ToString();
    }

    private static DateTime FromSerial(double serial)
    {
        var d = new DateTime(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified).AddDays(serial);
        // 부동소수 오차로 생기는 밀리초 이하 잔재를 정리한다.
        return new DateTime((d.Ticks + (TimeSpan.TicksPerMillisecond / 2)) / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Unspecified);
    }

    private static int ColumnIndex(string reference)
    {
        var n = 0;
        foreach (var ch in reference)
        {
            if (ch is >= 'A' and <= 'Z')
            {
                n = (n * 26) + (ch - 'A' + 1);
            }
            else if (ch is >= 'a' and <= 'z')
            {
                n = (n * 26) + (ch - 'a' + 1);
            }
            else
            {
                break;
            }
        }

        return n - 1;
    }

    private static string FindFirstSheet(ZipArchive zip)
    {
        var book = zip.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException("xlsx 형식이 아니다(xl/workbook.xml 없음).");
        string? rid = null;
        using (var s = book.Open())
        using (var x = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
        {
            while (x.Read())
            {
                if (x.NodeType == XmlNodeType.Element && x.LocalName == "sheet")
                {
                    rid = x.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                    break;
                }
            }
        }

        var rels = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (rid is not null && rels is not null)
        {
            using var s = rels.Open();
            using var x = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            while (x.Read())
            {
                if (x.NodeType == XmlNodeType.Element && x.LocalName == "Relationship" && x.GetAttribute("Id") == rid)
                {
                    var target = x.GetAttribute("Target") ?? string.Empty;
                    return target.StartsWith('/') ? target[1..] : "xl/" + target;
                }
            }
        }

        return "xl/worksheets/sheet1.xml";
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return list;
        }

        using var s = entry.Open();
        using var x = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        while (x.Read())
        {
            if (x.NodeType == XmlNodeType.Element && x.LocalName == "si")
            {
                var sb = new StringBuilder();
                if (!x.IsEmptyElement)
                {
                    using var sub = x.ReadSubtree();
                    sub.Read();
                    while (sub.Read())
                    {
                        // 서식 있는 글자의 <rPr> 안쪽이 아니라 <t> 만 모은다(<rPh> 후리가나는 제외).
                        if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "rPh")
                        {
                            sub.Skip();
                        }
                        else if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "t" && !sub.IsEmptyElement)
                        {
                            sb.Append(sub.ReadElementContentAsString());
                        }
                    }
                }

                list.Add(sb.ToString());
            }
        }

        return list;
    }

    private static HashSet<int> ReadDateStyles(ZipArchive zip)
    {
        var result = new HashSet<int>();
        var entry = zip.GetEntry("xl/styles.xml");
        if (entry is null)
        {
            return result;
        }

        var custom = new Dictionary<int, string>();
        var xfFormats = new List<int>();
        using var s = entry.Open();
        using var x = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var inCellXfs = false;
        while (x.Read())
        {
            if (x.NodeType == XmlNodeType.Element)
            {
                if (x.LocalName == "numFmt" && int.TryParse(x.GetAttribute("numFmtId"), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                {
                    custom[id] = x.GetAttribute("formatCode") ?? string.Empty;
                }
                else if (x.LocalName == "cellXfs")
                {
                    inCellXfs = true;
                }
                else if (inCellXfs && x.LocalName == "xf")
                {
                    xfFormats.Add(int.TryParse(x.GetAttribute("numFmtId"), NumberStyles.None, CultureInfo.InvariantCulture, out var nf) ? nf : 0);
                }
            }
            else if (x.NodeType == XmlNodeType.EndElement && x.LocalName == "cellXfs")
            {
                inCellXfs = false;
            }
        }

        for (var i = 0; i < xfFormats.Count; i++)
        {
            var id = xfFormats[i];
            if (IsBuiltInDate(id) || (custom.TryGetValue(id, out var code) && IsDateFormat(code)))
            {
                result.Add(i);
            }
        }

        return result;
    }

    private static bool IsBuiltInDate(int id) => id is >= 14 and <= 22 || id is >= 45 and <= 47;

    private static bool IsDateFormat(string code)
    {
        var inQuote = false;
        var inBracket = false;
        for (var i = 0; i < code.Length; i++)
        {
            var ch = code[i];
            if (ch == '"')
            {
                inQuote = !inQuote;
            }
            else if (inQuote)
            {
                continue;
            }
            else if (ch == '\\' || ch == '_' || ch == '*')
            {
                i++;
            }
            else if (ch == '[')
            {
                inBracket = true;
            }
            else if (ch == ']')
            {
                inBracket = false;
            }
            else if (!inBracket && char.ToLowerInvariant(ch) is 'y' or 'd' or 'h' or 's' or 'm')
            {
                return true;
            }
        }

        return false;
    }
}
