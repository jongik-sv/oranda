using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Oranda.Core.Grid;

/// <summary>
/// 결과 셀 값 → 표시 글자 / SQL 리터럴 변환(TRD 4.4 「타입 표시」, PRD GRID-02·GRID-08). 순수 함수라 UI 없이 시험한다.
/// </summary>
public static class CellValueFormatter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly GridDisplayOptions FullVector = new() { ExpandVector = true, MaxVectorElements = int.MaxValue };

    /// <summary>셀 1개의 표시 글자.</summary>
    public static string Format(ResultColumn column, object? value, GridDisplayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(column);
        var o = options ?? GridDisplayOptions.Default;
        if (value is null or DBNull)
        {
            return o.NullText;
        }

        string text;
        if (column.Kind == ColumnDataKind.IntervalYearMonth && value is long or int)
        {
            text = FormatIntervalYm(Convert.ToInt64(value, Inv));
        }
        else
        {
            text = value switch
            {
                string s => FormatString(column, s, o),
                bool b => b ? o.TrueText : o.FalseText,
                decimal or long or int or short or byte or sbyte or ushort or uint or ulong => FormatNumber(Convert.ToString(value, Inv)!, o),
                double d => FormatFloating(d),
                float f => FormatFloating(f),
                DateTime dt => dt.ToString(column.Kind is ColumnDataKind.Timestamp ? o.TimestampFormat : o.DateFormat, Inv),
                DateTimeOffset dto => dto.ToString(o.TimestampTzFormat, Inv),
                TimeSpan ts => FormatIntervalDs(ts),
                byte[] bytes when IsByteArray(bytes) => column.Kind == ColumnDataKind.Vector ? FormatVector(bytes, o) : Convert.ToHexString(bytes),
                float[] or double[] or sbyte[] => FormatVector(value, o),
                LobValue lob => FormatLob(lob),
                JsonElement je => FormatJson(je.GetRawText(), o),
                JsonDocument jd => FormatJson(jd.RootElement.GetRawText(), o),
                _ => Convert.ToString(value, Inv) ?? string.Empty,
            };
        }

        return o.SingleLine ? Flatten(text) : text;
    }

    /// <summary>INSERT·IN 목록에 넣을 SQL 리터럴.</summary>
    public static string ToSqlLiteral(ResultColumn column, object? value)
    {
        ArgumentNullException.ThrowIfNull(column);
        switch (value)
        {
            case null or DBNull:
                return "NULL";
            case bool b:
                return b ? "TRUE" : "FALSE";
            case decimal or short or byte or sbyte or ushort or uint or ulong:
                return Convert.ToString(value, Inv)!;
            case long or int when column.Kind == ColumnDataKind.IntervalYearMonth:
                return $"INTERVAL '{FormatIntervalYm(Convert.ToInt64(value, Inv)).TrimStart('+')}' YEAR TO MONTH";
            case long or int:
                return Convert.ToString(value, Inv)!;
            case double d:
                return FloatingLiteral(d, "TO_BINARY_DOUBLE");
            case float f:
                return FloatingLiteral(f, "TO_BINARY_FLOAT");
            case DateTime dt when column.Kind == ColumnDataKind.Timestamp:
                return $"TO_TIMESTAMP('{dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", Inv)}', 'YYYY-MM-DD HH24:MI:SS.FF6')";
            case DateTime dt:
                return $"TO_DATE('{dt.ToString("yyyy-MM-dd HH:mm:ss", Inv)}', 'YYYY-MM-DD HH24:MI:SS')";
            case DateTimeOffset dto:
                return $"TO_TIMESTAMP_TZ('{dto.ToString("yyyy-MM-dd HH:mm:ss.ffffff zzz", Inv)}', 'YYYY-MM-DD HH24:MI:SS.FF6 TZH:TZM')";
            case TimeSpan ts:
                return $"INTERVAL '{FormatIntervalDs(ts).TrimStart('+')}' DAY TO SECOND";
            case byte[] bytes when IsByteArray(bytes) && column.Kind == ColumnDataKind.Vector:
                return $"TO_VECTOR('{FormatVector(bytes, FullVector)}')";
            case byte[] bytes when IsByteArray(bytes):
                return bytes.Length == 0 ? "NULL" : $"HEXTORAW('{Convert.ToHexString(bytes)}')";
            case float[] or double[] or sbyte[]:
                return $"TO_VECTOR('{FormatVector(value, FullVector)}')";
            case LobValue lob:
                return LobLiteral(lob);
            case string s when column.Kind == ColumnDataKind.Number && IsPlainNumber(s):
                return s;
            case string s when column.Kind == ColumnDataKind.Xml:
                return $"XMLTYPE({Quote(s)})";
            case string s:
                return Quote(s);
            case JsonElement je:
                return Quote(je.GetRawText());
            default:
                return Quote(Convert.ToString(value, Inv) ?? string.Empty);
        }
    }

    /// <summary>SQL 문자열 리터럴(<c>'…'</c>, 작은따옴표는 두 번).</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>식별자가 단순 대문자 이름이면 그대로, 아니면 큰따옴표로 감싼다.</summary>
    public static string QuoteIdentifier(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var simple = name.Length > 0 && name[0] is >= 'A' and <= 'Z'
            && name.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '$' or '#');
        return simple ? name : "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>JSON 글자를 압축 또는 들여쓰기 형태로. 파싱 실패 시 원문.</summary>
    public static string FormatJson(string json, GridDisplayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            using var doc = JsonDocument.Parse(json);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = options.PrettyJson, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                doc.WriteTo(w);
            }

            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (JsonException)
        {
            return json;
        }
    }

    /// <summary>Oracle 날짜 형식 표기(<c>YYYY-MM-DD HH24:MI:SS</c>, <c>DD-MON-RR</c> …)를 .NET 사용자 지정 형식으로 바꾼다.</summary>
    public static string NlsToDotNetFormat(string nls)
    {
        ArgumentNullException.ThrowIfNull(nls);
        var sb = new StringBuilder();
        var i = 0;
        while (i < nls.Length)
        {
            if (nls[i] == '"')
            {
                var end = nls.IndexOf('"', i + 1);
                end = end < 0 ? nls.Length : end;
                sb.Append('\'').Append(nls, i + 1, end - i - 1).Append('\'');
                i = end + 1;
                continue;
            }

            var hit = false;
            foreach (var (oracle, net) in NlsMap)
            {
                if (string.Compare(nls, i, oracle, 0, oracle.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    sb.Append(net);
                    i += oracle.Length;
                    hit = true;
                    break;
                }
            }

            if (!hit)
            {
                var ch = nls[i++];
                sb.Append(char.IsLetter(ch) || ch is '/' or ':' or '.' or '\\' or '\'' or '%' ? "\\" + ch : ch.ToString());
            }
        }

        return sb.ToString();
    }

    private static readonly (string Oracle, string Net)[] NlsMap =
    [
        ("TZH:TZM", "zzz"), ("HH24", "HH"), ("HH12", "hh"), ("YYYY", "yyyy"), ("RRRR", "yyyy"), ("MONTH", "MMMM"),
        ("DAY", "dddd"), ("MON", "MMM"), ("HH", "hh"), ("MI", "mm"), ("SS", "ss"), ("DD", "dd"), ("MM", "MM"),
        ("YY", "yy"), ("RR", "yy"), ("DY", "ddd"), ("AM", "tt"), ("PM", "tt"), ("FF9", "fffffffff"), ("FF8", "ffffffff"),
        ("FF7", "fffffff"), ("FF6", "ffffff"), ("FF5", "fffff"), ("FF4", "ffff"), ("FF3", "fff"), ("FF2", "ff"), ("FF1", "f"),
        ("FF", "ffffff"),
    ];

    /// <summary>CLR 배열 공변성 때문에 <c>sbyte[] is byte[]</c> 가 참이다. 정확한 형식만 가려낸다.</summary>
    private static bool IsByteArray(object o) => o.GetType() == typeof(byte[]);

    private static string FormatString(ResultColumn column, string s, GridDisplayOptions o) => column.Kind switch
    {
        ColumnDataKind.Number => FormatNumber(s, o),
        ColumnDataKind.Json => FormatJson(s, o),
        _ => s,
    };

    private static string FormatNumber(string s, GridDisplayOptions o)
    {
        if (!o.ThousandsSeparator || !IsPlainNumber(s) || s.Contains('E', StringComparison.OrdinalIgnoreCase))
        {
            return s;
        }

        var start = s[0] is '-' or '+' ? 1 : 0;
        var dot = s.IndexOf('.', StringComparison.Ordinal);
        var intEnd = dot < 0 ? s.Length : dot;
        var digits = s[start..intEnd];
        var sb = new StringBuilder(s[..start]);
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0)
            {
                sb.Append(',');
            }

            sb.Append(digits[i]);
        }

        return sb.Append(s, intEnd, s.Length - intEnd).ToString();
    }

    private static string FormatFloating(double d) =>
        double.IsNaN(d) ? "NaN" : double.IsPositiveInfinity(d) ? "Inf" : double.IsNegativeInfinity(d) ? "-Inf" : d.ToString("R", Inv);

    private static string FormatFloating(float f) =>
        float.IsNaN(f) ? "NaN" : float.IsPositiveInfinity(f) ? "Inf" : float.IsNegativeInfinity(f) ? "-Inf" : f.ToString("R", Inv);

    private static string FloatingLiteral(double d, string fn) =>
        double.IsNaN(d) ? $"{fn}('NaN')" : double.IsInfinity(d) ? $"{fn}('{(d > 0 ? "INF" : "-INF")}')" : d.ToString("R", Inv);

    private static string FormatIntervalDs(TimeSpan ts)
    {
        var sign = ts < TimeSpan.Zero ? '-' : '+';
        var a = ts.Duration();
        var micro = a.Ticks % TimeSpan.TicksPerSecond / 10;
        return string.Create(Inv, $"{sign}{a.Days:00} {a.Hours:00}:{a.Minutes:00}:{a.Seconds:00}.{micro:000000}");
    }

    private static string FormatIntervalYm(long months)
    {
        var sign = months < 0 ? '-' : '+';
        var a = Math.Abs(months);
        return string.Create(Inv, $"{sign}{a / 12:00}-{a % 12:00}");
    }

    private static string FormatVector(object value, GridDisplayOptions o)
    {
        var (count, format) = value switch
        {
            float[] f => (f.Length, "FLOAT32"),
            double[] d => (d.Length, "FLOAT64"),
            byte[] b when IsByteArray(b) => (b.Length * 8, "BINARY"),
            sbyte[] s => (s.Length, "INT8"),
            _ => (0, "FLOAT32"),
        };
        if (!o.ExpandVector)
        {
            return $"VECTOR({count}, {format})";
        }

        var shown = Math.Min(count, o.MaxVectorElements);
        var items = new List<string>(Math.Min(shown, 4096));
        for (var i = 0; i < shown; i++)
        {
            items.Add(value switch
            {
                float[] f => f[i].ToString("R", Inv),
                double[] d => d[i].ToString("R", Inv),
                byte[] b when IsByteArray(b) => b[i].ToString(Inv),
                sbyte[] s => s[i].ToString(Inv),
                _ => string.Empty,
            });
        }

        return "[" + string.Join(", ", items) + (shown < count ? ", …" : string.Empty) + "]";
    }

    private static string FormatLob(LobValue lob)
    {
        var more = lob.IsTruncated ? "…" : string.Empty;
        switch (lob.Kind)
        {
            case LobKind.Clob or LobKind.NClob:
                return (lob.TextPreview ?? string.Empty) + more;
            case LobKind.Blob:
                var n = Math.Min(16, lob.BinaryPreview.Length);
                var tail = lob.BinaryPreview.Length > n || lob.IsTruncated ? "…" : string.Empty;
                return string.Create(Inv, $"(BLOB {lob.Length:N0} B) ") + Convert.ToHexString(lob.BinaryPreview.Span[..n]) + tail;
            default:
                return string.Create(Inv, $"(BFILE {lob.Length:N0} B)");
        }
    }

    private static string LobLiteral(LobValue lob)
    {
        if (lob.IsTruncated)
        {
            return "NULL /* LOB truncated */";
        }

        if (lob.Kind is LobKind.Clob or LobKind.NClob)
        {
            var text = lob.TextPreview ?? string.Empty;
            return text.Length == 0 ? "NULL" : string.Join(" || ", Chunks(text, 4000).Select(c => $"TO_CLOB({Quote(c)})"));
        }

        if (lob.Kind == LobKind.Blob && lob.BinaryPreview.Length > 0)
        {
            var hex = Convert.ToHexString(lob.BinaryPreview.Span);
            return string.Join(" || ", Chunks(hex, 4000).Select(c => $"TO_BLOB(HEXTORAW('{c}'))"));
        }

        return "NULL";
    }

    private static IEnumerable<string> Chunks(string s, int size)
    {
        for (var i = 0; i < s.Length; i += size)
        {
            yield return s.Substring(i, Math.Min(size, s.Length - i));
        }
    }

    private static bool IsPlainNumber(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        var i = s[0] is '-' or '+' ? 1 : 0;
        var digits = 0;
        var dots = 0;
        for (; i < s.Length; i++)
        {
            if (char.IsAsciiDigit(s[i]))
            {
                digits++;
            }
            else if (s[i] == '.' && ++dots == 1)
            {
                continue;
            }
            else if (s[i] is 'E' or 'e' && digits > 0)
            {
                var rest = s[(i + 1)..].TrimStart('+', '-');
                return rest.Length > 0 && rest.All(char.IsAsciiDigit);
            }
            else
            {
                return false;
            }
        }

        return digits > 0;
    }

    private static string Flatten(string s)
    {
        if (s.AsSpan().IndexOfAny('\r', '\n', '\t') < 0)
        {
            return s;
        }

        return s.Replace("\r\n", "↵", StringComparison.Ordinal).Replace('\n', '↵').Replace('\r', '↵').Replace('\t', ' ');
    }
}
