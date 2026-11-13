using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Oranda.Core.Events;
using Oranda.Core.Sessions;
using Oranda.Core.Storage;

namespace Oranda.Core.Metadata;

/// <summary>
/// 객체 목록 캐시 흐름의 DBMS 공통 부분. 메타데이터 제공자(<see cref="IMetadataProvider"/> 구현)가 이 형식을 상속하고 인터페이스는 스스로 선언한다:
/// <see cref="ListObjectsAsync"/>·<see cref="FindObjectsByPrefixAsync"/> 는 여기 공개 메서드가 그대로 인터페이스 멤버가 된다.
/// <para>
/// 객체 목록은 메모리가 아닌 <see cref="IMetadataCache"/>(SQLite) 에 서버 전체 목록으로 둔다. 캐시가 있으면 서버를 읽지 않고 답하고,
/// 뒤에서 <see cref="RefreshObjectListAsync"/> 가 마지막 DDL 시각(<see cref="MetaObject.LastDdlTime"/>)·상태 변경을 감지해 <see cref="MetadataInvalidated"/> 를 낸다.
/// 테이블·프로시저 같은 상세는 객체 행의 payload 에 담아 두고 <see cref="FindCachedAsync"/>·<see cref="TryReadPayload"/>·<see cref="StorePayloadAsync"/> 로 쓴다.
/// </para>
/// <para>
/// 상속한 형식이 정하는 것: 서버에서 객체 목록 읽기(<see cref="FetchObjectsAsync"/>), 백그라운드 갱신이 삼켜도 되는 실패(<see cref="IsIgnorableBackgroundRefreshFailure"/>).
/// </para>
/// <para>
/// 뒤 갱신이 세션이 바빠(<see cref="SessionBusyException"/>) 멈추면 「남은 일」로 두고 짧은 간격으로 다시 한다(<see cref="BackgroundRetryDelay"/>·<see cref="BackgroundRetryLimit"/>).
/// 세션 잠금(<c>SessionCommandGate</c>)에는 풀릴 때의 알림이 없고, 메타데이터 전용 세션은 접속 정보·인증을 Core 가 다시 다뤄야 해서 다시 하기를 골랐다.
/// 다시 해도 넘으면 남은 일로 두었다가 다음 목록 조회가 갱신 간격을 기다리지 않고 바로 다시 시작한다. 갱신 시작·끝·건수·실패는 앱 로그(<see cref="Trace"/>)에 남긴다.
/// </para>
/// <para>
/// D-041 원칙: 뒤 갱신은 사용자의 실행 뒤에 줄을 서지 않는다. 질의를 시작하는(커서를 여는) 잠금은 기다리지 않고(0) 바쁘면 바로 다시 하기로 가며,
/// 세션 상태가 <see cref="SessionState.Busy"/> 이면 시도하지 않는다. 잠금을 기다려 주는 것은 이미 시작한 읽기를 이어 갈 때(Oracle 의 행마다 다시 잡기)뿐이다
/// (<see cref="SessionWait.BriefWithResume"/>·<see cref="SessionWait.Resume"/>). 줄을 서면 여러 문장 스크립트·결과 페이지 넘김 사이의 빈틈에 뒤 갱신이 끼어
/// 분 단위 전체 목록 읽기가 사용자의 다음 문장을 막는다.
/// </para>
/// <para>
/// 같은 <c>DbKey</c> 의 전체 갱신·유형 새로 고침·캐시 초기화는 한 잠금(<c>_refreshGates</c>)으로 차례를 지킨다: 겹치면 오래된 스냅샷이 새 결과를 지우거나 되살린다.
/// </para>
/// </summary>
public abstract class CachedMetadataProvider
{
    /// <summary>캐시로 답한 뒤 백그라운드 갱신을 다시 시작하는 기본 최소 간격.</summary>
    protected static readonly TimeSpan DefaultBackgroundRefreshInterval = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IMetadataCache _cache;
    private readonly IEventBus _events;
    private readonly TimeSpan _backgroundRefreshInterval;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRefresh = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TimeSpan> _refreshCost = new(StringComparer.Ordinal);

    /// <summary>
    /// 도는 중인 뒤 갱신(다시 하기 기다림 포함). DbKey 하나에 하나만. 어느 세션으로 할지는 그 안에 두고, 같은 DbKey 의 다른 세션이 목록을 물으면 그 세션으로 넘긴다.
    /// </summary>
    private readonly ConcurrentDictionary<string, BackgroundRun> _running = new(StringComparer.Ordinal);

