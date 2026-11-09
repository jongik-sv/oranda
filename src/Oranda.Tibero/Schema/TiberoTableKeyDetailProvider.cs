using System.Globalization;
using System.Text.RegularExpressions;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Schema;

/// <summary>
/// 객체 상세에서 인덱스·제약 줄을 고를 때 그 키의 열을 읽는다 — Oracle <c>OracleTableKeyDetailProvider</c> 대응(T3b,
/// 카탈로그 <c>meta.index_columns</c>·<c>meta.constraint_columns</c>). 조회는 세션 명령 잠금을 쥐고 보낸다(<see cref="TiberoSessionQuery"/>).
/// </summary>
[OrandaService(typeof(ITableKeyDetailProvider), Dbms = Dbms.Tibero)]
internal sealed partial class TiberoTableKeyDetailProvider : ITableKeyDetailProvider
{
    private readonly CatalogRunner _run;

    public TiberoTableKeyDetailProvider()
        : this(TiberoCatalogQuery.RunAsync)
    {
    }

    /// <summary>시험용: 카탈로그 실행기를 바꿔 끼운다.</summary>
    internal TiberoTableKeyDetailProvider(CatalogRunner run)
    {
        _run = run;
    }

    public Task<SchemaFolderResult<IndexColumnDetail>> GetIndexColumnsAsync(ISession s, DbObjectRef table, string indexName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrEmpty(indexName);
        return TiberoCatalogQuery.FolderAsync(_run, s, "meta.index_columns", Binds(table, "index_name", indexName), ToIndexColumn, ct);
    }

    public Task<SchemaFolderResult<ConstraintColumnDetail>> GetConstraintColumnsAsync(ISession s, DbObjectRef table, string constraintName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrEmpty(constraintName);
        // 열: position(1부터 — Tibero 의 0부터 위치는 카탈로그 SQL 이 +1 한다), column_name, r_owner, r_table_name, r_column_name
        return TiberoCatalogQuery.FolderAsync(_run, s, "meta.constraint_columns", Binds(table, "constraint_name", constraintName),
            (r, i) => new ConstraintColumnDetail(Int(r[0]) ?? i + 1, TiberoSessionQuery.Str(r[1]) ?? string.Empty)
            {
                ReferencedOwner = TiberoSessionQuery.Str(r[2]),
                ReferencedTable = TiberoSessionQuery.Str(r[3]),
                ReferencedColumn = TiberoSessionQuery.Str(r[4]),
            }, ct);
    }

    /// <summary>
    /// 카탈로그 한 줄(column_position, column_name, descend, column_expression) → 인덱스 열(Oracle 과 같은 규칙).
    /// 내림차순 일반 열이 숨은 열 + 식 <c>"COL"</c> 로 오면 따옴표를 벗겨 열 이름으로 쓰고 식은 비운다.
    /// </summary>
    internal static IndexColumnDetail ToIndexColumn(object?[] r, int index)
    {
        ArgumentNullException.ThrowIfNull(r);
        var name = TiberoSessionQuery.Str(r[1]) ?? string.Empty;
        var descending = string.Equals(TiberoSessionQuery.Str(r[2]), "DESC", StringComparison.OrdinalIgnoreCase);
        var expression = TiberoSessionQuery.Str(r[3])?.Trim();
        if (string.IsNullOrEmpty(expression))
        {
            expression = null;
        }
        else if (QuotedName().Match(expression) is { Success: true } m)
        {
            name = m.Groups[1].Value;
            expression = null;
        }

        return new IndexColumnDetail(Int(r[0]) ?? index + 1, name, expression, descending);
    }

    private static Dictionary<string, object?> Binds(DbObjectRef table, string keyBind, string keyName)
    {
        var binds = TiberoCatalogQuery.TableBinds(table);
        binds[keyBind] = keyName;
        return binds;
    }

    private static int? Int(object? v) => v is null ? null : Convert.ToInt32(v, CultureInfo.InvariantCulture);

    [GeneratedRegex("^\"([^\"]+)\"$")]
    private static partial Regex QuotedName();
}
