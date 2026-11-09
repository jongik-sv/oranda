using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Schema;

/// <summary>
/// 스키마 브라우저 표·뷰 하위 폴더 중 Triggers · Snapshot Log 읽기 — Oracle <c>OracleSchemaTreeProvider</c> 대응(T3b).
/// 「볼 수 없음」(뷰 없음·권한 부족)은 「권한 없음」 폴더, 맞는 카탈로그 변형이 없으면 빈 폴더, 나머지 오류는 던진다.
/// </summary>
[OrandaService(typeof(ISchemaTreeProvider), Dbms = Dbms.Tibero)]
internal sealed class TiberoSchemaTreeProvider : ISchemaTreeProvider
{
    private readonly CatalogRunner _run;

    public TiberoSchemaTreeProvider()
        : this(TiberoCatalogQuery.RunAsync)
    {
    }

    /// <summary>시험용: 카탈로그 실행기를 바꿔 끼운다.</summary>
    internal TiberoSchemaTreeProvider(CatalogRunner run)
    {
        _run = run;
    }

    public Task<SchemaFolderResult<TreeTrigger>> ListTriggersAsync(ISession s, DbObjectRef table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        // 열: trigger_name, trigger_type, triggering_event, status, when_clause, owner
        return FolderAsync(s, "meta.detail_triggers", TableBinds(table), r => new TreeTrigger(
            TiberoSessionQuery.Str(r[5]) ?? table.Owner, TiberoSessionQuery.Str(r[0]) ?? string.Empty, TiberoSessionQuery.Str(r[3]) ?? string.Empty), ct);
    }

    public Task<SchemaFolderResult<SnapshotLogRef>> ListSnapshotLogsAsync(ISession s, DbObjectRef table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        // 열: log_owner, log_table
        return FolderAsync(s, "meta.mview_log", TableBinds(table), r => new SnapshotLogRef(
            TiberoSessionQuery.Str(r[0]) ?? table.Owner, TiberoSessionQuery.Str(r[1]) ?? string.Empty), ct);
    }

    private Task<SchemaFolderResult<T>> FolderAsync<T>(ISession s, string id, IReadOnlyDictionary<string, object?> binds, Func<object?[], T> map, CancellationToken ct) =>
        TiberoCatalogQuery.FolderAsync(_run, s, id, binds, (r, _) => map(r), ct);

    private static Dictionary<string, object?> TableBinds(DbObjectRef table) => TiberoCatalogQuery.TableBinds(table);
}
