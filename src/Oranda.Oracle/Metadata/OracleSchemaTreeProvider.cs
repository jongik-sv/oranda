using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Catalog;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Oracle.Execution;
using Oranda.Oracle.Schema;

namespace Oranda.Oracle.Metadata;

/// <summary>스키마 브라우저 표·뷰 하위 폴더 중 Triggers · Snapshot Log 읽기. 소유: TSK-13-08.</summary>
[OrandaService(typeof(ISchemaTreeProvider), Dbms = Dbms.Oracle)]
internal sealed class OracleSchemaTreeProvider : ISchemaTreeProvider
{
    // 「볼 수 없음」(뷰 없음·권한 부족, DbErrorKinds.IsNoAccess)은 「권한 없음」, 나머지 오류는 그대로 던진다.
    private static bool IsDenied(OracleException ex) => OracleErrorClassifier.IsNoAccess(ex);

    public async Task<SchemaFolderResult<TreeTrigger>> ListTriggersAsync(ISession s, DbObjectRef table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        try
        {
            var rows = await SchemaQuery.RunAsync(s, "meta.detail_triggers", null, c => BindTable(c, table), ct);
            return new SchemaFolderResult<TreeTrigger>(
            [
                .. rows.Rows.Select(r => new TreeTrigger(
                    SchemaQuery.Str(r[5]) ?? table.Owner, SchemaQuery.Str(r[0]) ?? string.Empty, SchemaQuery.Str(r[3]) ?? string.Empty)),
            ]);
        }
        catch (OracleException ex) when (IsDenied(ex))
        {
            return new SchemaFolderResult<TreeTrigger>([], Denied: true);
        }
        catch (CatalogQueryNotFoundException)
        {
            return new SchemaFolderResult<TreeTrigger>([]);
        }
    }

    public async Task<SchemaFolderResult<SnapshotLogRef>> ListSnapshotLogsAsync(ISession s, DbObjectRef table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        try
        {
            var rows = await SchemaQuery.RunAsync(s, "meta.mview_log", null, c => BindTable(c, table), ct);
            return new SchemaFolderResult<SnapshotLogRef>(
                [.. rows.Rows.Select(r => new SnapshotLogRef(SchemaQuery.Str(r[0]) ?? table.Owner, SchemaQuery.Str(r[1]) ?? string.Empty))]);
        }
        catch (OracleException ex) when (IsDenied(ex))
        {
            return new SchemaFolderResult<SnapshotLogRef>([], Denied: true);
        }
        catch (CatalogQueryNotFoundException)
        {
            // 이 서버 버전에 맞는 카탈로그 변형이 없다: 앱을 멈추지 않고 이 폴더만 비운다.
            return new SchemaFolderResult<SnapshotLogRef>([]);
        }
    }

    private static void BindTable(OracleCommand c, DbObjectRef table)
    {
        c.Parameters.Add("owner", table.Owner);
        c.Parameters.Add("name", table.Name);
    }
}
