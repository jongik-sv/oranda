using System.Globalization;
using Oranda.Core.Catalog;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 복사 세션 하나의 대기 원인을 한 번 읽은 값(<c>copy.wait_cause</c> 한 행). 대기 이벤트가 없으면(<see cref="Event"/> = null) DB 가 처리 중이다.
/// </summary>
public sealed record CopyWaitCause(
    string? Event,
    string? WaitClass,
    long? SecondsInWait,
    long? BlockingSid,
    string? BlockingUser,
    string? BlockingProgram,
    string? BlockingMachine)
{
    /// <summary>
    /// 대기 줄에 덧붙이는 글: <c>행 잠금(enq: TX) · 막는 세션 123 (APPUSER@was01, JDBC Thin Client)</c>.
    /// 알아보는 이벤트(<see cref="Kind"/>)는 우리말 이름 + 짧은 이벤트 이름, 모르는 이벤트는 이벤트 이름 그대로다. 막는 세션이 없으면 앞부분만.
    /// </summary>
    public string Describe()
    {
        var head = Event is null
            ? "DB 처리 중(대기 이벤트 없음)"
            : Kind(Event, WaitClass, BlockingSid is not null) is { } kind
                ? $"{kind}({ShortEvent(Event)})"
                : Event;
        if (BlockingSid is not { } sid)
        {
            return head;
        }

        var who = BlockingUser is null ? null : BlockingMachine is null ? BlockingUser : $"{BlockingUser}@{BlockingMachine}";
        var parts = new[] { who, BlockingProgram }.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var blocker = "막는 세션 " + sid.ToString(CultureInfo.InvariantCulture) + (parts.Count == 0 ? string.Empty : " (" + string.Join(", ", parts) + ")");
        return head + " · " + blocker;
    }

    /// <summary>
    /// 이벤트의 우리말 이름(모르면 null). Oracle 이벤트 이름으로 고르고, 이름을 모르는 DBMS(Tibero) 이벤트는 막는 세션이 있으면 「잠금 대기」,
    /// 없으면 대기 종류(<paramref name="waitClass"/>, Tibero 판도 Oracle 이름으로 바꿔 돌려준다)로 고른다.
    /// </summary>
    public static string? Kind(string evt, string? waitClass, bool blocked)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (evt.StartsWith("enq: TX - row lock", StringComparison.OrdinalIgnoreCase))
        {
            return "행 잠금";
        }

        if (evt.StartsWith("enq: TX - index contention", StringComparison.OrdinalIgnoreCase))
        {
            return "인덱스 분할 경합";
        }

        if (evt.StartsWith("enq: TX - allocate ITL", StringComparison.OrdinalIgnoreCase))
        {
            return "블록 ITL 부족";
        }

        if (evt.StartsWith("enq: TM", StringComparison.OrdinalIgnoreCase))
        {
            return "테이블 잠금";
        }

        if (evt.StartsWith("enq: ", StringComparison.OrdinalIgnoreCase))
        {
            return "잠금";
        }

        if (evt.StartsWith("library cache", StringComparison.OrdinalIgnoreCase) || evt.StartsWith("cursor: pin", StringComparison.OrdinalIgnoreCase))
        {
            return "객체 잠금(DDL·컴파일)";
        }

        if (evt.StartsWith("log file", StringComparison.OrdinalIgnoreCase))
        {
            return "리두 로그 기록 대기";
        }

        if (evt.StartsWith("buffer busy", StringComparison.OrdinalIgnoreCase))
        {
            return "블록 경합";
        }

        if (evt.StartsWith("SQL*Net message from client", StringComparison.OrdinalIgnoreCase)
            || evt.StartsWith("SQL*Net more data from client", StringComparison.OrdinalIgnoreCase))
        {
            return "앱 쪽 응답 대기";
        }

        if (evt.StartsWith("SQL*Net", StringComparison.OrdinalIgnoreCase))
        {
            return "네트워크 대기";
        }

        if (blocked)
        {
            return "잠금 대기";
        }

        return waitClass switch
        {
            "User I/O" or "System I/O" => "디스크 I/O",
            "Concurrency" => "동시성 경합",
            "Commit" => "커밋 대기",
            "Configuration" => "설정 한도 대기",
            _ => null,
        };
    }

    /// <summary><c>enq: TX - row lock contention</c> → <c>enq: TX</c>. 다른 이벤트는 그대로.</summary>
    public static string ShortEvent(string evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var dash = evt.IndexOf(" - ", StringComparison.Ordinal);
        return evt.StartsWith("enq: ", StringComparison.OrdinalIgnoreCase) && dash > 0 ? evt[..dash] : evt;
    }
}

