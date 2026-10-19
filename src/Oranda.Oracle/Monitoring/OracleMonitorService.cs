using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Catalog;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Monitoring;

/// <summary>
/// 세션 모니터(목록·델타·블로킹 트리·상세·Kill·트레이스). Top SQL 부분은 .TopSql.cs(TSK-09-02). 소유: TSK-09-01.
/// <para>
/// 조회는 카탈로그 <c>session.*</c>(모두 <c>license: none</c>, <c>gv$</c>)를 모니터 세션 연결로 직접 읽는다. 쿼리에 팩이 필요하다고 적혀 있으면
/// 프로필 보유 선언·서버 <c>control_management_pack_access</c> 를 대조해 모자라면 보내지 않는다(SAFE-07).
/// Kill·Disconnect·트레이스는 상태를 바꾸므로 <see cref="IExecutionService"/>(정책 관문: 위험 명령·운영 확인, 이력 source=Monitor)로만 보낸다.
/// </para>
/// </summary>
[OrandaService(typeof(IMonitorService))]
internal sealed partial class OracleMonitorService : IMonitorService
{
    /// <summary>델타(SESS-06)로 보는 누적 통계 이름. <c>session.list</c> 의 23~27 열 순서와 같다.</summary>
    internal static readonly string[] DeltaStatNames =
    [
        "CPU used by this session",
        "session logical reads",
        "physical reads",
        "execute count",
        "redo size",
    ];

    private static readonly Lazy<SqlCatalog> SessionCatalog = new(EmbeddedCatalog.Load);

