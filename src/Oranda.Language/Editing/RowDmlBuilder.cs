using System.Collections.Frozen;
using System.Text;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;

namespace Oranda.Language.Editing;

/// <summary>변경 1건의 DML 과 바인드 값. 미리보기(GRID-05)는 <see cref="Statement"/> 본문과 <see cref="Binds"/> 를 함께 보인다.</summary>
public sealed record RowDml(RowChange Change, SqlStatement Statement, IReadOnlyDictionary<string, BindValue> Binds);

/// <summary>
/// 그리드 변경 → 바인드 DML(PRD 6.5.1-3). 값은 절대 본문에 이어 붙이지 않는다. 식별자는 필요할 때만 큰따옴표로 감싼다.
/// UPDATE·DELETE 는 키 조건이 반드시 있다(키가 없거나 NULL 이면 예외). 바인드 이름: 키 <c>:k1…</c>(ROWID 는 <c>:rid</c>),
/// 새 값 <c>:v1…</c>, 낙관적 잠금 원래 값 <c>:o1…</c>.
/// </summary>
public static class RowDmlBuilder
{
    public const string RowIdKey = "ROWID";

    // V$RESERVED_WORDS 의 RESERVED='Y' 와 SQL 예약어. 열 이름이 이 낱말이면 따옴표로 감싼다.
    private static readonly FrozenSet<string> Reserved = new[]
    {
        "ACCESS", "ADD", "ALL", "ALTER", "AND", "ANY", "AS", "ASC", "AUDIT", "BETWEEN", "BY", "CHAR", "CHECK", "CLUSTER",
        "COLUMN", "COMMENT", "COMPRESS", "CONNECT", "CREATE", "CURRENT", "DATE", "DECIMAL", "DEFAULT", "DELETE", "DESC",
        "DISTINCT", "DROP", "ELSE", "EXCLUSIVE", "EXISTS", "FILE", "FLOAT", "FOR", "FROM", "GRANT", "GROUP", "HAVING",
        "IDENTIFIED", "IMMEDIATE", "IN", "INCREMENT", "INDEX", "INITIAL", "INSERT", "INTEGER", "INTERSECT", "INTO", "IS",
        "LEVEL", "LIKE", "LOCK", "LONG", "MAXEXTENTS", "MINUS", "MLSLABEL", "MODE", "MODIFY", "NOAUDIT", "NOCOMPRESS", "NOT",
        "NOWAIT", "NULL", "NUMBER", "OF", "OFFLINE", "ON", "ONLINE", "OPTION", "OR", "ORDER", "PCTFREE", "PRIOR", "PUBLIC",
        "RAW", "RENAME", "RESOURCE", "REVOKE", "ROW", "ROWID", "ROWNUM", "ROWS", "SELECT", "SESSION", "SET", "SHARE", "SIZE",
        "SMALLINT", "START", "SUCCESSFUL", "SYNONYM", "SYSDATE", "TABLE", "THEN", "TO", "TRIGGER", "UID", "UNION", "UNIQUE",
        "UPDATE", "USER", "VALIDATE", "VALUES", "VARCHAR", "VARCHAR2", "VIEW", "WHENEVER", "WHERE", "WITH",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>변경 1건의 DML. 바꿀 값이 없는 INSERT·UPDATE 는 null.</summary>
    public static RowDml? Build(RowChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var table = QualifiedName(change.Table);
        var binds = new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        var sb = new StringBuilder();

        string Bind(string name, object? value)
        {
            binds[name] = ToBind(value);
            names.Add(name);
            return ":" + name;
        }

        switch (change.Kind)
        {
            case RowChangeKind.Insert:
                {
                    if (change.NewValues.Count == 0)
                    {
                        return null;
                    }

                    var cols = new List<string>();
                    var vals = new List<string>();
                    var n = 0;
                    foreach (var (col, value) in change.NewValues)
                    {
                        cols.Add(QuoteIdentifier(col));
                        vals.Add(Bind(Name("v", ++n), value));
                    }

                    sb.Append("INSERT INTO ").Append(table).Append(" (").AppendJoin(", ", cols)
                      .Append(") VALUES (").AppendJoin(", ", vals).Append(')');
                    break;
                }

            case RowChangeKind.Update:
                {
                    if (change.NewValues.Count == 0)
                    {
                        return null;
                    }

                    sb.Append("UPDATE ").Append(table).Append(" SET ");
                    var n = 0;
                    foreach (var (col, value) in change.NewValues)
                    {
                        if (n > 0)
                        {
                            sb.Append(", ");
                        }

                        sb.Append(QuoteIdentifier(col)).Append(" = ").Append(Bind(Name("v", ++n), value));
                    }

                    AppendWhere(sb, change, Bind);
                    break;
                }

            case RowChangeKind.Delete:
                sb.Append("DELETE FROM ").Append(table);
                AppendWhere(sb, change, Bind);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(change), change.Kind, null);
        }

        var verb = change.Kind switch
        {
            RowChangeKind.Insert => "INSERT",
            RowChangeKind.Update => "UPDATE",
            _ => "DELETE",
        };
        var stmt = new SqlStatement(sb.ToString(), default, StatementKind.Dml) { Verb = verb, BindNames = names };
        return new RowDml(change, stmt, binds);
    }

