using System.Globalization;
using System.Text;
using Oranda.Core.Execution;
using Oranda.Core.Text;

namespace Oranda.Core.Rollback.Compare;

/// <summary>비교 질의 한 개와 바인드.</summary>
public sealed record CompareQuery(string Sql, IReadOnlyDictionary<string, BindValue> Binds);

/// <summary>
/// 시점 비교가 읽는 질의 글(설계 8.2, RB-31). 범위 좁히기 세 단계(VERSIONS_XID → 문장 WHERE → 표 전체)와 키로 한 시점의 값 읽기.
/// 값은 방언의 읽기 식(고정 형식 글, 6.6)으로 읽고, 키 조건은 방언 리터럴로 쓴다. 시점 번호는 <see cref="IRollbackDialect.AsOf"/>.
/// </summary>
public static class PointCompareQueries
{
    /// <summary>표 전체 비교의 상한(8.2 의 3번).</summary>
    public const int FullTableLimit = 100_000;

    /// <summary>키 IN 목록 한 번의 최대 개수(ORA-01795).</summary>
    public const int MaxInList = 1000;

    public const string RowIdAlias = "ORANDA$RID";

    public const string TooManyRows = "범위를 좁힐 수 없습니다. 시각과 조건을 직접 지정하세요";

    /// <summary>
    /// 8.2 의 1번: 내 트랜잭션이 바꾼 행의 키. <paramref name="rawXid"/> 는 RAW XID 16진 글.
    /// 결과 열 = 키 열(PK 가 없으면 <see cref="RowIdAlias"/>). Oracle 문법이며 VERSIONS 문장이 카탈로그에 없는 DBMS 에서는 부르지 않는다.
    /// </summary>
    public static CompareQuery VersionKeys(RollbackTable table, IRollbackDialect dialect, long basePoint, long commitPoint, string rawXid, Func<string, string>? quote = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentException.ThrowIfNullOrEmpty(rawXid);
        var q = quote ?? (n => SqlIdentifier.Quote(n));
        var sql = "SELECT DISTINCT " + KeyList(table, dialect, q) + " FROM " + Name(table, q)
                  + " VERSIONS BETWEEN SCN :b AND :c WHERE VERSIONS_XID = HEXTORAW(:xid)";
        return new CompareQuery(sql, new Dictionary<string, BindValue>(StringComparer.Ordinal)
        {
            ["b"] = new(BindDataType.Number, (decimal)basePoint),
            ["c"] = new(BindDataType.Number, (decimal)commitPoint),
            ["xid"] = new(BindDataType.Varchar2, rawXid),
        });
    }

    /// <summary>
    /// 8.2 의 2번: 문장 WHERE(바인드 그대로)를 한 시점에서 다시 써서 맞는 키를 읽는다. <paramref name="alias"/> 는 원래 문장의 별칭.
    /// 기준·커밋 두 시점에서 각각 부르고 합친다.
    /// </summary>
    public static CompareQuery WhereKeys(RollbackTable table, IRollbackDialect dialect, long point, string? whereText, string? alias,
                                         IReadOnlyDictionary<string, BindValue> binds, Func<string, string>? quote = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(binds);
        var q = quote ?? (n => SqlIdentifier.Quote(n));
        var sb = new StringBuilder("SELECT ").Append(KeyList(table, dialect, q, alias)).Append(" FROM ").Append(Name(table, q)).Append(' ').Append(dialect.AsOf(point));
        if (alias is { Length: > 0 })
        {
            sb.Append(' ').Append(q(alias));
        }

        if (whereText is { Length: > 0 })
        {
            sb.Append(" WHERE ").Append(whereText);
        }

        return new CompareQuery(sb.ToString(), binds);
    }

    /// <summary>표의 행 수(한 시점, <paramref name="point"/> 가 null 이면 지금). 표 전체 비교 전 상한 확인용.</summary>
    public static CompareQuery Count(RollbackTable table, IRollbackDialect dialect, long? point, Func<string, string>? quote = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(dialect);
        var q = quote ?? (n => SqlIdentifier.Quote(n));
        return new CompareQuery("SELECT COUNT(*) FROM " + Name(table, q) + At(dialect, point), Empty);
    }