    private static readonly ConditionalWeakTable<OracleSession, SemaphoreSlim> SessionGates = new();

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
            var stats = new long?[DeltaStatNames.Length];
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
                "session.locks", c =>
                {
                    AddInt(c, "inst_id", target.InstanceId);
                    AddLong(c, "sid", target.Sid);
                }, ct),
            await SectionAsync(monitor, "통계", ["이름", "값"],
                "session.stats", c =>
                {
                    AddInt(c, "inst_id", target.InstanceId);
                    AddLong(c, "sid", target.Sid);
                }, ct),
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

    /// <summary>
    /// 블로킹 행 목록으로 트리를 만든다. 간선 = 직접 블로커(<c>blocking_session</c>). 뿌리 = 목록 안에 블로커가 없는 세션(최종 블로커).
    /// 순환(교착)이 있으면 순환 안의 가장 작은 키를 뿌리로 삼아 끊는다.
    /// </summary>
    internal static BlockingTree BuildBlockingTree(IEnumerable<BlockingRow> source)
    {
        var rows = new Dictionary<SessionKey, BlockingRow>();
        var bySid = new Dictionary<(int, long), SessionKey>();
        foreach (var r in source)
        {
            if (rows.TryAdd(r.Key, r))
            {
                bySid.TryAdd((r.Key.InstanceId, r.Key.Sid), r.Key);
            }
        }

        // 직접 블로커를 목록 안의 키로 바꾼다(없거나 자기 자신이면 뿌리).
        SessionKey? ParentOf(BlockingRow r) =>
            r.Blocker is { } b && bySid.TryGetValue((b.InstanceId, b.Sid), out var p) && p != r.Key ? p : null;

        var children = new Dictionary<SessionKey, List<SessionKey>>();
        var rootKeys = new List<SessionKey>();
        foreach (var r in rows.Values)
        {
            if (ParentOf(r) is { } p)
            {
                if (!children.TryGetValue(p, out var list))
                {
                    children[p] = list = [];
                }

                list.Add(r.Key);
            }
            else
            {
                rootKeys.Add(r.Key);
            }
        }

        var placed = new HashSet<SessionKey>();
        var roots = new List<BlockingNode>();
        foreach (var key in Ordered(rootKeys))
        {
            roots.Add(Build(key));
        }

        // 순환(교착): 아직 놓이지 않은 세션이 남으면 그중 가장 작은 키를 뿌리로 삼는다.
        foreach (var key in Ordered(rows.Keys))
        {
            if (!placed.Contains(key))
            {
                roots.Add(Build(key));
            }
        }

        return new BlockingTree(roots);

        BlockingNode Build(SessionKey key)
        {
            placed.Add(key);
            var r = rows[key];
            var waiters = new List<BlockingNode>();
            if (children.TryGetValue(key, out var list))
            {
                foreach (var w in Ordered(list))
                {
                    if (!placed.Contains(w))
                    {
                        waiters.Add(Build(w));
                    }
                }
            }

            return new BlockingNode(key, waiters)
            {
                Username = r.Username,
                Event = r.Event,
                SecondsInWait = r.SecondsInWait,
                SqlId = r.SqlId,
                LockedObject = r.LockedObject,
            };
        }

        static List<SessionKey> Ordered(IEnumerable<SessionKey> keys) =>
            [.. keys.OrderBy(k => k.InstanceId).ThenBy(k => k.Sid).ThenBy(k => k.Serial)];
    }

    /// <summary>직전·이번 누적값 차이. 직전 값이 없거나 줄었으면(세션 재사용 등) 그 통계는 뺀다.</summary>
    internal static IReadOnlyDictionary<string, long> ComputeDeltas(long?[] before, long?[] now)
    {
        var deltas = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < DeltaStatNames.Length && i < before.Length && i < now.Length; i++)
        {
            if (before[i] is { } b && now[i] is { } n && n >= b)
            {
                deltas[DeltaStatNames[i]] = n - b;
            }
        }

        return deltas;
    }

    private async Task ExecuteChangeAsync(ISession monitor, SqlStatement stmt, CancellationToken ct)
    {
        var gate = monitor is OracleSession os ? SessionGates.GetValue(os, _ => new SemaphoreSlim(1, 1)) : null;
        if (gate is not null)
        {
            await gate.WaitAsync(ct);
        }

        ExecutionResult result;
        try
        {
            result = await _execution.ExecuteAsync(monitor, stmt, new Dictionary<string, BindValue>(),
                new ExecutionOptions { Source = HistorySource.Monitor }, ct);
        }
        finally
        {
            gate?.Release();
        }

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

    /// <summary>카탈로그 <c>session.*</c> 쿼리를 모니터 세션 연결로 읽는다. 세션별 문으로 한 번에 하나만 보낸다.</summary>
    private static async Task<List<object?[]>> SessionQueryAsync(ISession s, string id, Action<OracleCommand> bind, CancellationToken ct)
    {
        var session = s as OracleSession ?? throw new ArgumentException("OracleSession 이 아닌 세션은 쓸 수 없습니다.", nameof(s));
        var query = SessionCatalog.Value.Get(id, s.Server);
        EnsureLicense(s, query.Header.License);
        var sql = query.Sql.Replace("@PFX@", s.Server.Capabilities.CanQueryDictionary ? "dba" : "all", StringComparison.Ordinal);

        var gate = SessionGates.GetValue(session, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
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

    private static void AddText(OracleCommand c, string name, string? value) =>
        c.Parameters.Add(new OracleParameter(name, OracleDbType.Varchar2)
        {
            Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim(),
        });

    private static void AddInt(OracleCommand c, string name, int value) =>
        c.Parameters.Add(new OracleParameter(name, OracleDbType.Int32) { Value = value });

    private static void AddLong(OracleCommand c, string name, long value) =>
        c.Parameters.Add(new OracleParameter(name, OracleDbType.Int64) { Value = value });

    /// <summary><c>session.list</c>·<c>session.detail</c> 공통 열 0~22.</summary>
    private static SessionRow ToSessionRow(object?[] r)
    {
        var key = new SessionKey(Int(r[0]) ?? 1, Long(r[1]) ?? 0, Long(r[2]) ?? 0);
        SessionKey? blocker = Long(r[19]) is { } bs ? new SessionKey(Int(r[18]) ?? key.InstanceId, bs, 0) : null;
        return new SessionRow(key, Str(r[3]) ?? string.Empty)
        {
            Username = Str(r[4]),
            OsUser = Str(r[5]),
            Machine = Str(r[6]),
            Program = Str(r[7]),
            Module = Str(r[8]),
            Action = Str(r[9]),
            ClientIdentifier = Str(r[10]),
            SqlId = Str(r[11]),
            PrevSqlId = Str(r[13]),
            Event = Str(r[14]),
            WaitClass = Str(r[15]),
            SecondsInWait = Long(r[16]),
            LastCallElapsedSeconds = Long(r[17]),
            BlockingSession = blocker,
            LogonTime = r[20] is DateTime dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), TimeSpan.Zero) : null,
        };
    }

    private static BlockingRow ToBlockingRow(object?[] r)
    {
        var key = new SessionKey(Int(r[0]) ?? 1, Long(r[1]) ?? 0, Long(r[2]) ?? 0);
        SessionKey? blocker = Long(r[8]) is { } bs ? new SessionKey(Int(r[7]) ?? key.InstanceId, bs, 0) : null;
        return new BlockingRow(key, blocker)
        {
            Username = Str(r[3]),
            Event = Str(r[4]),
            SecondsInWait = Long(r[5]),
            SqlId = Str(r[6]),
            LockedObject = Str(r[11]),
        };
    }

    private static string FirstLine(string message)
    {
        var i = message.IndexOf('\n', StringComparison.Ordinal);
        return (i < 0 ? message : message[..i]).Trim();
    }

    private static string? Str(object? v) => v is null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);

    private static int? Int(object? v) => v is null ? null : Convert.ToInt32(v, CultureInfo.InvariantCulture);

    private static long? Long(object? v) => v is null ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture);
}

/// <summary>
/// 블로킹 트리 재료 한 행. <see cref="Blocker"/> 의 Serial 은 서버가 주지 않아 0 이다 — 간선 대조는 (인스턴스, SID) 로 한다.
/// </summary>
internal sealed record BlockingRow(SessionKey Key, SessionKey? Blocker)
{
    public string? Username { get; init; }

    public string? Event { get; init; }

    public long? SecondsInWait { get; init; }

    public string? SqlId { get; init; }

    public string? LockedObject { get; init; }
}
