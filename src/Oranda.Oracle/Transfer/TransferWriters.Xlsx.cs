using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Oranda.Core.Grid;
using Oranda.Core.Transfer;

namespace Oranda.Oracle.Transfer;

internal static partial class TransferWriters
{
    // ---- XLSX ----
    private sealed class XlsxWriter : ITransferWriter
    {
        private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const int MaxCellChars = 32767;
        private static readonly DateTime Epoch = new(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified);
        private static readonly DateTime MinDate = new(1900, 3, 1, 0, 0, 0, DateTimeKind.Unspecified);

        /// <summary>열 너비를 맞추려고 살펴보는 앞쪽 행 수.</summary>
        private const int WidthSampleRows = 1000;

        private const double MaxColumnWidth = 60;

        private readonly ZipArchive _zip;
        private readonly bool _header;
        private readonly long _maxData;
        private readonly string? _sheetName;
        private List<object?[]>? _pending;
        private double[] _widths = [];
        private IReadOnlyList<ResultColumn> _columns = [];
        private string[] _colRefs = [];
        private XmlWriter? _xml;
        private Stream? _entry;
        private int _sheetCount;
        private long _rowNo;
        private long _dataInSheet;

        public XlsxWriter(Stream destination, bool header, int maxRowsPerSheet, string? sheetName)
        {
            _sheetName = sheetName;
            _zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
            _header = header;
            var cap = header ? 1_048_575L : 1_048_576L;
            _maxData = Math.Clamp(maxRowsPerSheet, 1, cap);
        }

        public IList<string> Warnings { get; } = [];

        public void Begin(IReadOnlyList<ResultColumn> columns)
        {
            _columns = columns;
            _colRefs = Enumerable.Range(0, columns.Count).Select(ColumnName).ToArray();

            // 열 너비는 시트 머리(cols)에 먼저 적어야 하므로 앞 행을 모아 둔 뒤 첫 시트를 연다.
            _pending = [];
        }

        public void WriteRow(object?[] values)
        {
            if (_pending is { } pending)
            {
                pending.Add((object?[])values.Clone());
                if (pending.Count >= WidthSampleRows)
                {
                    OpenFirstSheet();
                }

                return;
            }

            WriteDataRow(values);
        }

        private void OpenFirstSheet()
        {
            var pending = _pending!;
            _pending = null;
            _widths = MeasureWidths(pending);
            StartSheet();
            foreach (var row in pending)
            {
                WriteDataRow(row);
            }
        }

        private void WriteDataRow(object?[] values)
        {
            if (_dataInSheet >= _maxData)
            {
                EndSheet();
                StartSheet();
            }

            var x = _xml!;
            _rowNo++;
            _dataInSheet++;
            x.WriteStartElement("row");
            x.WriteAttributeString("r", Num(_rowNo));
            for (var i = 0; i < values.Length; i++)
            {
                WriteCell(x, _colRefs[i] + Num(_rowNo), _columns[i], values[i]);
            }

            x.WriteEndElement();
        }

        public void Finish()
        {
            if (_pending is not null)
            {
                OpenFirstSheet();
            }

            EndSheet();
            WriteStatic();
        }

        public void Dispose()
        {
            _xml?.Dispose();
            _entry?.Dispose();
            _zip.Dispose();
        }

        private static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);

        private static string ColumnName(int index)
        {
            var sb = new StringBuilder();
            for (var n = index + 1; n > 0; n = (n - 1) / 26)
            {
                sb.Insert(0, (char)('A' + ((n - 1) % 26)));
            }

            return sb.ToString();
        }

        private void StartSheet()
        {
            _sheetCount++;
            _entry = _zip.CreateEntry($"xl/worksheets/sheet{_sheetCount}.xml", CompressionLevel.Fastest).Open();
            _xml = XmlWriter.Create(_entry, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CheckCharacters = false, CloseOutput = false });
            var x = _xml;
            x.WriteStartDocument(true);
            x.WriteStartElement("worksheet", MainNs);
            if (_header)
            {
                x.WriteStartElement("sheetViews");
                x.WriteStartElement("sheetView");
                x.WriteAttributeString("workbookViewId", "0");
                x.WriteStartElement("pane");
                x.WriteAttributeString("ySplit", "1");
                x.WriteAttributeString("topLeftCell", "A2");
                x.WriteAttributeString("activePane", "bottomLeft");
                x.WriteAttributeString("state", "frozen");
                x.WriteEndElement();
                x.WriteEndElement();
                x.WriteEndElement();
            }

