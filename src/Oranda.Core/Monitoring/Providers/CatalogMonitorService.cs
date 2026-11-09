using System.Collections.Concurrent;
using Oranda.Core.Catalog.Providers;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using static Oranda.Core.Catalog.Providers.CatalogValues;
using static Oranda.Core.Monitoring.Providers.MonitorRows;

namespace Oranda.Core.Monitoring.Providers;

/// <summary>
/// 세션 모니터(목록·델타·블로킹 트리·상세·Kill·트레이스)의 DBMS 공통 본문. Lock 부분은 .Locks.cs(TSK-05-02), Top SQL 부분은 .TopSql.cs(TSK-09-02).
/// 소유: TSK-09-01(본문), P0-b(기반 클래스로 옮김).
/// <para>
/// 조회는 카탈로그 <c>session.*</c>(모두 <c>license: none</c>, <c>gv$</c>)를 모니터 세션 연결로 직접 읽는다. 쿼리에 팩이 필요하다고 적혀 있으면
/// 질의 포트가 프로필 보유 선언·서버 <c>control_management_pack_access</c> 를 대조해 모자라면 보내지 않는다(SAFE-07, <see cref="CatalogLicenseGate"/>).
/// Kill·Disconnect·트레이스는 상태를 바꾸므로 <see cref="IExecutionService"/>(정책 관문: 위험 명령·운영 확인, 이력 source=Monitor)로만 보낸다.
/// </para>
/// <para>
/// DBMS 에 매이지 않는다(D2). 등록은 DBMS 쪽 하위 형식이 한다(<c>[OrandaService(typeof(IMonitorService), Dbms = …)]</c>) — 이 형식 자체는 등록하지 않는다.
/// DBMS 차이는 질의 포트 <see cref="QueryAsync"/>, 서버 오류 판정(<see cref="IsServerError"/>·<see cref="IsNoAccess"/>)과 고리로만 남긴다.
/// </para>
/// </summary>
public abstract partial class CatalogMonitorService : IMonitorService
{
    /// <summary>session.*·lock.*: <c>@PFX@</c> = 딕셔너리 권한이면 dba, 아니면 all.</summary>
    protected static readonly CatalogQueryOptions SessionQueryOptions = new() { DictionaryPrefix = true };

    private readonly IExecutionService _execution;

    /// <summary>모니터 세션 Id → 직전 조회의 세션별 누적 통계(델타 계산용).</summary>
    private readonly ConcurrentDictionary<Guid, Dictionary<SessionKey, long?[]>> _previousStats = new();

    protected CatalogMonitorService(IExecutionService execution)
    {
        _execution = execution;
    }

    public async Task<IReadOnlyList<SessionRow>> GetSessionsAsync(ISession monitor, SessionFilter f, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(f);

        var rows = (await SessionQueryAsync(monitor, "session.list",
        [
            CatalogBinds.Trimmed("username", f.Username),
            CatalogBinds.Int("active_only", f.ActiveOnly ? 1 : 0),
            CatalogBinds.Int("include_bg", f.IncludeBackground ? 1 : 0),
            CatalogBinds.Trimmed("text", f.Text),
        ], ct)).Rows;

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
        var rows = (await SessionQueryAsync(monitor, "session.blocking_tree", CatalogBinds.None, ct)).Rows;
        return BuildBlockingTree(rows.Select(ToBlockingRow));
    }