/// <summary>
/// 복사 쓰기가 오래 기다릴 때 원인을 보는 감시(WH1). 기다리는 복사 세션은 명령 중이라 자기 <c>V$SESSION</c> 을 못 읽으므로, 같은 접속으로 감시용 짧은 세션을
/// 하나 열어(<c>openMonitor</c>: 실행 때 받은 비밀번호를 다시 쓴다, 새로 묻지 않음) 복사 세션의 SID(<see cref="ServerInfo.SessionId"/>, 접속 때 읽은 값)로
/// <c>copy.wait_cause</c> 를 읽는다.
/// <list type="bullet">
/// <item>대기가 <see cref="Threshold"/>(30초)를 넘어야 읽는다. 한 복사 세션은 <see cref="MinInterval"/> 안에 다시 읽지 않고 앞 값을 쓴다.</item>
/// <item>감시 세션은 실행당 동시에 하나다(여러 테이블이 함께 기다려도 하나를 나눠 쓴다). 기다리는 세션이 모두 끝나면(<see cref="EndWaitAsync"/>) 닫는다.</item>
/// <item>볼 권한이 없으면(객체 없음·권한 부족) 조용히 그만두고 <see cref="TakeNotice"/> 가 안내를 한 번만 돌려준다. 감시 세션을 못 열거나 다른 서버 오류면 이번 실행에서는 더 묻지 않는다.</item>
/// </list>
/// 복사를 막지 않는다: 어떤 실패도 null 로 끝나고 예외는 취소(<see cref="OperationCanceledException"/>)만 나간다. 여러 스레드에서 불러도 된다.
/// </summary>
public sealed class CopyWaitProbe : IAsyncDisposable
{
    /// <summary>이만큼 기다리면 원인을 읽기 시작한다.</summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(30);

    /// <summary>한 복사 세션의 원인을 다시 읽는 최소 간격.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(15);

    /// <summary>감시 조회 한 번의 한도. 감시 세션도 막히면 복사 대기 표시를 늦추지 않게 짧게 끊는다.</summary>
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    /// <summary>볼 권한이 없을 때 한 번 내는 안내.</summary>
    public const string NoAccessNotice = "대기 원인을 보려면 V$SESSION 조회 권한이 필요합니다(대기 원인 없이 대기 시간만 보입니다).";

    private const string QueryId = "copy.wait_cause";

    private readonly Func<CancellationToken, Task<ISession?>> _openMonitor;
    private readonly IBulkExecutionService _bulk;
    private readonly ISqlCatalog _catalog;
    private readonly IBulkValueCodec _codec;
    private readonly IDbErrorClassifier _errors;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _noticeLock = new();

    // 키 = (인스턴스 이름, SID): RAC 의 다른 인스턴스에 같은 SID 인 복사 세션이 둘이어도 섞이지 않는다.
    private readonly Dictionary<(string?, long), (long At, string? Text)> _last = [];
    private readonly HashSet<(string?, long)> _waiting = [];

    private ISession? _monitor;
    private bool _off;
    private bool _noticePending;
    private bool _noticeGiven;
    private bool _disposed;

