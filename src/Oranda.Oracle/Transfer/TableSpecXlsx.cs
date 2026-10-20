using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Oranda.Core.Transfer;

namespace Oranda.Oracle.Transfer;

/// <summary>
/// 테이블 정의서 xlsx: 목차 시트 하나 + 표마다 시트 하나. 문자열은 인라인 문자열로 쓰고 서식은 머리 줄(굵게·바탕색·테두리)과 본문 테두리뿐이다.
/// 목차의 표 이름은 그 표 시트로 가는 내부 링크다. 외부 라이브러리 없이 ZIP + XML 만으로 쓴다(TSK-13-06).
/// </summary>
internal static class TableSpecXlsx
{
    internal const string TocSheetName = "목차";

    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const int MaxSheetName = 31;
    private const int MaxCellChars = 32767;

    // 스타일 번호(styles.xml 의 cellXfs 순서): 0 보통 · 1 머리 줄 · 2 본문 · 3 제목 · 4 링크 본문
    private const int StyleNormal = 0;
    private const int StyleHeader = 1;
    private const int StyleBody = 2;
    private const int StyleTitle = 3;
    private const int StyleLink = 4;

    private static readonly double[] ColumnWidths = [6, 26, 20, 7, 5, 28, 22, 40];
    private static readonly double[] IndexWidths = [30, 7, 50];
    private static readonly double[] TocWidths = [6, 40, 50];

    internal static void Write(TableSpecDocument doc, Stream destination)
    {
        var names = SheetNames(doc);
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        var sheetCount = doc.Tables.Count + 1;

        Entry(zip, "[Content_Types].xml", x =>
        {
            x.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
            Default(x, "rels", "application/vnd.openxmlformats-package.relationships+xml");
            Default(x, "xml", "application/xml");
            Override(x, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
            Override(x, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
            for (var i = 1; i <= sheetCount; i++)
            {
                Override(x, $"/xl/worksheets/sheet{i}.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
            }

            x.WriteEndElement();
        });
        Entry(zip, "_rels/.rels", x =>
        {
            x.WriteStartElement("Relationships", PackageRelNs);
            Relationship(x, "rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml");
            x.WriteEndElement();
        });
        Entry(zip, "xl/workbook.xml", x =>
        {
            x.WriteStartElement("workbook", MainNs);
            x.WriteAttributeString("xmlns", "r", null, RelNs);
            x.WriteStartElement("sheets");
            for (var i = 0; i < sheetCount; i++)
            {
                x.WriteStartElement("sheet");
                x.WriteAttributeString("name", names[i]);
                x.WriteAttributeString("sheetId", Num(i + 1));
                x.WriteAttributeString("id", RelNs, $"rId{i + 1}");
                x.WriteEndElement();
            }

            x.WriteEndElement();
            x.WriteEndElement();
        });
        Entry(zip, "xl/_rels/workbook.xml.rels", x =>
        {
            x.WriteStartElement("Relationships", PackageRelNs);
            for (var i = 1; i <= sheetCount; i++)
            {
                Relationship(x, $"rId{i}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", $"worksheets/sheet{i}.xml");
            }

            Relationship(x, $"rId{sheetCount + 1}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml");
            x.WriteEndElement();
        });
        Entry(zip, "xl/styles.xml", WriteStyles);
        Entry(zip, "xl/worksheets/sheet1.xml", x => WriteToc(x, doc, names));
        for (var i = 0; i < doc.Tables.Count; i++)
        {
            var table = doc.Tables[i];
            Entry(zip, $"xl/worksheets/sheet{i + 2}.xml", x => WriteTable(x, table));
        }
    }

    /// <summary>시트 이름(목차 + 표마다): 31자 이하·금지 글자(<c>[]:*?/\</c>) 없음·대소문자 무시로 유일.</summary>
    internal static string[] SheetNames(TableSpecDocument doc)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TocSheetName, "History" }; // History 는 Excel 예약 이름
        var result = new string[doc.Tables.Count + 1];
        result[0] = TocSheetName;
        for (var i = 0; i < doc.Tables.Count; i++)
        {
            var baseName = Sanitize(doc.Tables[i].Name);
            var name = baseName;
            for (var n = 2; !used.Add(name); n++)
            {
                var suffix = "_" + n.ToString(CultureInfo.InvariantCulture);
                name = baseName[..Math.Min(baseName.Length, MaxSheetName - suffix.Length)] + suffix;
            }

            result[i + 1] = name;
        }

        return result;
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in Clean(name))
        {
            sb.Append(ch is '[' or ']' or ':' or '*' or '?' or '/' or '\\' or '\t' or '\n' or '\r' ? '_' : ch);
        }

        var s = sb.ToString();
        if (s.Length > MaxSheetName)
        {
            // 서로게이트 쌍을 가르지 않는다.
            s = s[..(char.IsHighSurrogate(s[MaxSheetName - 1]) ? MaxSheetName - 1 : MaxSheetName)];
        }

        s = s.Trim('\''); // 작은따옴표로 시작·끝날 수 없다
        return s.Length == 0 ? "_" : s;
    }

