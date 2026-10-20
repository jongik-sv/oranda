using System.Globalization;
using System.Text;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;

namespace Oranda.Language.Editing;

/// <summary>플래시백 질의 1건: 본문과 바인드 값(<c>:mins</c>·<c>:k1_1</c> …). 값은 본문에 이어 붙이지 않는다.</summary>
public sealed record FlashbackQuery(SqlStatement Statement, IReadOnlyDictionary<string, BindValue> Binds);

/// <summary>
/// 플래시백 조회·복구(TSK-13-05)의 질의 생성. <c>AS OF TIMESTAMP</c> 로 N분 전 값을, <c>VERSIONS BETWEEN</c> 으로 한 행의 변경 이력을 읽고,
/// 이전 값으로 되돌리는 UPDATE 는 <see cref="RowDmlBuilder"/> 에 맡긴다. 키 열은 기본키·복합 키·<c>ROWID</c> 모두 같은 규칙으로 바인드한다.
/// </summary>
public static class FlashbackQueryBuilder
{
    /// <summary>한 질의에 담는 키 행 수 상한(그리드에 불러온 행이 이보다 많으면 앞쪽만 쓴다).</summary>
    public const int MaxKeyRows = 1000;

    /// <summary>IN 목록 하나에 담는 키 수(오라클 IN 목록 1000개 제한 아래로 끊어 OR 로 잇는다).</summary>
    private const int ChunkSize = 500;

    /// <summary>시간 상한(분): 1년.</summary>
    public const int MaxMinutes = 525_600;

    /// <summary>
    /// 키 행 목록의 N분 전 값: <c>SELECT 열… FROM 표 AS OF TIMESTAMP (SYSTIMESTAMP - NUMTODSINTERVAL(:mins, 'MINUTE')) WHERE 키 IN (…)</c>.
    /// 키 값이 NULL 인 행은 만들 수 없으므로 부른 쪽이 미리 거른다(NULL 이면 예외).
    /// </summary>
    public static FlashbackQuery BuildAsOf(DbObjectRef table, IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns,
                                           IReadOnlyList<IReadOnlyList<object?>> keyRows, int minutes)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(keyColumns);
        ArgumentNullException.ThrowIfNull(keyRows);
        CheckMinutes(minutes);
        CheckKeys(columns, keyColumns);
        if (keyRows.Count == 0 || keyRows.Count > MaxKeyRows)
        {
            throw new ArgumentException($"키 행은 1~{MaxKeyRows}개여야 합니다.", nameof(keyRows));
        }

