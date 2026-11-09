using System.Collections.Concurrent;
using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Catalog;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Monitoring;
using Oranda.Core.Monitoring.Providers;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Sessions;
using static Oranda.Core.Catalog.Providers.CatalogValues;
using static Oranda.Core.Monitoring.Providers.MonitorRows;

namespace Oranda.Oracle.Monitoring;

/// <summary>
/// 세션 모니터(목록·델타·블로킹 트리·상세·Kill·트레이스). Top SQL 부분은 .TopSql.cs(TSK-09-02). 소유: TSK-09-01.
/// <para>
/// 조회는 카탈로그 <c>session.*</c>(모두 <c>license: none</c>, <c>gv$</c>)를 모니터 세션 연결로 직접 읽는다. 쿼리에 팩이 필요하다고 적혀 있으면
/// 프로필 보유 선언·서버 <c>control_management_pack_access</c> 를 대조해 모자라면 보내지 않는다(SAFE-07).
/// Kill·Disconnect·트레이스는 상태를 바꾸므로 <see cref="IExecutionService"/>(정책 관문: 위험 명령·운영 확인, 이력 source=Monitor)로만 보낸다.
/// </para>
/// </summary>
[OrandaService(typeof(IMonitorService), Dbms = Dbms.Oracle)]
internal sealed partial class OracleMonitorService : IMonitorService
{
    private static readonly Lazy<SqlCatalog> SessionCatalog = new(EmbeddedCatalog.Load);

    private readonly IExecutionService _execution;

    /// <summary>모니터 세션 Id → 직전 조회의 세션별 누적 통계(델타 계산용).</summary>
    private readonly ConcurrentDictionary<Guid, Dictionary<SessionKey, long?[]>> _previousStats = new();

    public OracleMonitorService(IExecutionService execution)
    {
        _execution = execution;
    }

    public async Task<IReadOnlyList<SessionRow>> GetSessionsAsync(ISession monitor, SessionFilter f, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(f);

        var rows = await SessionQueryAsync(monitor, "session.list", c =>
        {
            AddText(c, "username", f.Username);
            AddInt(c, "active_only", f.ActiveOnly ? 1 : 0);
            AddInt(c, "include_bg", f.IncludeBackground ? 1 : 0);
            AddText(c, "text", f.Text);
        }, ct);

        var current = new Dictionary<SessionKey, long?[]>();
        _previousStats.TryGetValue(monitor.Id, out var previous);
        var result = new List<SessionRow>(rows.Count);
        foreach (var r in rows)
        {
            var row = ToSessionRow(r);
            var stats = new long?[DeltaStatNames.Count];
            for (var i = 0; i < stats.Length; i++)
            {
                stats[i] = Long(r[23 + i]);
            }

            current[row.Key] = stats;
            result.Add(previous is not null && previous.TryGetValue(row.Key, out var before)
                ? row with { Deltas = ComputeDeltas(before, stats) }
                : row);
        }

        _previousStats[monitor.Id] = current;
        return result;
    }

    public async Task<BlockingTree> GetBlockingTreeAsync(ISession monitor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var rows = await SessionQueryAsync(monitor, "session.blocking_tree", _ => { }, ct);
        return BuildBlockingTree(rows.Select(ToBlockingRow));
    }

    public async Task<BlockerSnapshot> GetMyBlockingAsync(ISession monitor, long workSid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var rows = await SessionQueryAsync(monitor, "session.my_blocking", c => AddLong(c, "sid", workSid), ct);
        var r = rows.Count == 0 ? new object?[4] : rows[0];
        return new BlockerSnapshot(Int(r[0]) ?? 0)
        {
            TransactionAgeSeconds = Long(r[1]),
            LastCallElapsedSeconds = Long(r[2]),
            MaxIdleBlockerMinutes = Long(r[3]) ?? 0,
        };
    }

