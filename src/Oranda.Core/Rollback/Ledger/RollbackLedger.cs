using Oranda.Core.Events;
using Oranda.Core.Execution.Providers;
using Oranda.Core.Metadata;

namespace Oranda.Core.Rollback.Ledger;

/// <summary>
/// 원장(되돌리기 지점 설계 7.3·7.4·11.2, RD-6·RD-7·RD-8·RD-13·RD-16, RB-04). 세션마다 열린 묶음(주 묶음 하나 + 원복 실행·비교 반영이 만든 반대 문장 묶음)을 들고
/// 문장 기록·저장점·트랜잭션 끝을 받아 상태를 바꾼 뒤 저장소에 남긴다.
/// <para>
/// 공급자는 같은 세션의 <see cref="Record"/>·<see cref="EndAsync"/> 를 차례대로 기다려 부른다. 세션이 다르면 동시에 불러도 된다.
/// 같은 세션의 <see cref="BeforeEndAsync"/>·<see cref="EndAsync"/> 는 세션마다 하나씩만 돈다(저장 중 끝이 겹쳐 같은 묶음이 두 행이 되지 않게).
/// 저장소가 없거나(<c>null</c>) 저장에 실패해도 예외를 던지지 않는다(사용자 실행을 막지 않는다).
/// 시작 때 「열림」 묶음을 「확인 필요」로 바꾸는 일은 Platform 의 시작 정리(RB-22)가 한다. 원장은 하지 않는다.
/// </para>
/// </summary>
public sealed class RollbackLedger : IRollbackLedger
{
    /// <summary>이만큼 저장할 때마다 보관 기간이 지난 묶음을 지운다(설계 12.3).</summary>
    public const int PruneEvery = 100;

    /// <summary>원장이 알리지 않는 내부 저장점 이름(설계 6.2 의 0단계).</summary>
    private const string InternalSavepoint = "ORANDA$RB";

    private readonly IRollbackStore? _store;
    private readonly IEventBus? _events;
    private readonly Func<RollbackSettings> _settings;
    private readonly TimeProvider _time;
    private readonly Dictionary<Guid, SessionLedger> _sessions = [];
    private readonly object _gate = new();
    private int _saves;

    public RollbackLedger(IRollbackStore? store, IEventBus? events, Func<RollbackSettings>? settings = null, TimeProvider? time = null)
    {
        _store = store;
        _events = events;
        _settings = settings ?? (static () => new RollbackSettings());
        _time = time ?? TimeProvider.System;
    }

    public void Record(RollbackStatementRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var session = Session(record.SessionId, create: true)!;
        lock (session)
        {
            session.Main ??= new OpenGroup(RollbackOrigin.Execution, null);
            session.Main.Add(record, session.NextOrdinal++);
        }

        Publish(new RollbackStatementCaptured(record));
    }

    public void RecordDerived(Guid sessionId, RollbackOrigin origin, long originGroupId, RollbackStatementRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var session = Session(sessionId, create: true)!;
        lock (session)
        {
            var group = session.Derived.FirstOrDefault(g => g.Origin == origin && g.OriginGroupId == originGroupId);
            if (group is null)
            {
                group = new OpenGroup(origin, originGroupId);
                session.Derived.Add(group);
            }

            group.Add(record with { SessionId = sessionId }, session.NextOrdinal++);
        }
    }

