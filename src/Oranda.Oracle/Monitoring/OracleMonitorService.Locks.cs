using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using Oranda.Core.Catalog;
using Oranda.Core.Monitoring;
using Oranda.Core.Sessions;
using Oranda.Oracle.Execution;
using Oranda.Oracle.Sessions;
using static Oranda.Core.Catalog.Providers.CatalogValues;
using static Oranda.Core.Monitoring.Providers.MonitorRows;

namespace Oranda.Oracle.Monitoring;

/// <summary>
/// Lock 모니터 조회(PRD 10장 LK-01~12). 소유: TSK-05-02.
/// <para>카탈로그 <c>lock.*</c>(모두 SELECT, <c>license: none</c>)만 쓴다. 권한·뷰가 없으면 예외 대신 빈 결과를 돌려주고 이유는 화면의 MonitorAccess 가 보인다.
/// 잠긴 행 값만 카탈로그 밖 SELECT 다 — 식별자는 딕셔너리에서 얻은 이름만 따옴표로 감싸 넣고, rowid 는 바인드로 넘긴다.</para>
/// </summary>
internal sealed partial class OracleMonitorService
{
    /// <summary>잠금 목록 행 상한(ROWNUM).</summary>
    internal const int LockListMaxRows = 2000;

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

        var rows = await LockQueryAsync(monitor, "lock.list", c =>
        {
            AddText(c, "types", LockTypesBind(filter.Types));
            AddText(c, "username", filter.Username);
            AddText(c, "schema", filter.Schema);
            c.Parameters.Add(new OracleParameter("inst_id", OracleDbType.Int32) { Value = (object?)filter.InstanceId ?? DBNull.Value });
            AddInt(c, "waiting_only", filter.WaitingOrBlockingOnly ? 1 : 0);
            AddInt(c, "min_wait", Math.Max(0, filter.MinWaitSeconds));
            AddInt(c, "max_rows", LockListMaxRows);
        }, ct);
        if (rows is null)
        {
            return empty;
        }

        var tree = await LockQueryAsync(monitor, "session.blocking_tree", _ => { }, ct);
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

        var rows = await LockQueryAsync(monitor, "lock.blocker_impact", _ => { }, ct);
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

        var rows = await LockQueryAsync(monitor, "lock.locked_row", c => BindKey(c, waiter), ct);
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

        if (LockedRowSelect(owner, table) is not { } sql)
        {
            return LockedRowError(info, "객체 이름에 따옴표가 있어 행 값을 읽지 않습니다.");
        }

        try
        {
            var (names, values) = await LockedRowValuesAsync(monitor, sql, rowId, ct);
            return values is null
                ? LockedRowError(info, "행을 찾지 못했습니다(지워졌거나 아직 커밋되지 않은 삽입일 수 있습니다).")
                : info with { ColumnNames = names, Values = values };
        }
        catch (OracleException ex) when (IsAccessError(ex))
        {
            return LockedRowError(info, $"이 표를 읽을 권한이 없습니다: {FirstLine(ex.Message)}");
        }
        catch (OracleException ex)
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

        var rows = await LockQueryAsync(monitor, "lock.ddl_locks", _ => { }, ct);
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

        var rows = await LockQueryAsync(monitor, "lock.waiting_binds", c =>
        {
            var cursor = target.Cursor;
            var session = target.Session;
            AddText(c, "sql_id", cursor?.SqlId);
            c.Parameters.Add(new OracleParameter("child", OracleDbType.Int32) { Value = (object?)cursor?.ChildNumber ?? DBNull.Value });
            c.Parameters.Add(new OracleParameter("inst_id", OracleDbType.Int32)
            {
                Value = (object?)(cursor is not null ? cursor.InstanceId : session?.InstanceId) ?? DBNull.Value,
            });
            c.Parameters.Add(new OracleParameter("sid", OracleDbType.Int64) { Value = (object?)session?.Sid ?? DBNull.Value });
            c.Parameters.Add(new OracleParameter("serial", OracleDbType.Int64) { Value = (object?)session?.Serial ?? DBNull.Value });
        }, ct);