    public async Task<BlockerSnapshot> GetMyBlockingAsync(ISession monitor, long workSid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var rows = (await SessionQueryAsync(monitor, "session.my_blocking", [CatalogBinds.Long("sid", workSid)], ct)).Rows;
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

        var head = (await SessionQueryAsync(monitor, "session.detail", BindKey(target), ct)).Rows;
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

        var unavailable = new Dictionary<string, string>(StringComparer.Ordinal);
        var sections = new List<DetailSection>
        {
            await SectionAsync(monitor, "트랜잭션", ["XID", "상태", "시작", "UNDO 블록", "UNDO 레코드", "논리 I/O", "물리 I/O"],
                "session.transaction", BindKey(target), unavailable, ct),
            await SectionAsync(monitor, "잠금 객체", ["소유자", "객체", "유형", "잠금 모드"],
                "session.locks", BindSid(target), unavailable, ct),
            await SectionAsync(monitor, "통계", ["이름", "값"],
                "session.stats", BindSid(target), unavailable, ct),
            await SectionAsync(monitor, "I/O", ["블록 읽기", "일관 읽기", "물리 읽기", "블록 변경", "일관 변경"],
                "session.io", BindSid(target), unavailable, ct),
            await SectionAsync(monitor, "대기 이력", ["순번", "이벤트", "대기(µs)", "직전 대기 후(µs)", "P1", "P2", "P3"],
                "session.wait_history", BindSid(target), unavailable, ct),
            await SectionAsync(monitor, "긴 작업", ["작업", "대상", "진행률(%)", "처리", "전체", "단위", "경과(초)", "남은 시간(초)", "시작", "메시지"],
                "session.longops", BindKey(target), unavailable, ct),
            await SectionAsync(monitor, "열린 커서", ["SQL ID", "커서 유형", "마지막 실행", "SQL"],
                "session.open_cursors", BindSid(target), unavailable, ct),
        };

        CursorRef? cursor = null;
        if (session.SqlId is { Length: > 0 } sqlId)
        {
            cursor = new CursorRef(sqlId, childNumber, target.InstanceId);
            sections.Add(await SectionAsync(monitor, "실제 계획", ["계획"], "session.plan",
                [CatalogBinds.Trimmed("sql_id", sqlId), CatalogBinds.Int("child", childNumber)], unavailable, ct));
        }

        var detail = new SessionDetail(session)
        {
            CurrentSqlText = currentText,
            PreviousSqlText = previousText,
            CurrentCursor = cursor,
            Sections = sections,
        };
        return unavailable.Count == 0 ? detail : detail with { UnavailableSections = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(unavailable) };
    }

    public async Task KillAsync(ISession monitor, SessionKey target, bool immediate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var stmt = KillStatement(monitor, target, immediate);
        await ExecuteChangeAsync(monitor, stmt, ct);
    }

