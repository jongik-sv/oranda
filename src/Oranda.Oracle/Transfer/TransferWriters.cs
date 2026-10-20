using System.Globalization;
using System.Text;
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

internal static partial class TransferWriters
{
    internal static readonly GridDisplayOptions ExportDisplay = GridDisplayOptions.Plain with { MaxVectorElements = int.MaxValue };

    internal static ITransferWriter Create(ExportOptions options, Stream destination) => options.Format switch
    {
        TransferFormat.Csv => new DelimitedWriter(destination, ',', options.IncludeHeader),
        TransferFormat.Tsv => new DelimitedWriter(destination, '\t', options.IncludeHeader),
        TransferFormat.Json => new JsonRowWriter(destination),
        TransferFormat.Insert => new InsertWriter(destination, options.InsertTableName),
        TransferFormat.Xlsx => new XlsxWriter(destination, options.IncludeHeader, options.MaxRowsPerSheet, options.SheetName),
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

    /// <summary>
    /// xlsx 시트 이름. Excel 은 31자 이하이고 <c>[ ] : * ? / \</c> 를 못 쓰며 앞뒤 작은따옴표·빈 이름·<c>History</c> 도 안 된다.
    /// 이름이 비면 <c>Sheet{index}</c>. 둘째 시트부터는 <c>_{index}</c> 를 붙여 31자 안에서 겹치지 않게 한다.
    /// </summary>
    internal static string XlsxSheetName(string? name, int index)
    {
        var clean = new string((name ?? string.Empty).Select(ch => ch is '[' or ']' or ':' or '*' or '?' or '/' or '\\' || char.IsControl(ch) ? '_' : ch).ToArray())
            .Trim().Trim('\'').Trim();
        if (clean.Length == 0)
        {
            return "Sheet" + index.ToString(CultureInfo.InvariantCulture);
        }

        if (clean.Equals("History", StringComparison.OrdinalIgnoreCase))
        {
            clean += "_";
        }

        var suffix = index > 1 ? "_" + index.ToString(CultureInfo.InvariantCulture) : string.Empty;
        var room = 31 - suffix.Length;
        if (clean.Length > room)
        {
            // 서로게이트 쌍을 가운데서 자르지 않는다.
            var cut = char.IsHighSurrogate(clean[room - 1]) ? room - 1 : room;
            clean = clean[..cut];
        }

        return clean + suffix;
    }

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
}