    /// <param name="openMonitor">감시 세션을 연다(복사 대상·원본과 같은 접속, <see cref="SessionPurpose.Monitor"/>). 못 열면 null 또는 예외.</param>
    /// <param name="bulk">조회를 보낸다(<see cref="CopyCatalogQueries.QueryAsync(IBulkExecutionService, ISession, string, IReadOnlyDictionary{string, BindValue}, CancellationToken)"/>).</param>
    /// <param name="catalog">감시 세션 DBMS 의 카탈로그(<c>copy.wait_cause</c>).</param>
    /// <param name="codec">값 해석(감시 세션 DBMS).</param>
    /// <param name="errors">서버 오류 분류(볼 권한 없음 판정, <see cref="CopyErrors.IsNoAccess"/>).</param>
    /// <param name="time">시계(시험용). null = 시스템 시계.</param>
    /// <param name="threshold">원인을 읽기 시작하는 대기(시험용). null = <see cref="Threshold"/>(30초).</param>
    public CopyWaitProbe(Func<CancellationToken, Task<ISession?>> openMonitor, IBulkExecutionService bulk, ISqlCatalog catalog, IBulkValueCodec codec,
                         IDbErrorClassifier errors, TimeProvider? time = null, TimeSpan? threshold = null)
    {
        ArgumentNullException.ThrowIfNull(openMonitor);
        ArgumentNullException.ThrowIfNull(bulk);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(errors);
        _openMonitor = openMonitor;
        _bulk = bulk;
        _catalog = catalog;
        _codec = codec;
        _errors = errors;
        _time = time ?? TimeProvider.System;
        StartAfter = threshold ?? Threshold;
    }

    /// <summary>이 감시가 원인을 읽기 시작하는 대기(기본 <see cref="Threshold"/>).</summary>
    public TimeSpan StartAfter { get; }

    /// <summary>
    /// 복사 세션 <paramref name="copySession"/> 이 <paramref name="waited"/> 만큼 기다리는 중일 때 대기 줄에 덧붙일 원인 글(<see cref="CopyWaitCause.Describe"/>).
    /// 30초 전이거나 읽지 못하면 null. 읽은 글은 <see cref="MinInterval"/> 동안 다시 쓴다(세션을 찾지 못한 null 도).
    /// </summary>
    public async Task<string?> DescribeAsync(ISession copySession, TimeSpan waited, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(copySession);
        if (waited <= StartAfter)
        {
            return null;
        }

        var sid = copySession.Server.SessionId;
        var key = KeyOf(copySession);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_off || _disposed || sid <= 0)
            {
                return null;
            }

            _waiting.Add(key);
            var now = _time.GetTimestamp();
            if (_last.TryGetValue(key, out var last) && _time.GetElapsedTime(last.At, now) < MinInterval)
            {
                return last.Text;
            }

            var cause = await ReadAsync(copySession, sid, ct).ConfigureAwait(false);
            var text = cause?.Describe();
            if (!_off)
            {
                _last[key] = (now, text);
            }

            return text;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 감시 세션으로 대기 원인 한 행을 읽는다(30초·간격 규칙 없이). 감시 세션은 열어 둔다(<see cref="EndWaitAsync"/> 가 닫는다).
    /// 세션을 찾지 못하면(이미 끝남·다른 인스턴스) 또는 읽지 못하면 null.
    /// </summary>
    public async Task<CopyWaitCause?> ProbeAsync(ISession copySession, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(copySession);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_off || _disposed || copySession.Server.SessionId <= 0)
            {
                return null;
            }

