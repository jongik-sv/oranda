using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using Oranda.Core.Grid;
using Oranda.Core.Transfer;

namespace Oranda.Oracle.Transfer;

/// <summary>내보내기 파일 쓰기(행 단위, 스트리밍). 소유: TSK-06-07.</summary>
internal interface ITransferWriter : IDisposable
{
    IList<string> Warnings { get; }

    void Begin(IReadOnlyList<ResultColumn> columns);

    void WriteRow(object?[] values);

    void Finish();
}

internal static class TransferWriters
{
    internal static readonly GridDisplayOptions ExportDisplay = GridDisplayOptions.Plain with { MaxVectorElements = int.MaxValue };

    internal static ITransferWriter Create(ExportOptions options, Stream destination) => options.Format switch
    {
        TransferFormat.Csv => new DelimitedWriter(destination, ',', options.IncludeHeader),
        TransferFormat.Tsv => new DelimitedWriter(destination, '\t', options.IncludeHeader),
        TransferFormat.Json => new JsonRowWriter(destination),
        TransferFormat.Insert => new InsertWriter(destination, options.InsertTableName),
        TransferFormat.Xlsx => new XlsxWriter(destination, options.IncludeHeader, options.MaxRowsPerSheet),
        _ => throw new ArgumentOutOfRangeException(nameof(options)),
    };

    /// <summary>LOB 미리보기까지만 가져온 값이면 경고를 한 번 남기려고 쓰는 글자 변환. LOB 은 내용 그대로(CLOB 글자, BLOB 16진수).</summary>
    internal static string Text(ResultColumn column, object? value, ICollection<string> warnings)
    {
        if (value is LobValue lob)
        {
            if (lob.IsTruncated)
            {
                var msg = $"열 {column.Name}: LOB 값은 미리보기 길이까지만 내보냈다.";
                if (!warnings.Contains(msg))
                {
                    warnings.Add(msg);
                }
            }

            return lob.Kind is LobKind.Blob or LobKind.BFile
                ? Convert.ToHexString(lob.BinaryPreview.Span)
                : lob.TextPreview ?? string.Empty;
        }

        return CellValueFormatter.Format(column, value, ExportDisplay);
    }

    private static bool IsNull(object? v) => v is null or DBNull;

    // ---- CSV / TSV ----
    private sealed class DelimitedWriter : ITransferWriter
    {
        private readonly StreamWriter _w;
        private readonly char _delimiter;
        private readonly bool _header;
        private IReadOnlyList<ResultColumn> _columns = [];

        public DelimitedWriter(Stream destination, char delimiter, bool header)
        {
            // Excel 에서 한글이 깨지지 않게 BOM 을 붙인다.
            _w = new StreamWriter(destination, new UTF8Encoding(true), 64 * 1024, leaveOpen: true) { NewLine = "\r\n" };
            _delimiter = delimiter;
            _header = header;
        }

        public IList<string> Warnings { get; } = [];

        public void Begin(IReadOnlyList<ResultColumn> columns)
        {
            _columns = columns;
            if (_header)
            {
                WriteFields(columns.Select(c => c.Name));
            }
        }

        public void WriteRow(object?[] values) =>
            WriteFields(values.Select((v, i) => IsNull(v) ? string.Empty : Text(_columns[i], v, Warnings)));

        public void Finish() => _w.Flush();

        public void Dispose() => _w.Dispose();

        private void WriteFields(IEnumerable<string> fields)
        {
            var first = true;
            foreach (var f in fields)
            {
                if (!first)
                {
                    _w.Write(_delimiter);
                }

                first = false;
                _w.Write(Escape(f));
            }

            _w.Write("\r\n");
        }

        private string Escape(string s)
        {
            if (s.Length == 0)
            {
                return s;
            }

            var needs = s.Contains(_delimiter, StringComparison.Ordinal) || s.Contains('"', StringComparison.Ordinal)
                || s.Contains('\r', StringComparison.Ordinal) || s.Contains('\n', StringComparison.Ordinal)
                || s[0] == ' ' || s[^1] == ' ';
            return needs ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;
        }
    }

    // ---- JSON ----
    private sealed class JsonRowWriter : ITransferWriter
    {
        private readonly Utf8JsonWriter _w;
        private IReadOnlyList<ResultColumn> _columns = [];
        private string[] _names = [];