        return rows is null ? [] : [.. TopSqlBinds(rows).Select(b => new WaitingBind(b.Name, b.Position, b.DataType, b.Value) { CapturedAt = b.CapturedAt })];
    }

    public async Task<IReadOnlyList<IdleTransaction>> GetIdleTransactionsAsync(ISession monitor, int minIdleSeconds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        if (!monitor.Server.Capabilities.CanQueryPerformanceViews)
        {
            return [];
        }

        var rows = await LockQueryAsync(monitor, "lock.idle_transactions", c => AddInt(c, "min_idle", Math.Max(0, minIdleSeconds)), ct);
        return rows is null ? [] : [.. rows.Select(ToIdleTransaction)];
    }

    public async Task<IReadOnlyList<DeadlockEvent>> GetRecentDeadlocksAsync(ISession monitor, int max, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        if (max <= 0 || !monitor.Server.Capabilities.CanQueryPerformanceViews)
        {
            return [];
        }

        try
        {
            SessionCatalog.Value.Get("lock.deadlocks", monitor.Server);
        }
        catch (CatalogQueryNotFoundException)
        {
            return []; // 11.2: v$diag_alert_ext 없음.
        }

        var rows = await LockQueryAsync(monitor, "lock.deadlocks", c => AddInt(c, "max", Math.Min(max, 100)), ct);
        return rows is null ? [] : [.. rows.Select(ToDeadlockEvent)];
    }

    /// <summary>잠긴 행 값 SELECT. 이름에 따옴표가 있으면(따옴표 식별자로도 만들 수 없음) null — 문장을 만들지 않는다.</summary>
    internal static string? LockedRowSelect(string owner, string table)
    {
        if (owner.Length == 0 || table.Length == 0 || owner.Contains('"', StringComparison.Ordinal) || table.Contains('"', StringComparison.Ordinal)
            || owner.Contains('\0', StringComparison.Ordinal) || table.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        return $"SELECT * FROM \"{owner}\".\"{table}\" WHERE ROWID = CHARTOROWID(:r)";
    }

    /// <summary>권한·뷰 없음(ORA-00942·01031·01039). 이때는 빈 결과로 돌린다.</summary>
    internal static bool IsAccessError(OracleException ex) => OracleErrorClassifier.IsNoAccess(ex);

    private static LockedRowInfo LockedRowError(LockedRowInfo info, string reason) =>
        info with { ColumnNames = ["오류"], Values = [reason] }; // 상세 구획 오류와 같은 꼴(SectionAsync)

    /// <summary>카탈로그 쿼리. 권한·뷰가 없으면(ORA-00942·01031·01039) null — 예외를 화면까지 올리지 않는다.</summary>
    private static async Task<List<object?[]>?> LockQueryAsync(ISession monitor, string id, Action<OracleCommand> bind, CancellationToken ct)
    {
        try
        {
            return await SessionQueryAsync(monitor, id, bind, ct);
        }
        catch (OracleException ex) when (IsAccessError(ex))
        {
            return null;
        }
    }

    /// <summary>잠긴 행 값을 모니터 세션 연결로 읽는다(세션 명령 잠금 공유, D-041). 행이 없으면 값 null.</summary>
    private static async Task<(IReadOnlyList<string> Names, IReadOnlyList<string?>? Values)> LockedRowValuesAsync(
        ISession s, string sql, string rowId, CancellationToken ct)
    {
        var session = s as OracleSession ?? throw new ArgumentException("OracleSession 이 아닌 세션은 쓸 수 없습니다.", nameof(s));
        var gate = session.CommandGate; // 세션 연결의 명령 잠금(D-041): 실행·결과 페이지·다른 도구와 한 줄로 선다
        await gate.EnterAsync(ct);
        try
        {
            await using var cmd = session.Connection.CreateCommand();
            cmd.BindByName = true;
            cmd.InitialLONGFetchSize = LockedValueMaxLength; // LONG 은 앞부분만, LOB 은 아래에서 앞부분만 읽는다
            cmd.CommandText = sql;
            cmd.Parameters.Add(new OracleParameter("r", OracleDbType.Varchar2) { Value = rowId });

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var names = new string[reader.FieldCount];
            for (var i = 0; i < names.Length; i++)
            {
                names[i] = reader.GetName(i);
            }

            if (!await reader.ReadAsync(ct))
            {
                return (names, null);
            }

            var values = new string?[names.Length];
            for (var i = 0; i < values.Length; i++)
            {
                if (await reader.IsDBNullAsync(i, ct))
                {
                    continue;
                }

                try
                {
                    values[i] = reader.GetProviderSpecificFieldType(i) switch
                    {
                        var t when t == typeof(OracleClob) => ReadClobHead(reader.GetOracleClob(i)),
                        var t when t == typeof(OracleBlob) => ReadBlobHead(reader.GetOracleBlob(i)),
                        _ => FormatLockedValue(reader.GetValue(i)),
                    };
                }
                catch (Exception ex) when (ex is InvalidCastException or OverflowException)
                {
                    values[i] = FormatLockedValue(reader.GetOracleValue(i)?.ToString()); // NUMBER 정밀도가 .NET decimal 을 넘는 경우
                }
            }

            return (names, values);
        }
        catch (OracleException ex) when (session.ReportFailure(ex))
        {
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private static string ReadClobHead(OracleClob clob)
    {
        using (clob)
        {
            var buffer = new char[LockedValueMaxLength];
            var read = clob.Read(buffer, 0, buffer.Length);
            var text = new string(buffer, 0, Math.Max(0, read));
            return clob.Length / 2 > read ? text + "…" : text; // Length 는 바이트(UTF-16)
        }
    }

    private static string ReadBlobHead(OracleBlob blob)
    {
        using (blob)
        {
            var buffer = new byte[64];
            var read = blob.Read(buffer, 0, buffer.Length);
            var hex = "0x" + Convert.ToHexString(buffer, 0, Math.Max(0, read));
            return blob.Length > read ? hex + $"… ({blob.Length} bytes)" : hex;
        }
    }
}