    /// <summary>미리보기 글: 문장마다 본문 한 줄 + 바인드 값 주석 한 줄.</summary>
    public static string FormatPreview(IEnumerable<RowDml> dml)
    {
        ArgumentNullException.ThrowIfNull(dml);
        var sb = new StringBuilder();
        foreach (var d in dml)
        {
            sb.Append(d.Statement.Text).AppendLine(";");
            if (d.Statement.BindNames.Count > 0)
            {
                sb.Append("  -- ");
                sb.AppendJoin(", ", d.Statement.BindNames.Select(n => ":" + n + " = " + FormatValue(d.Binds[n].Value)));
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary><c>OWNER.NAME</c>(필요하면 따옴표).</summary>
    public static string QualifiedName(DbObjectRef table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return string.IsNullOrEmpty(table.Owner) ? QuoteIdentifier(table.Name) : QuoteIdentifier(table.Owner) + "." + QuoteIdentifier(table.Name);
    }

    /// <summary>대문자 일반 식별자이고 예약어가 아니면 그대로, 아니면 큰따옴표로 감싼다.</summary>
    public static string QuoteIdentifier(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Contains('"', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException($"식별자에 쓸 수 없는 문자가 있습니다: {name}", nameof(name));
        }

        var plain = name[0] is >= 'A' and <= 'Z' && name.Length <= 128
                    && name.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '$' or '#')
                    && !Reserved.Contains(name);
        return plain ? name : "\"" + name + "\"";
    }

    /// <summary>.NET 값 → 바인드(형식은 값으로 정한다). NULL 은 VARCHAR2 NULL(어느 열에나 들어간다).</summary>
    public static BindValue ToBind(object? value) => value switch
    {
        null or DBNull => new BindValue(BindDataType.Varchar2, null),
        string s => new BindValue(BindDataType.Varchar2, s),
        decimal d => new BindValue(BindDataType.Number, d),
        int or long or short or byte or sbyte or ushort or uint or ulong =>
            new BindValue(BindDataType.Number, Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture)),
        double or float => new BindValue(BindDataType.BinaryDouble, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)),
        DateTime dt => new BindValue(BindDataType.Timestamp, dt),
        DateTimeOffset dto => new BindValue(BindDataType.TimestampTz, dto),
        byte[] b => new BindValue(BindDataType.Raw, b),
        bool b => new BindValue(BindDataType.Boolean, b),
        _ => throw new NotSupportedException($"그리드 편집에서 바인드할 수 없는 값 형식입니다: {value.GetType().Name}"),
    };

    private static void AppendWhere(StringBuilder sb, RowChange change, Func<string, object?, string> bind)
    {
        if (change.Key.Count == 0)
        {
            throw new ArgumentException($"행 {change.RowIndex}: 키 없는 {change.Kind} 는 만들지 않습니다(PRD 6.5.1).", nameof(change));
        }

        sb.Append(" WHERE ");
        var n = 0;
        foreach (var (col, value) in change.Key)
        {
            if (value is null or DBNull)
            {
                throw new ArgumentException($"행 {change.RowIndex}: 키 {col} 값이 NULL 입니다.", nameof(change));
            }

            if (n > 0)
            {
                sb.Append(" AND ");
            }

            if (string.Equals(col, RowIdKey, StringComparison.OrdinalIgnoreCase))
            {
                if (change.Key.Count != 1)
                {
                    throw new ArgumentException("ROWID 키는 단독으로만 씁니다.", nameof(change));
                }

                sb.Append("ROWID = ").Append(bind("rid", value));
                n++;
                continue;
            }

            sb.Append(QuoteIdentifier(col)).Append(" = ").Append(bind(Name("k", ++n), value));
        }

        var o = 0;
        foreach (var (col, value) in change.OriginalValues)
        {
            sb.Append(" AND ");
            if (value is null or DBNull)
            {
                sb.Append(QuoteIdentifier(col)).Append(" IS NULL");
            }
            else
            {
                sb.Append(QuoteIdentifier(col)).Append(" = ").Append(bind(Name("o", ++o), value));
            }
        }
    }

    private static string Name(string prefix, int n) => prefix + n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatValue(object? v) => v switch
    {
        null => "NULL",
        string s => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'",
        byte[] b => "HEXTORAW('" + Convert.ToHexString(b) + "')",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF zzz", System.Globalization.CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => v.ToString() ?? string.Empty,
    };
}