        public JsonRowWriter(Stream destination)
        {
            _w = new Utf8JsonWriter(destination, new JsonWriterOptions
            {
                Indented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }

        public IList<string> Warnings { get; } = [];

        public void Begin(IReadOnlyList<ResultColumn> columns)
        {
            _columns = columns;
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            _names = columns.Select(c =>
            {
                seen.TryGetValue(c.Name, out var n);
                seen[c.Name] = n + 1;
                return n == 0 ? c.Name : c.Name + "_" + (n + 1).ToString(CultureInfo.InvariantCulture);
            }).ToArray();
            _w.WriteStartArray();
        }

        public void WriteRow(object?[] values)
        {
            _w.WriteStartObject();
            for (var i = 0; i < values.Length; i++)
            {
                _w.WritePropertyName(_names[i]);
                WriteValue(_columns[i], values[i]);
            }

            _w.WriteEndObject();
            if (_w.BytesPending > 32 * 1024)
            {
                _w.Flush();
            }
        }

        public void Finish()
        {
            _w.WriteEndArray();
            _w.Flush();
        }

        public void Dispose() => _w.Dispose();

        private void WriteValue(ResultColumn c, object? v)
        {
            switch (v)
            {
                case null or DBNull:
                    _w.WriteNullValue();
                    break;
                case bool b:
                    _w.WriteBooleanValue(b);
                    break;
                case decimal or long or int or short or byte or sbyte or ushort or uint or ulong:
                    _w.WriteRawValue(Convert.ToString(v, CultureInfo.InvariantCulture)!);
                    break;
                case double d when double.IsFinite(d):
                    _w.WriteRawValue(d.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case float f when float.IsFinite(f):
                    _w.WriteRawValue(f.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case string s when c.Kind == ColumnDataKind.Number && IsJsonNumber(s):
                    _w.WriteRawValue(s);
                    break;
                case string s when c.Kind == ColumnDataKind.Json && TryParseJson(s, out var doc):
                    using (doc)
                    {
                        doc.RootElement.WriteTo(_w);
                    }

                    break;
                case DateTime dt:
                    _w.WriteStringValue(dt.ToString(c.Kind == ColumnDataKind.Timestamp ? "yyyy-MM-dd'T'HH:mm:ss.ffffff" : "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
                    break;
                case DateTimeOffset dto:
                    _w.WriteStringValue(dto.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture));
                    break;
                default:
                    _w.WriteStringValue(Text(c, v, Warnings));
                    break;
            }
        }

        private static bool IsJsonNumber(string s)
        {
            // JSON 숫자 문법: -?(0|[1-9]\d*)(\.\d+)?([eE][+-]?\d+)?  (Oracle 의 .5, 5. 같은 표기는 글자로 둔다)
            var i = 0;
            if (i < s.Length && s[i] == '-')
            {
                i++;
            }

            var intStart = i;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
            {
                i++;
            }

            if (i == intStart || (s[intStart] == '0' && i - intStart > 1))
            {
                return false;
            }

            if (i < s.Length && s[i] == '.')
            {
                i++;
                var f = i;
                while (i < s.Length && char.IsAsciiDigit(s[i]))
                {
                    i++;
                }

                if (i == f)
                {
                    return false;
                }
            }

            if (i < s.Length && s[i] is 'e' or 'E')
            {
                i++;
                if (i < s.Length && s[i] is '+' or '-')
                {
                    i++;
                }

                var e = i;
                while (i < s.Length && char.IsAsciiDigit(s[i]))
                {
                    i++;
                }

                if (i == e)
                {
                    return false;
                }
            }

            return i == s.Length;
        }

        private static bool TryParseJson(string s, out JsonDocument doc)
        {
            try
            {
                doc = JsonDocument.Parse(s);
                return true;
            }
            catch (JsonException)
            {
                doc = null!;
                return false;
            }
        }
    }

    // ---- INSERT ----
    private sealed class InsertWriter : ITransferWriter
    {
        private readonly StreamWriter _w;
        private readonly string _table;
        private IReadOnlyList<ResultColumn> _columns = [];
        private string _prefix = string.Empty;

        public InsertWriter(Stream destination, string? table)
        {
            _w = new StreamWriter(destination, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { NewLine = "\n" };
            _table = string.IsNullOrWhiteSpace(table) ? "TABLE_NAME" : table.Trim();
        }

        public IList<string> Warnings { get; } = [];

        public void Begin(IReadOnlyList<ResultColumn> columns)
        {
            _columns = columns;
            _prefix = "INSERT INTO " + _table + " (" + string.Join(", ", columns.Select(c => CellValueFormatter.QuoteIdentifier(c.Name))) + ") VALUES (";
        }

        public void WriteRow(object?[] values)
        {
            _w.Write(_prefix);
            for (var i = 0; i < values.Length; i++)
            {
                if (i > 0)
                {
                    _w.Write(", ");
                }

                _w.Write(Literal(_columns[i], values[i]));
            }

            _w.WriteLine(");");
        }

        public void Finish() => _w.Flush();

        public void Dispose() => _w.Dispose();

        private string Literal(ResultColumn c, object? v)
        {
            if (v is LobValue { IsTruncated: true })
            {
                var msg = $"열 {c.Name}: LOB 값은 미리보기 길이까지만 내보냈다.";
                if (!Warnings.Contains(msg))
                {
                    Warnings.Add(msg);
                }
            }

            return CellValueFormatter.ToSqlLiteral(c, v);
        }
    }

    // ---- XLSX ----
    private sealed class XlsxWriter : ITransferWriter
    {
        private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const int MaxCellChars = 32767;
        private static readonly DateTime Epoch = new(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified);
        private static readonly DateTime MinDate = new(1900, 3, 1, 0, 0, 0, DateTimeKind.Unspecified);

        private readonly ZipArchive _zip;
        private readonly bool _header;
        private readonly long _maxData;
        private IReadOnlyList<ResultColumn> _columns = [];
        private string[] _colRefs = [];
        private XmlWriter? _xml;
        private Stream? _entry;
        private int _sheetCount;
        private long _rowNo;
        private long _dataInSheet;

        public XlsxWriter(Stream destination, bool header, int maxRowsPerSheet)
        {
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
            StartSheet();
        }

        public void WriteRow(object?[] values)
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
            if (_xml is null)
            {
                StartSheet();
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
                var msg = "셀 글자 수가 Excel 한도(32,767)를 넘어 잘랐다.";
                if (!Warnings.Contains(msg))
                {
                    Warnings.Add(msg);
                }

                text = text[..MaxCellChars];
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
                    w.WriteAttributeString("name", "Sheet" + Num(i));
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
                w.WriteRaw("<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>");
                w.WriteRaw("<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>");
                w.WriteRaw("<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");
                w.WriteRaw("<cellXfs count=\"4\">"
                    + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"
                    + "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
                    + "<xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
                    + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>"
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