    public async Task<SessionDetail> GetSessionDetailAsync(ISession monitor, SessionKey target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        var head = await SessionQueryAsync(monitor, "session.detail", c => BindKey(c, target), ct);
        if (head.Count == 0)
        {
            throw new InvalidOperationException(
                $"세션을 찾을 수 없습니다(이미 끝났을 수 있습니다): SID {target.Sid}, SERIAL# {target.Serial}, 인스턴스 {target.InstanceId}.");
        }

        var r = head[0];
        var session = ToSessionRow(r);
        var childNumber = Int(r[12]);

        var currentText = await SqlTextAsync(monitor, target.InstanceId, session.SqlId, ct);
        var previousText = string.Equals(session.PrevSqlId, session.SqlId, StringComparison.Ordinal)
            ? currentText
            : await SqlTextAsync(monitor, target.InstanceId, session.PrevSqlId, ct);

        var sections = new List<DetailSection>
        {
            await SectionAsync(monitor, "트랜잭션", ["XID", "상태", "시작", "UNDO 블록", "UNDO 레코드", "논리 I/O", "물리 I/O"],
                "session.transaction", c => BindKey(c, target), ct),
            await SectionAsync(monitor, "잠금 객체", ["소유자", "객체", "유형", "잠금 모드"],
                "session.locks", c => BindSid(c, target), ct),
            await SectionAsync(monitor, "통계", ["이름", "값"],
                "session.stats", c => BindSid(c, target), ct),
            await SectionAsync(monitor, "I/O", ["블록 읽기", "일관 읽기", "물리 읽기", "블록 변경", "일관 변경"],
                "session.io", c => BindSid(c, target), ct),
            await SectionAsync(monitor, "대기 이력", ["순번", "이벤트", "대기(µs)", "직전 대기 후(µs)", "P1", "P2", "P3"],
                "session.wait_history", c => BindSid(c, target), ct),
            await SectionAsync(monitor, "긴 작업", ["작업", "대상", "진행률(%)", "처리", "전체", "단위", "경과(초)", "남은 시간(초)", "시작", "메시지"],
                "session.longops", c => BindKey(c, target), ct),
            await SectionAsync(monitor, "열린 커서", ["SQL ID", "커서 유형", "마지막 실행", "SQL"],
                "session.open_cursors", c => BindSid(c, target), ct),
        };

        CursorRef? cursor = null;
        if (session.SqlId is { Length: > 0 } sqlId)
        {
            cursor = new CursorRef(sqlId, childNumber, target.InstanceId);
            sections.Add(await SectionAsync(monitor, "실제 계획", ["계획"], "session.plan", c =>
            {
                AddText(c, "sql_id", sqlId);
                c.Parameters.Add(new OracleParameter("child", OracleDbType.Int32) { Value = (object?)childNumber ?? DBNull.Value });
            }, ct));
        }

        return new SessionDetail(session)
        {
            CurrentSqlText = currentText,
            PreviousSqlText = previousText,
            CurrentCursor = cursor,
            Sections = sections,
        };
    }

    public async Task KillAsync(ISession monitor, SessionKey target, bool immediate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var stmt = KillStatement(target, immediate, monitor.Server.IsRac);
        await ExecuteChangeAsync(monitor, stmt, ct);
    }

    public async Task<string?> SetTraceAsync(ISession monitor, SessionKey target, bool enable, TraceOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(options);

        await ExecuteChangeAsync(monitor, TraceStatement(target, enable, options), ct);
        if (!enable)
        {
            return null;
        }

        var rows = await SessionQueryAsync(monitor, "session.trace_file", c => BindKey(c, target), ct);
        if (rows.Count == 0)
        {
            return null;
        }

        return Str(rows[0][0]) ?? Str(rows[0][1]);
    }

    /// <summary><c>ALTER SYSTEM KILL SESSION 'sid,serial#[,@inst]' [IMMEDIATE]</c>. 위험 명령 확인 대상(재입력 이름 = 세션 식별).</summary>
    internal static SqlStatement KillStatement(SessionKey target, bool immediate, bool rac)
    {
        var id = string.Create(CultureInfo.InvariantCulture, $"{target.Sid},{target.Serial}");
        if (rac)
        {
            id += string.Create(CultureInfo.InvariantCulture, $",@{target.InstanceId}");
        }

        return new SqlStatement($"ALTER SYSTEM KILL SESSION '{id}'{(immediate ? " IMMEDIATE" : string.Empty)}", default, StatementKind.Other)
        {
            Verb = "ALTER SYSTEM",
            DangerousTarget = id,
        };
    }

    /// <summary><c>DBMS_MONITOR.SESSION_TRACE_ENABLE/DISABLE</c> 블록. 값은 숫자·불리언뿐이라 글자로 넣는다.</summary>
    internal static SqlStatement TraceStatement(SessionKey target, bool enable, TraceOptions options)
    {
        var sid = target.Sid.ToString(CultureInfo.InvariantCulture);
        var serial = target.Serial.ToString(CultureInfo.InvariantCulture);
        var text = enable
            ? $"BEGIN DBMS_MONITOR.SESSION_TRACE_ENABLE(session_id => {sid}, serial_num => {serial}, waits => {Bool(options.Waits)}, binds => {Bool(options.Binds)}); END;"
            : $"BEGIN DBMS_MONITOR.SESSION_TRACE_DISABLE(session_id => {sid}, serial_num => {serial}); END;";
        return new SqlStatement(text, default, StatementKind.PlSql) { Verb = "BEGIN" };

        static string Bool(bool b) => b ? "TRUE" : "FALSE";
    }