            if (_widths.Length > 0)
            {
                x.WriteStartElement("cols");
                for (var i = 0; i < _widths.Length; i++)
                {
                    x.WriteStartElement("col");
                    x.WriteAttributeString("min", Num(i + 1));
                    x.WriteAttributeString("max", Num(i + 1));
                    x.WriteAttributeString("width", _widths[i].ToString("0.##", CultureInfo.InvariantCulture));
                    x.WriteAttributeString("customWidth", "1");
                    x.WriteEndElement();
                }

                x.WriteEndElement();
            }

            x.WriteStartElement("sheetData");
            _rowNo = 0;
            _dataInSheet = 0;
            if (_header)
            {
                _rowNo = 1;
                x.WriteStartElement("row");
                x.WriteAttributeString("r", "1");
                for (var i = 0; i < _columns.Count; i++)
                {
                    WriteString(x, _colRefs[i] + "1", _columns[i].Name, style: 3);
                }

                x.WriteEndElement();
            }
        }

        private void EndSheet()
        {
            if (_xml is null)
            {
                return;
            }

            _xml.WriteEndElement(); // sheetData
            if (_header && _colRefs.Length > 0)
            {
                _xml.WriteStartElement("autoFilter");
                _xml.WriteAttributeString("ref", $"A1:{_colRefs[^1]}{Num(_rowNo)}");
                _xml.WriteEndElement();
            }

            _xml.WriteEndElement(); // worksheet
            _xml.WriteEndDocument();
            _xml.Flush();
            _xml.Dispose();
            _entry!.Dispose();
            _xml = null;
            _entry = null;
        }

        private void WriteCell(XmlWriter x, string r, ResultColumn c, object? v)
        {
            switch (v)
            {
                case null or DBNull:
                    return;
                case bool b:
                    x.WriteStartElement("c");
                    x.WriteAttributeString("r", r);
                    x.WriteAttributeString("t", "b");
                    x.WriteElementString("v", b ? "1" : "0");
                    x.WriteEndElement();
                    return;
                case decimal or long or int or short or byte or sbyte or ushort or uint or ulong:
                    WriteNumberOrText(x, r, Convert.ToString(v, CultureInfo.InvariantCulture)!);
                    return;
                case double d when double.IsFinite(d):
                    WriteNumberOrText(x, r, d.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case float f when float.IsFinite(f):
                    WriteNumberOrText(x, r, f.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case string s when c.Kind == ColumnDataKind.Number && IsPlainNumber(s):
                    WriteNumberOrText(x, r, s);
                    return;
                case DateTime dt when dt >= MinDate:
                    x.WriteStartElement("c");
                    x.WriteAttributeString("r", r);
                    x.WriteAttributeString("s", c.Kind == ColumnDataKind.Timestamp ? "2" : "1");
                    x.WriteElementString("v", Math.Round((dt - Epoch).TotalDays, 9).ToString("0.#########", CultureInfo.InvariantCulture));
                    x.WriteEndElement();
                    return;
                default:
                    WriteString(x, r, Text(c, v, Warnings), style: 0);
                    return;
            }
        }

        /// <summary>Excel 숫자는 유효숫자 15자리까지만 정확하다. 넘으면 값이 바뀌지 않게 글자로 쓴다.</summary>
        private void WriteNumberOrText(XmlWriter x, string r, string text)
        {
            if (SignificantDigits(text) > 15 || !IsPlainNumber(text))
            {
                WriteString(x, r, text, 0);
                return;
            }

            x.WriteStartElement("c");
            x.WriteAttributeString("r", r);
            x.WriteElementString("v", text);
            x.WriteEndElement();
        }

        private static int SignificantDigits(string s)
        {
            var mant = s;
            var e = s.IndexOfAny(['e', 'E']);
            if (e >= 0)
            {
                mant = s[..e];
            }

            var digits = mant.Where(char.IsAsciiDigit).ToArray();
            var start = 0;
            while (start < digits.Length - 1 && digits[start] == '0')
            {
                start++;
            }

            var end = digits.Length;
            if (mant.Contains('.', StringComparison.Ordinal))
            {
                while (end > start + 1 && digits[end - 1] == '0')
                {
                    end--;
                }
            }

            return end - start;
        }

        private static bool IsPlainNumber(string s) =>
            s.Length > 0 && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
            && !s.Contains(' ', StringComparison.Ordinal) && char.IsAsciiDigit(s[^1]) && s.All(ch => char.IsAsciiDigit(ch) || ch is '-' or '+' or '.' or 'E' or 'e');

        private void WriteString(XmlWriter x, string r, string text, int style)
        {
            text = Sanitize(text);
            if (text.Length > MaxCellChars)
            {
                var msg = "셀 글자 수가 Excel 한도(32,767)를 넘어 잘라 끝에 …를 붙였다.";
                if (!Warnings.Contains(msg))
                {
                    Warnings.Add(msg);
                }

                // 끝에 「…」 한 글자를 붙여 잘렸음을 알린다. 서로게이트 쌍을 가운데서 자르지 않는다.
                var cut = MaxCellChars - 1;
                if (char.IsHighSurrogate(text[cut - 1]))
                {
                    cut--;
                }

                text = text[..cut] + "…";
            }

            x.WriteStartElement("c");
            x.WriteAttributeString("r", r);
            if (style != 0)
            {
                x.WriteAttributeString("s", Num(style));
            }

            x.WriteAttributeString("t", "inlineStr");
            x.WriteStartElement("is");
            x.WriteStartElement("t");
            x.WriteAttributeString("xml", "space", null, "preserve");
            x.WriteString(text);
            x.WriteEndElement();
            x.WriteEndElement();
            x.WriteEndElement();
        }

        /// <summary>머리글과 앞 행의 표시 길이로 열 너비(글자 수 단위)를 정한다. 한글 같은 넓은 글자는 두 칸, 최대 60.</summary>
        private double[] MeasureWidths(List<object?[]> rows)
        {
            var widths = new double[_columns.Count];
            for (var c = 0; c < widths.Length; c++)
            {
                // 머리글은 굵고 자동 필터 단추가 붙으므로 여유를 더 둔다.
                var w = _header ? DisplayWidth(_columns[c].Name) + 3 : 0;
                foreach (var row in rows)
                {
                    w = Math.Max(w, DisplayWidth(Sample(_columns[c], row[c])));
                }

                widths[c] = Math.Clamp(w + 2, 8, MaxColumnWidth);
            }

            return widths;
        }

        private static string Sample(ResultColumn c, object? v)
        {
            switch (v)
            {
                case null or DBNull:
                    return string.Empty;
                case LobValue lob:
                    return lob.Kind is LobKind.Blob or LobKind.BFile ? new string('0', (int)Math.Min(lob.BinaryPreview.Length * 2L, 64)) : lob.TextPreview ?? string.Empty;
                case DateTime:
                    return c.Kind == ColumnDataKind.Timestamp ? "0000-00-00 00:00:00.000" : "0000-00-00 00:00:00";
                case string s:
                    // 줄 바꿈이 있는 글은 가장 긴 줄 기준.
                    return s.Split('\n').MaxBy(l => l.Length) ?? string.Empty;
                default:
                    return CellValueFormatter.Format(c, v, ExportDisplay);
            }
        }

        private static int DisplayWidth(string s)
        {
            var w = 0;
            foreach (var ch in s)
            {
                w += ch >= 0x1100 && (ch <= 0x115F || (ch >= 0x2E80 && ch <= 0xA4CF) || (ch >= 0xAC00 && ch <= 0xD7A3) || (ch >= 0xF900 && ch <= 0xFAFF)
                    || (ch >= 0xFE30 && ch <= 0xFE6F) || (ch >= 0xFF00 && ch <= 0xFF60) || (ch >= 0xFFE0 && ch <= 0xFFE6)) ? 2 : 1;
            }

            return w;
        }

        /// <summary>XML 1.0 에서 쓸 수 없는 글자를 뺀다.</summary>
        private static string Sanitize(string s)
        {
            var bad = false;
            for (var i = 0; i < s.Length; i++)
            {
                if (!IsXmlChar(s, i))
                {
                    bad = true;
                    break;
                }
            }

            if (!bad)
            {
                return s;
            }

            var sb = new StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                if (IsXmlChar(s, i))
                {
                    sb.Append(s[i]);
                }
            }

            return sb.ToString();
        }

        private static bool IsXmlChar(string s, int i)
        {
            var ch = s[i];
            if (ch is '\t' or '\n' or '\r' || (ch >= 0x20 && ch < 0xD800) || (ch >= 0xE000 && ch <= 0xFFFD))
            {
                return true;
            }

            if (char.IsHighSurrogate(ch))
            {
                return i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]);
            }

            return char.IsLowSurrogate(ch) && i > 0 && char.IsHighSurrogate(s[i - 1]);
        }

        private void WriteStatic()
        {
            Part("[Content_Types].xml", w =>
            {
                w.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
                Default(w, "rels", "application/vnd.openxmlformats-package.relationships+xml");
                Default(w, "xml", "application/xml");
                Override(w, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
                Override(w, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
                for (var i = 1; i <= _sheetCount; i++)
                {
                    Override(w, $"/xl/worksheets/sheet{i}.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
                }

                w.WriteEndElement();
            });

            Part("_rels/.rels", w =>
            {
                w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
                Rel(w, "rId1", RelNs + "/officeDocument", "xl/workbook.xml");
                w.WriteEndElement();
            });

            Part("xl/workbook.xml", w =>
            {
                w.WriteStartElement("workbook", MainNs);
                w.WriteAttributeString("xmlns", "r", null, RelNs);
                w.WriteStartElement("sheets");
                for (var i = 1; i <= _sheetCount; i++)
                {
                    w.WriteStartElement("sheet");
                    w.WriteAttributeString("name", XlsxSheetName(_sheetName, i));
                    w.WriteAttributeString("sheetId", Num(i));
                    w.WriteAttributeString("r", "id", RelNs, "rId" + Num(i));
                    w.WriteEndElement();
                }

                w.WriteEndElement();
                w.WriteEndElement();
            });

            Part("xl/_rels/workbook.xml.rels", w =>
            {
                w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
                for (var i = 1; i <= _sheetCount; i++)
                {
                    Rel(w, "rId" + Num(i), RelNs + "/worksheet", $"worksheets/sheet{i}.xml");
                }

                Rel(w, "rId" + Num(_sheetCount + 1), RelNs + "/styles", "styles.xml");
                w.WriteEndElement();
            });

            Part("xl/styles.xml", w =>
            {
                w.WriteStartElement("styleSheet", MainNs);
                w.WriteStartElement("numFmts");
                w.WriteAttributeString("count", "2");
                NumFmt(w, 164, "yyyy\\-mm\\-dd\\ hh:mm:ss");
                NumFmt(w, 165, "yyyy\\-mm\\-dd\\ hh:mm:ss.000");
                w.WriteEndElement();
                w.WriteStartElement("fonts");
                w.WriteAttributeString("count", "2");
                w.WriteRaw("<font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font>");
                w.WriteEndElement();
                w.WriteRaw("<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>"
                    + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFEDEDED\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>");
                w.WriteRaw("<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>");
                w.WriteRaw("<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");
                w.WriteRaw("<cellXfs count=\"4\">"
                    + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"
                    + "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
                    + "<xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
                    + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>"
                    + "</cellXfs>");
                w.WriteRaw("<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>");
                w.WriteEndElement();
            });
        }

        private static void Default(XmlWriter w, string ext, string type)
        {
            w.WriteStartElement("Default");
            w.WriteAttributeString("Extension", ext);
            w.WriteAttributeString("ContentType", type);
            w.WriteEndElement();
        }

        private static void Override(XmlWriter w, string part, string type)
        {
            w.WriteStartElement("Override");
            w.WriteAttributeString("PartName", part);
            w.WriteAttributeString("ContentType", type);
            w.WriteEndElement();
        }

        private static void Rel(XmlWriter w, string id, string type, string target)
        {
            w.WriteStartElement("Relationship");
            w.WriteAttributeString("Id", id);
            w.WriteAttributeString("Type", type);
            w.WriteAttributeString("Target", target);
            w.WriteEndElement();
        }

        private static void NumFmt(XmlWriter w, int id, string code)
        {
            w.WriteStartElement("numFmt");
            w.WriteAttributeString("numFmtId", Num(id));
            w.WriteAttributeString("formatCode", code);
            w.WriteEndElement();
        }

        private void Part(string name, Action<XmlWriter> body)
        {
            using var stream = _zip.CreateEntry(name, CompressionLevel.Fastest).Open();
            using var w = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false });
            w.WriteStartDocument(true);
            body(w);
            w.WriteEndDocument();
        }
    }
}
