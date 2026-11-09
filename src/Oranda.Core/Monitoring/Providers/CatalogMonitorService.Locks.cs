using Oranda.Core.Catalog.Providers;
using Oranda.Core.Sessions;
using static Oranda.Core.Catalog.Providers.CatalogValues;
using static Oranda.Core.Monitoring.Providers.MonitorRows;

namespace Oranda.Core.Monitoring.Providers;

/// <summary>
/// Lock 모니터 조회(PRD 10장 LK-01~12). 소유: TSK-05-02(본문), P0-b(기반 클래스로 옮김).
/// <para>카탈로그 <c>lock.*</c>(모두 SELECT, <c>license: none</c>)만 쓴다. 권한·뷰가 없으면(<see cref="IsNoAccess"/>) 예외 대신 빈 결과를 돌려주고 이유는 화면의 MonitorAccess 가 보인다.
/// 잠긴 행 값만 카탈로그 밖 SELECT 다(<see cref="LockedRowSelect"/>) — 식별자는 딕셔너리에서 얻은 이름만 따옴표로 감싸 넣고, rowid 는 바인드로 넘긴다.</para>
/// </summary>
public abstract partial class CatalogMonitorService
{
    /// <summary>잠금 목록 행 상한(ROWNUM).</summary>
    public const int LockListMaxRows = 2000;

    /// <summary>값을 읽을 수 있는 객체 유형(row_wait_obj# 가 가리키는 것).</summary>
    private static readonly string[] RowValueObjectTypes = ["TABLE", "TABLE PARTITION", "TABLE SUBPARTITION"];