        var binds = new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        var sb = new StringBuilder("SELECT ");
        sb.AppendJoin(", ", columns.Select(Column)).Append(" FROM ").Append(RowDmlBuilder.QualifiedName(table))
          .Append(" AS OF TIMESTAMP ").Append(PastExpression(binds, names, minutes)).Append(" WHERE ");
        AppendKeyFilter(sb, keyColumns, keyRows, binds, names);
        return Make(sb, names, binds);
    }

    /// <summary>
    /// 한 행의 변경 이력: <c>VERSIONS BETWEEN TIMESTAMP (N분 전) AND SYSTIMESTAMP</c> 에 <c>VERSIONS_STARTTIME</c>·<c>VERSIONS_ENDTIME</c>·
    /// <c>VERSIONS_OPERATION</c>·<c>VERSIONS_XID</c>(16진 문자열)를 앞세운다. 최신 변경이 위.
    /// <paramref name="notBefore"/>(표 정의가 바뀐 시각)가 있으면 창 시작을 <c>GREATEST(N분 전, 그 시각 + 1초)</c> 로 당긴다(ORA-01466 피하기).
    /// </summary>
    public static FlashbackQuery BuildVersions(DbObjectRef table, IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns,
                                               IReadOnlyList<object?> keyValues, int minutes,
                                               DateTime? notBefore = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(keyColumns);
        ArgumentNullException.ThrowIfNull(keyValues);
        CheckMinutes(minutes);
        CheckKeys(columns, keyColumns);
        var binds = new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        var sb = new StringBuilder("SELECT VERSIONS_STARTTIME, VERSIONS_ENDTIME, VERSIONS_OPERATION, RAWTOHEX(VERSIONS_XID) AS VERSIONS_XID, ");
        sb.AppendJoin(", ", columns.Select(Column)).Append(" FROM ").Append(RowDmlBuilder.QualifiedName(table))
          .Append(" VERSIONS BETWEEN TIMESTAMP ").Append(notBefore is { } ddl ? WindowStart(binds, names, minutes, ddl) : PastExpression(binds, names, minutes))
          .Append(" AND SYSTIMESTAMP WHERE ");
        AppendKeyFilter(sb, keyColumns, [keyValues], binds, names);
        sb.Append(" ORDER BY VERSIONS_STARTSCN DESC NULLS LAST");
        return Make(sb, names, binds);
    }

    /// <summary>
    /// 이전 값으로 되돌리는 UPDATE(<c>UPDATE 표 SET 열 = :v1 … WHERE 키 = :k1</c>). 바꿀 열이 없으면 null.
    /// 키는 <see cref="RowDmlBuilder"/> 의 규칙대로 바인드된다(ROWID 는 <c>:rid</c>).
    /// </summary>
    public static RowDml? BuildRestore(DbObjectRef table, long rowIndex, IReadOnlyDictionary<string, object?> key,
                                       IReadOnlyDictionary<string, object?> pastValues)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(pastValues);
        return RowDmlBuilder.Build(new RowChange(RowChangeKind.Update, rowIndex, table) { Key = key, NewValues = pastValues });
    }

    private static string Column(string name) =>
        string.Equals(name, RowDmlBuilder.RowIdKey, StringComparison.OrdinalIgnoreCase) ? RowDmlBuilder.RowIdKey : RowDmlBuilder.QuoteIdentifier(name);

    private static string PastExpression(Dictionary<string, BindValue> binds, List<string> names, int minutes)
    {
        binds["mins"] = new BindValue(BindDataType.Number, (decimal)minutes);
        names.Add("mins");
        return "(SYSTIMESTAMP - NUMTODSINTERVAL(:mins, 'MINUTE'))";
    }

    private static string WindowStart(Dictionary<string, BindValue> binds, List<string> names, int minutes, DateTime ddl)
    {
        binds["mins"] = new BindValue(BindDataType.Number, (decimal)minutes);
        binds["ddl"] = new BindValue(BindDataType.Date, ddl);
        names.Add("mins");
        names.Add("ddl");
        return "GREATEST(CAST(SYSTIMESTAMP - NUMTODSINTERVAL(:mins, 'MINUTE') AS TIMESTAMP), CAST(:ddl AS TIMESTAMP) + INTERVAL '1' SECOND)";
    }

    private static void CheckMinutes(int minutes)
    {
        if (minutes < 1 || minutes > MaxMinutes)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes), minutes, $"분은 1~{MaxMinutes} 이어야 합니다.");
        }
    }

    private static void CheckKeys(IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns)
    {
        if (columns.Count == 0 || keyColumns.Count == 0)
        {
            throw new ArgumentException("열과 키 열이 있어야 합니다.");
        }

        if (keyColumns.Count > 1 && keyColumns.Any(k => string.Equals(k, RowDmlBuilder.RowIdKey, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("ROWID 키는 단독으로만 씁니다.", nameof(keyColumns));
        }
    }

    /// <summary>키 열 1개는 <c>"K" IN (:k1_1, …)</c>, 여럿은 <c>("A", "B") IN ((:k1_1, :k1_2), …)</c>. 500개씩 끊어 OR 로 잇는다.</summary>
    private static void AppendKeyFilter(StringBuilder sb, IReadOnlyList<string> keyColumns, IReadOnlyList<IReadOnlyList<object?>> rows,
                                        Dictionary<string, BindValue> binds, List<string> names)
    {
        var head = keyColumns.Count == 1 ? Column(keyColumns[0]) : "(" + string.Join(", ", keyColumns.Select(Column)) + ")";
        var chunks = (rows.Count + ChunkSize - 1) / ChunkSize;
        if (chunks > 1)
        {
            sb.Append('(');
        }

        for (var c = 0; c < chunks; c++)
        {
            if (c > 0)
            {
                sb.Append(" OR ");
            }

            sb.Append(head).Append(" IN (");
            var end = Math.Min(rows.Count, (c + 1) * ChunkSize);
            for (var r = c * ChunkSize; r < end; r++)
            {
                var row = rows[r];
                if (row.Count != keyColumns.Count)
                {
                    throw new ArgumentException($"키 행 {r + 1} 의 값 수가 키 열 수와 다릅니다.", nameof(rows));
                }

                if (r > c * ChunkSize)
                {
                    sb.Append(", ");
                }

                var parts = new List<string>(row.Count);
                for (var k = 0; k < row.Count; k++)
                {
                    if (row[k] is null or DBNull)
                    {
                        throw new ArgumentException($"키 행 {r + 1} 의 {keyColumns[k]} 값이 NULL 입니다.", nameof(rows));
                    }

                    var name = $"k{(r + 1).ToString(CultureInfo.InvariantCulture)}_{(k + 1).ToString(CultureInfo.InvariantCulture)}";
                    binds[name] = RowDmlBuilder.ToBind(row[k]);
                    names.Add(name);
                    parts.Add(":" + name);
                }

                sb.Append(keyColumns.Count == 1 ? parts[0] : "(" + string.Join(", ", parts) + ")");
            }

            sb.Append(')');
        }

        if (chunks > 1)
        {
            sb.Append(')');
        }
    }

    private static FlashbackQuery Make(StringBuilder sb, List<string> names, Dictionary<string, BindValue> binds) =>
        new(new SqlStatement(sb.ToString(), default, StatementKind.Query) { Verb = "SELECT", BindNames = names }, binds);
}