            _waiting.Add(KeyOf(copySession));
            return await ReadAsync(copySession, copySession.Server.SessionId, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>볼 권한이 없어 그만둔 뒤 처음 한 번만 <see cref="NoAccessNotice"/>, 그 밖에는 null.</summary>
    public string? TakeNotice()
    {
        lock (_noticeLock)
        {
            if (!_noticePending || _noticeGiven)
            {
                return null;
            }

            _noticeGiven = true;
            return NoAccessNotice;
        }
    }

    /// <summary>이번 실행에서 감시를 그만두었는가(권한 없음·감시 세션 못 엶·서버 오류).</summary>
    public bool IsOff => Volatile.Read(ref _off);

    /// <summary>감시 세션이 열려 있는가(시험·진단용).</summary>
    public bool HasMonitorSession => Volatile.Read(ref _monitor) is not null;

    /// <summary>
    /// 복사 세션 <paramref name="copySession"/> 의 대기가 끝났다. 기다리는 세션이 더 없으면 감시 세션을 닫는다(다음 대기는 다시 연다). 그 세션의 앞 값도 버린다.
    /// </summary>
    public async Task EndWaitAsync(ISession copySession)
    {
        ArgumentNullException.ThrowIfNull(copySession);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _waiting.Remove(KeyOf(copySession));
            _last.Remove(KeyOf(copySession));
            if (_waiting.Count == 0)
            {
                await CloseMonitorAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _waiting.Clear();
            await CloseMonitorAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>문 안(<see cref="_gate"/>)에서만 부른다.</summary>
    private async Task<CopyWaitCause?> ReadAsync(ISession copySession, long sid, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(QueryTimeout);
        var opened = _monitor is not null;
        try
        {
            if (_monitor is null)
            {
                _monitor = await _openMonitor(timeout.Token).ConfigureAwait(false);
                opened = _monitor is not null;
            }

            if (_monitor is null)
            {
                Volatile.Write(ref _off, true);
                return null;
            }

            var sql = CopyCatalogQueries.Sql(_catalog, _monitor.Server, QueryId);
            var binds = new Dictionary<string, BindValue>(StringComparer.Ordinal)
            {
                ["SID"] = new(BindDataType.Varchar2, sid.ToString(CultureInfo.InvariantCulture)),
                ["INST"] = new(BindDataType.Varchar2, copySession.Server.InstanceName),
            };
            var rows = await CopyCatalogQueries.QueryAsync(_bulk, _monitor, sql, binds, timeout.Token).ConfigureAwait(false);
            if (rows.FirstOrDefault() is not { } r)
            {
                return null;
            }

            return new CopyWaitCause(
                Blank(_codec.ToText(At(r, 0))), Blank(_codec.ToText(At(r, 1))), _codec.ToInt64(At(r, 2)), _codec.ToInt64(At(r, 3)),
                Blank(_codec.ToText(At(r, 4))), Blank(_codec.ToText(At(r, 5))), Blank(_codec.ToText(At(r, 6))));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // 감시 조회가 한도 안에 끝나지 않았다(감시 세션도 막혔다): 이번 원인은 비우고 다음 간격에 다시 본다. 막힌 세션은 닫아 다음에 새로 연다.
            await CloseMonitorAsync().ConfigureAwait(false);
            return null;
        }
#pragma warning disable CA1031 // 감시는 복사를 막지 않는다: 어떤 실패도 원인 없음으로 끝낸다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // 볼 권한 안내는 감시 세션이 열린 뒤 조회가 막힌 때만(세션을 열다 난 권한 오류는 V$SESSION 권한 문제가 아니다).
            if (opened && CopyErrors.IsNoAccess(_errors, ex))
            {
                lock (_noticeLock)
                {
                    _noticePending = true;
                }
            }

            Volatile.Write(ref _off, true);
            await CloseMonitorAsync().ConfigureAwait(false);
            return null;
        }
    }

    private async Task CloseMonitorAsync()
    {
        var monitor = _monitor;
        _monitor = null;
        if (monitor is null)
        {
            return;
        }

        try
        {
            await monitor.DisposeAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // 감시 세션 닫기 실패는 복사와 상관없다.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static (string?, long) KeyOf(ISession s) => (s.Server.InstanceName, s.Server.SessionId);

    private static object? At(object?[] row, int i) => i < row.Length ? row[i] : null;

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
