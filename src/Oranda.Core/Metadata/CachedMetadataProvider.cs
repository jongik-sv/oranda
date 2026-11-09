using System.Collections.Concurrent;
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

    /// <param name="cache">객체 목록·상세 캐시. 여기서는 받아 두기만 한다(서버만 읽는 시험은 null 을 넘긴다).</param>
    /// <param name="events">변경 감지 알림(<see cref="MetadataInvalidated"/>)을 낼 버스.</param>
    /// <param name="backgroundRefreshInterval">캐시로 답한 뒤 백그라운드 갱신을 다시 시작하는 최소 간격. <see cref="Timeout.InfiniteTimeSpan"/> = 하지 않음(시험용).</param>
    protected CachedMetadataProvider(IMetadataCache cache, IEventBus events, TimeSpan backgroundRefreshInterval)
    {
        _cache = cache;
        _events = events;
        _backgroundRefreshInterval = backgroundRefreshInterval;
    }

    /// <summary>
    /// payload_json 형식 버전(맨 위 <c>"v"</c>). 다르면 캐시가 없는 것으로 본다. payload 는 Core 모델(<see cref="TableInfo"/>·<see cref="ProcedureInfo"/>)의 JSON 이라 기본값을 함께 쓴다.
    /// 2: 외래키 제약에 참조 키 이름(<see cref="ConstraintInfo.ReferencedConstraint"/>)을 더했다.
    /// 한 DBMS 의 읽기만 바뀌어 그 DBMS 캐시만 버려야 하면 상속한 형식이 올린다.
    /// </summary>
    protected virtual int PayloadVersion => 2;

    /// <summary>
    /// 서버에서 조건 <paramref name="q"/> 에 맞는 객체 목록을 읽는다. 빈 조건(<c>new ObjectQuery()</c>)이면 캐시에 넣을 서버 전체 목록이다.
    /// 행마다 <see cref="MetaObject.DbKey"/> = <c>s.DbKey</c>, <see cref="MetaObject.FetchedAt"/> = <paramref name="fetchedAt"/> 으로 채운다.
    /// </summary>
    protected abstract Task<IReadOnlyList<MetaObject>> FetchObjectsAsync(ISession s, ObjectQuery q, DateTimeOffset fetchedAt, CancellationToken ct);

    /// <summary>
    /// 백그라운드 갱신 실패 중 삼켜도 되는 것(세션이 닫혔거나 끊겼거나 바쁘다). 다음 조회 때 다시 시도한다.
    /// false 면 그대로 던진다(관찰되지 않는 작업 오류로 남는다).
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
        if (source?.RefreshedAt is not null)
        {
            var cached = await TryCacheAsync(() => _cache.GetObjectsAsync(s.DbKey, q.Owner, null, ct));
            if (cached is not null)
            {
                StartBackgroundRefresh(s);
                return ToRefs(FilterCached(cached, q));
            }
        }

        // 캐시 없음: 전체 목록을 한 번 받아 캐시에 채운 뒤 거기서 답한다.
        var all = await RefreshObjectListAsync(s, ct);
        return ToRefs(FilterCached(all, q));
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
    {
        ArgumentNullException.ThrowIfNull(s);
        var dbKey = s.DbKey;
        var gate = _refreshGates.GetOrAdd(dbKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var fetchedAt = DateTimeOffset.UtcNow;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var fresh = await FetchObjectsAsync(s, new ObjectQuery(), fetchedAt, ct);
            _refreshCost[dbKey] = timer.Elapsed;

            var previousSource = await TryCacheAsync(() => _cache.GetSourceAsync(dbKey, ct));
            var cached = previousSource is null
                ? []
                : await TryCacheAsync(() => _cache.GetObjectsAsync(dbKey, null, null, ct)) ?? [];
            var cachedByKey = cached.ToDictionary(o => (o.Owner, o.Name, o.Type));
            var freshKeys = fresh.Select(o => (o.Owner, o.Name, o.Type)).ToHashSet();

            var vanished = cached.Where(o => !freshKeys.Contains((o.Owner, o.Name, o.Type))).ToList();
            var changed = new List<MetaObject>();
            var added = new List<MetaObject>();
            foreach (var o in fresh)
            {
                if (!cachedByKey.TryGetValue((o.Owner, o.Name, o.Type), out var old))
                {
                    added.Add(o);
                }
                else if (old.LastDdlTime != o.LastDdlTime || !string.Equals(old.Status, o.Status, StringComparison.Ordinal))
                {
                    changed.Add(o);
                }
            }

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

            if (previousSource?.RefreshedAt is not null && (vanished.Count + changed.Count + added.Count) > 0)
            {
                _events.Publish(new MetadataInvalidated(dbKey, [.. vanished.Concat(changed).Concat(added).Select(ToRef)]));
            }

            if (!stored)
            {
                return fresh;
            }

            // 바뀌지 않은 항목의 payload 를 보존하려고 캐시 값을 다시 합친다.
            var result = fresh.Select(o => cachedByKey.TryGetValue((o.Owner, o.Name, o.Type), out var old)
                && old.LastDdlTime == o.LastDdlTime && string.Equals(old.Status, o.Status, StringComparison.Ordinal)
                    ? old
                    : o);
            return [.. result];
        }
        finally
        {
            gate.Release();
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

    private void StartBackgroundRefresh(ISession s)
    {
        if (_backgroundRefreshInterval == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        // 전체 목록 읽기가 오래 걸리는 서버(사전 권한 없는 계정의 전체 객체 뷰는 분 단위)는 그만큼 드물게 갱신한다:
        // 읽는 동안 같은 접속의 다른 질의(INVALID 거르기 포함)가 그 뒤에 줄을 선다. 읽는 데 걸린 시간의 30 배 간격, 최소는 기본 간격.
        var interval = _refreshCost.TryGetValue(s.DbKey, out var cost) && cost * 30 > _backgroundRefreshInterval ? cost * 30 : _backgroundRefreshInterval;
        if (_lastRefresh.TryGetValue(s.DbKey, out var last) && now - last < interval)
        {
            return;
        }

        _lastRefresh[s.DbKey] = now;
        _ = Task.Run(async () =>
        {
            // 세션이 다른 명령(긴 실행 등)을 처리 중이면 기다리지 않고 건너뛴다(D-041): 끝난 뒤 그 실행 뒤에 줄을 서 사용자의 다음 실행을 막지 않게.
            using var _ = SessionWait.Brief(TimeSpan.Zero);
            try
            {
                await RefreshObjectListAsync(s, CancellationToken.None);
            }
            catch (Exception ex) when (IsIgnorableBackgroundRefreshFailure(ex))
            {
                // 세션이 닫혔거나 끊겼거나 바쁘다. 다음 조회 때 다시 시도한다.
            }
        });
    }
}
