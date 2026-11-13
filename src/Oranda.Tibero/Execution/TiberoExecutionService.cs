using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Execution.Providers;
using Oranda.Core.Grid;
using Oranda.Core.History;
using Oranda.Core.Journal;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Backup;
using Oranda.Core.Rollback.Capture;
using Oranda.Core.Sessions;
using Oranda.Tibero.Driver;
using Oranda.Tibero.Results;
using Oranda.Tibero.Rollback;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Execution;

/// <summary>
/// Tibero 실행 엔진(Oracle <c>OracleExecutionService</c> 대응, E1 골격). 파이프라인: 치환 변수 → 정책 관문 → (SQL*Plus 명령이면 처리기)
/// → 바인드 → 드라이버 실행 → 트랜잭션 추적(<c>LOCAL_TRANSACTION_ID</c>) → 이벤트. 실측 근거 = docs/design/tibero-driver-exec.md 1~3장.
/// <para>
/// 세션마다 실행을 한 줄로 세운다: 세션의 명령 잠금(<see cref="TiberoSession.CommandGate"/>, D-041)을 같은 연결을 쓰는 모든 기능이 함께 쓴다.
/// 자동 커밋 끄기는 세션이 맡는다(연 직후 잡은 트랜잭션, 결정21) — 드라이버 연결에는 자동 커밋 속성이 없다. 커밋·롤백은 늘 SQL 문장
/// (<c>COMMIT</c>·<c>ROLLBACK</c>)으로 보내고 트랜잭션 객체 메서드는 부르지 않는다(부르면 자동 커밋으로 돌아간다).
/// 드라이버의 <c>*Async</c> 는 동기로 돌므로 드라이버 단계 전체를 스레드 풀(<c>Task.Run</c>)로 옮긴다.
/// </para>
/// <para>
/// DBMS_OUTPUT 은 질의가 아닌 문장 뒤(성공·서버 오류 모두) <see cref="TiberoDbmsOutput.CollectBlock"/> 으로 거둔다(E3).
/// 질의와 분류가 불확실한 문장은 리더로 받아, 행이 있으면 <see cref="TiberoResultSet"/>(E4), REF CURSOR OUT 도 결과 집합으로 돌려준다.
/// 결과 집합이 명령·리더를 소유하고 페이지마다 명령 잠금을 다시 잡는다(실행은 리더를 연 뒤 잠금을 놓는다, Oracle 과 같다).
/// 취소(E2)는 Oracle 과 같다: 토큰 → <c>DbCommand.Cancel()</c>(<see cref="CommandCancellation.CancelOnToken"/>)이고, 드라이버가 돌아올 때까지
/// (Tibero 서버는 10ms~58초 뒤에 멈춘다, 실측 4장) 이 호출이 끝나지 않으므로 호출한 쪽의 「취소 중」 상태가 그만큼 이어진다 — 서비스는 별도 이벤트를 내지 않는다.
/// 문장 시간 제한은 Oracle 서비스에도 없어 두지 않는다(드라이버가 CommandTimeout 을 무시하므로 필요해지면 연결 취소 토큰으로 건다).
/// 변경 일지(E5, D4): ALTER·DROP·CREATE OR REPLACE·RENAME 직전에 <see cref="TiberoDdlSource"/> 로 <see cref="ChangeCapture"/> 를 돌려
/// 「보관 안 함 + 이유」 행을 <see cref="ChangeSnapshotCaptured"/> 로 알린다(서버에 아무것도 보내지 않는다). 실제 이전 DDL 보관은 T3.
/// </para>
/// <para>
/// 되돌리기 지점(설계 13.5, RB-11): 쓰기 문장이면 정책 관문 앞(명령 잠금 밖)에서 <see cref="IRollbackCapture.PrepareAsync"/>(잠그는 통로) →
/// 관문에 제안(<see cref="ConfirmExtras"/>)을 넘기고 답을 받는다 → 명령 잠금 안에서 <see cref="IRollbackCapture.BeginAsync"/>(쥔 통로) →
/// 캡처가 준 문장·바인드를 보낸다 → 성공이면 <see cref="IStatementCapture.CompleteAsync"/>, 사용자 문장이 끝나기 전의 실패·취소면
/// <see cref="IStatementCapture.AbandonAsync"/>(내부 저장점으로 잠금을 푼다). 결과의 <see cref="ExecutionResult.Statement"/> 는 사용자 문장 그대로다.
/// </para>
/// </summary>
[OrandaService(typeof(IExecutionService), Dbms = Dbms.Tibero)]
internal sealed partial class TiberoExecutionService : IExecutionService
{
    /// <summary>
    /// 읽기 전용 프로필의 문장마다 보내는 블록. 최상위 <c>SET TRANSACTION READ ONLY</c> 는 TBR-7001 이고 <c>DBMS_TRANSACTION.READ_ONLY</c> 는 없다
    /// → PSM 블록 안에서만 된다(실측 2장). 앞 트랜잭션을 먼저 끝내 이전 스냅숏을 끌고 가지 않는다.
    /// 주의: Tibero 의 READ ONLY 트랜잭션은 쓰기를 막지 않는다(INSERT·DDL·FOR UPDATE 가 성공, 쓰기 IT #28) — 이 블록은 읽기 일관성만 준다.
    /// 읽기 전용 프로필의 쓰기 차단은 정책 관문(<c>ExecutionPolicy</c> ReadOnlyProfile)이 서버로 보내기 전에 한다.
    /// </summary>
    internal const string ReadOnlyBlock = "BEGIN ROLLBACK; SET TRANSACTION READ ONLY; END;";

    /// <summary>미커밋 판정 문장. 트랜잭션이 없으면 NULL(실측 2장, Oracle 과 같은 문장).</summary>
    internal const string TransactionIdSql = "SELECT DBMS_TRANSACTION.LOCAL_TRANSACTION_ID FROM dual";

    private readonly IExecutionPolicy _policy;
    private readonly IExecutionInteraction _interaction;
    private readonly IEventBus _events;
    private readonly ISqlPlusCommandProcessor? _sqlPlus;
    private readonly IGridFetchSettings? _grid;
    private readonly IRollbackCapture _capture;
    private readonly IDmlAnalyzer _analyzer;
    private readonly ConditionalWeakTable<TiberoSession, SessionRuntime> _runtimes = new();

    public TiberoExecutionService(IExecutionPolicy policy, IExecutionInteraction interaction, IEventBus events,
                                  ISqlPlusCommandProcessor sqlPlus)
        : this(policy, interaction, events, sqlPlus, dbmsOutput: true)
    {
    }

    internal TiberoExecutionService(IExecutionPolicy policy, IExecutionInteraction interaction, IEventBus events,
                                    ISqlPlusCommandProcessor? sqlPlus, bool dbmsOutput = true, int pageSize = 500,
                                    IGridFetchSettings? grid = null, IRollbackCapture? capture = null, IDmlAnalyzer? analyzer = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(events);
        _policy = policy;
        _interaction = interaction;
        _events = events;
        _sqlPlus = sqlPlus;
        DbmsOutputEnabled = dbmsOutput;
        PageSize = pageSize;
        _grid = grid;
        _capture = capture ?? RollbackCapture.Disabled;
        _analyzer = analyzer ?? NullDmlAnalyzer.Instance;
    }

