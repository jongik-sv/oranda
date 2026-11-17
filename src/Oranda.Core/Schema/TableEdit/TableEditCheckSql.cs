using System.Globalization;
using Oranda.Core.Metadata;
using Oranda.Core.Text;

namespace Oranda.Core.Schema.TableEdit;

/// <summary>
/// 검사 질의의 WHERE 조건과 「그 행 보기」 SELECT(설계 10장). Oracle·Tibero 공통 꼴이다 — <see cref="ITableEditChecks"/> 구현이 건수 질의
/// (<see cref="CountSql"/>)에 그대로 써도 된다. 이름은 사전 표기를 받아 필요할 때만 따옴표로 감싼다.
/// </summary>
public static class TableEditCheckSql
{
    /// <summary><c>SELECT * FROM o.t WHERE …</c>.</summary>
    public static string RowsSql(DbObjectRef table, string where) => $"SELECT * FROM {Name(table)} t WHERE {where}";

    /// <summary><c>SELECT COUNT(*) FROM o.t WHERE …</c>.</summary>
    public static string CountSql(DbObjectRef table, string where) => $"SELECT COUNT(*) FROM {Name(table)} t WHERE {where}";

    /// <summary>새 정의 <paramref name="target"/> 에 담기지 않는 문자 값(<c>LENGTH</c> 글자·<c>LENGTHB</c> 바이트·RAW 는 <c>UTL_RAW.LENGTH</c>).</summary>
    public static string OverflowWhere(string column, ColumnDraft target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var c = "t." + Id(column);
        var n = (target.Length ?? 0).ToString(CultureInfo.InvariantCulture);
        return ColumnTypeText.Family(target.DataType) switch
        {
            "RAW" => $"UTL_RAW.LENGTH({c}) > {n}",
            "VARCHAR2" or "CHAR" when target.CharSemantics == CharSemantics.Bytes => $"LENGTHB({c}) > {n}",
            _ => $"LENGTH({c}) > {n}",
        };
    }

    public static string NullWhere(string column) => $"t.{Id(column)} IS NULL";

    public static string NonNullWhere(string column) => $"t.{Id(column)} IS NOT NULL";

    /// <summary>
    /// 키가 겹치는 행. 모든 키 열이 NULL 인 행은 겹침이 아니고, 일부만 NULL 인 키는 NULL 끼리 같은 값으로 본다(UK 규칙 — <c>DECODE</c> 는 NULL 을 같게 비교).
    /// </summary>
    public static string DuplicateWhere(DbObjectRef table, IReadOnlyList<string> columns)
    {
        var anyNotNull = string.Join(" OR ", columns.Select(c => $"t.{Id(c)} IS NOT NULL"));
        var same = string.Join(" AND ", columns.Select(c => $"DECODE(d.{Id(c)}, t.{Id(c)}, 1, 0) = 1"));
        return $"({anyNotNull}) AND EXISTS (SELECT 1 FROM {Name(table)} d WHERE {same} AND d.ROWID <> t.ROWID)";
    }

    /// <summary>부모에 짝이 없는 행(키 열이 하나라도 NULL 이면 빼고).</summary>
    public static string OrphanWhere(IReadOnlyList<string> columns, DbObjectRef referencedTable, IReadOnlyList<string> referencedColumns)
    {
        var notNull = string.Join(" AND ", columns.Select(c => $"t.{Id(c)} IS NOT NULL"));
        var match = string.Join(" AND ", columns.Zip(referencedColumns).Select(p => $"p.{Id(p.Second)} = t.{Id(p.First)}"));
        return $"{notNull} AND NOT EXISTS (SELECT 1 FROM {Name(referencedTable)} p WHERE {match})";
    }

    /// <summary>CHECK 조건을 어기는 행(UNKNOWN 은 어기지 않음).</summary>
    public static string CheckViolationWhere(string condition) => $"NOT ({condition})";

    private static string Name(DbObjectRef t) => Id(t.Owner) + "." + Id(t.Name);

    private static string Id(string name) => SqlIdentifier.Quote(name, TableEditDefaults.IsReservedWord);
}
