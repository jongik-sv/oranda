using System.Globalization;
using System.Text;
using Oranda.Core.Metadata;
using Oranda.Core.Text;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 「테이블 다시 만들기」(설계 6.2.3)의 대상 DDL. 원본 계정은 소유자가 아닐 수 있어 <c>DBMS_METADATA</c> 를 쓰지 않고,
/// SELECT 권한만으로 읽는 메타데이터(<see cref="TableInfo"/> 의 열·PK·UK·CHECK·FK·인덱스·주석)로 만든다.
/// 테이블스페이스·저장 속성·파티션·압축·트리거·권한은 만들지 않는다(대상 기본값을 쓴다).
/// </summary>
public static class CreateTableDdl
{
    /// <summary>
    /// 순서: <c>CREATE TABLE</c>(열·PK·UK·CHECK 포함) → 작업 안 표끼리의 FK → 제약에 속하지 않은 인덱스 → <c>COMMENT ON</c>.
    /// FK 는 부모가 먼저 만들어져 있어야 하므로 실행기가 부모 → 자식 순서로 보낸다.
    /// </summary>
    /// <param name="tablesInJob">함께 다시 만드는 표 이름. 이 집합 밖 표를 참조하는 FK 는 만들지 않는다</param>
    /// <param name="isServerName">서버가 지은 이름인가(그 이름은 명시하지 않고 대상 서버가 새로 짓게 둔다). null = Oracle 판정(<see cref="IsServerName(string)"/>)</param>
    public static IReadOnlyList<string> Build(TableInfo source, string targetOwner, IReadOnlySet<string> tablesInJob, Func<string, bool>? isServerName = null)
    {
        var (now, _) = Build(source, targetOwner, tablesInJob, isServerName, keysAfterLoad: false);
        return now;
    }

