using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.History;
using Oranda.Core.Language;

namespace Oranda.Language.Ai.Handlers.External;

/// <summary>외부 연결 도구 결과 JSON 의 공용 조각: 줄·열, 분류 낱말, 셀 값.</summary>
internal static class ExternalJson
{
    /// <summary>셀 글 하나의 최대 글자. 넘으면 자르고 끝에 「…」.</summary>
    public const int MaxCellChars = 4000;

    /// <summary>위치(UTF-16)의 줄·열(둘 다 1부터). 줄 바꿈은 <c>\n</c> 으로 센다(<c>\r\n</c> 도 한 줄).</summary>
    public static (int Line, int Col) LineCol(string text, int offset)
    {
        ArgumentNullException.ThrowIfNull(text);
        offset = Math.Clamp(offset, 0, text.Length);
        var line = 1;
        var lineStart = 0;
        for (var i = 0; i < offset; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        return (line, offset - lineStart + 1);
    }

    /// <summary>문장 분류 낱말(<c>query</c>·<c>dml</c>·<c>ddl</c>·<c>plsql</c>·<c>transaction_control</c>·<c>sqlplus</c>·<c>other</c>).</summary>
    public static string KindName(StatementKind kind) => kind switch
    {
        StatementKind.Query => "query",
        StatementKind.Dml => "dml",
        StatementKind.Ddl => "ddl",
        StatementKind.PlSql => "plsql",
        StatementKind.TransactionControl => "transaction_control",
        StatementKind.SqlPlus => "sqlplus",
        _ => "other",
    };

    public static string SeverityName(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => "error",
        DiagnosticSeverity.Warning => "warning",
        _ => "info",
    };

    /// <summary>이력 출처 낱말(snake_case, 예 <c>grid_edit</c>).</summary>
    public static string SourceName(HistorySource source) => Snake(source.ToString());

    /// <summary>출처 낱말을 읽는다(대소문자·<c>_</c> 무시). 모르면 false.</summary>
    public static bool TryParseSource(string text, out HistorySource source)
    {
        var key = text.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).Trim();
        foreach (var value in Enum.GetValues<HistorySource>())
        {
            if (string.Equals(value.ToString(), key, StringComparison.OrdinalIgnoreCase))
            {
                source = value;
                return true;
            }
        }

        source = default;
        return false;
    }

    public static string StatusName(HistoryStatus status) => Snake(status.ToString());

    /// <summary>글을 <paramref name="max"/> 자로 자른다. 잘랐으면 끝에 「…」.</summary>
    public static string Clip(string text, int max, out bool clipped)
    {
        clipped = text.Length > max;
        return clipped ? text[..max] + "…" : text;
    }

    /// <summary>
    /// 결과 셀 값 → JSON. 숫자·참거짓은 그대로, 날짜는 ISO 꼴 글, 이진 값은 16진 글, LOB 은 미리 읽은 앞부분, 나머지는 글. 긴 글은 <see cref="MaxCellChars"/> 에서 자른다.
    /// </summary>
    public static JsonNode? Cell(object? value)
    {
        switch (value)
        {
            case null or DBNull:
                return null;
            case string s:
                return JsonValue.Create(Clip(s, MaxCellChars, out _));
            case bool b:
                return JsonValue.Create(b);
            case int or long or short or byte or sbyte or uint or ulong or ushort:
                return JsonValue.Create(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
            case decimal m:
                return JsonValue.Create(m);
            case double d:
                return double.IsFinite(d) ? JsonValue.Create(d) : JsonValue.Create(d.ToString(CultureInfo.InvariantCulture));
            case float f:
                return float.IsFinite(f) ? JsonValue.Create((double)f) : JsonValue.Create(f.ToString(CultureInfo.InvariantCulture));
            case DateTime dt:
                return JsonValue.Create(dt.ToString(dt.TimeOfDay.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd'T'HH:mm:ss" : "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture));
            case DateTimeOffset dto:
                return JsonValue.Create(dto.ToString("O", CultureInfo.InvariantCulture));
            case TimeSpan ts:
                return JsonValue.Create(ts.ToString("c", CultureInfo.InvariantCulture));
            case byte[] bytes:
                return JsonValue.Create(Hex(bytes));
            case ReadOnlyMemory<byte> memory:
                return JsonValue.Create(Hex(memory.Span));
            case LobValue lob:
                return lob.TextPreview is { } preview
                    ? JsonValue.Create(Clip(preview, MaxCellChars, out _) + (lob.IsTruncated && preview.Length <= MaxCellChars ? "…" : string.Empty))
                    : JsonValue.Create(Hex(lob.BinaryPreview.Span));
            default:
                return JsonValue.Create(Clip(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, MaxCellChars, out _));
        }
    }

    private static string Hex(ReadOnlySpan<byte> bytes)
    {
        const int maxBytes = MaxCellChars / 2;
        var hex = Convert.ToHexString(bytes.Length > maxBytes ? bytes[..maxBytes] : bytes);
        return bytes.Length > maxBytes ? hex + "…" : hex;
    }

    private static string Snake(string pascal)
    {
        var sb = new StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c) && i > 0)
            {
                sb.Append('_');
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }
}