    /// <summary>세션이 바빠 끝내지 못한 뒤 갱신(남은 일). 다음 목록 조회가 갱신 간격을 기다리지 않고 다시 시작한다.</summary>
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);

    /// <param name="cache">객체 목록·상세 캐시. 여기서는 받아 두기만 한다(서버만 읽는 시험은 null 을 넘긴다).</param>
    /// <param name="events">변경 감지 알림(<see cref="MetadataInvalidated"/>)을 낼 버스.</param>
    /// <param name="backgroundRefreshInterval">캐시로 답한 뒤 백그라운드 갱신을 다시 시작하는 최소 간격. <see cref="Timeout.InfiniteTimeSpan"/> = 하지 않음(시험용).</param>
    protected CachedMetadataProvider(IMetadataCache cache, IEventBus events, TimeSpan backgroundRefreshInterval)
    {
        _cache = cache;
        _events = events;
        _backgroundRefreshInterval = backgroundRefreshInterval;

        // 세션이 닫히거나 끊기면 그 세션으로 다시 하기를 기다리던 뒤 갱신을 바로 깨운다(옛 세션에 묶여 같은 DbKey 의 새 세션 갱신을 막지 않게).
        events?.Subscribe<SessionClosed>(this, e => WakeRunsOf(e.SessionId));
        events?.Subscribe<SessionBroken>(this, e => WakeRunsOf(e.SessionId));
    }

    /// <summary>
    /// payload_json 형식 버전(맨 위 <c>"v"</c>). 다르면 캐시가 없는 것으로 본다. payload 는 Core 모델(<see cref="TableInfo"/>·<see cref="ProcedureInfo"/>)의 JSON 이라 기본값을 함께 쓴다.
    /// 2: 외래키 제약에 참조 키 이름(<see cref="ConstraintInfo.ReferencedConstraint"/>)을 더했다.
    /// 한 DBMS 의 읽기만 바뀌어 그 DBMS 캐시만 버려야 하면 상속한 형식이 올린다.
    /// </summary>
    protected virtual int PayloadVersion => 2;

    /// <summary>
    /// 뒤 갱신이 이미 시작한 읽기를 이어 갈 때(Oracle: 커서를 연 뒤 행마다 잠금을 다시 잡을 때) 세션 잠금을 기다리는 한도. 이보다 오래 다른 명령(긴 실행 등)이
    /// 잠금을 쥐면 바쁨으로 멈추고 나중에 다시 한다. 0 이 아닌 까닭: 캐시로 답한 직후 트리·속성 창이 보내는 짧은 질의마다 분 단위 전체 목록 읽기가 끊기지 않게.
    /// 질의를 시작하는 잠금은 이 값과 상관없이 기다리지 않는다(D-041: 사용자 실행 뒤에 줄을 서지 않음. 줄을 서면 여러 문장 스크립트·결과 페이지 넘김 사이의
    /// 빈틈에 뒤 갱신이 먼저 잠금을 받아 사용자의 다음 문장이 분 단위로 기다린다).
    /// </summary>
    protected TimeSpan BackgroundBusyWait { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>바빠 멈춘 뒤 갱신을 다시 하기까지 처음 기다리는 시간. 다시 할 때마다 두 배(최대 1분).</summary>
    protected TimeSpan BackgroundRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>바빠 멈춘 뒤 갱신을 다시 하는 최대 횟수. 넘으면 남은 일로 두고 다음 목록 조회 때 다시 시작한다.</summary>
    protected int BackgroundRetryLimit { get; set; } = 8;

    /// <summary>
    /// 서버에서 조건 <paramref name="q"/> 에 맞는 객체 목록을 읽는다. 빈 조건(<c>new ObjectQuery()</c>)이면 캐시에 넣을 서버 전체 목록이다.
    /// 행마다 <see cref="MetaObject.DbKey"/> = <c>s.DbKey</c>, <see cref="MetaObject.FetchedAt"/> = <paramref name="fetchedAt"/> 으로 채운다.
    /// </summary>
    protected abstract Task<IReadOnlyList<MetaObject>> FetchObjectsAsync(ISession s, ObjectQuery q, DateTimeOffset fetchedAt, CancellationToken ct);

    /// <summary>
    /// 백그라운드 갱신 실패 중 삼켜도 되는 것(세션이 닫혔거나 끊겼거나 바쁘다). 다음 조회 때 다시 시도한다.
    /// false 인 실패도 뒤 갱신은 앱 로그에 오류로 남기고 끝낸다(남은 일은 지운다: 같은 실패로 목록 조회마다 전체 갱신을 다시 시작하지 않게).
    /// </summary>
    protected abstract bool IsIgnorableBackgroundRefreshFailure(Exception ex);

    /// <summary>객체 이름 목록. 캐시가 있으면 캐시로 답하고 뒤에서 갱신을 시작한다. 캐시가 없으면 전체 목록을 한 번 받아 채운 뒤 답한다.</summary>
    public async Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(q);

        // 생성 시각은 캐시에 없다. 이 조건만 서버에 직접 묻는다.
        // INVALID 상태도 서버에 묻는다: 캐시의 상태는 마지막 갱신 때 값이라, 표를 지우거나 열을 바꿔 새로 INVALID 가 된 객체를 놓친다
        // (캐시로 답하면 「INVALID 만」 거르기가 비어 나온다). 서버 질의는 소유자·유형으로 좁혀 보내므로 작다.
        if (q.CreatedAfter is not null || q.InvalidOnly)
        {
            return ToRefs(await FetchObjectsAsync(s, q, DateTimeOffset.UtcNow, ct));
        }

        var source = await TryCacheAsync(() => _cache.GetSourceAsync(s.DbKey, ct));
        if (source?.RefreshedAt is { } refreshedAt)
        {
            var cached = await TryCacheAsync(() => _cache.GetObjectsAsync(s.DbKey, q.Owner, null, ct));
            if (cached is not null)
            {
                // 목록이 최대 유효기간(설정)을 넘었으면 읽는 데 걸린 시간에 맞춘 긴 간격을 기다리지 않는다(접속한 뒤 첫 조회에서 바로 다시 받는다).
                var maxAge = TryMaxAge();
                var stale = maxAge is { } max && max > TimeSpan.Zero && DateTimeOffset.UtcNow - refreshedAt > max;
                StartBackgroundRefresh(s, stale);
                return ToRefs(FilterCached(cached, q));
            }
        }

        // 캐시 없음: 전체 목록을 한 번 받아 캐시에 채운 뒤 거기서 답한다.
        var (all, _) = await RefreshObjectListCoreAsync(s, "캐시 없음", ct);
        return ToRefs(FilterCached(all, q));
    }

    /// <summary>객체 목록 캐시의 기준 시각(마지막 전체 목록, UTC). 캐시가 없거나 무효화됐거나 캐시를 못 쓰면 null.</summary>
    public async Task<DateTimeOffset?> GetObjectListRefreshedAtAsync(ISession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        return (await TryCacheAsync(() => _cache.GetSourceAsync(s.DbKey, ct)))?.RefreshedAt;
    }

    /// <summary>
    /// 한 소유자·한 유형의 객체 목록을 서버에서 다시 받아(카탈로그 <c>meta.objects</c> 에 소유자·유형 조건) 캐시의 그 유형 행을 서버 목록으로 바꾼다:
    /// 새로 생긴 것은 넣고, DDL 시각·상태가 바뀐 것은 상세를 비우고, 사라진 것은 지운다. 바뀐 것이 있으면 <see cref="MetadataInvalidated"/> 를 낸다.
    /// 목록 기준 시각(<c>refreshed_at</c>)은 그대로 둔다(전체 목록을 받은 때가 아니다). 캐시가 없거나 무효화됐으면 전체 목록을 받는다.
    /// 세션 잠금은 보통 명령처럼 차례를 기다린다(사용자가 기다리는 동기 경로). 같은 DbKey 의 전체 갱신·초기화와는 차례를 지킨다(도는 중이면 끝난 뒤에 한다).
    /// </summary>
    public async Task<ObjectListChange> RefreshObjectTypeAsync(ISession s, string owner, string objectType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentException.ThrowIfNullOrEmpty(objectType);
        var dbKey = s.DbKey;
        var gate = _refreshGates.GetOrAdd(dbKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // 잡은 뒤에 읽는다: 그동안 전체 갱신·초기화가 목록을 바꿨을 수 있다.
            var source = await TryCacheAsync(() => _cache.GetSourceAsync(dbKey, ct));
            if (source?.RefreshedAt is not null)
            {
                return await RefreshObjectTypeLockedAsync(s, owner, objectType, ct);
            }
        }
        finally
        {
            gate.Release();
        }

        // 그 유형만 채우면 나머지 유형이 빈 채로 「목록이 있다」가 된다: 전체를 받는다(같은 잠금을 다시 잡으므로 놓은 뒤에).
        var (_, whole) = await RefreshObjectListCoreAsync(s, "유형 새로 고침(캐시 없음)", ct);
        return whole;
    }

    /// <summary><see cref="RefreshObjectTypeAsync"/> 본체. 갱신 잠금(<c>_refreshGates</c>)을 쥔 채 부른다.</summary>
    private async Task<ObjectListChange> RefreshObjectTypeLockedAsync(ISession s, string owner, string objectType, CancellationToken ct)
    {
        var dbKey = s.DbKey;
        var reason = $"유형 새로 고침 {owner}.{objectType}";
        Trace.TraceInformation("메타데이터 객체 목록 갱신 시작 ({0}, {1})", dbKey, reason);
        var timer = Stopwatch.StartNew();
        try
        {
            var fetchedAt = DateTimeOffset.UtcNow;
            var fresh = await FetchObjectsAsync(s, new ObjectQuery { Owner = owner, ObjectTypes = [objectType] }, fetchedAt, ct);
            // 서버 조건과 같은 것만 비교한다(구현이 유형 조건을 덜 거른 경우에도 다른 유형 행을 지우지 않게).
            fresh = [.. fresh.Where(o => string.Equals(o.Owner, owner, StringComparison.Ordinal) && string.Equals(o.Type, objectType, StringComparison.Ordinal))];

            // 기준 시각은 서버를 읽은 뒤 다시 읽은 값만 지킨다: 그 사이 다른 곳(열 순서 바꾸기 등)이 무효화해 비웠으면 비운 채로 둔다.
            var current = await TryCacheAsync(() => _cache.GetSourceAsync(dbKey, ct));
            var cached = await TryCacheAsync(() => _cache.GetObjectsAsync(dbKey, owner, objectType, ct)) ?? [];
            var diff = Diff(cached, fresh, fetchedAt);

            var keep = new MetaSource
            {
                DbKey = dbKey,
                ServerVersion = s.Server.Version,
                RefreshedAt = current?.RefreshedAt, // 지울 때 비워진 기준 시각을 되돌린다(전체 목록 시각은 바뀌지 않았다)
                LastUsedAt = fetchedAt,
            };
            await TryCacheAsync(async () =>
            {
                if (diff.Vanished.Count > 0)
                {
                    await _cache.InvalidateAsync(dbKey, [.. diff.Vanished.Select(ToRef)], ct);
                }

                await _cache.UpsertAsync(keep, [.. diff.Added, .. diff.Changed], ct);
                return true;
            });

            var change = diff.Change;
            if (change.Total > 0)
            {
                _events?.Publish(new MetadataInvalidated(dbKey, [.. diff.Vanished.Concat(diff.Changed).Concat(diff.Added).Select(ToRef)]));
            }

            TraceDone(dbKey, reason, change, fresh.Count, timer.Elapsed);
            return change;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TraceFailed(dbKey, reason, ex);
            throw;
        }
    }

    /// <summary>
    /// 이 접속의 객체 목록 캐시를 모두 비우고(<see cref="IMetadataCache.InvalidateAsync"/> 빈 목록 = 전체) 서버의 전체 목록을 바로 다시 받는다.
    /// 끝나면 그 DB 전체의 <see cref="MetadataInvalidated"/>(빈 목록)를 낸다: 자동 완성 등도 다시 읽는다.
    /// </summary>
    public async Task<ObjectListChange> ResetObjectListCacheAsync(ISession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var dbKey = s.DbKey;
        Trace.TraceInformation("메타데이터 캐시 초기화 ({0})", dbKey);
        // 비우기도 갱신 잠금 안에서 한다: 도는 유형 새로 고침이 비운 뒤에 옛 기준 시각을 되살리지 않게.
        var (_, change) = await RefreshObjectListCoreAsync(s, "캐시 초기화", ct, resetFirst: true);
        _events?.Publish(new MetadataInvalidated(dbKey, []));
        return change;
    }

    /// <summary>
    /// 캐시에서만 찾는다: 키 입력마다 불리므로 서버의 전체 목록을 읽지 않고 갱신도 시작하지 않는다(캐시는 <see cref="ListObjectsAsync"/> 가 채운다).
    /// 캐시로 답할 수 없으면 실패로 끝낸다: 빈 목록으로 답하면 부른 쪽 메모리에 남아 캐시가 찬 뒤에도 그 접두어의 후보가 나오지 않는다.
    /// </summary>
    /// <exception cref="InvalidOperationException">객체 목록 캐시가 아직 없다.</exception>
    public async Task<IReadOnlyList<DbObjectRef>> FindObjectsByPrefixAsync(ISession s, string namePrefix, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(namePrefix);
        var source = await TryCacheAsync(() => _cache.GetSourceAsync(s.DbKey, ct));
        var found = source?.RefreshedAt is null ? null : await TryCacheAsync(() => _cache.FindByPrefixAsync(s.DbKey, namePrefix, limit, ct));
        return found is null ? throw new InvalidOperationException("객체 목록 캐시가 아직 없습니다.") : ToRefs(found);
    }

    /// <summary>
    /// 서버의 전체 객체 목록을 받아 캐시와 비교한다. 새로 생기거나 마지막 DDL 시각(<see cref="MetaObject.LastDdlTime"/>)·상태가 바뀐 것은 상세(payload)를 비우고,
    /// 사라진 것은 지운다. 이전 갱신이 있었고 바뀐 것이 있으면 <see cref="MetadataInvalidated"/> 를 낸다. 같은 <c>DbKey</c> 의 갱신은 한 번에 하나만 돈다.
    /// </summary>
    /// <returns>갱신 후 캐시에 있는 전체 목록(캐시를 못 쓰면 서버에서 받은 목록).</returns>
    protected async Task<IReadOnlyList<MetaObject>> RefreshObjectListAsync(ISession s, CancellationToken ct)
        => (await RefreshObjectListCoreAsync(s, "직접 호출", ct)).All;

    /// <summary><see cref="RefreshObjectListAsync"/> 본체. <paramref name="reason"/> 은 로그에 남길 계기(뒤 갱신·캐시 없음·초기화 …).</summary>
    /// <param name="resetFirst">갱신 잠금을 잡은 뒤 서버를 읽기 전에 이 DbKey 의 캐시를 모두 비우고 남은 일을 지운다(캐시 초기화).</param>
    /// <param name="background">
    /// 뒤 갱신: 같은 DbKey 의 다른 갱신(사용자가 기다리는 초기화·유형 새로 고침 — 그것도 사용자 실행 뒤에 줄을 섰을 수 있다)이 갱신 잠금을 쥐었으면
    /// 그 뒤에 줄을 서지 않고 바쁨(<see cref="SessionBusyException"/>)으로 끝나 다시 하기로 간다(D-041).
    /// </param>
    private async Task<(IReadOnlyList<MetaObject> All, ObjectListChange Change)> RefreshObjectListCoreAsync(
        ISession s, string reason, CancellationToken ct, bool resetFirst = false, bool background = false)
    {
        ArgumentNullException.ThrowIfNull(s);
        var dbKey = s.DbKey;
        var gate = _refreshGates.GetOrAdd(dbKey, _ => new SemaphoreSlim(1, 1));
        if (!background)
        {
            await gate.WaitAsync(ct);
        }
        else if (!await gate.WaitAsync(TimeSpan.Zero, ct))
        {
            throw new SessionBusyException("같은 접속의 다른 객체 목록 갱신이 도는 중입니다.");
        }

        try
        {
            if (resetFirst)
            {
                await TryCacheAsync(async () =>
                {
                    await _cache.InvalidateAsync(dbKey, [], ct);
                    return true;
                });
                _pending.TryRemove(dbKey, out _);
            }

            return await RefreshObjectListLockedAsync(s, reason, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary><see cref="RefreshObjectListCoreAsync"/> 의 갱신 잠금 안쪽.</summary>
    private async Task<(IReadOnlyList<MetaObject> All, ObjectListChange Change)> RefreshObjectListLockedAsync(ISession s, string reason, CancellationToken ct)
    {
        var dbKey = s.DbKey;
        Trace.TraceInformation("메타데이터 객체 목록 갱신 시작 ({0}, {1})", dbKey, reason);
        var timer = Stopwatch.StartNew();
        try
        {
            var fetchedAt = DateTimeOffset.UtcNow;
            IReadOnlyList<MetaObject> fresh;
            using (var meter = SessionWait.MeasureHold())
            {
                fresh = await FetchObjectsAsync(s, new ObjectQuery(), fetchedAt, ct);
                // 갱신 비용(다음 뒤 갱신 간격의 바탕)은 서버에 명령을 보낸 시간만 잰다: 세션 잠금을 기다린 시간(긴 실행 중 「캐시 초기화」 등)을 넣으면
                // 간격이 그 30 배로 늘어 몇 시간씩 갱신하지 않는다. 세션 잠금을 쓰지 않는 구현(시험 가짜)은 걸린 시간 전체.
                _refreshCost[dbKey] = meter.Entered ? meter.Held : timer.Elapsed;
            }

            var previousSource = await TryCacheAsync(() => _cache.GetSourceAsync(dbKey, ct));
            var cached = previousSource is null
                ? []
                : await TryCacheAsync(() => _cache.GetObjectsAsync(dbKey, null, null, ct)) ?? [];
            // 이 읽기를 시작한 뒤에 받은 행(fetched_at 이 더 늦음)은 이 스냅샷보다 새것이라 사라짐·바뀜으로 보지 않는다.
            var diff = Diff(cached, fresh, fetchedAt);
            var cachedByKey = diff.CachedByKey;
            var vanished = diff.Vanished;
            var changed = diff.Changed;
            var added = diff.Added;

            var source = new MetaSource
            {
                DbKey = dbKey,
                ServerVersion = s.Server.Version,
                RefreshedAt = fetchedAt,
                LastUsedAt = fetchedAt,
            };
            var stored = await TryCacheAsync(async () =>
            {
                if (vanished.Count > 0)
                {
                    await _cache.InvalidateAsync(dbKey, [.. vanished.Select(ToRef)], ct);
                }

                await _cache.UpsertAsync(source, [.. added, .. changed], ct);
                return true;
            });

            _lastRefresh[dbKey] = DateTimeOffset.UtcNow; // 읽기를 마친 때부터 간격을 센다
            var change = diff.Change;
            TraceDone(dbKey, reason, change, fresh.Count, timer.Elapsed);

            if (previousSource?.RefreshedAt is not null && change.Total > 0)
            {
                _events?.Publish(new MetadataInvalidated(dbKey, [.. vanished.Concat(changed).Concat(added).Select(ToRef)]));
            }

            if (!stored)
            {
                return (fresh, change);
            }

            _events?.Publish(new ObjectListRefreshed(dbKey, fetchedAt, change));

            // 바뀌지 않은 항목(이 스냅샷보다 새로 받은 행 포함)의 payload 를 보존하려고 캐시 값을 다시 합친다.
            var result = fresh.Select(o => cachedByKey.TryGetValue((o.Owner, o.Name, o.Type), out var old)
                && (old.FetchedAt > fetchedAt
                    || (old.LastDdlTime == o.LastDdlTime && string.Equals(old.Status, o.Status, StringComparison.Ordinal)))
                    ? old
                    : o);
            return ([.. result], change);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !IsSessionBusy(ex))
        {
            TraceFailed(dbKey, reason, ex); // 바쁨은 뒤 갱신이 다시 하기와 함께 따로 남긴다
            throw;
        }
    }

    /// <summary>
    /// 캐시 행과 서버 행을 (소유자, 이름, 유형) 으로 맞춰 본다. 캐시 행 중 <paramref name="snapshotAt"/>(이 서버 목록을 읽기 시작한 때)보다 늦게 받은 것은
    /// 이 목록보다 새것이라 사라짐·바뀜에서 뺀다.
    /// </summary>
    private static ListDiff Diff(IReadOnlyList<MetaObject> cached, IReadOnlyList<MetaObject> fresh, DateTimeOffset snapshotAt)
    {
        var cachedByKey = cached.ToDictionary(o => (o.Owner, o.Name, o.Type));
        var freshKeys = fresh.Select(o => (o.Owner, o.Name, o.Type)).ToHashSet();
        var vanished = cached.Where(o => !freshKeys.Contains((o.Owner, o.Name, o.Type)) && o.FetchedAt <= snapshotAt).ToList();
        var changed = new List<MetaObject>();
        var added = new List<MetaObject>();
        foreach (var o in fresh)
        {
            if (!cachedByKey.TryGetValue((o.Owner, o.Name, o.Type), out var old))
            {
                added.Add(o);
            }
            else if (old.FetchedAt <= snapshotAt
                && (old.LastDdlTime != o.LastDdlTime || !string.Equals(old.Status, o.Status, StringComparison.Ordinal)))
            {
                changed.Add(o);
            }
        }

        return new ListDiff(cachedByKey, added, changed, vanished);
    }

    private sealed record ListDiff(
        Dictionary<(string Owner, string Name, string Type), MetaObject> CachedByKey,
        List<MetaObject> Added,
        List<MetaObject> Changed,
        List<MetaObject> Vanished)
    {
        public ObjectListChange Change => new(Added.Count, Changed.Count, Vanished.Count);
    }

    private static void TraceDone(string dbKey, string reason, ObjectListChange change, int count, TimeSpan elapsed)
        => Trace.TraceInformation(
            "메타데이터 객체 목록 갱신 끝 ({0}, {1}): 추가 {2} · 바뀜 {3} · 지움 {4}, 서버 {5}개, {6}초",
            dbKey, reason, change.Added, change.Changed, change.Removed, count, Math.Round(elapsed.TotalSeconds, 1));

    // 메시지만 남긴다: 예외 전체(ToString)에는 드라이버가 붙인 접속 정보가 섞일 수 있다.
    private static void TraceFailed(string dbKey, string reason, Exception ex)
        => Trace.TraceWarning("메타데이터 객체 목록 갱신 실패 ({0}, {1}): {2}: {3}", dbKey, reason, ex.GetType().Name, ex.Message);

    /// <summary>세션 잠금을 한도 안에 잡지 못했다(<see cref="SessionBusyException"/>, 감싼 것 포함). 서버에는 그 명령을 보내지 않았다.</summary>
    private static bool IsSessionBusy(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is SessionBusyException)
            {
                return true;
            }

            if (e is AggregateException agg && agg.InnerExceptions.Any(IsSessionBusy))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>캐시의 객체 목록 최대 유효기간. 캐시가 없거나(시험) 읽지 못하면 null(기한 없음).</summary>
    private TimeSpan? TryMaxAge()
    {
        try
        {
            return _cache?.ObjectListMaxAge;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>캐시 오류는 기능을 막지 않는다(캐시는 언제든 다시 만들 수 있다). 취소는 그대로 전파한다.</summary>
    protected static async Task<T?> TryCacheAsync<T>(Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return default;
        }
    }

    /// <summary>캐시에 있는 객체 행 하나(상세 payload 를 읽고 쓰는 자리). 유형이 비어 있으면 소유자·이름만 맞춘다. 없거나 캐시를 못 쓰면 null.</summary>
    protected async Task<MetaObject?> FindCachedAsync(ISession s, DbObjectRef obj, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(obj);
        var found = await TryCacheAsync(() => _cache.FindByPrefixAsync(s.DbKey, obj.Name, 500, ct));
        return found?.FirstOrDefault(o =>
            string.Equals(o.Owner, obj.Owner, StringComparison.Ordinal)
            && string.Equals(o.Name, obj.Name, StringComparison.Ordinal)
            && (string.IsNullOrEmpty(obj.ObjectType) || string.Equals(o.Type, obj.ObjectType, StringComparison.Ordinal)));
    }

    /// <summary>상세 <paramref name="value"/> 를 객체 행 <paramref name="row"/> 의 payload(<c>{"v": 버전, field: 값}</c>)로 저장한다. 캐시 오류는 삼킨다.</summary>
    protected async Task StorePayloadAsync<T>(ISession s, MetaObject? row, string field, T value, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (row is null)
        {
            return; // 목록에 없는 객체는 캐시 행이 없으므로 상세만 저장하지 않는다.
        }

        var payload = new Dictionary<string, object> { ["v"] = PayloadVersion, [field] = value! };
        var source = new MetaSource
        {
            DbKey = s.DbKey,
            ServerVersion = s.Server.Version,
            LastUsedAt = DateTimeOffset.UtcNow,
        };
        await TryCacheAsync(async () =>
        {
            await _cache.UpsertAsync(source, [row with { PayloadJson = JsonSerializer.Serialize(payload, Json) }], ct);
            return true;
        });
    }

    /// <summary>payload 에서 <paramref name="field"/> 를 읽는다. 형식 버전이 <see cref="PayloadVersion"/> 와 다르거나 깨졌으면 false.</summary>
    protected bool TryReadPayload<T>(string json, string field, out T? value)
    {
        ArgumentNullException.ThrowIfNull(json);
        value = default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != PayloadVersion
                || !doc.RootElement.TryGetProperty(field, out var element))
            {
                return false;
            }

            value = element.Deserialize<T>(Json);
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>SQL LIKE(<c>%</c>, <c>_</c>, 이스케이프 <c>\</c>) 를 정규식으로 옮긴다. 서버 쿼리와 같은 뜻(대소문자 구분)이다.</summary>
    public static Regex LikeToRegex(string like)
    {
        ArgumentNullException.ThrowIfNull(like);
        var sb = new StringBuilder("^");
        for (var i = 0; i < like.Length; i++)
        {
            var ch = like[i];
            if (ch == '\\' && i + 1 < like.Length)
            {
                sb.Append(Regex.Escape(like[++i].ToString()));
            }
            else if (ch == '%')
            {
                sb.Append("(?s:.*)");
            }
            else if (ch == '_')
            {
                sb.Append("(?s:.)");
            }
            else
            {
                sb.Append(Regex.Escape(ch.ToString()));
            }
        }

        return new Regex(sb.Append('$').ToString(), RegexOptions.CultureInvariant);
    }

    /// <summary>캐시 행 → 객체 참조(소유자·이름·유형).</summary>
    protected static DbObjectRef ToRef(MetaObject o)
    {
        ArgumentNullException.ThrowIfNull(o);
        return new(o.Owner, o.Name, o.Type);
    }

    private static List<DbObjectRef> ToRefs(IEnumerable<MetaObject> objects) => [.. objects.Select(ToRef)];

    private static IEnumerable<MetaObject> FilterCached(IEnumerable<MetaObject> cached, ObjectQuery q)
    {
        var types = q.ObjectTypes.Count == 0 ? null : new HashSet<string>(q.ObjectTypes, StringComparer.Ordinal);
        var pattern = q.NamePattern is null ? null : LikeToRegex(q.NamePattern);
        var result = cached.Where(o =>
            (q.Owner is null || string.Equals(o.Owner, q.Owner, StringComparison.Ordinal))
            && (types is null || types.Contains(o.Type))
            && (pattern is null || pattern.IsMatch(o.Name))
            && (q.ModifiedAfter is null || o.LastDdlTime > q.ModifiedAfter))
            .OrderBy(o => o.Owner, StringComparer.Ordinal)
            .ThenBy(o => o.Type, StringComparer.Ordinal)
            .ThenBy(o => o.Name, StringComparer.Ordinal);
        return q.Limit is { } limit ? result.Take(limit) : result;
    }


    /// <summary>마지막 객체 목록 갱신의 비용(다음 뒤 갱신 간격의 바탕, 세션 잠금을 쥔 시간). 아직 없으면 null(시험·진단용).</summary>
    protected TimeSpan? RefreshCostOf(string dbKey) => _refreshCost.TryGetValue(dbKey, out var cost) ? cost : null;

    /// <summary>시험·진단용: 이 DbKey 의 뒤 갱신(다시 하기 기다림 포함)이 끝나는 작업. 돌고 있지 않으면 끝난 작업.</summary>
    protected Task BackgroundRefreshCompletion(string dbKey)
        => _running.TryGetValue(dbKey, out var running) ? running.Completion : Task.CompletedTask;

    /// <param name="stale">목록이 최대 유효기간을 넘었다: 읽는 데 걸린 시간에 맞춘 긴 간격 대신 기본 간격만 지킨다.</param>
    private void StartBackgroundRefresh(ISession s, bool stale)
    {
        if (_backgroundRefreshInterval == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        var dbKey = s.DbKey;
        if (_running.TryGetValue(dbKey, out var current))
        {
            current.HandOver(s); // 이미 돈다(다시 하기를 기다리는 중 포함): 다음 시도는 지금 물은 세션으로
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var pending = _pending.ContainsKey(dbKey);
        // 전체 목록 읽기가 오래 걸리는 서버(사전 권한 없는 계정의 전체 객체 뷰는 분 단위)는 그만큼 드물게 갱신한다:
        // 읽는 동안 같은 접속의 다른 질의(INVALID 거르기 포함)가 그 뒤에 줄을 선다. 읽는 데 걸린 시간의 30 배 간격, 최소는 기본 간격.
        // 남은 일(바빠 멈춤)·오래된 목록은 기본 간격만 지킨다.
        var interval = !pending && !stale && _refreshCost.TryGetValue(dbKey, out var cost) && cost * 30 > _backgroundRefreshInterval
            ? cost * 30
            : _backgroundRefreshInterval;
        if (_lastRefresh.TryGetValue(dbKey, out var last) && now - last < interval && !pending)
        {
            return;
        }

        var run = new BackgroundRun(dbKey, s);
        if (!_running.TryAdd(dbKey, run))
        {
            if (_running.TryGetValue(dbKey, out var other))
            {
                other.HandOver(s); // 방금 다른 흐름이 시작했다
            }

            return;
        }

        _lastRefresh[dbKey] = now;
        var reason = pending ? "뒤 갱신(남은 일)" : stale ? "뒤 갱신(목록 기준 시각이 유효기간을 넘음)" : "뒤 갱신";
        _ = Task.Run(async () =>
        {
            try
            {
                await RunBackgroundRefreshAsync(run, reason);
            }
            catch (Exception ex)
            {
                // 삼키지 못한 실패(카탈로그 변형 없음 등): 남은 일을 지우고(목록 조회마다 다시 시작하지 않게) 로그로 남긴다. 작업 예외는 여기서 관찰한다.
                _pending.TryRemove(dbKey, out _);
                Trace.TraceError("메타데이터 객체 목록 뒤 갱신이 오류로 끝남 ({0}): {1}: {2}. 남은 일을 지웠다", dbKey, ex.GetType().Name, ex.Message);
            }
            finally
            {
                _running.TryRemove(new KeyValuePair<string, BackgroundRun>(dbKey, run));
                run.Done.TrySetResult();
            }
        });
    }

    /// <summary>세션 <paramref name="sessionId"/> 로 다시 하기를 기다리는 뒤 갱신을 깨운다(세션이 닫혔거나 끊겼다).</summary>
    private void WakeRunsOf(Guid sessionId)
    {
        foreach (var run in _running.Values)
        {
            run.Wake(sessionId);
        }
    }

    /// <summary>
    /// 뒤 갱신 한 번: 질의 시작 잠금은 기다리지 않고(0) 이어 읽기 잠금은 <see cref="BackgroundBusyWait"/> 까지만 기다리며 전체 목록을 받는다(D-041:
    /// 사용자 실행 뒤에 줄을 서지 않는다). 세션 상태가 바쁨이면 시도하지 않는다. 바빠 멈추면 남은 일로 두고 <see cref="BackgroundRetryDelay"/>(두 배씩, 최대 1분) 뒤 다시,
    /// <see cref="BackgroundRetryLimit"/> 번까지. 기다리는 동안 그 세션이 닫히거나 끊기거나 같은 DbKey 의 다른 세션이 넘어오면 바로 깨어난다.
    /// 세션이 닫혔거나 끊겼으면 그만두되 남은 일은 둔다(같은 DbKey 로 다시 접속한 세션의 다음 목록 조회가 바로 다시 시작한다). 삼켜도 되는 실패는 로그만 남긴다.
    /// </summary>
    private async Task RunBackgroundRefreshAsync(BackgroundRun run, string reason)
    {
        var dbKey = run.DbKey;
        var delay = BackgroundRetryDelay;
        for (var attempt = 1; ; attempt++)
        {
            var wake = run.NextWake();
            var s = run.Session;
            if (s.State is SessionState.Closed or SessionState.Broken)
            {
                Trace.TraceInformation("메타데이터 객체 목록 뒤 갱신을 그만둠 ({0}): 세션이 닫혔거나 끊겼다", dbKey);
                return;
            }

            if (s.State != SessionState.Busy)
            {
                try
                {
                    using (SessionWait.BriefWithResume(TimeSpan.Zero, BackgroundBusyWait))
                    {
                        await RefreshObjectListCoreAsync(s, attempt == 1 ? reason : $"{reason}, {attempt}번째", CancellationToken.None, background: true);
                    }

                    _pending.TryRemove(dbKey, out _);
                    return;
                }
                catch (Exception ex) when (IsSessionBusy(ex))
                {
                    // 아래에서 다시 하기
                }
                catch (Exception ex) when (IsIgnorableBackgroundRefreshFailure(ex))
                {
                    // 세션이 닫혔거나 끊겼거나 서버가 거절했다(실패는 갱신 본체가 로그에 남겼다). 다음 조회 때 다시 시도한다.
                    // 세션이 닫혀 끝난 것이면 남은 일은 둔다(다시 접속한 세션이 이어 받는다).
                    if (s.State is not (SessionState.Closed or SessionState.Broken))
                    {
                        _pending.TryRemove(dbKey, out _);
                    }

                    return;
                }
            }

            _pending[dbKey] = 0;
            if (attempt > BackgroundRetryLimit)
            {
                Trace.TraceWarning("메타데이터 객체 목록 뒤 갱신을 남겨 둠 ({0}): 세션이 계속 바빠 {1}번 멈췄다. 다음 목록 조회 때 다시 한다", dbKey, attempt);
                return;
            }

            Trace.TraceInformation("메타데이터 객체 목록 뒤 갱신이 멈춤 ({0}): 세션이 바쁘다. {1}초 뒤 다시 한다({2}번째)", dbKey, Math.Round(delay.TotalSeconds, 1), attempt);
            // 깨우기 신호가 먼저 오면 기다리던 Delay 타이머를 취소해 남기지 않는다(CA2027).
            using (var delayCancel = new CancellationTokenSource())
            {
                var woken = await Task.WhenAny(Task.Delay(delay, delayCancel.Token), wake) == wake;
                await delayCancel.CancelAsync();
                if (woken)
                {
                    Trace.TraceInformation("메타데이터 객체 목록 뒤 갱신 기다림을 끝냄 ({0}): 세션이 닫혔거나 끊겼거나 다른 세션으로 넘어왔다", dbKey);
                }
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromMinutes(1).Ticks));
        }
    }

    /// <summary>DbKey 하나의 도는 뒤 갱신. 어느 세션으로 할지(<see cref="Session"/>)는 같은 DbKey 의 다른 세션이 목록을 물으면 그쪽으로 넘어간다.</summary>
    private sealed class BackgroundRun(string dbKey, ISession session)
    {
        private readonly Lock _lock = new();
        private ISession _session = session;
        private TaskCompletionSource _wake = NewSignal();

        public string DbKey { get; } = dbKey;

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completion => Done.Task;

        public ISession Session
        {
            get
            {
                lock (_lock)
                {
                    return _session;
                }
            }
        }

        /// <summary>이번 시도 뒤 다시 하기 기다림을 끝낼 신호. 지난번 신호가 이미 왔으면 새것으로 바꾼다(이번 시도 중에 온 신호는 이 기다림을 끝낸다).</summary>
        public Task NextWake()
        {
            lock (_lock)
            {
                if (_wake.Task.IsCompleted)
                {
                    _wake = NewSignal();
                }

                return _wake.Task;
            }
        }

        /// <summary>다음 시도는 <paramref name="s"/> 로 한다. 다른 세션이면 기다림을 깨운다.</summary>
        public void HandOver(ISession s)
        {
            TaskCompletionSource wake;
            lock (_lock)
            {
                if (_session.Id == s.Id)
                {
                    return;
                }

                _session = s;
                wake = _wake;
            }

            wake.TrySetResult();
        }

        /// <summary>지금 세션이 <paramref name="sessionId"/> 이면 기다림을 깨운다(닫힘·끊김).</summary>
        public void Wake(Guid sessionId)
        {
            TaskCompletionSource wake;
            lock (_lock)
            {
                if (_session.Id != sessionId)
                {
                    return;
                }

                wake = _wake;
            }

            wake.TrySetResult();
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