    /// <summary>
    /// <see cref="Build(TableInfo, string, IReadOnlySet{string}, Func{string, bool}?)"/> 와 같되, <paramref name="keysAfterLoad"/> 이면 PK·UK·인덱스를 CREATE TABLE 에서 떼어
    /// 적재 뒤 보낼 문장(<c>AfterLoad</c>: <c>ALTER TABLE … ADD [CONSTRAINT n] PRIMARY KEY|UNIQUE (…)</c> → <c>CREATE INDEX</c>)으로 돌려준다(PF4, 조정 결정 74: 새 표는 인덱스 유지 없이 넣는다).
    /// <c>Now</c> = CREATE TABLE(열·CHECK) → 작업 안 FK → COMMENT. 떼지 않으면 <c>AfterLoad</c> 는 비고 <c>Now</c> 가 예전 <see cref="Build(TableInfo, string, IReadOnlySet{string}, Func{string, bool}?)"/> 와 같다.
    /// </summary>
    public static (IReadOnlyList<string> Now, IReadOnlyList<string> AfterLoad) Build(TableInfo source, string targetOwner, IReadOnlySet<string> tablesInJob,
                                                                                    Func<string, bool>? isServerName, bool keysAfterLoad)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetOwner);
        ArgumentNullException.ThrowIfNull(tablesInJob);
        var server = isServerName ?? IsSystemName;

        var table = Qualified(targetOwner, source.Table.Name);
        var statements = new List<string>();
        var afterLoad = new List<string>();

        var parts = source.Columns.OrderBy(c => c.Position)
            .Select(c => ColumnDefinition(c with { DefaultValue = Reowned(c.DefaultValue, source.Table.Owner, targetOwner) }))
            .ToList();
        foreach (var c in source.Constraints)
        {
            var body = c.Kind switch
            {
                ConstraintKind.PrimaryKey => $"PRIMARY KEY ({Quoted(c.Columns)})",
                ConstraintKind.Unique => $"UNIQUE ({Quoted(c.Columns)})",
                ConstraintKind.Check when !string.IsNullOrWhiteSpace(c.Condition) => $"CHECK ({c.Condition})",
                _ => null,
            };
            if (body is null)
            {
                continue;
            }

            var definition = ConstraintPrefix(c, server) + body + (c.IsEnabled ? string.Empty : " DISABLE");
            if (keysAfterLoad && c.Kind is ConstraintKind.PrimaryKey or ConstraintKind.Unique)
            {
                afterLoad.Add($"ALTER TABLE {table} ADD {definition}");
            }
            else
            {
                parts.Add(definition);
            }
        }

        statements.Add($"CREATE TABLE {table} ({string.Join(", ", parts)})");

        foreach (var fk in source.Constraints.Where(c => c.Kind == ConstraintKind.ForeignKey))
        {
            if (fk.ReferencedTable is not { } parent || !tablesInJob.Contains(parent.Name))
            {
                continue;
            }

            // 부모 열 목록은 TableInfo 에 없으므로 부모의 PK 를 참조한다(REFERENCES 에 열을 쓰지 않음).
            statements.Add($"ALTER TABLE {table} ADD {ConstraintPrefix(fk, server)}FOREIGN KEY ({Quoted(fk.Columns)})"
                           + $" REFERENCES {Qualified(targetOwner, parent.Name)}" + (fk.IsEnabled ? string.Empty : " DISABLE"));
        }

        foreach (var index in CreatedIndexes(source, server))
        {
            (keysAfterLoad ? afterLoad : statements).Add($"CREATE {(index.IsUnique ? "UNIQUE " : string.Empty)}INDEX {Qualified(targetOwner, index.Name)}"
                                                        + $" ON {table} ({Quoted(index.Columns)})");
        }

        if (!string.IsNullOrEmpty(source.Comment))
        {
            statements.Add($"COMMENT ON TABLE {table} IS {Literal(source.Comment)}");
        }

        foreach (var c in source.Columns.OrderBy(c => c.Position).Where(c => !string.IsNullOrEmpty(c.Comment)))
        {
            statements.Add($"COMMENT ON COLUMN {table}.{SqlIdentifier.QuoteAlways(c.Name)} IS {Literal(c.Comment!)}");
        }

        return (statements, afterLoad);
    }

    /// <summary>적재 뒤 문장(<c>AfterLoad</c>) 가운데 PK·UK 를 붙이는 것인가(실패하면 키 없는 표가 남으므로 실패로 본다. 인덱스는 알림만).</summary>
    public static bool IsKeyStatement(string text) => text.StartsWith("ALTER TABLE ", StringComparison.Ordinal);

    /// <summary>
    /// <see cref="Build"/> 가 대상 소유자 스키마에 이름을 지정해 만드는 제약·인덱스 이름(FK 제외). PK·UK 는 같은 이름의 인덱스도 만든다.
    /// 사전 점검이 대상의 다른 표와 이름이 겹치는지 본다(겹치면 DROP 뒤 CREATE 가 ORA-02264·ORA-00955 로 실패한다).
    /// </summary>
    public static (IReadOnlyList<string> Constraints, IReadOnlyList<string> Indexes) NamedObjects(TableInfo source, Func<string, bool>? isServerName = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var server = isServerName ?? IsSystemName;
        var constraints = source.Constraints
            .Where(c => !server(c.Name) && (c.Kind is ConstraintKind.PrimaryKey or ConstraintKind.Unique
                                                  || (c.Kind == ConstraintKind.Check && !string.IsNullOrWhiteSpace(c.Condition))))
            .Select(c => c.Name)
            .ToList();
        var indexes = source.Constraints
            .Where(c => !server(c.Name) && c.Kind is ConstraintKind.PrimaryKey or ConstraintKind.Unique)
            .Select(c => c.Name)
            .Concat(CreatedIndexes(source, server).Select(i => i.Name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return (constraints, indexes);
    }

    /// <summary>제약에 속하지 않은 일반 인덱스(제약이 만든 인덱스, 함수 기반·LOB·도메인 인덱스, 서버가 지은 이름은 뺀다).</summary>
    private static IEnumerable<IndexInfo> CreatedIndexes(TableInfo source, Func<string, bool> server)
    {
        var keyColumnSets = source.Constraints
            .Where(c => c.Kind is ConstraintKind.PrimaryKey or ConstraintKind.Unique)
            .Select(c => string.Join(",", c.Columns))
            .ToHashSet(StringComparer.Ordinal);
        var constraintNames = source.Constraints.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        return source.Indexes.Where(index =>
            !(constraintNames.Contains(index.Name) || keyColumnSets.Contains(string.Join(",", index.Columns))
              || server(index.Name) || !IsNormalIndex(index) || index.Columns.Count == 0
              || index.Columns.Any(col => col.StartsWith("SYS_NC", StringComparison.Ordinal))));
    }

    /// <summary>서버가 지은 이름(SYS_C…, SYS_IL…, BIN$…)인가.</summary>
    public static bool IsServerName(string name) => IsSystemName(name);

    /// <summary>열 정의 하나. 가상 열은 식으로, IDENTITY 는 복사한 값을 넣을 수 있게 <c>BY DEFAULT</c> 로 만든다.</summary>
    internal static string ColumnDefinition(ColumnInfo c)
    {
        // 문법: 열 [형식] [INVISIBLE] { [GENERATED ALWAYS] AS (식) [VIRTUAL] | DEFAULT … | IDENTITY … } — INVISIBLE 은 생성 절보다 앞이다.
        var sb = new StringBuilder(SqlIdentifier.QuoteAlways(c.Name)).Append(' ').Append(TypeText(c));
        if (c.IsInvisible)
        {
            sb.Append(" INVISIBLE");
        }

        if (c.IsVirtual)
        {
            return sb.Append(" GENERATED ALWAYS AS (").Append(c.DefaultValue).Append(") VIRTUAL").ToString();
        }

        if (c.IsIdentity)
        {
            // 원본 기본값(ISEQ$$_….nextval)은 원본 시퀀스를 가리키므로 쓰지 않는다.
            sb.Append(" GENERATED BY DEFAULT AS IDENTITY");
        }
        else if (!string.IsNullOrWhiteSpace(c.DefaultValue))
        {
            sb.Append(" DEFAULT ").Append(c.DefaultValue.Trim());
        }

        if (!c.IsNullable)
        {
            sb.Append(" NOT NULL");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 기본값·가상 열 식 안의 원본 소유자 접두(<c>"SRC".</c>)를 대상 소유자로 바꾼다. 서버는 <c>DEFAULT SEQ.NEXTVAL</c> 을
    /// <c>"SRC"."SEQ"."NEXTVAL"</c> 처럼 소유자를 붙여 저장하므로, 그대로 쓰면 대상 DB 에서 원본 스키마 객체를 찾다 CREATE 가 실패한다.
    /// 대상에 같은 이름의 객체가 있어야 한다(사전 점검이 알린다). 문자열 리터럴 안의 같은 글자도 바뀔 수 있다(드묾).
    /// </summary>
    internal static string? Reowned(string? expression, string sourceOwner, string targetOwner)
    {
        if (string.IsNullOrEmpty(expression) || string.Equals(sourceOwner, targetOwner, StringComparison.Ordinal))
        {
            return expression;
        }

        return expression.Replace(SqlIdentifier.QuoteAlways(sourceOwner) + ".", SqlIdentifier.QuoteAlways(targetOwner) + ".", StringComparison.Ordinal);
    }

    /// <summary>식이 원본 소유자의 객체를 가리키는가(<see cref="Reowned"/> 가 바꿀 글자가 있는가).</summary>
    public static bool ReferencesOwner(string? expression, string sourceOwner) =>
        expression?.Contains(SqlIdentifier.QuoteAlways(sourceOwner) + ".", StringComparison.Ordinal) == true;

    /// <summary>
    /// 식 안에서 <c>"SRC"."이름"</c> 으로 가리키는 원본 소유자 객체 이름(나온 순서, 중복 없음).
    /// 다시 만들 때 대상 소유자의 같은 이름 객체를 쓰게 되므로 사전 점검이 대상에 있는지 본다.
    /// </summary>
    public static IReadOnlyList<string> ReferencedObjects(string? expression, string sourceOwner)
    {
        var names = new List<string>();
        if (string.IsNullOrEmpty(expression))
        {
            return names;
        }

        var prefix = SqlIdentifier.QuoteAlways(sourceOwner) + ".\"";
        var at = expression.IndexOf(prefix, StringComparison.Ordinal);
        while (at >= 0)
        {
            var start = at + prefix.Length;
            var end = expression.IndexOf('"', start);
            if (end < 0)
            {
                break;
            }

            var name = expression[start..end];
            if (name.Length > 0 && !names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }

            at = expression.IndexOf(prefix, end, StringComparison.Ordinal);
        }

        return names;
    }

    /// <summary>서버 형식 이름 + 길이·정밀도. 이름에 이미 괄호가 있으면(TIMESTAMP(6), INTERVAL …) 그대로 쓴다.</summary>
    internal static string TypeText(ColumnInfo c)
    {
        var t = c.DataType.Trim();
        if (t.Contains('(', StringComparison.Ordinal))
        {
            return t;
        }

        var upper = t.ToUpperInvariant();
        return upper switch
        {
            "VARCHAR2" or "CHAR" when c.Length is { } n => $"{upper}({N(n)} CHAR)",
            "NVARCHAR2" or "NCHAR" or "RAW" when c.Length is { } n => $"{upper}({N(n)})",
            // Tibero 사전은 VARCHAR2·NVARCHAR2 열을 VARCHAR·NVARCHAR 로 낸다(Oracle 사전은 이 이름을 내지 않는다) — 같은 뜻의 Oracle 꼴로 쓴다.
            "VARCHAR" when c.Length is { } n => $"VARCHAR2({N(n)} CHAR)",
            "NVARCHAR" when c.Length is { } n => $"NVARCHAR2({N(n)})",
            "NUMBER" => c switch
            {
                { Precision: { } p, Scale: { } s } when s != 0 => $"NUMBER({N(p)},{N(s)})",
                { Precision: { } p } => $"NUMBER({N(p)})",
                { Scale: 0 } => "NUMBER(*,0)",
                _ => "NUMBER",
            },
            "FLOAT" when c.Precision is { } p => $"FLOAT({N(p)})",
            _ => t,
        };
    }

    private static string ConstraintPrefix(ConstraintInfo c, Func<string, bool> server) =>
        server(c.Name) ? string.Empty : "CONSTRAINT " + SqlIdentifier.QuoteAlways(c.Name) + " ";

    /// <summary>서버가 지은 이름(SYS_C…, SYS_IL…, BIN$…)은 대상 DB 에서 겹칠 수 있어 쓰지 않는다(서버가 새로 짓게 둔다).</summary>
    private static bool IsSystemName(string name) =>
        name.StartsWith("SYS_", StringComparison.Ordinal) || name.StartsWith("BIN$", StringComparison.Ordinal);

    private static bool IsNormalIndex(IndexInfo index) =>
        index.IndexType is null || string.Equals(index.IndexType, "NORMAL", StringComparison.OrdinalIgnoreCase);

    private static string Qualified(string owner, string name) => SqlIdentifier.QuoteAlways(owner) + "." + SqlIdentifier.QuoteAlways(name);

    private static string Quoted(IEnumerable<string> columns) => string.Join(", ", columns.Select(SqlIdentifier.QuoteAlways));

    private static string Literal(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
}