    private async Task ExecuteChangeAsync(ISession monitor, SqlStatement stmt, CancellationToken ct)
    {
        // 실행 엔진이 세션 명령 잠금(D-041)을 잡는다. 여기서 먼저 잡으면 같은 잠금을 두 번 잡아 교착이다.
        var result = await _execution.ExecuteAsync(monitor, stmt, new Dictionary<string, BindValue>(),
            new ExecutionOptions { Source = HistorySource.Monitor }, ct);

        switch (result.Status)
        {
            case ExecutionStatus.Succeeded:
                return;
            case ExecutionStatus.Cancelled:
                throw new OperationCanceledException("사용자가 취소했습니다.");
            case ExecutionStatus.Denied:
                throw new UnauthorizedAccessException(result.Policy?.Message ?? "정책 관문이 실행을 막았습니다.");
            default:
                throw new InvalidOperationException(result.Error?.Message ?? "실행하지 못했습니다.");
        }
    }

    private static async Task<string?> SqlTextAsync(ISession monitor, int instanceId, string? sqlId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sqlId))
        {
            return null;
        }

        var rows = await SessionQueryAsync(monitor, "session.sql_text", c =>
        {
            AddInt(c, "inst_id", instanceId);
            AddText(c, "sql_id", sqlId);
        }, ct);
        return rows.Count == 0 ? null : Str(rows[0][0]);
    }

    /// <summary>상세 구획 하나. 권한·뷰 문제로 실패하면 구획을 비우지 않고 오류 한 줄을 담는다(다른 구획은 계속 보인다).</summary>
    private static async Task<DetailSection> SectionAsync(ISession monitor, string title, string[] columns, string id,
        Action<OracleCommand> bind, CancellationToken ct)
    {
        try
        {
            var rows = await SessionQueryAsync(monitor, id, bind, ct);
            return new DetailSection(title, columns, [.. rows.Select(r => (IReadOnlyList<object?>)r)]);
        }
        catch (OracleException ex)
        {
            return new DetailSection(title, ["오류"], [[FirstLine(ex.Message)]]);
        }
    }

    /// <summary>카탈로그 <c>session.*</c> 쿼리를 모니터 세션 연결로 읽는다. 세션 명령 잠금(D-041)으로 한 번에 하나만 보낸다.</summary>
    private static async Task<List<object?[]>> SessionQueryAsync(ISession s, string id, Action<OracleCommand> bind, CancellationToken ct)
    {
        var session = s as OracleSession ?? throw new ArgumentException("OracleSession 이 아닌 세션은 쓸 수 없습니다.", nameof(s));
        var query = SessionCatalog.Value.Get(id, s.Server);
        EnsureLicense(s, query.Header.License);
        var sql = query.Sql.Replace("@PFX@", s.Server.Capabilities.CanQueryDictionary ? "dba" : "all", StringComparison.Ordinal);

        var gate = session.CommandGate; // 세션 연결의 명령 잠금(D-041): 실행·결과 페이지·다른 도구와 한 줄로 선다
        await gate.EnterAsync(ct);
        try
        {
            await using var cmd = session.Connection.CreateCommand();
            cmd.BindByName = true;
            cmd.InitialLOBFetchSize = -1;
            cmd.CommandText = sql;
            bind(cmd);

            var rows = new List<object?[]>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }

            return rows;
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

    /// <summary>쿼리가 팩을 요구하면 보유 선언(서버 값을 알면 둘 중 낮은 것)과 대조한다. 모자라면 드라이버로 보내지 않는다.</summary>
    internal static void EnsureLicense(ISession s, CatalogLicense required)
    {
        if (required == CatalogLicense.None)
        {
            return;
        }

        var need = required == CatalogLicense.Tuning ? LicensePack.DiagnosticsAndTuning : LicensePack.Diagnostics;
        var held = s.Profile.LicensePack;
        if (ExecutionPolicy.ParseServerPackAccess(s.Server.ManagementPackAccess) is { } server && server < held)
        {
            held = server;
        }

        if (need > held)
        {
            throw new UnauthorizedAccessException(
                $"이 조회는 {need} 팩이 필요합니다. 접속 '{s.Profile.Name}' 의 보유 선언(또는 서버 설정)이 없어 보내지 않았습니다.");
        }
    }

    private static void BindKey(OracleCommand c, SessionKey key)
    {
        AddInt(c, "inst_id", key.InstanceId);
        AddLong(c, "sid", key.Sid);
        AddLong(c, "serial", key.Serial);
    }

    /// <summary>SERIAL# 열이 없는 뷰(잠금·통계·I/O·대기 이력·열린 커서)용: inst_id, sid 만 묶는다.</summary>
    private static void BindSid(OracleCommand c, SessionKey key)
    {
        AddInt(c, "inst_id", key.InstanceId);
        AddLong(c, "sid", key.Sid);
    }

    private static void AddText(OracleCommand c, string name, string? value) =>
        c.Parameters.Add(new OracleParameter(name, OracleDbType.Varchar2)
        {
            Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim(),
        });

    private static void AddInt(OracleCommand c, string name, int value) =>
        c.Parameters.Add(new OracleParameter(name, OracleDbType.Int32) { Value = value });

    private static void AddLong(OracleCommand c, string name, long value) =>
        c.Parameters.Add(new OracleParameter(name, OracleDbType.Int64) { Value = value });
}