    public async Task<LockSnapshot> GetLocksAsync(ISession monitor, LockFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(filter);

        var empty = new LockSnapshot([], new BlockingTree([]), DateTimeOffset.UtcNow);
        if (!monitor.Server.Capabilities.CanQueryPerformanceViews)
        {
            return empty;
        }

        var rows = (await LockQueryAsync(monitor, "lock.list", CatalogBinds.Defer(() =>
        [
            CatalogBinds.Trimmed("types", LockTypesBind(filter.Types)),
            CatalogBinds.Trimmed("username", filter.Username),
            CatalogBinds.Trimmed("schema", filter.Schema),
            CatalogBinds.Int("inst_id", filter.InstanceId),
            CatalogBinds.Int("waiting_only", filter.WaitingOrBlockingOnly ? 1 : 0),
            CatalogBinds.Int("min_wait", Math.Max(0, filter.MinWaitSeconds)),
            CatalogBinds.Int("max_rows", LockListMaxRows),
        ]), ct))?.Rows;
        if (rows is null)
        {
            return empty;
        }

        var tree = (await LockQueryAsync(monitor, "session.blocking_tree", CatalogBinds.None, ct))?.Rows;
        return new LockSnapshot(
            [.. rows.Select(ToLockRow)],
            tree is null ? new BlockingTree([]) : BuildBlockingTree(tree.Select(ToBlockingRow)),
            DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<BlockerImpact>> GetBlockerImpactAsync(ISession monitor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        if (!monitor.Server.Capabilities.CanQueryPerformanceViews)
        {
            return [];
        }

        var rows = (await LockQueryAsync(monitor, "lock.blocker_impact", CatalogBinds.None, ct))?.Rows;
        return rows is null
            ? []
            : [.. rows.Select(r => new BlockerImpact(Key(r, 0), Int(r[3]) ?? 0, Long(r[4]) ?? 0, Long(r[5]) ?? 0) { Status = r.Length > 6 ? Str(r[6]) : null })];
    }

    public async Task<LockedRowInfo?> GetLockedRowAsync(ISession monitor, SessionKey waiter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        if (!monitor.Server.Capabilities.CanQueryPerformanceViews)
        {
            return null;
        }

        var rows = (await LockQueryAsync(monitor, "lock.locked_row", BindKey(waiter), ct))?.Rows;
        if (rows is not { Count: > 0 } || Str(rows[0][0]) is not { } owner || Str(rows[0][1]) is not { } table || Str(rows[0][3]) is not { } rowId)
        {
            return null;
        }

        var info = new LockedRowInfo(rowId, owner, table)
        {
            FileNumber = rows[0].Length > 6 ? Int(rows[0][4]) : null,
            BlockNumber = rows[0].Length > 6 ? Long(rows[0][5]) : null,
            RowNumber = rows[0].Length > 6 ? Int(rows[0][6]) : null,
        };
        var objectType = Str(rows[0][2]) ?? string.Empty;
        if (!RowValueObjectTypes.Contains(objectType, StringComparer.Ordinal))
        {
            return LockedRowError(info, $"표가 아닌 객체({objectType})라 행 값을 읽지 않습니다.");
        }

        if (LockedRowSelect(monitor, owner, table) is not { } sql)
        {
            return LockedRowError(info, "객체 이름에 따옴표가 있어 행 값을 읽지 않습니다.");
        }

        try
        {
            var (names, values) = await ReadLockedRowValuesAsync(monitor, sql, rowId, ct);
            return values is null
                ? LockedRowError(info, "행을 찾지 못했습니다(지워졌거나 아직 커밋되지 않은 삽입일 수 있습니다).")
                : info with { ColumnNames = names, Values = values };
        }
        catch (Exception ex) when (IsNoAccess(ex))
        {
            return LockedRowError(info, $"이 표를 읽을 권한이 없습니다: {FirstLine(ex.Message)}");
        }
        catch (Exception ex) when (IsServerError(ex))
        {
            return LockedRowError(info, FirstLine(ex.Message));
        }
        catch (Exception ex) when (ex is InvalidCastException or NotSupportedException or OverflowException)
        {
            return LockedRowError(info, $"열 값을 읽지 못했습니다: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<DdlLockRow>> GetDdlLocksAsync(ISession monitor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        // dba_ddl_locks 는 all_ 짝이 없다. 딕셔너리 권한이 없으면 보내지 않는다(기능 끔).
        if (!monitor.Server.Capabilities.CanQueryDictionary || !monitor.Server.Capabilities.CanQueryPerformanceViews)
        {
            return [];
        }

        var rows = (await LockQueryAsync(monitor, "lock.ddl_locks", CatalogBinds.None, ct))?.Rows;
        return rows is null
            ? []
            : [.. rows.Select(r => new DdlLockRow(Key(r, 0), Str(r[3]) ?? string.Empty, Str(r[4]) ?? string.Empty, Str(r[5]) ?? string.Empty)
            {
                ModeHeld = Str(r[6]),
                ModeRequested = Str(r[7]),
            })];
    }

    public async Task<IReadOnlyList<WaitingBind>> GetWaitingBindsAsync(ISession monitor, WaitingBindTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(target);
        if (!monitor.Server.Capabilities.CanQueryPerformanceViews || (target.Cursor is null && target.Session is null))
        {
            return [];
        }

        var cursor = target.Cursor;
        var session = target.Session;
        var rows = (await LockQueryAsync(monitor, "lock.waiting_binds",
        [
            CatalogBinds.Trimmed("sql_id", cursor?.SqlId),
            CatalogBinds.Int("child", cursor?.ChildNumber),
            CatalogBinds.Int("inst_id", cursor is not null ? cursor.InstanceId : session?.InstanceId),
            CatalogBinds.Long("sid", session?.Sid),
            CatalogBinds.Long("serial", session?.Serial),
        ], ct))?.Rows;

        return rows is null ? [] : [.. TopSqlBinds(rows).Select(b => new WaitingBind(b.Name, b.Position, b.DataType, b.Value) { CapturedAt = b.CapturedAt })];
    }

    public async Task<IReadOnlyList<IdleTransaction>> GetIdleTransactionsAsync(ISession monitor, int minIdleSeconds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        if (!monitor.Server.Capabilities.CanQueryPerformanceViews)
        {
            return [];
        }

        var rows = (await LockQueryAsync(monitor, "lock.idle_transactions", [CatalogBinds.Int("min_idle", Math.Max(0, minIdleSeconds))], ct))?.Rows;
        return rows is null ? [] : [.. rows.Select(ToIdleTransaction)];
    }

    /// <summary>최근 데드락. 원본이 없는 DBMS(<see cref="Connection.DbmsFeature.Deadlocks"/>)는 재정의해 <see cref="NotSupportedException"/>.</summary>
    public virtual async Task<IReadOnlyList<DeadlockEvent>> GetRecentDeadlocksAsync(ISession monitor, int max, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        if (max <= 0 || !monitor.Server.Capabilities.CanQueryPerformanceViews)
        {
            return [];
        }

        if (!HasQuery(monitor, "lock.deadlocks"))
        {
            return []; // Oracle 11.2: v$diag_alert_ext 없음.
        }

        var rows = (await LockQueryAsync(monitor, "lock.deadlocks", [CatalogBinds.Int("max", Math.Min(max, 100))], ct))?.Rows;
        return rows is null ? [] : [.. rows.Select(ToDeadlockEvent)];
    }

    /// <summary>잠긴 행 값 SELECT(<c>:r</c> = rowid 글). 문장을 만들 수 없는 이름이면 null — 문장을 만들지 않는다.</summary>
    protected abstract string? LockedRowSelect(ISession monitor, string owner, string table);

    /// <summary>
    /// 잠긴 행 값을 모니터 세션 연결로 읽는다(세션 명령 잠금 공유, D-041). 행이 없으면 값 null.
    /// 값은 <see cref="FormatLockedValue"/> 꼴로, LOB 은 앞부분만(LOB 머리 읽기는 공급자 몫).
    /// </summary>
    protected abstract Task<(IReadOnlyList<string> Names, IReadOnlyList<string?>? Values)> ReadLockedRowValuesAsync(
        ISession monitor, string sql, string rowId, CancellationToken ct);

    private static LockedRowInfo LockedRowError(LockedRowInfo info, string reason) =>
        info with { ColumnNames = ["오류"], Values = [reason] }; // 상세 구획 오류와 같은 꼴(SectionAsync)

    /// <summary>카탈로그 쿼리. 권한·뷰가 없으면(<see cref="IsNoAccess"/>) null — 예외를 화면까지 올리지 않는다.</summary>
    private async Task<CatalogRows?> LockQueryAsync(ISession monitor, string id, IReadOnlyList<CatalogBind> binds, CancellationToken ct)
    {
        try
        {
            return await SessionQueryAsync(monitor, id, binds, ct);
        }
        catch (Exception ex) when (IsNoAccess(ex))
        {
            return null;
        }
    }
}
