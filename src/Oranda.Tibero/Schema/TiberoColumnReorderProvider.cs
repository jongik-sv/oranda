using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Schema;

/// <summary>
/// 열 순서 바꾸기의 표 종류·참조 소스 줄 조회(읽기 전용) — Oracle <c>ColumnReorderProvider</c> 대응(D4).
/// 쿼리는 Tibero 카탈로그 <c>dbinfo.column_reorder_*</c> 에서 꺼내 <see cref="CatalogRunner"/> 로 보낸다. 이름·상한은 모두 바인드다.
/// Oracle 과 달라진 점: <c>*_tables</c> 에 <c>cluster_name</c> 열이 없어 클러스터 표는 알아내지 못하고(늘 비클러스터), 참조 소스는 <c>ROWNUM</c> 으로 자른다.
/// </summary>
[OrandaService(typeof(IColumnReorderProvider), Dbms = Dbms.Tibero)]
internal sealed class TiberoColumnReorderProvider : IColumnReorderProvider
{
    internal const string TraitsId = "dbinfo.column_reorder_traits";
    internal const string SourceId = "dbinfo.column_reorder_source";

    /// <summary>한 번에 가져올 소스 줄 수 상한(넘으면 앞쪽만 쓴다). Oracle 과 같다.</summary>
    internal const int MaxSourceLines = 5000;

    /// <summary>이 제공자가 쓰는 카탈로그 id 전부(시험이 오타를 잡는다).</summary>
    internal static readonly IReadOnlyList<string> CatalogIds = [TraitsId, SourceId];

    private readonly CatalogRunner _run;

    public TiberoColumnReorderProvider()
        : this(TiberoCatalogQuery.RunAsync)
    {
    }

    /// <summary>시험용: 카탈로그 실행기를 바꿔 끼운다.</summary>
    internal TiberoColumnReorderProvider(CatalogRunner run)
    {
        _run = run;
    }

    public async Task<ColumnReorderTraits> GetTraitsAsync(ISession s, DbObjectRef table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(table);

        var result = await _run(s, TraitsId, TiberoCatalogQuery.TableBinds(table), ct).ConfigureAwait(false);
        if (result.Rows.Count == 0)
        {
            // 표가 아닌 객체(뷰 등) 또는 볼 수 없는 표.
            return new ColumnReorderTraits(false, false, null);
        }

        // 열: temporary, cluster_name(늘 NULL), external_count
        var row = result.Rows[0];
        return new ColumnReorderTraits(
            IsExternal: (TiberoSessionQuery.Long(row[2]) ?? 0) > 0,
            IsTemporary: string.Equals(TiberoSessionQuery.Str(row[0]), "Y", StringComparison.Ordinal),
            ClusterName: TiberoSessionQuery.Str(row[1]));
    }

    public async Task<IReadOnlyList<SourceSearchHit>> FindReferencingSourceLinesAsync(ISession s, DbObjectRef table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(table);

        var binds = TiberoCatalogQuery.TableBinds(table);
        binds["max_rows"] = MaxSourceLines;
        var result = await _run(s, SourceId, binds, ct).ConfigureAwait(false);

        // 열: owner, name, type, line, text
        return
        [
            .. result.Rows.Select(r => new SourceSearchHit(
                new DbObjectRef(TiberoSessionQuery.Str(r[0])!, TiberoSessionQuery.Str(r[1])!, TiberoSessionQuery.Str(r[2])!),
                (int)(TiberoSessionQuery.Long(r[3]) ?? 0),
                (TiberoSessionQuery.Str(r[4]) ?? string.Empty).TrimEnd('\r', '\n'))),
        ];
    }
}
