using System.Globalization;
using System.Text;
using Oranda.Core.Metadata;
using Oranda.Language.Editing;

namespace Oranda.App.Features.Flashback;

/// <summary>
/// 「변경 추적하기」(TSK-13-31)가 새 SQL 탭에 넣는 글. 사람이 읽고 고쳐 다시 돌릴 수 있게 키 값을 바인드가 아니라 리터럴로 쓴다.
/// 기간을 묻지 않고 플래시백이 가능한 전체 기간(<c>VERSIONS BETWEEN SCN MINVALUE AND MAXVALUE</c>)을 읽는다.
/// </summary>
internal static class FlashbackTrackSql
{
    /// <summary>한 줄 주석(대상 표·키·한계)과 변경 이력 질의 한 문장. 키 값이 NULL 이면 <see cref="ArgumentException"/>.</summary>
    public static string Build(DbObjectRef table, IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns, IReadOnlyList<object?> keyValues)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(keyColumns);
        ArgumentNullException.ThrowIfNull(keyValues);
        if (columns.Count == 0 || keyColumns.Count == 0 || keyColumns.Count != keyValues.Count)
        {
            throw new ArgumentException("열과 키 열·키 값의 수가 맞아야 합니다.");
        }

        var conditions = new List<string>(keyColumns.Count);
        var shown = new List<string>(keyColumns.Count);
        for (var i = 0; i < keyColumns.Count; i++)
        {
            if (keyValues[i] is null or DBNull)
            {
                throw new ArgumentException($"키 {keyColumns[i]} 값이 NULL 입니다.", nameof(keyValues));
            }

            var rowId = IsRowId(keyColumns[i]);
            var literal = Literal(keyValues[i]!, rowId);
            conditions.Add($"{Column(keyColumns[i])} = {literal}");
            shown.Add($"{keyColumns[i]} = {literal}");
        }

        var sb = new StringBuilder();
        sb.Append("-- ").Append(OneLine(RowDmlBuilder.QualifiedName(table))).Append(" 의 변경 이력(키: ").Append(OneLine(string.Join(", ", shown)))
          .Append("). undo 보존 기간과 표 정의 변경 시점까지만 보입니다.").Append('\n');
        sb.Append("SELECT VERSIONS_STARTTIME, VERSIONS_ENDTIME, VERSIONS_OPERATION, RAWTOHEX(VERSIONS_XID) AS VERSIONS_XID, ")
          .AppendJoin(", ", columns.Select(Column)).Append('\n')
          .Append("FROM ").Append(RowDmlBuilder.QualifiedName(table)).Append(" VERSIONS BETWEEN SCN MINVALUE AND MAXVALUE\n")
          .Append("WHERE ").AppendJoin(" AND ", conditions).Append('\n')
          .Append("ORDER BY VERSIONS_STARTSCN DESC NULLS LAST");
        return sb.ToString();
    }

    private static bool IsRowId(string name) => string.Equals(name, RowDmlBuilder.RowIdKey, StringComparison.OrdinalIgnoreCase);

    private static string Column(string name) => IsRowId(name) ? RowDmlBuilder.RowIdKey : RowDmlBuilder.QuoteIdentifier(name);

    private static string OneLine(string text) => text.ReplaceLineEndings(" ");

    /// <summary>키 값 리터럴: 문자 = 따옴표 이스케이프, 날짜·타임스탬프 = TO_DATE·TO_TIMESTAMP, ROWID = CHARTOROWID, 숫자 = 그대로.</summary>
    internal static string Literal(object value, bool rowId)
    {
        var inv = CultureInfo.InvariantCulture;
        return value switch
        {
            string s when rowId => $"CHARTOROWID({Quote(s)})",
            string s => Quote(s),
            decimal d => d.ToString(inv),
            int or long or short or byte or sbyte or ushort or uint or ulong => Convert.ToString(value, inv)!,
            double or float => Convert.ToDouble(value, inv).ToString("R", inv),
            DateTime dt => dt.Ticks % TimeSpan.TicksPerSecond == 0
                ? $"TO_DATE('{dt.ToString("yyyy-MM-dd HH:mm:ss", inv)}', 'YYYY-MM-DD HH24:MI:SS')"
                : $"TO_TIMESTAMP('{dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", inv)}', 'YYYY-MM-DD HH24:MI:SS.FF6')",
            DateTimeOffset o => $"TO_TIMESTAMP_TZ('{o.ToString("yyyy-MM-dd HH:mm:ss.ffffff zzz", inv)}', 'YYYY-MM-DD HH24:MI:SS.FF6 TZH:TZM')",
            byte[] b => $"HEXTORAW('{Convert.ToHexString(b)}')",
            _ => throw new NotSupportedException($"리터럴로 쓸 수 없는 키 값 형식입니다: {value.GetType().Name}"),
        };
    }

    private static string Quote(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";
}