    /// <summary>되돌리기 방언(설계 15장).</summary>
    internal static IRollbackDialect Dialect => TiberoRollbackDialect.Instance;

    /// <summary>생성할 때 받은 결과 페이지 크기(설정 서비스가 없을 때의 값). 실행할 때는 <see cref="CurrentPageSize"/>.</summary>
    internal int PageSize { get; }

    /// <summary>결과 페이지 크기(<c>grid.pageSize</c>, 1 이상). 설정을 바꾸면 다음 실행부터 따라간다.</summary>
    internal int CurrentPageSize => _grid is { PageSize: >= 1 } g ? g.PageSize : Math.Max(1, PageSize);

    /// <summary>LOB 미리보기 길이(<c>grid.initialLobFetchSize</c>). 설정이 없으면 기본 4000.</summary>
    internal int CurrentLobFetchSize => _grid is { InitialLobFetchSize: >= 0 } g ? g.InitialLobFetchSize : TiberoValues.LobPreviewSize;

    /// <summary>
    /// <c>execution.dbmsOutput</c>(기본 켬, Oracle 과 같다). 켜면 세션 첫 실행 때 <c>DBMS_OUTPUT.ENABLE(NULL)</c>, 질의가 아닌 문장마다 뒤에 버퍼를 거둔다.
    /// SET SERVEROUTPUT ON/OFF 는 처리기가 보내는 ENABLE/DISABLE 블록일 뿐이라 서비스는 늘 거둔다(꺼진 버퍼는 0줄).
    /// </summary>
    internal bool DbmsOutputEnabled { get; }

    public async Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds,
                                                    ExecutionOptions? options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(stmt);
        binds ??= new Dictionary<string, BindValue>();
        options ??= ExecutionOptions.Default;