    /// <summary>
    /// 한 시점의 행 값. <paramref name="keys"/> 가 null 이면 표 전체(상한은 부르는 쪽이 <see cref="Count"/> 로 먼저 본다),
    /// 있으면 그 키만(<see cref="MaxInList"/> 개씩 나눈 질의 여러 개). 결과 열 = 표 열 순서의 읽기 식, 마지막에 ROWID 글.
    /// 리터럴로 쓸 수 없는 키 값이 있으면 그 키는 빼고 <paramref name="skipped"/> 에 센다.
    /// </summary>
    public static IReadOnlyList<CompareQuery> ReadRows(RollbackTable table, IRollbackDialect dialect, long? point, IReadOnlyList<IReadOnlyList<object?>>? keys,
                                                       out int skipped, Func<string, string>? quote = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(dialect);
        var q = quote ?? (n => SqlIdentifier.Quote(n));
        skipped = 0;
        var head = new StringBuilder("SELECT ");
        for (var i = 0; i < table.Columns.Count; i++)
        {
            var col = table.Columns[i];
            head.Append(i == 0 ? string.Empty : ", ")
                .Append(dialect.ReadExpression(col, "t." + q(col.Name))) // 큰 열은 NULL 여부 표지(1 = NULL)
                .Append(" AS C").Append(i.ToString(CultureInfo.InvariantCulture));
        }

        head.Append(", ROWIDTOCHAR(t.ROWID) AS ").Append(RowIdAlias).Append(" FROM ").Append(Name(table, q)).Append(At(dialect, point)).Append(" t");
        if (keys is null)
        {
            return [new CompareQuery(head.ToString(), Empty)];
        }

        var conditions = new List<string>(keys.Count);
        var keyCols = table.KeyColumns.Count == 0 ? null : table.KeyColumns.Select(k => table.Columns.First(c => c.Name == k)).ToArray();
        foreach (var key in keys)
        {
            if (KeyLiteral(dialect, keyCols, key) is { } lit)
            {
                conditions.Add(lit);
            }
            else
            {
                skipped++;
            }
        }

        var left = keyCols is null ? "t.ROWID" : keyCols.Length == 1 ? "t." + q(keyCols[0].Name) : "(" + string.Join(", ", keyCols.Select(c => "t." + q(c.Name))) + ")";
        return [.. conditions.Chunk(MaxInList).Select(chunk => new CompareQuery(head + " WHERE " + left + " IN (" + string.Join(", ", chunk) + ")", Empty))];
    }

    private static readonly IReadOnlyDictionary<string, BindValue> Empty = new Dictionary<string, BindValue>();

    private static string? KeyLiteral(IRollbackDialect dialect, RollbackColumn[]? keyCols, IReadOnlyList<object?> key)
    {
        if (keyCols is null)
        {
            return key.Count == 1 && key[0] is string rid && rid.Length > 0 ? dialect.RowIdLiteral(rid) : null;
        }

        if (key.Count != keyCols.Length)
        {
            return null;
        }

        var parts = new string[keyCols.Length];
        for (var i = 0; i < keyCols.Length; i++)
        {
            if (key[i] is null or DBNull || dialect.Literal(keyCols[i], key[i], out _) is not { } lit)
            {
                return null;
            }

            parts[i] = lit;
        }

        return parts.Length == 1 ? parts[0] : "(" + string.Join(", ", parts) + ")";
    }

    private static string At(IRollbackDialect dialect, long? point) => point is { } p ? " " + dialect.AsOf(p) : string.Empty;

    /// <summary>키 열 목록. 값은 방언의 읽기 식(고정 형식 글)으로 읽어 <see cref="ReadRows"/> 의 키 리터럴로 그대로 쓸 수 있게 한다.</summary>
    private static string KeyList(RollbackTable table, IRollbackDialect dialect, Func<string, string> q, string? alias = null)
    {
        var prefix = alias is { Length: > 0 } ? q(alias) + "." : string.Empty;
        return table.KeyColumns.Count == 0
            ? "ROWIDTOCHAR(" + prefix + "ROWID) AS " + RowIdAlias
            : string.Join(", ", table.KeyColumns.Select((k, i) =>
                dialect.ReadExpression(table.Columns.First(c => c.Name == k), prefix + q(k)) + " AS K" + i.ToString(CultureInfo.InvariantCulture)));
    }

    private static string Name(RollbackTable table, Func<string, string> q)
        => string.IsNullOrEmpty(table.Table.Owner) ? q(table.Table.Name) : q(table.Table.Owner) + "." + q(table.Table.Name);
}