    private static void WriteToc(XmlWriter x, TableSpecDocument doc, string[] names)
    {
        var rows = new List<Cell[]>
        {
            new[] { new Cell(doc.Title, StyleTitle) },
            new[] { new Cell("생성: " + doc.GeneratedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), StyleNormal) },
            Array.Empty<Cell>(),
            new[] { new Cell("순번", StyleHeader), new Cell("표", StyleHeader), new Cell("주석", StyleHeader) },
        };
        var links = new List<(string Ref, string Location)>();
        for (var i = 0; i < doc.Tables.Count; i++)
        {
            var t = doc.Tables[i];
            rows.Add([new Cell(Num(i + 1), StyleBody), new Cell(TableSpecWriter.Heading(t), StyleLink), new Cell(t.Comment ?? string.Empty, StyleBody)]);
            links.Add(($"B{rows.Count}", $"'{names[i + 1].Replace("'", "''", StringComparison.Ordinal)}'!A1"));
        }

        WriteSheet(x, TocWidths, rows, links);
    }

    private static void WriteTable(XmlWriter x, TableSpecTable t)
    {
        var rows = new List<Cell[]>
        {
            new[] { new Cell(TableSpecWriter.Heading(t), StyleTitle) },
            new[] { new Cell(t.Comment ?? string.Empty, StyleNormal) },
            Array.Empty<Cell>(),
            TableSpecWriter.ColumnHeaders.Select(h => new Cell(h, StyleHeader)).ToArray(),
        };
        rows.AddRange(t.Columns.Select(c => TableSpecWriter.ColumnRow(c).Select(v => new Cell(v, StyleBody)).ToArray()));
        if (t.Indexes.Count > 0)
        {
            rows.Add(Array.Empty<Cell>());
            rows.Add(TableSpecWriter.IndexHeaders.Select(h => new Cell(h, StyleHeader)).ToArray());
            rows.AddRange(t.Indexes.Select(i => TableSpecWriter.IndexRow(i).Select(v => new Cell(v, StyleBody)).ToArray()));
        }

        WriteSheet(x, ColumnWidths, rows, []);
    }

    private static void WriteSheet(XmlWriter x, double[] widths, List<Cell[]> rows, List<(string Ref, string Location)> links)
    {
        x.WriteStartElement("worksheet", MainNs);
        x.WriteStartElement("cols");
        for (var i = 0; i < widths.Length; i++)
        {
            x.WriteStartElement("col");
            x.WriteAttributeString("min", Num(i + 1));
            x.WriteAttributeString("max", Num(i + 1));
            x.WriteAttributeString("width", widths[i].ToString(CultureInfo.InvariantCulture));
            x.WriteAttributeString("customWidth", "1");
            x.WriteEndElement();
        }

        x.WriteEndElement();
        x.WriteStartElement("sheetData");
        for (var r = 0; r < rows.Count; r++)
        {
            x.WriteStartElement("row");
            x.WriteAttributeString("r", Num(r + 1));
            for (var c = 0; c < rows[r].Length; c++)
            {
                var cell = rows[r][c];
                x.WriteStartElement("c");
                x.WriteAttributeString("r", ColumnName(c) + Num(r + 1));
                x.WriteAttributeString("s", Num(cell.Style));
                if (cell.Text.Length > 0)
                {
                    x.WriteAttributeString("t", "inlineStr");
                    x.WriteStartElement("is");
                    x.WriteStartElement("t");
                    x.WriteAttributeString("xml", "space", null, "preserve");
                    x.WriteString(Clean(cell.Text));
                    x.WriteEndElement();
                    x.WriteEndElement();
                }

                x.WriteEndElement();
            }

            x.WriteEndElement();
        }

        x.WriteEndElement();
        if (links.Count > 0)
        {
            x.WriteStartElement("hyperlinks");
            foreach (var (cellRef, location) in links)
            {
                x.WriteStartElement("hyperlink");
                x.WriteAttributeString("ref", cellRef);
                x.WriteAttributeString("location", location);
                x.WriteEndElement();
            }

            x.WriteEndElement();
        }

        x.WriteEndElement();
    }

    private static void WriteStyles(XmlWriter x)
    {
        x.WriteStartElement("styleSheet", MainNs);
        x.WriteStartElement("fonts");
        x.WriteAttributeString("count", "4");
        Font(x, bold: false, color: null);
        Font(x, bold: true, color: null);
        Font(x, bold: true, color: null, size: 14);
        Font(x, bold: false, color: "FF0563C1", underline: true);
        x.WriteEndElement();
        x.WriteStartElement("fills");
        x.WriteAttributeString("count", "3");
        Fill(x, "none", null);
        Fill(x, "gray125", null);
        Fill(x, "solid", "FFDCE6F1");
        x.WriteEndElement();
        x.WriteStartElement("borders");
        x.WriteAttributeString("count", "2");
        x.WriteStartElement("border");
        foreach (var side in new[] { "left", "right", "top", "bottom", "diagonal" })
        {
            x.WriteElementString(side, null);
        }

        x.WriteEndElement();
        x.WriteStartElement("border");
        foreach (var side in new[] { "left", "right", "top", "bottom" })
        {
            x.WriteStartElement(side);
            x.WriteAttributeString("style", "thin");
            x.WriteEndElement();
        }

        x.WriteElementString("diagonal", null);
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteStartElement("cellStyleXfs");
        x.WriteAttributeString("count", "1");
        x.WriteStartElement("xf");
        x.WriteAttributeString("numFmtId", "0");
        x.WriteAttributeString("fontId", "0");
        x.WriteAttributeString("fillId", "0");
        x.WriteAttributeString("borderId", "0");
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteStartElement("cellXfs");
        x.WriteAttributeString("count", "5");
        Xf(x, fontId: 0, fillId: 0, borderId: 0, wrap: false);
        Xf(x, fontId: 1, fillId: 2, borderId: 1, wrap: false);
        Xf(x, fontId: 0, fillId: 0, borderId: 1, wrap: true);
        Xf(x, fontId: 2, fillId: 0, borderId: 0, wrap: false);
        Xf(x, fontId: 3, fillId: 0, borderId: 1, wrap: false);
        x.WriteEndElement();
        x.WriteStartElement("cellStyles");
        x.WriteAttributeString("count", "1");
        x.WriteStartElement("cellStyle");
        x.WriteAttributeString("name", "Normal");
        x.WriteAttributeString("xfId", "0");
        x.WriteAttributeString("builtinId", "0");
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();
    }

    private static void Font(XmlWriter x, bool bold, string? color, int size = 11, bool underline = false)
    {
        x.WriteStartElement("font");
        if (bold)
        {
            x.WriteElementString("b", null);
        }

        if (underline)
        {
            x.WriteElementString("u", null);
        }

        x.WriteStartElement("sz");
        x.WriteAttributeString("val", Num(size));
        x.WriteEndElement();
        if (color is not null)
        {
            x.WriteStartElement("color");
            x.WriteAttributeString("rgb", color);
            x.WriteEndElement();
        }

        x.WriteStartElement("name");
        x.WriteAttributeString("val", "Malgun Gothic");
        x.WriteEndElement();
        x.WriteEndElement();
    }

    private static void Fill(XmlWriter x, string pattern, string? rgb)
    {
        x.WriteStartElement("fill");
        x.WriteStartElement("patternFill");
        x.WriteAttributeString("patternType", pattern);
        if (rgb is not null)
        {
            x.WriteStartElement("fgColor");
            x.WriteAttributeString("rgb", rgb);
            x.WriteEndElement();
        }

        x.WriteEndElement();
        x.WriteEndElement();
    }

    private static void Xf(XmlWriter x, int fontId, int fillId, int borderId, bool wrap)
    {
        x.WriteStartElement("xf");
        x.WriteAttributeString("numFmtId", "49"); // 텍스트: 0 으로 시작하는 순번·값을 숫자로 바꾸지 않는다
        x.WriteAttributeString("fontId", Num(fontId));
        x.WriteAttributeString("fillId", Num(fillId));
        x.WriteAttributeString("borderId", Num(borderId));
        x.WriteAttributeString("xfId", "0");
        x.WriteAttributeString("applyNumberFormat", "1");
        x.WriteAttributeString("applyFont", "1");
        x.WriteAttributeString("applyFill", "1");
        x.WriteAttributeString("applyBorder", "1");
        if (wrap)
        {
            x.WriteAttributeString("applyAlignment", "1");
            x.WriteStartElement("alignment");
            x.WriteAttributeString("vertical", "top");
            x.WriteAttributeString("wrapText", "1");
            x.WriteEndElement();
        }

        x.WriteEndElement();
    }

    private static void Default(XmlWriter x, string extension, string contentType)
    {
        x.WriteStartElement("Default");
        x.WriteAttributeString("Extension", extension);
        x.WriteAttributeString("ContentType", contentType);
        x.WriteEndElement();
    }

    private static void Override(XmlWriter x, string partName, string contentType)
    {
        x.WriteStartElement("Override");
        x.WriteAttributeString("PartName", partName);
        x.WriteAttributeString("ContentType", contentType);
        x.WriteEndElement();
    }

    private static void Relationship(XmlWriter x, string id, string type, string target)
    {
        x.WriteStartElement("Relationship");
        x.WriteAttributeString("Id", id);
        x.WriteAttributeString("Type", type);
        x.WriteAttributeString("Target", target);
        x.WriteEndElement();
    }

    private static void Entry(ZipArchive zip, string name, Action<XmlWriter> write)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false });
        xml.WriteStartDocument(standalone: true);
        write(xml);
        xml.WriteEndDocument();
    }

    /// <summary>XML 1.0 에서 쓸 수 없는 글자를 뺀다. 칸 하나는 32,767자까지.</summary>
    private static string Clean(string s)
    {
        var sb = new StringBuilder(Math.Min(s.Length, MaxCellChars));
        for (var i = 0; i < s.Length && sb.Length < MaxCellChars; i++)
        {
            var ch = s[i];
            if (ch is '\t' or '\n' or '\r' || (ch >= 0x20 && ch <= 0xD7FF) || (ch >= 0xE000 && ch <= 0xFFFD))
            {
                sb.Append(ch);
            }
            else if (char.IsHighSurrogate(ch) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                sb.Append(ch).Append(s[++i]);
            }
        }

        return sb.ToString();
    }

    private static string ColumnName(int index)
    {
        var sb = new StringBuilder();
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            sb.Insert(0, (char)('A' + ((n - 1) % 26)));
        }

        return sb.ToString();
    }

    private static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);

    private readonly record struct Cell(string Text, int Style);
}
