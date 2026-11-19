using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Oranda.Core.Sessions;

namespace Oranda.Core.Metadata;

// CachedMetadataProvider 의 일부: 세부 종류·상태 표식(스키마 브라우저 아이콘 뱃지). 목록 질의(meta.objects)는 그대로 두고, 목록을 보인 뒤 소유자마다 보조 질의로 따로 읽는다.
public abstract partial class CachedMetadataProvider
{
    /// <summary>같은 소유자의 표식을 다시 읽는 최소 간격(목록 기준 시각이 바뀌어도 이보다 자주 읽지 않는다). 유형 새로 고침(F5)은 이 간격을 기다리지 않는다.</summary>
    protected TimeSpan MarksRefreshInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>(DbKey, 소유자) → 표식을 마지막으로 읽은 때와 그때의 목록 기준 시각. 앱을 다시 켜면 비어 첫 조회 때 한 번 다시 읽는다(캐시의 표식은 그동안 그대로 보인다).</summary>
    private readonly ConcurrentDictionary<(string DbKey, string Owner), (DateTimeOffset At, DateTimeOffset ListBase)> _marksRead = new();

    /// <summary>도는 표식 읽기(소유자마다 하나).</summary>
    private readonly ConcurrentDictionary<(string DbKey, string Owner), byte> _marksRunning = new();

    /// <summary>
    /// 표식 보조 질의 id(<see cref="ObjectMarkRows"/> 의 상수). 상속한 형식이 그 DBMS 에서 쓸 수 있는 것만 낸다. 빈 목록 = 표식을 읽지 않는다(기본).
    /// </summary>
    protected virtual IReadOnlyList<string> MarkQueryIds => [];

    /// <summary>표식 보조 질의 하나를 보낸다(바인드 <c>owner</c> 하나). <see cref="MarkQueryIds"/> 가 비어 있지 않으면 상속한 형식이 바꿔야 한다.</summary>
    protected virtual Task<IReadOnlyList<object?[]>> RunMarkQueryAsync(ISession s, string id, string owner, CancellationToken ct)
        => throw new NotSupportedException("표식 질의를 지원하지 않습니다: " + id);

    /// <summary>
    /// 소유자 하나의 표식(표식이 있는 객체만). 캐시로 바로 답하고, 이 소유자의 표식을 아직 안 읽었거나 목록 기준 시각이 바뀌고 <see cref="MarksRefreshInterval"/> 이 지났으면
    /// 뒤에서 보조 질의를 시작한다(끝나면 <see cref="ObjectMarksRefreshed"/>). 캐시가 없거나(목록을 아직 안 받음) 못 쓰면 빈 사전이다.
    /// </summary>
    public async Task<IReadOnlyDictionary<DbObjectRef, ObjectMark>> ListObjectMarksAsync(ISession s, string owner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentException.ThrowIfNullOrEmpty(owner);
        var result = new Dictionary<DbObjectRef, ObjectMark>();
        var source = await TryCacheAsync(() => _cache.GetSourceAsync(s.DbKey, ct));
        if (source?.RefreshedAt is not { } listBase)
        {
            return result;
        }

        foreach (var o in await TryCacheAsync(() => _cache.GetMarkedAsync(s.DbKey, owner, ct)) ?? [])
        {
            if (ObjectMark.Of(o) is { } mark)
            {
                result[ToRef(o)] = mark;
            }
        }

        if (MarkQueryIds.Count > 0 && MarksAreStale(s.DbKey, owner, listBase))
        {
            StartMarksRefresh(s, owner, listBase);
        }

        return result;
    }

    private bool MarksAreStale(string dbKey, string owner, DateTimeOffset listBase)
        => !_marksRead.TryGetValue((dbKey, owner), out var read)
            || (read.ListBase < listBase && DateTimeOffset.UtcNow - read.At >= MarksRefreshInterval);

    /// <summary>이 소유자(null = 이 DbKey 의 모든 소유자)의 표식을 다음 조회 때 간격과 상관없이 다시 읽게 한다(유형 새로 고침·캐시 초기화 뒤).</summary>
    protected void ForgetMarks(string dbKey, string? owner)
    {
        foreach (var key in _marksRead.Keys.Where(k => k.DbKey == dbKey && (owner is null || k.Owner == owner)))
        {
            _marksRead.TryRemove(key, out _);
        }
    }

    /// <summary>시험·진단용: 이 소유자의 표식 읽기가 도는 중인가.</summary>
    protected bool MarksRefreshRunning(string dbKey, string owner) => _marksRunning.ContainsKey((dbKey, owner));