    /// <summary><see cref="KillAsync"/> 와 같은 문장(<see cref="KillStatement"/>). 보내지 않는다.</summary>
    public SqlStatement PreviewKill(ISession monitor, SessionKey target, bool immediate)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return KillStatement(monitor, target, immediate);
    }

    public async Task<string?> SetTraceAsync(ISession monitor, SessionKey target, bool enable, TraceOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(options);

        var stmt = TraceStatement(monitor, target, enable, options)
            ?? throw new NotSupportedException("이 DBMS 는 세션 트레이스를 지원하지 않습니다.");
        await ExecuteChangeAsync(monitor, stmt, ct);
        if (!enable)
        {
            return null;
        }

        var rows = (await SessionQueryAsync(monitor, "session.trace_file", BindKey(target), ct)).Rows;
        if (rows.Count == 0)
        {
            return null;
        }

        return Str(rows[0][0]) ?? Str(rows[0][1]);
    }

    /// <summary>
    /// 질의 포트: 카탈로그 <paramref name="id"/> 를 모니터 세션 연결로 읽는다. 공급자가 맡는 것 = 세션 형식 확인, 카탈로그 Get,
    /// 라이선스 관문(<see cref="CatalogLicenseGate"/>), <paramref name="options"/> 적용, 세션 명령 잠금(D-041)으로 한 번에 하나만 보내기, 드라이버 실패 보고.
    /// <paramref name="binds"/> 는 명령을 만든 뒤 읽는다(<see cref="CatalogBinds.Defer"/>).
    /// </summary>
    protected abstract Task<CatalogRows> QueryAsync(ISession s, string id, IReadOnlyList<CatalogBind> binds, CatalogQueryOptions options, CancellationToken ct);

    /// <summary>드라이버가 낸 서버 오류인가(상세 구획·잠긴 행 값은 이때 오류 한 줄로 바꾼다). Oracle = OracleException.</summary>
    protected abstract bool IsServerError(Exception ex);

    /// <summary>권한·뷰 없음(「볼 수 없음」)인가. 이때 lock.* 조회는 빈 결과로 돌린다.</summary>
    protected abstract bool IsNoAccess(Exception ex);

    /// <summary>이 서버 판 카탈로그에 <paramref name="id"/> 가 있는가(없는 판이면 조회하지 않고 빈 결과).</summary>
    protected abstract bool HasQuery(ISession monitor, string id);

    /// <summary>세션 끊기 문장. 위험 명령 확인 대상(<see cref="SqlStatement.DangerousTarget"/> = 재입력 이름).</summary>
    protected abstract SqlStatement KillStatement(ISession monitor, SessionKey target, bool immediate);

    /// <summary>세션 트레이스 켜기·끄기 문장. null = 이 DBMS 는 지원하지 않음(<see cref="SetTraceAsync"/> 가 <see cref="NotSupportedException"/>). 기본 = null.</summary>
    protected virtual SqlStatement? TraceStatement(ISession monitor, SessionKey target, bool enable, TraceOptions options) => null;

    /// <summary>
    /// 상세 구획 원본이 이 DBMS 에 없으면 그 이유(질의하지 않고 빈 구획 + <see cref="SessionDetail.UnavailableSections"/>). 기본 = null(질의한다).
    /// </summary>
    protected virtual string? SectionUnavailableReason(ISession monitor, string id) => null;

    private static IReadOnlyList<CatalogBind> BindKey(SessionKey key) =>
        [CatalogBinds.Int("inst_id", key.InstanceId), CatalogBinds.Long("sid", key.Sid), CatalogBinds.Long("serial", key.Serial)];

    /// <summary>SERIAL# 열이 없는 뷰(잠금·통계·I/O·대기 이력·열린 커서)용: inst_id, sid 만 묶는다.</summary>
    private static IReadOnlyList<CatalogBind> BindSid(SessionKey key) =>
        [CatalogBinds.Int("inst_id", key.InstanceId), CatalogBinds.Long("sid", key.Sid)];

    /// <summary>카탈로그 <c>session.*</c> 쿼리를 모니터 세션 연결로 읽는다(<c>@PFX@</c> 치환).</summary>
    private Task<CatalogRows> SessionQueryAsync(ISession s, string id, IReadOnlyList<CatalogBind> binds, CancellationToken ct) =>
        QueryAsync(s, id, binds, SessionQueryOptions, ct);

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

    private async Task<string?> SqlTextAsync(ISession monitor, int instanceId, string? sqlId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sqlId))
        {
            return null;
        }

        var rows = (await SessionQueryAsync(monitor, "session.sql_text",
            [CatalogBinds.Int("inst_id", instanceId), CatalogBinds.Trimmed("sql_id", sqlId)], ct)).Rows;
        return rows.Count == 0 ? null : Str(rows[0][0]);
    }

    /// <summary>상세 구획 하나. 권한·뷰 문제로 실패하면 구획을 비우지 않고 오류 한 줄을 담는다(다른 구획은 계속 보인다).</summary>
    private async Task<DetailSection> SectionAsync(ISession monitor, string title, string[] columns, string id,
        IReadOnlyList<CatalogBind> binds, Dictionary<string, string> unavailable, CancellationToken ct)
    {
        if (SectionUnavailableReason(monitor, id) is { } reason)
        {
            unavailable[title] = reason;
            return new DetailSection(title, columns, []);
        }

        try
        {
            var rows = (await SessionQueryAsync(monitor, id, binds, ct)).Rows;
            return new DetailSection(title, columns, [.. rows.Select(r => (IReadOnlyList<object?>)r)]);
        }
        catch (Exception ex) when (IsServerError(ex))
        {
            return new DetailSection(title, ["오류"], [[FirstLine(ex.Message)]]);
        }
    }
}