    public void MarkSavepoint(Guid sessionId, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (string.Equals(name, InternalSavepoint, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var session = Session(sessionId, create: true)!;
        lock (session)
        {
            // 같은 이름을 다시 걸면 자리를 옮긴다(Oracle 과 같다). 거는 차례(Mark)는 같은 자리의 저장점끼리 앞뒤를 가린다.
            session.Savepoints[name] = new Savepoint(session.NextOrdinal, session.NextMark++);
        }
    }

    public RollbackOpenState? Open(Guid sessionId)
    {
        if (Session(sessionId, create: false) is not { } session)
        {
            return null;
        }

        lock (session)
        {
            var live = session.Main?.Statements.Where(s => !s.Invalidated).ToList();
            if (live is not { Count: > 0 })
            {
                return null;
            }

            return new RollbackOpenState(live.Count, live.Sum(s => s.Record.Steps.Count),
                                         live.Any(s => s.Record is { LockedRows: { } l, RowsAffected: { } r } && l != r),
                                         live.Any(s => s.Record.State == RollbackScriptState.Incomplete) || session.Main!.Overlap);
        }
    }

    public IReadOnlyList<RollbackProbe> Probes(Guid sessionId, int max = 5)
    {
        if (Session(sessionId, create: false) is not { } session)
        {
            return [];
        }

        lock (session)
        {
            return
            [
                .. session.Groups().SelectMany(g => g.Statements).Where(s => !s.Invalidated)
                    .OrderByDescending(s => s.Ordinal).SelectMany(s => s.Record.Probes).Take(Math.Max(0, max)),
            ];
        }
    }

    public async Task BeforeEndAsync(Guid sessionId, CancellationToken ct)
    {
        if (_store is null || Session(sessionId, create: false) is not { } session)
        {
            return;
        }

        await session.Saving.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            List<(OpenGroup Group, RollbackGroup Row, RollbackScriptDocument? Document)> pending = [];
            lock (session)
            {
                foreach (var group in session.Groups().Where(g => g.Changed && g.Statements.Count > 0))
                {
                    if (TryBuild(group, null, RollbackScriptState.Open) is { } row)
                    {
                        pending.Add((group, row, Document(group)));
                        group.Changed = false;
                    }
                }
            }

            foreach (var (group, row, document) in pending)
            {
                var id = await SaveAsync(row, document).ConfigureAwait(false);
                lock (session)
                {
                    if (id > 0)
                    {
                        group.PersistedId = id;
                    }
                    else
                    {
                        group.Changed = true; // 다음 끝 앞에서 다시 시도
                    }
                }
            }
        }
        catch (Exception)
        {
            // 저장 실패는 삼킨다(13.2).
        }
        finally
        {
            session.Saving.Release();
        }
    }

    public async Task EndAsync(TransactionEnded ended, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ended);
        if (Session(ended.SessionId, create: false) is not { } session)
        {
            return;
        }

        await session.Saving.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            List<(RollbackGroup Row, RollbackScriptDocument? Document)> finished = [];
            lock (session)
            {
                if (ended.Kind == TransactionEndKind.RollbackToSavepoint)
                {
                    RollbackTo(session, ended.Savepoint);
                    return;
                }

                var groups = session.Groups().Where(g => g.Statements.Count > 0).ToList();
                session.Main = null;
                session.Derived.Clear();
                session.Savepoints.Clear(); // 커밋·롤백은 저장점을 모두 없앤다
                foreach (var group in groups)
                {
                    var state = FinalState(group, ended);
                    if (TryBuild(group, ended, state) is not { } row)
                    {
                        continue;
                    }

                    // 무효 묶음은 원복 스크립트를 열 수 없다(11.2). 「열림」으로 저장한 파일이 있으면 내용을 비운 문서로 덮어 행 값·바인드를 남기지 않는다.
                    var document = state == RollbackScriptState.Invalid
                        ? (group.PersistedId > 0 && _store is { CanStoreScripts: true } ? new RollbackScriptDocument(RollbackScriptDocument.CurrentSchemaVersion, group.PersistedId, []) : null)
                        : Document(group);
                    finished.Add((row, document));
                }
            }

            foreach (var (row, document) in finished)
            {
                var id = await SaveAsync(row, document).ConfigureAwait(false);
                if (id > 0)
                {
                    Publish(new RollbackGroupSaved(id, row.State));
                }
            }
        }
        catch (Exception)
        {
            // 끝 처리 실패는 삼킨다(사용자 실행을 막지 않는다).
        }
        finally
        {
            session.Saving.Release();
            Forget(ended.SessionId, session, ended.Source is TransactionEndSource.SessionClosed or TransactionEndSource.SessionBroken);
        }
    }

    private static void RollbackTo(SessionLedger session, string? name)
    {
        var known = name is not null && session.Savepoints.ContainsKey(name);
        var target = known ? session.Savepoints[name!] : new Savepoint(long.MinValue, long.MinValue); // 모르는 저장점 = 묶음 전체 무효
        var reason = known ? $"ROLLBACK TO {name} 로 되돌림" : $"ROLLBACK TO {name ?? "?"} 로 되돌림(묶음이 열리기 전에 건 저장점)";
        foreach (var group in session.Groups())
        {
            foreach (var s in group.Statements.Where(s => s.Ordinal >= target.Ordinal && !s.Invalidated))
            {
                s.Invalidated = true;
                s.InvalidReason = reason;
                group.Changed = true;
            }
        }

        // 그 저장점 뒤에 건 저장점은 없어진다(Oracle 과 같다). 그 저장점 자체는 남는다.
        foreach (var later in session.Savepoints.Where(kv => kv.Value.Mark > target.Mark).Select(kv => kv.Key).ToList())
        {
            session.Savepoints.Remove(later);
        }
    }

    /// <summary>끝난 묶음의 상태(설계 11.2, RD-6·RD-16).</summary>
    private static RollbackScriptState FinalState(OpenGroup group, TransactionEnded ended)
    {
        var live = group.Statements.Where(s => !s.Invalidated).ToList();
        if (ended.Kind == TransactionEndKind.Rollback || live.Count == 0)
        {
            return RollbackScriptState.Invalid;
        }

        if (!live.Any(HasScript))
        {
            return RollbackScriptState.NotCreated; // 모름 끝도 「커밋(추정)」(RD-16)
        }

        if (ended.Kind == TransactionEndKind.Unknown)
        {
            return RollbackScriptState.Unverified;
        }

        // 하나라도 원복 문장을 만들지 않은 문장이 섞이면 스크립트만으로는 묶음을 다 되돌리지 못한다(11.2 「모두 완전하면 확정」).
        return live.Any(s => s.Record.State == RollbackScriptState.Incomplete || !HasScript(s)) || group.Overlap
            ? RollbackScriptState.Incomplete
            : RollbackScriptState.Confirmed;
    }

    private static bool HasScript(OpenStatement s) => s.Record.State is RollbackScriptState.Open or RollbackScriptState.Incomplete;

    private RollbackGroup? TryBuild(OpenGroup group, TransactionEnded? ended, RollbackScriptState state)
    {
        try
        {
            return Build(group, ended, state);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private RollbackGroup Build(OpenGroup group, TransactionEnded? ended, RollbackScriptState state)
    {
        var first = group.Statements[0].Record;
        var live = group.Statements.Where(s => !s.Invalidated).ToList();
        var endedAt = ended is null ? (DateTimeOffset?)null : _time.GetUtcNow();
        var assumed = ended is { Kind: TransactionEndKind.Unknown } && state == RollbackScriptState.NotCreated;
        return new RollbackGroup
        {
            Id = group.PersistedId,
            ProfileId = first.ProfileId,
            ProfileName = first.ProfileName,
            DbKey = first.DbKey,
            ServerName = first.ServerName,
            Environment = first.Environment,
            Dbms = first.Dbms,
            StartedAt = first.ExecutedAt,
            EndedAt = endedAt,
            BasePoint = group.Statements.Select(s => s.Record.BeforePoint).FirstOrDefault(p => p is not null),
            CommitPoint = ended?.CommitPoint,
            TransactionId = group.Statements.Select(s => s.Record.TransactionId).FirstOrDefault(t => t is not null),
            EndKind = ended is null ? null : assumed ? TransactionEndKind.Commit : ended.Kind,
            EndSource = ended?.Source,
            State = state,
            Reason = state == RollbackScriptState.NotCreated ? live.Select(s => s.Record.Reason).FirstOrDefault(r => r != NotCreatedReason.None) : NotCreatedReason.None,
            StateReason = StateReason(group, ended, state, assumed),
            StepCount = live.Sum(s => s.Record.Steps.Count),
            RowsTotal = live.Sum(s => s.Record.RowsAffected ?? 0),
            Tables = [.. group.Statements.Select(s => s.Record.Table).OfType<DbObjectRef>().Distinct()],
            Origin = group.Origin,
            OriginGroupId = group.OriginGroupId,
            ExpiresAt = (endedAt ?? first.ExecutedAt).AddDays(_settings().Normalized().RetentionDays),
            Statements = [.. group.Statements.Select(Info)],
        };
    }

    private static string? StateReason(OpenGroup group, TransactionEnded? ended, RollbackScriptState state, bool assumed)
    {
        if (assumed)
        {
            return "커밋(추정)";
        }

        var live = group.Statements.Where(s => !s.Invalidated).ToList();
        return state switch
        {
            RollbackScriptState.Unverified => ended?.Source switch
            {
                TransactionEndSource.AppRestart => "앱이 끝나 결과를 모름",
                TransactionEndSource.SessionClosed => "접속 닫힘",
                _ => "트랜잭션이 끝난 방법을 모름",
            },
            RollbackScriptState.Incomplete => string.Join(" · ", live.Select(s => s.Record.IncompleteReason).OfType<string>()
                .Append(group.Overlap ? "PK 없는 행을 두 번 바꿈" : null)
                .Append(live.Count(s => !HasScript(s)) is var n and > 0 ? $"원복 문장을 만들지 않은 문장 {n}개" : null)
                .OfType<string>().Distinct()),
            RollbackScriptState.Invalid => ended?.Kind == TransactionEndKind.Rollback ? "롤백함" : "모든 문장을 저장점까지 롤백함",
            RollbackScriptState.NotCreated => live.Select(s => s.Record.IncompleteReason ?? ReasonText(s.Record.Reason)).FirstOrDefault(r => r is not null),
            _ => null,
        };
    }

    /// <summary>「만들지 않음」 이유의 화면 글(설계 11.2). 열거 이름이 저장소·화면에 그대로 나가지 않게 한다.</summary>
    public static string? ReasonText(NotCreatedReason reason) => reason switch
    {
        NotCreatedReason.None => null,
        NotCreatedReason.NotProduction => "개발·테스트 접속",
        NotCreatedReason.SettingOff => "설정 끔",
        NotCreatedReason.Declined => "확인 창에서 끔",
        NotCreatedReason.OverLimit => "상한 넘음",
        NotCreatedReason.NotTarget => "대상 아님",
        NotCreatedReason.NoSecureStore => "안전한 저장소 없음",
        NotCreatedReason.Failed => "실패",
        NotCreatedReason.Suppressed => "시점만 기록",
        _ => reason.ToString(),
    };

    private static RollbackStatementInfo Info(OpenStatement s) =>
        new(s.Seq, s.Record.ExecutedAt, s.Record.Verb, s.Record.Table, ChangeCapture.Summarize(s.Record.Text), s.Record.RowsAffected,
            s.Record.BeforePoint, s.Invalidated ? RollbackScriptState.Invalid : s.Record.State,
            s.Invalidated ? s.InvalidReason : s.Record.IncompleteReason ?? ReasonText(s.Record.Reason),
            s.Record.Steps.Count, s.Invalidated)
        {
            Source = s.Record.Source,
            LockedRows = s.Record.LockedRows,
        };

    private RollbackScriptDocument? Document(OpenGroup group)
    {
        if (_store is not { CanStoreScripts: true })
        {
            return null; // 암호화 파일을 만들 수 없다(문장 글·바인드·행 값은 평문으로 남기지 않는다)
        }

        return new RollbackScriptDocument(RollbackScriptDocument.CurrentSchemaVersion, group.PersistedId,
        [
            .. group.Statements.Select(s => new RollbackScriptStatement(s.Seq, s.Record.ExecutedAt, s.Record.Text, s.Record.Binds,
                                                                        s.Record.Steps, s.Record.Probes, s.Invalidated,
                                                                        s.Record.IncompleteReason, s.Record.RowsAffected)),
        ]);
    }

    private async Task<long> SaveAsync(RollbackGroup row, RollbackScriptDocument? document)
    {
        if (_store is null)
        {
            return 0;
        }

        long id;
        try
        {
            id = await _store.SaveAsync(row, document, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return 0; // 저장 실패는 삼킨다(설계 13.2 BeforeEndAsync, 14장)
        }

        if (Interlocked.Increment(ref _saves) % PruneEvery == 0)
        {
            try
            {
                await _store.PruneAsync(_settings().Normalized().RetentionDays, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 정리 실패는 저장 결과에 영향을 주지 않는다. 다음 정리 때 다시 한다.
            }
        }

        return id;
    }

    private void Publish<TEvent>(TEvent e)
        where TEvent : OrandaEvent
    {
        try
        {
            _events?.Publish(e);
        }
        catch (Exception)
        {
            // 화면 알림 실패가 원장을 깨뜨리지 않는다.
        }
    }

    private SessionLedger? Session(Guid id, bool create)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var session) && create)
            {
                session = new SessionLedger();
                _sessions[id] = session;
            }

            return session;
        }
    }

    /// <summary>끝난 뒤 비었거나 세션이 닫힌 원장 칸을 지운다(세션이 많아도 메모리가 쌓이지 않게).</summary>
    private void Forget(Guid id, SessionLedger session, bool closed)
    {
        lock (_gate)
        {
            lock (session)
            {
                if (closed || (session.Main is null && session.Derived.Count == 0 && session.Savepoints.Count == 0))
                {
                    if (_sessions.TryGetValue(id, out var current) && ReferenceEquals(current, session))
                    {
                        _sessions.Remove(id);
                    }
                }
            }
        }
    }

    private readonly record struct Savepoint(long Ordinal, long Mark);

    private sealed class SessionLedger
    {
        public OpenGroup? Main { get; set; }

        public List<OpenGroup> Derived { get; } = [];

        /// <summary>저장점 이름(대소문자 무시) → 그 뒤 첫 문장의 순번과 거는 차례. 그 순번부터가 ROLLBACK TO 로 무효가 된다.</summary>
        public Dictionary<string, Savepoint> Savepoints { get; } = new(StringComparer.OrdinalIgnoreCase);

        public long NextOrdinal { get; set; }

        public long NextMark { get; set; }

        /// <summary>같은 세션의 저장(BeforeEndAsync·EndAsync)을 하나씩만 돌린다.</summary>
        public SemaphoreSlim Saving { get; } = new(1, 1);

        public IEnumerable<OpenGroup> Groups() => Main is null ? Derived : Derived.Prepend(Main);
    }

    private sealed class OpenGroup(RollbackOrigin origin, long? originGroupId)
    {
        public RollbackOrigin Origin { get; } = origin;

        public long? OriginGroupId { get; } = originGroupId;

        public List<OpenStatement> Statements { get; } = [];

        public long PersistedId { get; set; }

        public bool Changed { get; set; }

        /// <summary>한 묶음이 같은 행(ROWID)을 두 번 바꿨다(RD-13). 되살린 행·ROWID 로 찾는 원복이 서로 행을 못 찾는다.</summary>
        public bool Overlap { get; private set; }

        public void Add(RollbackStatementRecord record, long ordinal)
        {
            if (record.TouchedRowIds.Count > 0 && record.Table is { } table)
            {
                var ids = new HashSet<string>(record.TouchedRowIds, StringComparer.Ordinal);
                Overlap |= Statements.Any(s => !s.Invalidated && Equals(s.Record.Table, table) && s.Record.TouchedRowIds.Any(ids.Contains));
            }

            Statements.Add(new OpenStatement(Statements.Count + 1, ordinal, record));
            Changed = true;
        }
    }

    private sealed class OpenStatement(int seq, long ordinal, RollbackStatementRecord record)
    {
        public int Seq { get; } = seq;

        public long Ordinal { get; } = ordinal;

        public RollbackStatementRecord Record { get; } = record;

        public bool Invalidated { get; set; }

        public string? InvalidReason { get; set; }
    }
}
