using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Text;

namespace Oranda.Core.Transfer.Copy;

public sealed record SqlWithBinds(string Sql, IReadOnlyDictionary<string, BindValue> Binds);

/// <summary>
/// 작업 규칙에서 원본 읽기·대상 삭제·삽입·MERGE 문장을 만든다. 식별자는 항상 따옴표로 감싸고,
/// 기간 경계는 시작 포함·끝 제외(<c>&gt;=</c>, <c>&lt;</c>)이며 열에 <c>TO_DATE</c>·<c>TO_CHAR</c> 를 씌우지 않는다.
/// 기간 열 바인드 이름은 문장 안에 나타나는 순서대로 <c>:B1_FROM</c>, <c>:B1_TO</c>, <c>:B2_FROM</c> … 이다.
/// <para>
/// 표는 규칙 키(<see cref="CopyOwners.Key(CopyJob, TableRule)"/>)로 가리키고, 원본 문장은 규칙의 원본 계정, 대상 문장은 규칙의 대상 계정에 쓴다.
/// <paramref name="sourceTables"/>·<paramref name="targetTables"/> 도 규칙 키로 찾는다. 부모 키 규칙의 부모 표는 원본 쪽 = 부모 원본 계정,
/// 대상 쪽 = 부모 규칙의 대상 계정(작업 밖이면 <see cref="CopyOwners.MapTarget"/>)이다.
/// </para>
/// </summary>
public sealed class CopySqlBuilder(CopyJob job, ResolvedPeriod period,
                                   IReadOnlyDictionary<string, TableInfo> sourceTables,
                                   IReadOnlyDictionary<string, TableInfo> targetTables,
                                   IReadOnlySet<string>? freshTables = null)
{
    private static readonly Regex FromDtRegex = new(@":FROM_DT\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ToDtRegex = new(@":TO_DT\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <param name="key">규칙 키.</param>
    public SqlWithBinds SourceSelect(string key, IReadOnlyList<string> columns)
    {
        var binds = new Dictionary<string, BindValue>(StringComparer.Ordinal);
        var sql = $"SELECT {string.Join(", ", columns.Select(SqlIdentifier.QuoteAlways))} FROM {SourceTable(key)}"
                  + Where(key, isTarget: false, binds);
        return new SqlWithBinds(sql, binds);
    }

    public SqlWithBinds SourceCount(string key) => Count(key, isTarget: false);

    public SqlWithBinds TargetCount(string key) => Count(key, isTarget: true);

    /// <summary>
    /// <c>DeleteAllInsert</c> → 조건 없는 DELETE, <c>DeleteRangeInsert</c> → 범위 DELETE, 그 밖은 null. 지우는 표는 규칙의 대상 계정이다.
    /// 이번 실행에서 새로 만드는 표(<c>freshTables</c>, 「없으면 만들기」)는 비어 있으니 지우지 않는다(null).
    /// </summary>
    public SqlWithBinds? TargetDelete(string key)
    {
        var rule = RuleOf(key);
        if (freshTables?.Contains(key) == true) return null;
        if (rule.Action is not (TargetAction.DeleteAllInsert or TargetAction.DeleteRangeInsert)) return null;
        var binds = new Dictionary<string, BindValue>(StringComparer.Ordinal);
        var sql = $"DELETE FROM {TargetTable(key)}";
        if (rule.Action == TargetAction.DeleteRangeInsert) sql += Where(key, isTarget: true, binds);
        return new SqlWithBinds(sql, binds);
    }

    /// <summary>
    /// 바인드 이름은 <c>:C1</c>…<c>:Cn</c>, 열 순서와 같다. <paramref name="valueAt"/> 는 i 번째(0부터) 열의 값 자리 글이다(<see cref="ICopyDialect.BindPlaceholder"/>,
    /// 예: Tibero 의 <c>TO_DATE(:C2, …)</c>). 없으면 <c>:C{i+1}</c> 그대로다.
    /// </summary>
    public string Insert(string key, IReadOnlyList<string> columns, Func<int, string>? valueAt = null)
    {
        var value = valueAt ?? DefaultValueAt;
        return $"INSERT INTO {TargetTable(key)} ({Quoted(columns)}) VALUES ({string.Join(", ", columns.Select((_, i) => value(i)))})";
    }

    /// <summary>값 자리(<paramref name="valueAt"/>)는 <see cref="Insert"/> 와 같다. <c>USING (SELECT … FROM DUAL)</c> 의 값 자리에만 들어간다.</summary>
    public string Merge(string key, IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns, Func<int, string>? valueAt = null)
    {
        var value = valueAt ?? DefaultValueAt;
        if (keyColumns.Count == 0) throw new ArgumentException("MERGE 에는 키 열이 필요하다.", nameof(keyColumns));
        if (keyColumns.Any(k => !columns.Contains(k, StringComparer.Ordinal)))
            throw new ArgumentException("키 열이 복사 열에 없다.", nameof(keyColumns));
        var sb = new StringBuilder();
        sb.Append("MERGE INTO ").Append(TargetTable(key)).Append(" T USING (SELECT ");
        sb.Append(string.Join(", ", columns.Select((c, i) => $"{value(i)} AS {SqlIdentifier.QuoteAlways(c)}")));
        sb.Append(" FROM DUAL) S ON (");
        sb.Append(string.Join(" AND ", keyColumns.Select(k => $"T.{SqlIdentifier.QuoteAlways(k)} = S.{SqlIdentifier.QuoteAlways(k)}")));
        sb.Append(')');
        var updates = columns.Where(c => !keyColumns.Contains(c, StringComparer.Ordinal)).ToList();
        if (updates.Count > 0)
        {
            sb.Append(" WHEN MATCHED THEN UPDATE SET ");
            sb.Append(string.Join(", ", updates.Select(c => $"T.{SqlIdentifier.QuoteAlways(c)} = S.{SqlIdentifier.QuoteAlways(c)}")));
        }
        sb.Append(" WHEN NOT MATCHED THEN INSERT (").Append(Quoted(columns)).Append(") VALUES (");
        sb.Append(string.Join(", ", columns.Select(c => "S." + SqlIdentifier.QuoteAlways(c)))).Append(')');
        return sb.ToString();
    }

    /// <summary>값 자리 기본 글 <c>:C{i+1}</c>(<see cref="CopyCommonStatements.BindNames"/> 의 이름 앞에 쌍점).</summary>
    private static string DefaultValueAt(int index) => ":C" + (index + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>실패 행 다시 읽기. 키 바인드는 <c>:K1</c>…<c>:Kn</c>.</summary>
    public string SourceSelectByKeys(string key, IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns)
        => $"SELECT {Quoted(columns)} FROM {SourceTable(key)} WHERE "
           + string.Join(" AND ", keyColumns.Select((k, i) => $"{SqlIdentifier.QuoteAlways(k)} = :K{(i + 1).ToString(CultureInfo.InvariantCulture)}"));

    private SqlWithBinds Count(string key, bool isTarget)
    {
        var binds = new Dictionary<string, BindValue>(StringComparer.Ordinal);
        var from = isTarget ? TargetTable(key) : SourceTable(key);
        return new SqlWithBinds($"SELECT COUNT(*) FROM {from}{Where(key, isTarget, binds)}", binds);
    }

    private string Where(string table, bool isTarget, Dictionary<string, BindValue> binds)
    {
        var counter = 0;
        var predicate = Predicate(table, isTarget, binds, ref counter);
        return predicate is null ? "" : " WHERE " + predicate;
    }

    private string? Predicate(string table, bool isTarget, Dictionary<string, BindValue> binds, ref int counter, HashSet<string>? visiting = null)
    {
        var rule = RuleOf(table);
        switch (rule.Kind)
        {
            case RuleKind.Period:
            {
                // 기간식과 추가 조건은 AND 로 묶는다. 기간식(두 기간 열이면 OR 묶음)은 괄호로 감싸 AND 가 OR 한쪽에만 붙지 않게 한다.
                var periodWhere = PeriodPredicate(rule, table, isTarget, binds, ref counter);
                var extra = UserCondition(table, rule.ExtraWhere, binds);
                return (periodWhere, extra) switch
                {
                    (null, _) => extra,
                    (_, null) => periodWhere,
                    _ => $"({periodWhere}) AND {extra}",
                };
            }
            case RuleKind.Custom:
                return UserCondition(table, rule.CustomWhere, binds);
            case RuleKind.ParentKey:
            {
                var link = rule.Parent ?? throw new InvalidOperationException($"{table}: 부모 키 규칙에 부모 연결이 없다.");
                // 부모 표: 원본 쪽은 부모 원본 계정, 대상 쪽은 부모 규칙의 대상 계정(작업 밖이면 MapTarget). 작업 안의 부모는 키로 찾는다.
                var parentKey = CopyOwners.ParentKey(job, rule);
                var owner = isTarget ? CopyOwners.ParentTarget(job, rule) : CopyOwners.ParentSource(job, rule);
                visiting ??= new HashSet<string>(StringComparer.Ordinal) { table };
                if (!visiting.Add(parentKey))
                    throw new InvalidOperationException($"{table}: 부모 키 연결이 순환한다({parentKey}).");
                var child = ColumnList(link.ChildColumns);
                var parentCols = Quoted(link.ParentColumns);
                var parentWhere = CopyOwners.Find(job, parentKey) is not null
                    ? Predicate(parentKey, isTarget, binds, ref counter, visiting)
                    : null;
                var sub = $"SELECT {parentCols} FROM {Qualified(owner, link.ParentTable)}" + (parentWhere is null ? "" : " WHERE " + parentWhere);
                visiting.Remove(parentKey);
                return $"{child} IN ({sub})";
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// 사용자가 쓴 조건(직접 조건·기간 규칙의 추가 조건). 비었으면 null. 괄호로 감싸 앞뒤 식과 섞이지 않게 한다.
    /// 괄호 밖으로 빠져나가는 조건(예: <c>X=1) OR (1=1</c>)은 대상 지우기 범위를 표 전체로 넓히므로 최후 방어로 예외를 낸다(화면은 같은 검사로 먼저 막는다).
    /// </summary>
    private string? UserCondition(string table, string? where, Dictionary<string, BindValue> binds)
    {
        if (string.IsNullOrWhiteSpace(where)) return null;
        if (CheckUserCondition(where) is { } problem)
            throw new InvalidOperationException($"{table}: 조건을 쓸 수 없습니다. {problem}");
        if (CopyVariables.UnknownProblem(where, job.Variables) is { } unknown)
            throw new InvalidOperationException($"{table}: {unknown}");
        // 기간이 열려 있어도 조건에 이름이 있으면 NULL 로 묶는다(바인드 누락 오류를 막는다).
        if (FromDtRegex.IsMatch(where)) binds["FROM_DT"] = new BindValue(BindDataType.Date, period.TimeFrom);
        if (ToDtRegex.IsMatch(where)) binds["TO_DT"] = new BindValue(BindDataType.Date, period.TimeTo);
        string expanded;
        try
        {
            expanded = CopyVariables.Expand(where, job.Variables, binds); // 작업 변수: 값을 바인드하고 여러 값은 목록으로 펼친다
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"{table}: {ex.Message}", ex);
        }

        return $"({expanded})";
    }

    /// <summary>
    /// 사용자 조건이 괄호 하나 안에 머무는지 본다. 문제가 없으면 null, 있으면 이유.
    /// 작은따옴표 문자열과 큰따옴표 식별자 밖에서 괄호 짝이 맞아야 하고(닫는 괄호가 먼저 나오면 안 된다), 주석(<c>--</c>, <c>/*</c>)과 <c>;</c> 는 쓸 수 없다.
    /// 문자열 안의 괄호·<c>--</c> 는 상관없다(<c>''</c> 는 문자열 안의 작은따옴표 하나).
    /// </summary>
    public static string? CheckUserCondition(string where)
    {
        ArgumentNullException.ThrowIfNull(where);
        var depth = 0;
        for (var i = 0; i < where.Length; i++)
        {
            var c = where[i];
            if (c is '\'' or '"')
            {
                if (c == '\'' && IsAlternativeQuotePrefix(where, i)) return AlternativeQuoteProblem;
                var close = where.IndexOf(c, i + 1);
                if (close < 0) return c == '\'' ? "닫히지 않은 작은따옴표가 있습니다." : "닫히지 않은 큰따옴표가 있습니다.";
                i = close; // '' 는 닫고 바로 다시 여는 것과 같아 따로 다루지 않아도 된다
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')' && --depth < 0) return "여는 괄호보다 닫는 괄호가 먼저 나옵니다.";
            else if (c == ';') return "세미콜론(;)은 쓸 수 없습니다.";
            else if (c == '-' && i + 1 < where.Length && where[i + 1] == '-') return "주석(--)은 쓸 수 없습니다.";
            else if (c == '/' && i + 1 < where.Length && where[i + 1] == '*') return "주석(/* */)은 쓸 수 없습니다.";
        }
        return depth == 0 ? null : "괄호 짝이 맞지 않습니다.";
    }

    private const string AlternativeQuoteProblem = "대체 따옴표(q'…')는 쓸 수 없습니다.";

    /// <summary>
    /// 문자열 밖의 작은따옴표(<paramref name="quote"/> 자리) 바로 앞이 Oracle 대체 따옴표 머리(<c>q</c>, <c>nq</c>, 대소문자 무관)인지 본다.
    /// 대체 따옴표 안에서는 <c>'</c> 가 문자열을 닫지 않아 위 검사가 문자열 끝을 잘못 보므로 아예 막는다.
    /// 머리 앞이 식별자 글자면(<c>seq'a'</c> 의 <c>q</c>) 식별자의 끝이라 대체 따옴표가 아니다.
    /// </summary>
    private static bool IsAlternativeQuotePrefix(string where, int quote)
    {
        var q = quote - 1;
        if (q < 0 || where[q] is not ('q' or 'Q')) return false;
        if (q == 0 || !IsIdentifierChar(where[q - 1])) return true;
        var n = q - 1;
        return where[n] is 'n' or 'N' && (n == 0 || !IsIdentifierChar(where[n - 1]));
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';

    private string? PeriodPredicate(TableRule rule, string table, bool isTarget, Dictionary<string, BindValue> binds, ref int counter)
    {
        if (rule.PeriodColumns.Count == 0)
            throw new InvalidOperationException($"{table}: 기간 규칙에 기간 열이 없다.");
        var info = (isTarget ? targetTables : sourceTables).GetValueOrDefault(table);
        var groups = new List<string>();
        foreach (var pc in rule.PeriodColumns)
        {
            var parts = new List<string>();
            BindDataType type;
            object? from, to;
            if (pc.StringFormat is { } format)
            {
                type = BindDataType.Varchar2;
                var b = DateStringFormat.Bounds(period, format);
                from = period.From is null ? null : b.From;
                to = period.To is null ? null : b.To;
            }
            else
            {
                // DATE·TIMESTAMP 는 전기일 시각만큼 민 경계(7시 기준이면 11-01 07:00 이상 11-02 07:00 미만).
                type = BindTypeOf(info, pc.Column);
                from = period.TimeFrom;
                to = period.TimeTo;
            }
            if (from is null && to is null) continue;
            var n = ++counter;
            var col = SqlIdentifier.QuoteAlways(pc.Column);
            if (from is not null)
            {
                var name = $"B{n.ToString(CultureInfo.InvariantCulture)}_FROM";
                parts.Add($"{col} >= :{name}");
                binds[name] = new BindValue(type, from);
            }
            if (to is not null)
            {
                var name = $"B{n.ToString(CultureInfo.InvariantCulture)}_TO";
                parts.Add($"{col} < :{name}");
                binds[name] = new BindValue(type, to);
            }
            groups.Add(string.Join(" AND ", parts));
        }
        return groups.Count switch
        {
            0 => null,
            1 => groups[0],
            _ => string.Join(" OR ", groups.Select(g => $"({g})")),
        };
    }

    private static BindDataType BindTypeOf(TableInfo? info, string column)
    {
        var dataType = info?.Columns.FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.Ordinal))?.DataType
                       ?? throw new InvalidOperationException($"기간 열 '{column}' 의 타입을 알 수 없다.");
        var t = dataType.Trim().ToUpperInvariant();
        if (t == "DATE") return BindDataType.Date;
        if (t.StartsWith("TIMESTAMP", StringComparison.Ordinal))
        {
            if (t.Contains("WITH LOCAL TIME ZONE", StringComparison.Ordinal)) return BindDataType.TimestampLtz;
            return t.Contains("WITH TIME ZONE", StringComparison.Ordinal) ? BindDataType.TimestampTz : BindDataType.Timestamp;
        }
        throw new InvalidOperationException($"기간 열 '{column}' 의 타입 {dataType} 은 날짜로 비교할 수 없다.");
    }

    private TableRule RuleOf(string key)
        => CopyOwners.Find(job, key) ?? throw new InvalidOperationException($"작업에 테이블 '{key}' 규칙이 없다.");

    private string SourceTable(string key)
    {
        var rule = RuleOf(key);
        return Qualified(CopyOwners.Source(job, rule), rule.Table);
    }

    private string TargetTable(string key)
    {
        var rule = RuleOf(key);
        return Qualified(CopyOwners.Target(job, rule), rule.Table);
    }

    private static string Qualified(string owner, string table)
        => SqlIdentifier.QuoteAlways(owner) + "." + SqlIdentifier.QuoteAlways(table);

    private static string Quoted(IReadOnlyList<string> columns) => string.Join(", ", columns.Select(SqlIdentifier.QuoteAlways));

    // 한 열이면 그대로, 여러 열이면 튜플.
    private static string ColumnList(IReadOnlyList<string> columns)
        => columns.Count == 1 ? SqlIdentifier.QuoteAlways(columns[0]) : "(" + Quoted(columns) + ")";
}