        var result = await RunAsync(s, stmt, binds, options, ct).ConfigureAwait(false);
        _events.Publish(new StatementExecuted(s.Id, result.Statement, result, options));
        return result;
    }

    /// <summary>
    /// 스크립트: 문장마다 <see cref="ExecuteAsync"/> 를 불러 결과를 하나씩 낸다(Oracle 과 같다). 취소·정책 거절은 거기서 멈추고,
    /// 실패·거부는 SQL*Plus <c>WHENEVER SQLERROR EXIT</c>(처리기가 기억, 설정보다 앞선다) → <paramref name="mode"/>(멈춤·계속·묻기) 순으로 판단한다.
    /// 묻기에서 「이후 계속」을 고르면 남은 문장은 계속 모드로 돈다.
    /// </summary>
    public async IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode,
                                                                      ExecutionOptions? options,
                                                                      [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(stmts);
        options ??= new ExecutionOptions { Source = HistorySource.Script };
        var empty = new Dictionary<string, BindValue>();

        foreach (var stmt in stmts)
        {
            // 바인드 이름이 없는 문장에 값을 넘기면 바인드 빌더가 받은 값을 모두 붙인다(서버 오류). 그 문장에는 빈 사전.
            var binds = options.Binds is { } all && stmt.BindNames.Count > 0 ? all : empty;
            var result = await ExecuteAsync(s, stmt, binds, options, ct).ConfigureAwait(false);
            yield return result;

            if (result.Status == ExecutionStatus.Cancelled)
            {
                yield break; // 취소 또는 사용자가 확인을 거절했다.
            }

            if (result.Status is ExecutionStatus.Failed or ExecutionStatus.Denied)
            {
                // 스크립트 안의 WHENEVER SQLERROR EXIT 는 설정(묻기·계속)보다 앞선다(SQL*Plus 와 같다).
                if (_sqlPlus is IScriptDirectives directives && directives.ExitOnError(s))
                {
                    await directives.ApplyOnErrorTransactionAsync(s, ct).ConfigureAwait(false);
                    yield break;
                }

                if (mode == ScriptErrorMode.Stop)
                {
                    yield break;
                }

                if (mode == ScriptErrorMode.Ask)
                {
                    var failure = result.Error ?? new DbError("POLICY", result.Policy?.Message ?? "정책 관문이 거부했습니다.") { Dbms = Dbms.Tibero };
                    var action = await _interaction.AskScriptErrorAsync(stmt, failure, ct).ConfigureAwait(false);
                    if (action == ScriptErrorAction.Stop)
                    {
                        yield break;
                    }

                    if (action == ScriptErrorAction.ContinueAll)
                    {
                        mode = ScriptErrorMode.Continue;
                    }
                }
            }
        }
    }

    private async Task<ExecutionResult> RunAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds,
                                                 ExecutionOptions options, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return new ExecutionResult(stmt, ExecutionStatus.Cancelled);
        }

        var runtime = s is TiberoSession ts ? _runtimes.GetValue(ts, _ => new SessionRuntime()) : null;

        // 1. 치환 변수
        if (stmt.SubstitutionNames.Count > 0)
        {
            var substituted = await SubstituteAsync(s, stmt, runtime, ct).ConfigureAwait(false);
            if (substituted is null)
            {
                return new ExecutionResult(stmt, ExecutionStatus.Cancelled);
            }

            stmt = substituted with { Dml = null }; // 치환한 글은 만든 쪽이 준 모양과 다를 수 있다 → 다시 분석(설계 13.3)
        }

        // 2. 되돌리기 제안(설계 13.5): 쓰기 문장만, 정책 관문 앞·명령 잠금 밖. 실패는 엔진이 NotApplicable 로 삼키고 사용자 취소만 전파한다.
        var tiberoSession = s as TiberoSession;
        var rollback = tiberoSession is not null && ShouldCapture(s, stmt, options) ? NewRollbackRequest(s, stmt, binds, options) : null;
        var extras = ConfirmExtras.None;
        if (rollback is not null)
        {
            try
            {
                extras = await _capture.PrepareAsync(rollback, TiberoRollbackChannel.Gated(tiberoSession!), Dialect, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new ExecutionResult(stmt, ExecutionStatus.Cancelled);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 엔진은 던지지 않는 계약이다. 그래도 던지면(엔진 안 제한 시간·통로 밖 예외) 제안 없이 실행한다 — 결과 계약(예외 대신 결과)을 지킨다.
                extras = ConfirmExtras.None;
            }
        }

        // 2b. 데이터가 사라지는 DDL 앞 백업 테이블 제안(RB-14, Oracle RB-44 와 같은 규칙). 명령 잠금 밖.
        if (tiberoSession is not null)
        {
            try
            {
                if (await OfferBackupAsync(tiberoSession, stmt, ct).ConfigureAwait(false) is { } backup)
                {
                    extras = extras with { Backup = backup };
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new ExecutionResult(stmt, ExecutionStatus.Cancelled);
            }
        }

        // 3. 정책 관문. 거부면 드라이버를 부르지 않는다(SAFE-07). 미리 받은 확인은 치환 뒤 텍스트로 비교한다.
        var gate = await PolicyGate.DecideAsync(_policy, _interaction, _events, s, stmt, options, extras, ct).ConfigureAwait(false);
        switch (gate.Outcome)
        {
            case PolicyGateOutcome.Denied:
                return new ExecutionResult(stmt, ExecutionStatus.Denied) { Policy = gate.Decision };
            case PolicyGateOutcome.Cancelled:
                return new ExecutionResult(stmt, ExecutionStatus.Cancelled) { Policy = gate.Decision };
        }

        var applied = gate.Applied;

        // 3. SQL*Plus 명령: Core 처리기(앱 조립에서는 디스패처 → TiberoSqlPlusCommandProcessor)에 넘긴다.
        if (stmt.Kind == StatementKind.SqlPlus)
        {
            if (_sqlPlus is null)
            {
                return Failed(stmt, "ORANDA-SQLPLUS", "SQL*Plus 명령 처리기가 없습니다.", applied);
            }

            var r = await _sqlPlus.ExecuteAsync(s, stmt, ct).ConfigureAwait(false);
            return applied is null ? r : r with { Policy = applied };
        }

        if (s is not TiberoSession session || runtime is null)
        {
            throw new ArgumentException("Oranda Tibero 세션 관리자가 연 세션이 아닙니다.", nameof(s));
        }

        if (session.State is SessionState.Broken or SessionState.Closed)
        {
            return Failed(stmt, "ORANDA-SESSION", $"세션이 {session.State} 상태입니다. 다시 접속하세요.", applied);
        }

        // 세션 연결의 명령 잠금(D-041). 실행 안에서는 연결에 직접 보낸다(서비스 API 를 다시 부르면 같은 잠금을 다시 잡아 교착이다).
        try
        {
            await session.CommandGate.EnterAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ExecutionResult(stmt, ExecutionStatus.Cancelled) { Policy = applied };
        }

        // 실행이 실패해도 이미 만든 OUT REF CURSOR 결과 집합은 닫아야 한다. 결과 집합 정리는 명령 잠금을 다시 잡으므로(재진입 불가)
        // 잠금을 놓은 뒤에 한다 → 여기 모아 두었다가 아래 바깥 finally 에서 닫는다.
        var orphans = new List<IAsyncDisposable>();
        try
        {
            session.SetBusy(true);
            try
            {
                // 드라이버 호출은 동기로 돈다(실측 4장) → 부른 스레드(UI)를 막지 않게 드라이버 단계 전체를 스레드 풀로 옮긴다.
                var plan = rollback is null ? null : new RollbackPlan(rollback, extras, gate.Answer);
                // 확인 창이 고른 백업(RB-14). 이 실행의 제안에서 나온 글만 보낸다(정책 관문을 지나지 않는 글이다).
                var backupSql = stmt.Kind == StatementKind.Ddl && IsOfferedBackup(extras.Backup, gate.Answer.BackupSql) ? gate.Answer.BackupSql : null;
                return await Task.Run(() => RunOnDriverAsync(session, runtime, stmt, binds, applied, orphans, plan, backupSql, ct), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                session.SetBusy(false);
                session.CommandGate.Release();
            }
        }
        finally
        {
            foreach (var orphan in orphans)
            {
                await SafeDisposeAsync(orphan).ConfigureAwait(false);
            }
        }
    }

    private async Task<ExecutionResult> RunOnDriverAsync(TiberoSession session, SessionRuntime runtime, SqlStatement stmt,
                                                         IReadOnlyDictionary<string, BindValue> binds, PolicyDecision? applied,
                                                         List<IAsyncDisposable> orphans, RollbackPlan? plan, string? backupSql, CancellationToken ct)
    {
        var kind = ExecutionPolicy.EffectiveKind(stmt);
        var pendingBefore = session.HasPendingTransaction; // 트랜잭션 끝 판정(RollbackFlow.EndAfter)의 보내기 전 미커밋
        // 커밋으로 끝낼 수 있는 문장(COMMIT·DDL·PL/SQL)을 보내는 동안 끊기면 끊김을 「롤백」이 아니라 「모름」으로 알린다(Oracle 과 같은 범위).
        // 끝 범위: 캡처가 시작하는 쓰기(기록과 끊김 알림이 엇갈리지 않게)와 끝낼 수 있는 문장(끊김을 「롤백」이 아니라 「모름」으로)에 연다.
        // 끝 판단을 마치면 바로 닫는다 — 그 뒤(출력 거두기 등)의 끊김은 평소 경로(세션 끊김 → 「롤백」)로 간다.
        TiberoSession.EndScope? scope = null;
        var endDecided = false;

        IStatementCapture? capture = null;
        var began = false; // 시작 단계가 통로로 문장을 보냈다(내부 저장점·잠금 읽기를 했을 수 있다)
        long? rows = null;
        Dictionary<string, object?>? outValues = null; // 실패 경로의 마무리도 이미 읽은 OUT 값(ORANDA_RID)과 트랜잭션 id 를 쓴다
        string? transactionId = null;
        RollbackStatementSummary? summary = null;
        RollbackStatementSummary? backupLog = null; // 백업 테이블을 만들었다(원래 DDL 이 실패·취소돼도 남았음을 알린다)
        IReadOnlySet<string> added = EmptyNames;
        var sw = Stopwatch.StartNew();
        DbCommand? cmd = null;
        DbDataReader? reader = null; // 결과 집합에 넘기기 전까지 이 메서드가 소유한다

        // dispatched = 사용자 문장을 드라이버에 넘겼다(그 전에 끝나면 걸린 시간은 0 — 준비 문장만 돈 시간은 사용자 문장 시간이 아니다).
        // executed = 사용자 문장 실행이 돌아왔다(그 뒤에 실패해도 트랜잭션 상태를 갱신한다).
        var dispatched = false;
        var executed = false;
        TimeSpan Elapsed() => dispatched ? sw.Elapsed : TimeSpan.Zero;

        async Task DecideEndAsync(bool succeeded, bool pendingAfter)
        {
            if (endDecided)
            {
                return;
            }

            endDecided = true;
            if (dispatched && await EndTransactionAsync(session, stmt, succeeded, pendingBefore, pendingAfter).ConfigureAwait(false))
            {
                scope?.Reported();
            }

            scope?.Dispose();
            scope = null;
        }
        IReadOnlyList<TiberoOutBind> outBinds = [];
        IReadOnlyList<string>? collected = null; // 이미 거둔 DBMS_OUTPUT(버퍼는 비었다) — 뒤 단계가 실패하면 그 결과에 붙인다
        try
        {
            await PrepareSessionAsync(session, runtime, ct).ConfigureAwait(false);

            // 커밋으로 끝낼 수 있는 문장 앞(설계 7.4, RD-7): 원장이 열린 묶음을 「열림」으로 저장한다.
            await BeforeEndAsync(session, stmt, ct).ConfigureAwait(false);

            if (backupSql is not null)
            {
                if (await CreateBackupAsync(session, stmt, backupSql, applied, ct).ConfigureAwait(false) is { } backupFailed)
                {
                    return backupFailed with { Elapsed = sw.Elapsed };
                }

                backupLog = BackupSummary(backupSql);
                pendingBefore = session.HasPendingTransaction; // CTAS 가 앞 트랜잭션을 커밋했다(그 끝은 CTAS 쪽이 알렸다. 원래 DDL 의 끝은 묶음이 없으면 원장이 무시한다)
            }

            await CaptureBeforeChangeAsync(session, stmt, BackupPlanner.BackupNameOf(backupSql), ct).ConfigureAwait(false);

            // 되돌리기 시작(시점 읽기·내부 저장점·잠금 읽기). 보낼 글·바인드는 캡처가 정한다(한 행 INSERT 면 RETURNING 이 붙는다).
            var send = stmt;
            var sendBinds = binds;
            if (plan is not null)
            {
                scope = session.BeginEndScope(RollbackFlow.CanEndTransaction(stmt) ? TransactionEndKind.Unknown : TransactionEndKind.Rollback);
                var channel = TiberoRollbackChannel.Locked(session);
                try
                {
                    capture = await BeginCaptureAsync(channel, plan, ct).ConfigureAwait(false);
                }
                finally
                {
                    began = channel.HasSent; // 시작 단계가 서버에 무언가 보냈다(저장점·잠금 읽기·시점 읽기) → 실패해도 미커밋 표시를 갱신한다
                }

                if (capture is not null)
                {
                    send = capture.StatementToSend;
                    sendBinds = capture.BindsToSend;
                    added = AddedBindNames(binds, sendBinds);
                }
            }

            cmd = session.CreateCommand(send.Text);
            if (send.BindNames.Count > 0 || sendBinds.Count > 0)
            {
                // 값이 없는 바인드는 여기서 InvalidOperationException → ORANDA-EXEC, 문장은 보내지 않는다.
                var driver = session.Driver ?? throw new InvalidOperationException("세션에 Tibero 드라이버 어댑터가 없어 바인드를 만들 수 없습니다.");
                outBinds = TiberoBindBuilder.Build(driver, cmd, send, sendBinds);
            }

            var pageSize = CurrentPageSize; // 실행마다 한 번 읽는다: 한 결과 안에서 페이지 크기가 바뀌지 않게
            var lobFetchSize = CurrentLobFetchSize;
            var hasRows = false;

            // 취소: 토큰 → DbCommand.Cancel()(Oracle 과 같다). 드라이버 호출이 돌아올 때까지(서버가 멈출 때까지) 이 줄에서 기다린다.
            // 호출이 끝나면 등록을 푼다 — 늦은 취소가 같은 연결의 다음 문장으로 새지 않게.
            if (scope is null && RollbackFlow.CanEndTransaction(stmt))
            {
                scope = session.BeginEndScope();
            }

            using (cmd.CancelOnToken(ct))
            {
                dispatched = true;
                if (send.Kind == StatementKind.Query || send.IsClassificationUncertain)
                {
                    // 질의와 분류가 불확실한 문장은 리더로 받는다(PSM 블록도 FieldCount=0 으로 된다, 실측 3장). 행이 있으면 결과 집합.
                    reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    executed = true;
                    if (reader.FieldCount > 0)
                    {
                        hasRows = true;
                    }
                    else
                    {
                        rows = reader.RecordsAffected >= 0 ? reader.RecordsAffected : null;
                        // 행이 없으면 리더는 더 쓸 데가 없다: OUT 값을 읽기 전에 닫는다.
                        var empty = reader;
                        reader = null;
                        await SafeDisposeAsync(empty).ConfigureAwait(false);
                    }
                }
                else
                {
                    var n = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    executed = true;
                    rows = kind == StatementKind.Dml && n >= 0 ? n : null;
                }

                ct.ThrowIfCancellationRequested();
            }

            // REF CURSOR OUT 은 결과 집합이 된다. 만드는 즉시 orphans 에 올려(중간 OUT 값 읽기가 실패해도 새지 않게) 결과를 돌려주기 직전에 뺀다.
            outValues = ReadOutValues(session, outBinds, pageSize, lobFetchSize, orphans);

            sw.Stop();
            (var pending, transactionId) = await TrackTransactionIdAsync(session, stmt, kind, ct).ConfigureAwait(false);
            if (capture is not null)
            {
                // 사용자 문장은 이미 성공했다: 늦은 취소가 직후 값 읽기를 끊어 원복 스크립트만 망가지지 않게 사용자 토큰을 넘기지 않는다(14장).
                summary = await CompleteCaptureAsync(capture, rows, outValues, transactionId, CancellationToken.None).ConfigureAwait(false);
                capture = null; // 마무리했다: 뒤 단계가 실패해도 다시 마무리하지 않는다(요약은 summary 에 남는다)
            }

            await DecideEndAsync(succeeded: true, pending).ConfigureAwait(false);

            // 질의 뒤에는 숨은 문장(거두기)을 보내지 않는다(Oracle 과 같다): 직전 커서가 바뀌지 않게. 질의 안 함수가 낸 출력은 버퍼에 남아 다음 문장 뒤에 나온다.
            // 리더가 열려 있으면(행을 낸 불확실 문장·FOR UPDATE)도 거두지 않는다: 같은 연결에서 거두기 블록의 CLOB OUT 로케이터가 비어
            // 드라이버 안에서 NullReferenceException(TiberoClob.Read, 실서버 win144 #20 실측) — 출력은 다음 문장 뒤에 나온다.
            IReadOnlyList<string> output = IsPlainQuery(stmt, kind) || hasRows ? [] : await SafeCollectAsync(session, runtime, ct).ConfigureAwait(false);
            collected = output; // 뒤(결과 집합 만들기)가 실패해도 이미 비운 버퍼의 줄을 그 결과에 붙인다.

            if (kind == StatementKind.Ddl)
            {
                _events.Publish(new MetadataInvalidated(session.DbKey, []));
            }

            // 결과 집합은 실패할 일이 더 없는 맨 끝에서 만든다(만든 뒤 실패하면 명령 잠금을 쥔 채라 닫을 수 없다). 만들면 명령·리더는 결과 집합 소유.
            IResultSet? resultSet = null;
            if (hasRows)
            {
                resultSet = TiberoResultSets.Create(session, cmd, reader!, pageSize, lobFetchSize);
                reader = null;
                cmd = null;
            }

            orphans.Clear(); // OUT 결과 집합도 결과가 가져간다.
            return new ExecutionResult(stmt, ExecutionStatus.Succeeded)
            {
                Elapsed = sw.Elapsed,
                RowsAffected = rows,
                ResultSet = resultSet,
                Policy = applied,
                DbmsOutput = output,
                OutValues = WithoutAdded(outValues, added),
                TransactionPending = pending,
                Rollback = summary ?? backupLog,
            };
        }
        catch (Exception ex) when (IsCancellation(ex, ct))
        {
            if (executed)
            {
                await CloseCursorOutsAsync(session, outBinds).ConfigureAwait(false); // 서버가 연 REF CURSOR 를 감싸지 않은 채 남기지 않는다.
            }

            summary = await FinishCaptureAfterFailureAsync(session, capture, executed, rows, outValues, transactionId, null).ConfigureAwait(false) ?? summary;
            var pending = await SafeTrackAfterFailureAsync(session, stmt, kind).ConfigureAwait(false);
            await DecideEndAsync(executed, pending).ConfigureAwait(false);
            return new ExecutionResult(stmt, ExecutionStatus.Cancelled) { Elapsed = Elapsed(), Policy = applied, TransactionPending = pending, Rollback = summary ?? backupLog };
        }
        catch (Exception ex) when (TiberoErrorClassifier.IsDriverException(ex))
        {
            session.ReportFailure(ex);
            var error = TiberoErrorClassifier.ToDbError(ex, stmt.Text) ?? new DbError("TBR", ex.Message) { Dbms = Dbms.Tibero };
            summary = await FinishCaptureAfterFailureAsync(session, capture, executed, rows, outValues, transactionId, error).ConfigureAwait(false) ?? summary;
            var pending = await SafeTrackAfterFailureAsync(session, stmt, kind).ConfigureAwait(false);
            await DecideEndAsync(executed, pending).ConfigureAwait(false);
            // 실패 뒤에도 오류 앞에서 낸 출력을 거둔다(끊긴 세션은 빼고). 사용자 문장이 나가기 전의 실패(준비 문장)면 거둘 것이 없다.
            IReadOnlyList<string> output = !dispatched || session.State is SessionState.Broken or SessionState.Closed
                ? []
                : await SafeCollectAsync(session, runtime, ct).ConfigureAwait(false);
            return new ExecutionResult(stmt, ExecutionStatus.Failed)
            {
                Elapsed = Elapsed(),
                Error = error,
                Policy = applied,
                DbmsOutput = output,
                TransactionPending = pending,
                Rollback = summary ?? backupLog,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or FormatException or InvalidCastException)
        {
            // 문장이 돈 뒤(OUT 값 읽기 등)의 실패면 트랜잭션이 이미 바뀌었을 수 있다 → 상태를 갱신한다.
            var failed = Failed(stmt, "ORANDA-EXEC", ex.Message, applied) with { Elapsed = Elapsed() };
            summary = await FinishCaptureAfterFailureAsync(session, capture, executed, rows, outValues, transactionId, failed.Error).ConfigureAwait(false) ?? summary;
            if (!executed && !began)
            {
                return failed with { Rollback = summary ?? backupLog };
            }

            // 보내기 전 실패라도 캡처가 시작했으면(내부 저장점·잠금 읽기) 서버 트랜잭션이 열려 있을 수 있다 → 미커밋 표시를 갱신한다(6.2).
            var pending = await SafeTrackAfterFailureAsync(session, stmt, kind).ConfigureAwait(false);
            await DecideEndAsync(executed, pending).ConfigureAwait(false);
            var output = collected ?? (session.State is SessionState.Broken or SessionState.Closed
                ? []
                : await SafeCollectAsync(session, runtime, ct).ConfigureAwait(false));
            return failed with { TransactionPending = pending, DbmsOutput = output, Rollback = summary ?? backupLog };
        }
        finally
        {
            scope?.Dispose(); // 알리지 못한 채 끊겼으면 「모름」으로 알린다

            // 결과 집합에 넘기지 않은 리더·명령만 여기서 닫는다(넘겼으면 null).
            await SafeDisposeAsync(reader).ConfigureAwait(false);
            await SafeDisposeAsync(cmd).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 커밋으로 끝낼 수 있는 문장(<see cref="RollbackFlow.CanEndTransaction"/>: COMMIT·DDL·PL/SQL·불확실) 앞에서 원장의 열린 묶음을 저장한다(7.4).
    /// 읽기 전용 프로필은 기록이 없어 부르지 않는다. 사용자 취소만 전파하고 원장 실패는 삼킨다(<see cref="TiberoRollbackHooks.BeforeEndAsync"/>).
    /// </summary>
    private static Task BeforeEndAsync(TiberoSession session, SqlStatement stmt, CancellationToken ct) =>
        session.Profile.ReadOnly || !RollbackFlow.CanEndTransaction(stmt) ? Task.CompletedTask : session.Rollback.BeforeEndAsync(session, ct);

    /// <summary>
    /// 보낸 문장 뒤의 트랜잭션 끝(설계 7.3, 판단은 <see cref="RollbackFlow"/>): 사용자 저장점은 원장에 표시하고, 끝이면 세션 훅
    /// (<see cref="TiberoRollbackHooks.EndAsync"/>: 모름이면 판정, 커밋·모름이면 커밋 시점, 원장 끝, 알림)에 넘긴다. 명령 잠금을 쥔 채 부른다.
    /// 끊긴·닫힌 세션은 하지 않고 거짓을 돌려준다 — 끊김 범위(<see cref="TiberoSession.BeginEndScope"/>)나 세션 끊김 처리가 알린다.
    /// </summary>
    /// <returns>판단을 마쳤다(끝을 알렸거나 끝이 아니다)</returns>
    private static async Task<bool> EndTransactionAsync(TiberoSession session, SqlStatement stmt, bool succeeded, bool pendingBefore, bool pendingAfter)
    {
        if (session.Profile.ReadOnly)
        {
            return true;
        }

        if (session.State is SessionState.Broken or SessionState.Closed)
        {
            return false;
        }

        try
        {
            if (RollbackFlow.SavepointMarked(stmt, succeeded, Dialect.SavepointName) is { } savepoint)
            {
                session.Rollback.Ledger.MarkSavepoint(session.Id, savepoint);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 저장점 표시 실패는 문장 결과를 바꾸지 않는다.
        }

        // 미커밋이 없던 세션의 PL/SQL·불확실 문장도 이번 기록으로 묶음이 열렸으면 「트랜잭션이 있었다」로 본다: 블록 안 COMMIT 으로 끝난 묶음을
        // 다음 ROLLBACK 이 「롤백됨」으로 덮지 않게(리뷰 RB-11 3부). DML 은 미커밋을 남기고, DDL·트랜잭션 제어는 pendingBefore 를 보지 않는다.
        var before = pendingBefore
                     || ((stmt.Kind == StatementKind.PlSql || stmt.IsClassificationUncertain) && session.Rollback.Ledger.Open(session.Id) is not null);
        if (RollbackFlow.EndAfter(session.Id, stmt, succeeded, before, pendingAfter) is { } ended)
        {
            await session.Rollback.EndAsync(session, ended.Kind, ended.Source, ended.Savepoint).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>정책 관문에서 받은 답까지 모은 되돌리기 준비(명령 잠금 안으로 넘긴다).</summary>
    internal sealed record RollbackPlan(RollbackRequest Request, ConfirmExtras Extras, ConfirmAnswer Answer);

    /// <summary>
    /// 되돌리기 엔진에 넘길 문장인가(설계 7.1·7.5, 판단은 Core <see cref="RollbackFlow"/>): 기록 방식이 <see cref="ChangeRecording.None"/> 이 아니고
    /// (읽기 전용 프로필·에이전트·데이터 복사는 None) 원장에 기록하는 쓰기 문장(DML·PL/SQL·분류 불확실). SQL*Plus 명령은 처리기로 간다.
    /// </summary>
    internal static bool ShouldCapture(ISession s, SqlStatement stmt, ExecutionOptions options) =>
        stmt.Kind != StatementKind.SqlPlus
        && RollbackFlow.IsRecordedWrite(stmt)
        && RollbackFlow.RecordingFor(s, options) != ChangeRecording.None;

    /// <summary>엔진 요청. 모양은 만든 쪽이 준 <see cref="SqlStatement.Dml"/>(그리드 편집), 없으면 분석기. 분석기 실패는 「모양 없음」.</summary>
    private RollbackRequest NewRollbackRequest(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds, ExecutionOptions options)
    {
        DmlShape? shape;
        try
        {
            shape = stmt.Dml ?? _analyzer.Analyze(stmt.Text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            shape = null; // 분석기는 던지지 않는 계약이다. 그래도 던지면 원복 대상 밖으로 보고 실행은 막지 않는다.
        }

        return new RollbackRequest(s, stmt, binds, options) { Shape = shape };
    }

    /// <summary>
    /// 명령 잠금 안 시작 단계. 사용자 취소는 전파한다(사용자 문장을 보내지 않는다). 그 밖의 엔진 실패는 원복 없이 사용자 문장을 그대로 보낸다(null).
    /// </summary>
    private async Task<IStatementCapture?> BeginCaptureAsync(TiberoRollbackChannel channel, RollbackPlan plan, CancellationToken ct)
    {
        try
        {
            return await _capture.BeginAsync(plan.Request, plan.Extras, plan.Answer, channel, Dialect, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // 엔진 안 제한 시간: 사용자 취소가 아니므로 원복 없이 사용자 문장을 보낸다(엔진 실패와 같다)
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !TiberoErrorClassifier.IsDriverException(ex))
        {
            return null;
        }
    }

    private static readonly IReadOnlySet<string> EmptyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>캡처가 덧붙인 바인드 이름(<c>ORANDA_RID</c> 등): 보낸 바인드에는 있고 사용자 바인드에는 없는 것.</summary>
    internal static IReadOnlySet<string> AddedBindNames(IReadOnlyDictionary<string, BindValue> user, IReadOnlyDictionary<string, BindValue> sent)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in sent.Keys)
        {
            if (!user.ContainsKey(name))
            {
                names.Add(name);
            }
        }

        return names.Count == 0 ? EmptyNames : names;
    }

    /// <summary>결과에 싣는 OUT 값: 캡처가 덧붙인 내부 바인드는 뺀다(사용자에게는 사용자 문장의 OUT 값만, 설계 6.1). 엔진에는 다 넘긴다.</summary>
    internal static IReadOnlyDictionary<string, object?> WithoutAdded(Dictionary<string, object?> values, IReadOnlySet<string> added)
    {
        if (added.Count == 0)
        {
            return values;
        }

        var result = new Dictionary<string, object?>(values, StringComparer.OrdinalIgnoreCase);
        foreach (var name in added)
        {
            result.Remove(name);
        }

        return result;
    }

    /// <summary>사용자 문장 성공 뒤 마무리. 엔진은 던지지 않는 계약이고, 취소·실패가 와도 이미 성공한 사용자 문장 결과를 바꾸지 않는다.</summary>
    private static async Task<RollbackStatementSummary?> CompleteCaptureAsync(IStatementCapture capture, long? rows, IReadOnlyDictionary<string, object?> outValues,
                                                                             string? transactionId, CancellationToken ct)
    {
        try
        {
            return await capture.CompleteAsync(rows, outValues, transactionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new RollbackStatementSummary(RollbackScriptState.Incomplete, NotCreatedReason.Failed, 0)
            {
                Warning = "원복 스크립트를 마무리하지 못했습니다: " + ex.Message,
            };
        }
    }

    /// <summary>
    /// 실패·취소 뒤. 사용자 문장이 끝나기 전이면 <see cref="IStatementCapture.AbandonAsync"/>(내부 저장점으로 되돌려 잠금을 푼다).
    /// 사용자 문장은 이미 끝났고 그 뒤 단계(OUT 값 읽기·트랜잭션 추적)가 실패했으면 되돌리지 않고 마무리한다 — 저장점으로 되돌리면 성공한 사용자 문장이 지워진다.
    /// 끊긴 세션은 보낼 수 없으므로 엔진에 실패만 알린다(엔진은 통로 오류를 삼킨다).
    /// </summary>
    private static async Task<RollbackStatementSummary?> FinishCaptureAfterFailureAsync(TiberoSession session, IStatementCapture? capture, bool executed,
                                                                                        long? rows, IReadOnlyDictionary<string, object?>? outValues,
                                                                                        string? transactionId, DbError? error)
    {
        if (capture is null)
        {
            return null;
        }

        if (executed)
        {
            return await CompleteCaptureAsync(capture, rows, outValues ?? new Dictionary<string, object?>(), transactionId, CancellationToken.None)
                .ConfigureAwait(false);
        }

        try
        {
            return await capture.AbandonAsync(error, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new RollbackStatementSummary(RollbackScriptState.NotCreated, NotCreatedReason.Failed, 0)
            {
                Warning = session.State is SessionState.Broken or SessionState.Closed
                    ? null
                    : "원복 스크립트용 잠금을 풀지 못했습니다: " + ex.Message,
            };
        }
    }

    /// <summary>
    /// 변경 일지(Oracle <c>CaptureBeforeChangeAsync</c> 와 같은 자리·규칙): 정책·확인을 통과한 DDL 대상마다 일지 행을 알린다.
    /// T2 의 원천 <see cref="TiberoDdlSource"/> 는 서버를 읽지 않고 「보관 안 함 + 이유」 행만 만든다. 일지는 부가 기능 — 실행을 막지 않고, 사용자 취소만 전파한다.
    /// </summary>
    /// <param name="backupName">이 DDL 앞에서 만든 백업 테이블(<c>스키마.이름</c>). 있으면 일지 요약에 붙인다(설계 9장).</param>
    private async Task CaptureBeforeChangeAsync(TiberoSession session, SqlStatement stmt, string? backupName, CancellationToken ct)
    {
        if (stmt.ChangeTarget is not { } target || stmt.Kind != StatementKind.Ddl)
        {
            return;
        }

        try
        {
            var template = new JournalEntry
            {
                ProfileId = session.Profile.Id,
                ProfileName = session.Profile.Name,
                DbKey = session.DbKey,
                ServerName = session.Profile.DataSource,
                Cause = JournalCause.Ddl,
                StatementSummary = backupName is null
                    ? ChangeCapture.Summarize(stmt.Text)
                    : ChangeCapture.Summarize(stmt.Text) + " · 백업 테이블 " + backupName,
            };
            var fallbackOwner = (session.Server.SessionUser ?? session.Profile.User).ToUpperInvariant();
            var entries = await ChangeCapture.CaptureAsync(target, template, fallbackOwner, TiberoDdlSource.Instance, ChangeCapture.Timeout, ct)
                .ConfigureAwait(false);
            foreach (var entry in entries)
            {
                _events.Publish(new ChangeSnapshotCaptured(entry));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // 일지는 부가 기능이다. 어떤 이유로든 실행을 막지 않는다.
        }
    }

    /// <summary>
    /// OUT 값(R1b <see cref="TiberoValues.ReadOut"/>). REF CURSOR 리더는 결과 집합으로 감싸 <b>만드는 즉시</b> <paramref name="orphans"/> 에 올린다 —
    /// 뒤 OUT 값 읽기가 실패해도 앞 커서가 새지 않는다(잠금을 쥔 채라 여기서 결과 집합을 닫지 않는다: Dispose 가 잠금을 다시 잡는다).
    /// 감싸기 전 실패면 맨 리더는 잠금 아래에서 바로 닫는다. <see cref="TiberoResultSets.ReadOutValues"/> 와 같은 일을 하되 정리 순서를 지킨다.
    /// </summary>
    private static Dictionary<string, object?> ReadOutValues(TiberoSession session, IReadOnlyList<TiberoOutBind> outs, int pageSize, int lobFetchSize,
                                                             List<IAsyncDisposable> orphans)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (outs.Count == 0)
        {
            return values;
        }

        var driver = session.Driver ?? throw new InvalidOperationException("세션에 Tibero 드라이버 어댑터가 없어 OUT 값을 읽을 수 없습니다.");
        foreach (var o in outs)
        {
            var value = TiberoValues.ReadOut(driver, o.Parameter, o.Bind);
            if (value is DbDataReader cursor)
            {
                TiberoResultSet wrapped;
                try
                {
                    wrapped = TiberoResultSets.Create(session, null, cursor, pageSize, lobFetchSize);
                }
                catch
                {
                    cursor.Dispose();
                    throw;
                }

                orphans.Add(wrapped);
                value = wrapped;
            }

            values[o.Name] = value;
        }

        return values;
    }

    /// <summary>취소로 OUT 값을 못 읽은 채 끝날 때, 서버가 연 REF CURSOR 리더만 꺼내 닫는다(잠금 아래, 맨 리더라 안전). 실패는 삼킨다.</summary>
    private static async Task CloseCursorOutsAsync(TiberoSession session, IReadOnlyList<TiberoOutBind> outs)
    {
        if (session.Driver is not { } driver)
        {
            return;
        }

        foreach (var o in outs.Where(o => o.Bind.Type == BindDataType.RefCursor))
        {
            try
            {
                if (TiberoValues.ReadOut(driver, o.Parameter, o.Bind) is DbDataReader cursor)
                {
                    await SafeDisposeAsync(cursor).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or InvalidCastException
                                           || TiberoErrorClassifier.IsDriverException(ex))
            {
                // 닫지 못한 커서는 명령 Dispose 가 정리한다(드라이버). 취소 결과를 덮지 않는다.
            }
        }
    }

    /// <summary>
    /// 명령·리더·결과 집합을 닫는다. 닫기 실패(드라이버 오류·이미 닫힌 연결)가 이미 만든 결과를 덮지 않게 삼킨다 — Oracle 처럼 결과는 늘 돌려준다.
    /// 그 밖의 예외(프로그램 오류)는 그대로 낸다.
    /// </summary>
    internal static async ValueTask SafeDisposeAsync(IAsyncDisposable? resource)
    {
        if (resource is null)
        {
            return;
        }

        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException || TiberoErrorClassifier.IsDriverException(ex))
        {
            // 닫기 실패는 결과에 영향이 없다. 끊김이면 다음 실행이 세션 상태로 알게 된다.
        }
    }

    /// <summary>
    /// 세션 첫 실행 때 한 번 <c>DBMS_OUTPUT.ENABLE(NULL)</c>(<see cref="DbmsOutputEnabled"/> 일 때만), 읽기 전용 프로필이면 문장마다 읽기 전용 트랜잭션(<see cref="ReadOnlyBlock"/>).
    /// 자동 커밋은 여기서 끄지 않는다 — 세션이 연 직후 잡은 트랜잭션이 끈다(실측 1장).
    /// </summary>
    private async Task PrepareSessionAsync(TiberoSession session, SessionRuntime runtime, CancellationToken ct)
    {
        if (!runtime.Prepared)
        {
            if (DbmsOutputEnabled)
            {
                await ExecSqlAsync(session, TiberoDbmsOutput.EnableBlock, ct).ConfigureAwait(false);
            }

            runtime.Prepared = true;
        }

        if (session.Profile.ReadOnly)
        {
            // SAFE-02 이중 방어: 문장마다 새 읽기 전용 트랜잭션.
            await ExecSqlAsync(session, ReadOnlyBlock, ct).ConfigureAwait(false);
        }
    }

    /// <summary><c>DBMS_TRANSACTION.LOCAL_TRANSACTION_ID</c> 로 미커밋 상태를 갱신한다. 읽기 전용 프로필은 늘 false. 명령 잠금을 쥔 채 부른다.</summary>
    internal static async Task<bool> TrackTransactionAsync(TiberoSession session, SqlStatement stmt, StatementKind kind, CancellationToken ct) =>
        (await TrackTransactionIdAsync(session, stmt, kind, ct).ConfigureAwait(false)).Pending;

    /// <summary><see cref="TrackTransactionAsync"/> + 읽은 트랜잭션 id(<c>usn.slot.sqn</c>, 없거나 읽지 않았으면 null). 되돌리기 원장이 묶음에 남긴다(설계 7.1).</summary>
    internal static async Task<(bool Pending, string? TransactionId)> TrackTransactionIdAsync(TiberoSession session, SqlStatement stmt, StatementKind kind,
                                                                                            CancellationToken ct)
    {
        if (session.Profile.ReadOnly)
        {
            session.SetPending(false);
            return (false, null);
        }

        if (IsPlainQuery(stmt, kind))
        {
            return (session.HasPendingTransaction, null);
        }

        var cmd = session.CreateCommand(TransactionIdSql);
        await using (cmd.ConfigureAwait(false))
        {
            var id = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            // 드라이버 NULL 은 DBNull 이 아니라 IsNull 인 공급자 값일 수 있다(실측 3장) → 어댑터로 판정한다.
            var pending = session.Driver is { } driver ? !driver.IsProviderNull(id) : id is not null and not DBNull;
            session.SetPending(pending);
            var text = pending ? Convert.ToString(session.Driver is { } d ? d.ConvertProviderValue(id).Value : id, System.Globalization.CultureInfo.InvariantCulture) : null;
            return (pending, text);
        }
    }

    internal static async Task<bool> SafeTrackAfterFailureAsync(TiberoSession session, SqlStatement stmt, StatementKind kind)
    {
        if (session.State is SessionState.Broken or SessionState.Closed)
        {
            return false;
        }

        try
        {
            return await TrackTransactionAsync(session, stmt, kind == StatementKind.Query ? StatementKind.Dml : kind, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (TiberoErrorClassifier.IsDriverException(ex))
        {
            session.ReportFailure(ex);
            return session.HasPendingTransaction;
        }
    }

    /// <summary>
    /// 서버의 DBMS_OUTPUT 버퍼를 거두고 <see cref="DbmsOutputReceived"/> 를 낸다(명령 잠금을 쥔 채 부른다). 거두기는 결과를 깨지 않는다:
    /// 오류는 삼키고(끊김이면 세션에 알린다) 그때까지 거둔 줄만 돌려준다. 묶음 하나는 취소하지 않는다(GET_LINES 로 비운 줄을 잃지 않게) —
    /// <paramref name="ct"/> 는 묶음 <b>사이</b>에서만 본다: 출력이 아주 많을 때 취소하면 거둔 데까지 내고 나머지는 버퍼에 남는다.
    /// </summary>
    private async Task<IReadOnlyList<string>> SafeCollectAsync(TiberoSession session, SessionRuntime runtime, CancellationToken ct = default)
    {
        if (!DbmsOutputEnabled || !runtime.Prepared)
        {
            return [];
        }

        var lines = new List<string>();
        try
        {
            await CollectBatchesAsync(session, lines, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (TiberoErrorClassifier.IsDriverException(ex))
        {
            session.ReportFailure(ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or FormatException or InvalidCastException
                                       or NullReferenceException)
        {
            // 거두기 실패(바인드·값 읽기)는 문장 결과에 영향이 없다. 거둔 데까지만 낸다.
            // NullReferenceException = 드라이버 안 CLOB 읽기(로케이터가 빈 경우, win144 #20 실측) — 드라이버 예외 형식이 아니라 따로 받는다.
        }

        if (lines.Count > 0)
        {
            _events.Publish(new DbmsOutputReceived(session.Id, lines));
        }

        return lines;
    }

    /// <summary>한 번에 <see cref="TiberoDbmsOutput.Batch"/> 줄씩, 돌려받은 줄 수가 그보다 적을 때까지 거둔다. 묶음마다 새 명령(OUT 값이 남지 않게).</summary>
    private static async Task CollectBatchesAsync(TiberoSession session, List<string> lines, CancellationToken ct)
    {
        var driver = session.Driver ?? throw new InvalidOperationException("세션에 Tibero 드라이버 어댑터가 없어 DBMS_OUTPUT 을 거둘 수 없습니다.");
        var maxBind = new BindValue(BindDataType.Number, (decimal)TiberoDbmsOutput.Batch);
        var countBind = new BindValue(BindDataType.Number, null, BindDirection.Out);
        var packedBind = new BindValue(BindDataType.Clob, null, BindDirection.Out);

        while (true)
        {
            var cmd = session.CreateCommand(TiberoDbmsOutput.CollectBlock);
            int count;
            await using (cmd.ConfigureAwait(false))
            {
                cmd.Parameters.Add(TiberoBindBuilder.Create(driver, cmd, "max", maxBind));
                var pCount = TiberoBindBuilder.Create(driver, cmd, "n", countBind);
                cmd.Parameters.Add(pCount);
                var pPacked = TiberoBindBuilder.Create(driver, cmd, "r", packedBind);
                cmd.Parameters.Add(pPacked);

                await cmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);

                // 묶음 글자를 먼저 읽는다: GET_LINES 가 이미 버퍼를 비웠으니 줄 수 읽기가 실패해도 이 묶음의 줄은 남긴다.
                var batch = TiberoDbmsOutput.Decode(TiberoValues.ReadOut(driver, pPacked, packedBind) as string);
                lines.AddRange(batch);

                // 줄 수를 못 읽으면(NULL·알 수 없는 값) 풀어낸 줄 수로 판단한다 — 0 으로 보면 꽉 찬 묶음 뒤에서 멈춰 나머지가 다음 문장으로 간다.
                count = ToCount(TiberoValues.ReadOut(driver, pCount, countBind)) ?? batch.Count;
            }

            if (count < TiberoDbmsOutput.Batch || ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>OUT 로 받은 줄 수(NUMBER → decimal 또는 글자, 결정40). NULL·알 수 없는 값은 null.</summary>
    private static int? ToCount(object? value) => value switch
    {
        decimal d => (int)d,
        int i => i,
        long l => (int)l,
        string s when decimal.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => (int)d,
        _ => null,
    };

    private async Task<SqlStatement?> SubstituteAsync(ISession s, SqlStatement stmt, SessionRuntime? runtime, CancellationToken ct)
    {
        var defines = runtime?.Defines;

        // DEFINE 은 SQL*Plus 처리기가 기억한다. 같은 저장소를 봐야 「define d = 20」 뒤 &d 를 다시 묻지 않는다.
        var processor = _sqlPlus as IScriptDirectives;
        string? Known(string name) => processor?.GetDefine(s, name)
            ?? (defines is not null && defines.TryGetValue(name, out var d) ? d : null);
        var missing = stmt.SubstitutionNames.Where(n => Known(n) is null).ToList();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (missing.Count > 0)
        {
            var answers = await _interaction.AskSubstitutionsAsync(missing, ct).ConfigureAwait(false);
            if (answers is null)
            {
                return null;
            }

            foreach (var (k, v) in answers)
            {
                values[k] = v;
            }
        }

        var text = SubstitutionText.Apply(stmt.Text, name =>
            values.TryGetValue(name, out var v) ? v : Known(name),
            (name, value) =>
            {
                if (defines is not null)
                {
                    defines[name] = value; // && 는 세션 동안 기억한다.
                }

                processor?.SetDefine(s, name, value);
            });
        return stmt with { Text = text };
    }

    private static async Task ExecSqlAsync(TiberoSession session, string sql, CancellationToken ct)
    {
        var cmd = session.CreateCommand(sql);
        await using (cmd.ConfigureAwait(false))
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>취소: 토큰 취소, 또는 토큰이 취소된 뒤 드라이버가 낸 취소 오류(TBR-12040, 실측 4장).</summary>
    internal static bool IsCancellation(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException
        || (ct.IsCancellationRequested && TiberoErrorClassifier.ToDbError(ex, null) is { Kind: DbErrorKind.Cancelled });

    private static ExecutionResult Failed(SqlStatement stmt, string code, string message, PolicyDecision? applied) =>
        new(stmt, ExecutionStatus.Failed) { Error = new DbError(code, message) { Dbms = Dbms.Tibero }, Policy = applied };

    /// <summary>잠금 없는 질의(SELECT·WITH, FOR UPDATE 아님): 트랜잭션도 출력도 바꾸지 않는다고 보고 뒤따르는 숨은 문장을 보내지 않는다.</summary>
    private static bool IsPlainQuery(SqlStatement stmt, StatementKind kind) =>
        kind == StatementKind.Query && !ForUpdateRegex().IsMatch(stmt.Text);

    [GeneratedRegex(@"\bFOR\s+UPDATE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForUpdateRegex();

    /// <summary>세션별 실행 상태(준비 여부, && 치환 값). 실행 줄은 세션의 명령 잠금(<see cref="TiberoSession.CommandGate"/>)이다.</summary>
    internal sealed class SessionRuntime
    {
        public bool Prepared { get; set; }

        public Dictionary<string, string> Defines { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