    private void StartMarksRefresh(ISession s, string owner, DateTimeOffset listBase)
    {
        var key = (s.DbKey, owner);
        if (!_marksRunning.TryAdd(key, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunMarksRefreshAsync(s, owner, listBase);
            }
            catch (Exception ex)
            {
                // 삼키지 못한 실패: 표식만 비고 목록·화면은 그대로다. 다음 조회 때 다시 한다(읽은 때를 남기지 않았다).
                Trace.TraceWarning("메타데이터 표식 읽기 실패 ({0}, {1}): {2}: {3}", s.DbKey, owner, ex.GetType().Name, ex.Message);
            }
            finally
            {
                _marksRunning.TryRemove(key, out _);
            }
        });
    }

    /// <summary>
    /// 표식 읽기 한 번. 뒤 갱신과 같은 규칙(D-041)을 따른다: 같은 DbKey 의 목록 뒤 갱신이 돌면 끝나기를 기다리고(목록을 먼저 보인다), 세션이 바쁘면 시도하지 않고,
    /// 질의를 시작하는 잠금은 기다리지 않는다(0). 바빠 멈추면 <see cref="BackgroundRetryDelay"/>(두 배씩, 최대 1분) 뒤 다시, <see cref="BackgroundRetryLimit"/> 번까지.
    /// 세션이 닫혔거나 끊겼으면 그만둔다(다음 조회가 다시 시작한다).
    /// </summary>
    private async Task RunMarksRefreshAsync(ISession s, string owner, DateTimeOffset listBase)
    {
        var dbKey = s.DbKey;
        var delay = BackgroundRetryDelay;
        for (var attempt = 1; ; attempt++)
        {
            if (_running.TryGetValue(dbKey, out var list))
            {
                await list.Completion;
            }

            if (s.State is SessionState.Closed or SessionState.Broken)
            {
                return;
            }

            if (s.State != SessionState.Busy)
            {
                try
                {
                    IReadOnlyList<MetaObject> marked;
                    using (SessionWait.BriefWithResume(TimeSpan.Zero, BackgroundBusyWait))
                    {
                        marked = await FetchMarksAsync(s, owner);
                    }

                    await TryCacheAsync(async () =>
                    {
                        await _cache.SetMarksAsync(dbKey, owner, marked, CancellationToken.None);
                        return true;
                    });
                    _marksRead[(dbKey, owner)] = (DateTimeOffset.UtcNow, listBase);
                    _events?.Publish(new ObjectMarksRefreshed(dbKey, owner));
                    return;
                }
                catch (Exception ex) when (IsSessionBusy(ex))
                {
                    // 아래에서 다시 하기
                }
            }

            if (attempt > BackgroundRetryLimit)
            {
                Trace.TraceInformation("메타데이터 표식 읽기를 미룸 ({0}, {1}): 세션이 계속 바빠 {2}번 멈췄다. 다음 조회 때 다시 한다", dbKey, owner, attempt);
                return;
            }

            await Task.Delay(delay);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromMinutes(1).Ticks));
        }
    }

    /// <summary>
    /// 보조 질의를 차례로 보낸다. 바쁨·취소는 그대로 던지고(다시 하기), 그 밖의 실패(권한 없음·뷰 없음·시간 초과·변형 없음)는 그 질의만 빼고 계속한다.
    /// 끝나면 질의마다 걸린 시간·행 수를 로그 한 줄로 남긴다(실패한 질의는 이유).
    /// </summary>
    private async Task<IReadOnlyList<MetaObject>> FetchMarksAsync(ISession s, string owner)
    {
        var marked = new List<MetaObject>();
        var report = new List<string>();
        var total = Stopwatch.StartNew();
        foreach (var id in MarkQueryIds)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                var rows = await RunMarkQueryAsync(s, id, owner, CancellationToken.None);
                var marks = ObjectMarkRows.Read(id, owner, rows);
                marked.AddRange(marks.Select(m => new MetaObject
                {
                    DbKey = s.DbKey,
                    Owner = m.Ref.Owner,
                    Name = m.Ref.Name,
                    Type = m.Ref.ObjectType,
                    SubKind = m.Mark.SubKind,
                    StateMark = m.Mark.State,
                }));
                report.Add(string.Create(CultureInfo.InvariantCulture, $"{Short(id)} {rows.Count}행 {timer.Elapsed.TotalSeconds:0.00}초"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !IsSessionBusy(ex))
            {
                report.Add(string.Create(CultureInfo.InvariantCulture, $"{Short(id)} 실패 {timer.Elapsed.TotalSeconds:0.00}초({ex.GetType().Name}: {FirstLine(ex.Message)})"));
            }
        }

        Trace.TraceInformation("메타데이터 표식 읽기 끝 ({0}, {1}): {2}, 모두 {3}초", s.DbKey, owner, string.Join(" · ", report), Math.Round(total.Elapsed.TotalSeconds, 2));
        return marked;

        static string Short(string id) => id.StartsWith("meta.marks_", StringComparison.Ordinal) ? id["meta.marks_".Length..] : id;

        static string FirstLine(string message) => message.Split('\n', 2)[0].Trim();
    }
}
