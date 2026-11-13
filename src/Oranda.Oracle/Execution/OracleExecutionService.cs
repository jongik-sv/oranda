using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;
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
using Oranda.Oracle.Sessions;
using EndScope = Oranda.Oracle.Sessions.OracleSession.EndScope;

namespace Oranda.Oracle.Execution;

/// <summary>
/// 실행 엔진(TRD 4.3, 4.9). 파이프라인: 치환 변수 → 정책 관문 → (SQL*Plus 명령이면 처리기) → 바인드 → 드라이버 실행
/// → 트랜잭션 추적(<c>LOCAL_TRANSACTION_ID</c>) → DBMS_OUTPUT 수거 → 이벤트. internal(TC-05). 소유: TSK-06-01.
/// <para>
/// 세션마다 실행을 한 줄로 세운다: 세션의 명령 잠금(<see cref="OracleSession.CommandGate"/>, D-041) 하나를 결과 집합의 페이지 가져오기·
/// 커밋·메타데이터·모니터 등 같은 연결을 쓰는 모든 기능이 함께 쓴다. 연결은 자동 커밋·파이프라이닝을 끈다.
/// </para>
/// </summary>
[OrandaService(typeof(IExecutionService), Dbms = Dbms.Oracle)]
internal sealed partial class OracleExecutionService : IExecutionService
{
    /// <summary>ORA-01013: 사용자가 현재 작업을 취소함.</summary>
    internal const int CancelledNumber = 1013;

    private readonly IExecutionPolicy _policy;
    private readonly IExecutionInteraction _interaction;
    private readonly IEventBus _events;
    private readonly ISqlPlusCommandProcessor? _sqlPlus;
    private readonly IGridFetchSettings? _grid;
    private readonly IDmlAnalyzer _analyzer;
    private readonly ConditionalWeakTable<OracleSession, SessionRuntime> _runtimes = new();

    public OracleExecutionService(IExecutionPolicy policy, IExecutionInteraction interaction, IEventBus events,
                                  ISqlPlusCommandProcessor sqlPlus, IDmlAnalyzer? analyzer = null)
        : this(policy, interaction, events, sqlPlus, pageSize: 500, analyzer: analyzer)
    {
    }

    internal OracleExecutionService(IExecutionPolicy policy, IExecutionInteraction interaction, IEventBus events,
                                    ISqlPlusCommandProcessor? sqlPlus, int pageSize = 500, bool dbmsOutput = true,
                                    IGridFetchSettings? grid = null, IDmlAnalyzer? analyzer = null)
    {
        _analyzer = analyzer ?? NullDmlAnalyzer.Instance;
        _policy = policy;
        _interaction = interaction;
        _events = events;
        _sqlPlus = sqlPlus;
        PageSize = pageSize;
        DbmsOutputEnabled = dbmsOutput;
        _grid = grid;
    }

    /// <summary>생성할 때 받은 결과 페이지 크기(설정 서비스가 없을 때의 값). 실행할 때는 <see cref="CurrentPageSize"/>.</summary>
    internal int PageSize { get; }

    /// <summary>결과 페이지 크기(<c>grid.pageSize</c>). FetchSize = RowSize × 이 값. 설정을 바꾸면 다음 실행부터 따라간다.</summary>
    internal int CurrentPageSize => _grid is { PageSize: >= 1 } g ? g.PageSize : PageSize;

    /// <summary>LOB 미리 읽기 크기(<c>grid.initialLobFetchSize</c>). 설정이 없으면 기본 4000.</summary>
    internal int CurrentLobFetchSize => _grid is { InitialLobFetchSize: >= 0 } g ? g.InitialLobFetchSize : OracleValues.LobPreviewSize;

    /// <summary><c>execution.dbmsOutput</c>. 켜면 세션 첫 실행 때 <c>DBMS_OUTPUT.ENABLE(NULL)</c>.</summary>
    internal bool DbmsOutputEnabled { get; }

    public async Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds,
                                                    ExecutionOptions? options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(stmt);
        binds ??= new Dictionary<string, BindValue>();
        options ??= ExecutionOptions.Default;

        var result = await RunAsync(s, stmt, binds, options, ct);
        _events.Publish(new StatementExecuted(s.Id, result.Statement, result, options));
        return result;
    }

    public async IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts,
                                                                      ScriptErrorMode mode, ExecutionOptions? options,
                                                                      [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(stmts);
        options ??= new ExecutionOptions { Source = HistorySource.Script };
        var empty = new Dictionary<string, BindValue>();

        foreach (var stmt in stmts)
        {
            // 바인드 이름이 없는 문장에 값을 넘기면 BindBuilder 가 받은 값을 모두 붙인다(ORA-01036). 그 문장에는 빈 사전.
            var binds = options.Binds is { } all && stmt.BindNames.Count > 0 ? all : empty;
            var result = await ExecuteAsync(s, stmt, binds, options, ct);
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
                    await directives.ApplyOnErrorTransactionAsync(s, ct);
                    yield break;
                }

                if (mode == ScriptErrorMode.Stop)
                {
                    yield break;
                }

                if (mode == ScriptErrorMode.Ask)
                {
                    var failure = result.Error ?? new DbError("POLICY", result.Policy?.Message ?? "정책 관문이 거부했습니다.");
                    var action = await _interaction.AskScriptErrorAsync(stmt, failure, ct);
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

        var runtime = s is OracleSession os ? _runtimes.GetValue(os, _ => new SessionRuntime()) : null;

        // 1. 치환 변수
        if (stmt.SubstitutionNames.Count > 0)
        {
            var substituted = await SubstituteAsync(s, stmt, runtime, ct);
            if (substituted is null)
            {
                return new ExecutionResult(stmt, ExecutionStatus.Cancelled);
            }

            stmt = substituted;
        }

        // 2. 확인 창 제안(명령 잠금 밖): 원복 스크립트(설계 6.1 의 2단계)와 데이터가 사라지는 DDL 앞 백업 테이블(RB-44).
        var plan = RollbackPlan.None;
        var extras = ConfirmExtras.None;
        if (s is OracleSession prepared && stmt.Kind != StatementKind.SqlPlus)
        {
            try
            {
                plan = await PrepareRollbackQuietlyAsync(prepared, stmt, binds, options, ct);
                extras = plan.Extras;
                if (await OfferBackupAsync(prepared, stmt, ct) is { } backup)
                {
                    extras = extras with { Backup = backup };
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new ExecutionResult(stmt, ExecutionStatus.Cancelled);
            }
        }

        // 3. 정책 관문. 거부면 드라이버를 부르지 않는다(SAFE-07: 문자열 자체를 보내지 않음). 미리 받은 확인은 치환 뒤 텍스트로 비교한다.
        var gate = await PolicyGate.DecideAsync(_policy, _interaction, _events, s, stmt, options, extras, ct);
        switch (gate.Outcome)
        {
            case PolicyGateOutcome.Denied:
                return new ExecutionResult(stmt, ExecutionStatus.Denied) { Policy = gate.Decision };
            case PolicyGateOutcome.Cancelled:
                return new ExecutionResult(stmt, ExecutionStatus.Cancelled) { Policy = gate.Decision };
        }

        var applied = gate.Applied;

        // 3. SQL*Plus 명령
        if (stmt.Kind == StatementKind.SqlPlus)
        {
            if (_sqlPlus is null)
            {
                return Failed(stmt, "ORANDA-SQLPLUS", "SQL*Plus 명령 처리기가 없습니다.", applied);
            }

            var r = await _sqlPlus.ExecuteAsync(s, stmt, ct);
            return applied is null ? r : r with { Policy = applied };
        }

        if (s is not OracleSession session || runtime is null)
        {
            throw new ArgumentException("Oranda 세션 관리자가 연 세션이 아닙니다.", nameof(s));
        }

        if (session.State is SessionState.Broken or SessionState.Closed)
        {
            return Failed(stmt, "ORANDA-SESSION", $"세션이 {session.State} 상태입니다. 다시 접속하세요.", applied);
        }

        // 세션 연결의 명령 잠금(D-041): 메타데이터·모니터·커밋 등 같은 연결을 쓰는 다른 기능과 한 줄로 선다.
        // 실행 안에서는 연결에 직접 보낸다(서비스 API 를 다시 부르면 같은 잠금을 다시 잡아 교착이다).
        try
        {
            await session.CommandGate.EnterAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return new ExecutionResult(stmt, ExecutionStatus.Cancelled) { Policy = applied };
        }

        session.SetBusy(true);
        try
        {
            return await RunOnDriverAsync(session, runtime, stmt, binds, applied, gate.Answer, extras, plan, ct);
        }
        finally
        {
            session.SetBusy(false);
            session.CommandGate.Release();
        }
    }

    private async Task<ExecutionResult> RunOnDriverAsync(OracleSession session, SessionRuntime runtime, SqlStatement stmt,
                                                         IReadOnlyDictionary<string, BindValue> binds, PolicyDecision? applied,
                                                         ConfirmAnswer answer, ConfirmExtras extras, RollbackPlan plan, CancellationToken ct)
    {
        var conn = session.Connection;
        var kind = ExecutionPolicy.EffectiveKind(stmt);
        var sw = Stopwatch.StartNew();
        OracleCommand? cmd = null;
        OracleDataReader? reader = null;
        // 확인 창이 고른 백업(RB-44). 이 실행의 제안에서 나온 글만 보낸다(정책 관문을 지나지 않는 글이다).
        var backupSql = stmt.Kind == StatementKind.Ddl && IsOfferedBackup(extras.Backup, answer.BackupSql) ? answer.BackupSql : null;
        RollbackStatementSummary? backupLog = null;
        IStatementCapture? capture = null; // 엔진이 이 문장을 맡았을 때만(시작 단계가 저장점·잠금 읽기를 보냈을 수 있다)
        RollbackStatementSummary? completed = null;
        EndScope? scope = null;
        var pendingBefore = session.HasPendingTransaction;
        var sent = false; // 사용자 문장을 드라이버로 보내기 시작했다(그 뒤에만 트랜잭션 끝을 따진다)
        var executed = false; // 사용자 문장이 드라이버에서 끝났다(그 뒤 실패는 Abandon 이 아니라 Complete, 끝 판정은 성공으로)
        var endReported = false; // 이 문장의 트랜잭션 끝을 이미 따졌다(실패 경로에서 다시 알리지 않는다)
        long? doneRows = null;
        Dictionary<string, object?>? doneOut = null;
        try
        {
            await PrepareSessionAsync(session, runtime, ct);
            if (backupSql is not null)
            {
                if (await CreateBackupAsync(session, stmt, backupSql, applied, ct) is { } backupFailed)
                {
                    return backupFailed with { Elapsed = sw.Elapsed };
                }

                backupLog = BackupSummary(backupSql); // 원래 DDL 이 실패·취소돼도 백업 테이블이 남았음을 알린다
            }

            await CaptureBeforeChangeAsync(session, stmt, BackupPlanner.BackupNameOf(backupSql), ct);

            // 되돌리기(6.1 의 5단계·7.4): 끝낼 수 있는 문장이면 열린 묶음 저장과 끝 알림 범위, 그다음 시점 읽기·내부 저장점·잠금 읽기.
            pendingBefore = session.HasPendingTransaction;
            if (RollbackFlow.CanEndTransaction(stmt))
            {
                scope = session.BeginEndScope();
                await session.Rollback.BeforeEndAsync(session, ct);
            }

            capture = await BeginRollbackAsync(session, plan, answer, ct);
            var send = capture?.StatementToSend ?? stmt;
            var sendBinds = capture?.BindsToSend ?? binds;

            cmd = conn.CreateCommand();
            cmd.BindByName = true;
            cmd.CommandText = send.Text;
            var parameters = BindBuilder.Build(cmd, send, sendBinds);
            sent = true;
            var pageSize = CurrentPageSize; // 실행마다 한 번 읽는다: 한 결과 안에서 페이지 크기가 바뀌지 않게
            var lobFetchSize = CurrentLobFetchSize;

            IResultSet? resultSet = null;
            long? rows = null;
            using (cmd.CancelOnToken(ct))
            {
                // 질의와 분류가 불확실한 문장은 리더로 받는다(결과가 있으면 결과 집합).
                if (stmt.Kind == StatementKind.Query || stmt.IsClassificationUncertain)
                {
                    cmd.InitialLOBFetchSize = lobFetchSize; // 이 크기까지의 LOB 은 행과 함께 와서 미리보기에 왕복이 더 들지 않는다
                    reader = (OracleDataReader)await cmd.ExecuteReaderAsync(ct);
                    if (reader.FieldCount > 0)
                    {
                        resultSet = new OracleResultSet(session, cmd, reader, pageSize, lobFetchSize);
                        reader = null;
                        cmd = null; // 결과 집합이 소유한다.
                    }
                    else
                    {
                        rows = reader.RecordsAffected >= 0 ? reader.RecordsAffected : null;
                    }
                }
                else
                {
                    var n = await cmd.ExecuteNonQueryAsync(ct);
                    rows = kind == StatementKind.Dml && n >= 0 ? n : null;
                }

                executed = true;
                doneRows = rows;

                ct.ThrowIfCancellationRequested();
            }

            var allOut = parameters.Count == 0
                ? new Dictionary<string, object?>()
                : OracleValues.ReadOutValues(session, parameters, pageSize, lobFetchSize);
            doneOut = allOut;
            var outValues = UserOutValues(allOut, stmt);

            sw.Stop();
            var (pending, transactionId) = await TrackTransactionIdAsync(session, stmt, kind, ct);
            // 6.1 의 8단계: 행 수 대조·직후 값 읽기·원장 기록. 문장은 이미 성공했으므로 취소하지 않는다(엔진은 실패를 불완전으로 담는다).
            if (capture is not null)
            {
                completed = await CompleteQuietlyAsync(capture, rows, allOut, transactionId);
                capture = null; // 한 번만 마무리한다(뒤 단계가 실패해도 다시 부르지 않는다)
            }

            endReported = true;
            await AfterStatementAsync(session, stmt, succeeded: true, pendingBefore, pending);
            scope?.Reported();
            // 질의 뒤에는 숨은 문장(GET_LINES)을 보내지 않는다: 세션의 직전 커서가 바뀌면 다음 문장의
            // dbms_xplan.display_cursor(sql_id => null) 가 사용자 질의 대신 그 문장을 보인다(TSK-06-14).
            // 질의 안 함수가 낸 출력은 버퍼에 남아 다음 질의 아닌 문장 뒤에 함께 나온다.
            IReadOnlyList<string> output = IsPlainQuery(stmt, kind) ? [] : await CollectDbmsOutputAsync(session, runtime, ct);

            if (kind == StatementKind.Ddl)
            {
                _events.Publish(new MetadataInvalidated(session.DbKey, []));
            }

            return new ExecutionResult(stmt, ExecutionStatus.Succeeded)
            {
                Elapsed = sw.Elapsed,
                RowsAffected = rows,
                ResultSet = resultSet,
                Policy = applied,
                DbmsOutput = output,
                OutValues = outValues,
                TransactionPending = pending,
                Rollback = backupLog ?? completed,
            };
        }
        catch (Exception ex) when (IsCancellation(ex, ct))
        {
            var finished = await FinishAfterFailureAsync(session, capture, executed, doneRows, doneOut, null,
                new DbError("ORA-01013", "사용자가 취소했습니다.") { Kind = DbErrorKind.Cancelled });
            var pending = await SafeTrackAfterFailureAsync(session, stmt, kind);
            await EndAfterFailureAsync(session, stmt, sent && !endReported, executed, pendingBefore, pending, scope);
            return new ExecutionResult(stmt, ExecutionStatus.Cancelled)
            {
                Elapsed = sw.Elapsed, Policy = applied, TransactionPending = pending, Rollback = backupLog ?? completed ?? finished,
            };
        }
        catch (OracleException ex)
        {
            session.ReportFailure(ex);
            var error = ToDbError(ex, stmt.Text);
            var finished = await FinishAfterFailureAsync(session, capture, executed, doneRows, doneOut, null, error);
            var pending = await SafeTrackAfterFailureAsync(session, stmt, kind);
            await EndAfterFailureAsync(session, stmt, sent && !endReported, executed, pendingBefore, pending, scope);
            var output = session.State is SessionState.Broken ? [] : await SafeCollectAsync(session, runtime);
            return new ExecutionResult(stmt, ExecutionStatus.Failed)
            {
                Elapsed = sw.Elapsed,
                Error = error,
                Policy = applied,
                DbmsOutput = output,
                TransactionPending = pending,
                Rollback = backupLog ?? completed ?? finished,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or FormatException or InvalidCastException)
        {
            var error = new DbError("ORANDA-EXEC", ex.Message);
            var began = capture is not null; // 시작 단계가 저장점·잠금 읽기로 트랜잭션을 열었을 수 있다
            var finished = await FinishAfterFailureAsync(session, capture, executed, doneRows, doneOut, null, error);
            var pending = sent || began ? await SafeTrackAfterFailureAsync(session, stmt, kind) : session.HasPendingTransaction;
            await EndAfterFailureAsync(session, stmt, sent && !endReported, executed, pendingBefore, pending, scope);
            return Failed(stmt, "ORANDA-EXEC", ex.Message, applied) with
            {
                Elapsed = sw.Elapsed, TransactionPending = pending, Rollback = backupLog ?? completed ?? finished,
            };
        }
        finally
        {
            scope?.Dispose();
            if (reader is not null)
            {
                await reader.DisposeAsync();
            }

            if (cmd is not null)
            {
                await cmd.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// 변경 일지(TSK-13-07): ALTER·DROP·CREATE OR REPLACE·RENAME 를 보내기 직전에 대상의 현재 DDL 을 같은 세션에서 읽어
    /// <see cref="ChangeSnapshotCaptured"/> 로 알린다. 정책·확인을 통과한 뒤에 부르므로 거부된 문장은 남지 않는다.
    /// DBMS_METADATA 조회는 SELECT 뿐이다(세션 변환 설정·COMMIT·ROLLBACK 없음). 그래서 사용자 트랜잭션에 영향이 없고,
    /// 뒤이은 DDL 의 암시적 커밋과도 섞이지 않는다. 보관 실패(권한·시간 초과·객체 없음)는 일지에 이유만 남기고 실행은 계속한다.
    /// 사용자의 취소만 밖으로 전파한다.
    /// </summary>
    /// <param name="backupName">이 DDL 앞에서 만든 백업 테이블(<c>스키마.이름</c>). 있으면 일지 요약에 붙인다(설계 9장).</param>
    private async Task CaptureBeforeChangeAsync(OracleSession session, SqlStatement stmt, string? backupName, CancellationToken ct)
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
            var entries = await ChangeCapture.CaptureAsync(target, template, fallbackOwner, new OracleDdlSource(session.Connection),
                ChangeCapture.Timeout, ct);
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

    /// <summary>연결 설정(자동 커밋 끔·파이프라이닝 끔·DBMS_OUTPUT 켬)과 읽기 전용 트랜잭션.</summary>
    private async Task PrepareSessionAsync(OracleSession session, SessionRuntime runtime, CancellationToken ct)
    {
        var conn = session.Connection;
        if (!runtime.Prepared)
        {
            conn.AutoCommit = false; // SAFE-04: 수동 커밋 기본
            conn.Pipelining = false;
            if (DbmsOutputEnabled)
            {
                await ExecSqlAsync(conn, "BEGIN DBMS_OUTPUT.ENABLE(NULL); END;", ct);
            }

            runtime.Prepared = true;
        }

        if (session.Profile.ReadOnly)
        {
            // SAFE-02 이중 방어: 문장마다 새 읽기 전용 트랜잭션(이전 스냅숏을 끌고 가지 않게 먼저 끝낸다).
            await ExecSqlAsync(conn, "BEGIN ROLLBACK; DBMS_TRANSACTION.READ_ONLY; END;", ct);
        }
    }

    /// <summary>DBMS_TRANSACTION.LOCAL_TRANSACTION_ID 로 미커밋 상태를 갱신한다. 읽기 전용 프로필은 늘 false.</summary>
    internal static async Task<bool> TrackTransactionAsync(OracleSession session, SqlStatement stmt, StatementKind kind, CancellationToken ct) =>
        (await TrackTransactionIdAsync(session, stmt, kind, ct)).Pending;

    /// <summary><see cref="TrackTransactionAsync"/> 와 같고, 읽은 트랜잭션 id(<c>usn.slot.sqn</c>, 없으면 null)도 돌려준다(원장 묶음, 설계 7.1).</summary>
    internal static async Task<(bool Pending, string? TransactionId)> TrackTransactionIdAsync(OracleSession session, SqlStatement stmt,
                                                                                          StatementKind kind, CancellationToken ct)
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

        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = "SELECT DBMS_TRANSACTION.LOCAL_TRANSACTION_ID FROM dual";
        var id = OracleValues.ToText(await cmd.ExecuteScalarAsync(ct));
        var pending = id is not null;
        session.SetPending(pending);
        return (pending, id);
    }

    /// <summary>
    /// 실패·취소한 문장 뒤의 트랜잭션 끝(실패한 DDL 앞 암시적 커밋 등, 7.3). 드라이버로 보내지 않았거나 이미 따진 문장은 건너뛴다.
    /// 드라이버에서 끝난 뒤의 실패(OUT 값 읽기 등)는 성공한 문장으로 따진다(COMMIT·ROLLBACK TO·SAVEPOINT 가 제 종류대로 남게).
    /// 끝 알림 범위가 없는 문장(DML 등)이 끊기면 세션이 이미 끊김 끝을 알렸으므로 다시 알리지 않는다.
    /// </summary>
    private static async Task EndAfterFailureAsync(OracleSession session, SqlStatement stmt, bool due, bool executed, bool pendingBefore,
                                                   bool pendingAfter, EndScope? scope)
    {
        if (!due || (scope is null && session.State is SessionState.Broken or SessionState.Closed))
        {
            return;
        }

        await AfterStatementAsync(session, stmt, succeeded: executed, pendingBefore, pendingAfter);
        scope?.Reported();
    }

    internal static async Task<bool> SafeTrackAfterFailureAsync(OracleSession session, SqlStatement stmt, StatementKind kind)
    {
        if (session.State is SessionState.Broken or SessionState.Closed)
        {
            return false;
        }

        try
        {
            return await TrackTransactionAsync(session, stmt, kind == StatementKind.Query ? StatementKind.Dml : kind, CancellationToken.None);
        }
        catch (OracleException ex)
        {
            session.ReportFailure(ex);
            return session.HasPendingTransaction;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return session.HasPendingTransaction; // 연결이 닫혔다: 마지막으로 안 상태
        }
    }

    /// <summary>DBMS_OUTPUT.GET_LINES 로 한 번에 거둔다(최대 1,000줄씩 반복).</summary>
    private async Task<IReadOnlyList<string>> CollectDbmsOutputAsync(OracleSession session, SessionRuntime runtime, CancellationToken ct)
    {
        if (!DbmsOutputEnabled || !runtime.Prepared)
        {
            return [];
        }

        const int batch = 1000;
        var lines = new List<string>();
        await using var cmd = session.Connection.CreateCommand();
        cmd.BindByName = true;
        cmd.CommandText = "BEGIN DBMS_OUTPUT.GET_LINES(:lines, :n); END;";
        var pLines = cmd.Parameters.Add("lines", OracleDbType.Varchar2, System.Data.ParameterDirection.Output);
        pLines.CollectionType = OracleCollectionType.PLSQLAssociativeArray;
        pLines.Size = batch;
        pLines.ArrayBindSize = Enumerable.Repeat(32767, batch).ToArray();
        var pCount = cmd.Parameters.Add("n", OracleDbType.Int32, batch, System.Data.ParameterDirection.InputOutput);

        while (true)
        {
            pCount.Value = batch;
            await cmd.ExecuteNonQueryAsync(ct);
            var count = OracleValues.ToInt(pCount.Value);
            if (count > 0 && pLines.Value is Array arr)
            {
                for (var i = 0; i < count && i < arr.Length; i++)
                {
                    lines.Add(OracleValues.ToText(arr.GetValue(i)) ?? string.Empty);
                }
            }

            if (count < batch)
            {
                break;
            }
        }

        if (lines.Count > 0)
        {
            _events.Publish(new DbmsOutputReceived(session.Id, lines));
        }

        return lines;
    }

    private async Task<IReadOnlyList<string>> SafeCollectAsync(OracleSession session, SessionRuntime runtime)
    {
        try
        {
            return await CollectDbmsOutputAsync(session, runtime, CancellationToken.None);
        }
        catch (OracleException)
        {
            return [];
        }
    }

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
            var answers = await _interaction.AskSubstitutionsAsync(missing, ct);
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
        return stmt with { Text = text, Dml = null }; // 치환한 글은 만든 쪽이 준 모양과 다를 수 있다: 다시 분석한다(설계 13.3)
    }

    private static async Task ExecSqlAsync(OracleConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    internal static bool IsCancellation(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException
        || (ex is OracleException oe && oe.Number == CancelledNumber && ct.IsCancellationRequested);

    private static ExecutionResult Failed(SqlStatement stmt, string code, string message, PolicyDecision? applied) =>
        new(stmt, ExecutionStatus.Failed) { Error = new DbError(code, message), Policy = applied };

    /// <summary>ORA 오류 → <see cref="DbError"/>(문장 안 위치·줄·열 포함).</summary>
    internal static DbError ToDbError(OracleException ex, string text)
    {
        int? offset = null;
        if (ex.Errors.Count > 0 && ex.Errors[0].ParseErrorOffset is var o and > 0 && o <= text.Length)
        {
            offset = o;
        }

        int? line = null, column = null;
        if (offset is { } off)
        {
            var before = text.AsSpan(0, off);
            line = before.Count('\n') + 1;
            column = off - (before.LastIndexOf('\n') + 1) + 1;
        }

        return OracleErrorClassifier.ToDbError(ex) with { Offset = offset, Line = line, Column = column };
    }

    /// <summary>잠금 없는 질의(SELECT·WITH, FOR UPDATE 아님): 트랜잭션도 출력도 바꾸지 않는다고 보고 뒤따르는 숨은 문장을 보내지 않는다.</summary>
    private static bool IsPlainQuery(SqlStatement stmt, StatementKind kind) =>
        kind == StatementKind.Query && !ForUpdateRegex().IsMatch(stmt.Text);

    [GeneratedRegex(@"\bFOR\s+UPDATE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForUpdateRegex();

    /// <summary>세션별 실행 상태(준비 여부, && 치환 값). 실행 줄은 세션의 명령 잠금(<see cref="OracleSession.CommandGate"/>)이다.</summary>
    internal sealed class SessionRuntime
    {
        public bool Prepared { get; set; }

        public Dictionary<string, string> Defines { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 같은 세션의 연결로 DBMS_METADATA·딕셔너리를 읽는다(<see cref="ChangeCapture"/> 의 Oracle 원천). 시간 제한은 토큰 취소(드라이버 Break)와 명령 제한 시간 둘 다 건다.
    /// </summary>
    internal sealed class OracleDdlSource(OracleConnection connection) : IDdlSource
    {
        /// <summary>ORA-31603: 객체가 없다.</summary>
        internal const int ObjectNotFound = 31603;

        /// <summary>객체는 있으나 DDL 을 읽을 수 없다(ORA-31603 + ALL_OBJECTS 에 있음). 실패 행으로 남긴다. 번호는 내부 표지로 ORA-01031 을 빌린다.</summary>
        internal const int InsufficientPrivileges = 1031;

        private const string DefaultOwner = "NVL(:owner, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA'))"; // ResolveTypeAsync 용

        public int ObjectNotFoundNumber => ObjectNotFound;

        public string DescribeError(int number, string? message) => $"ORA-{number:D5} {message}".TrimEnd();

        public async Task<string?> ResolveTypeAsync(string? owner, string name, CancellationToken ct)
        {
            try
            {
                await using var cmd = Command();
                cmd.CommandText = $"""
                    SELECT object_type FROM (
                      SELECT object_type FROM all_objects
                       WHERE owner = {DefaultOwner} AND object_name = :name
                         AND object_type IN ('TABLE', 'VIEW', 'SEQUENCE', 'SYNONYM', 'MATERIALIZED VIEW')
                       ORDER BY DECODE(object_type, 'MATERIALIZED VIEW', 0, 'TABLE', 1, 'VIEW', 2, 3))
                     WHERE ROWNUM = 1
                    """;
                Bind(cmd, owner, name);
                using (cmd.CancelOnToken(ct))
                {
                    return OracleValues.ToText(await cmd.ExecuteScalarAsync(ct));
                }
            }
            catch (OracleException)
            {
                return null;
            }
        }

        /// <summary>
        /// 핸들(<c>DBMS_METADATA.OPEN</c>)에 변환 설정을 직접 걸어 읽는다: 사용자가 DDL 보기에서 세션 변환(저장소·테이블스페이스 등)을
        /// 바꿔 두었어도 보관본이 달라지지 않는다(같으면 해시가 같아 중복이 쌓이지 않는다). 끝맺음(<c>SQLTERMINATOR</c>)은 켠다.
        /// DDL 이 없으면(ORA-31603 예외, 본문 없는 패키지는 NULL) ALL_OBJECTS 로 「객체 없음」과 「있으나 읽을 수 없음」을 가른다(실 DB 확인: 없는 표·SYS.DUAL 모두 ORA-31603).
        /// </summary>
        private const string ReadBlock = """
            DECLARE
              h NUMBER;
              th NUMBER;
              l_open BOOLEAN := TRUE;
              l_owner VARCHAR2(128) := NVL(:owner, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA'));
              l_ddl CLOB;
              l_exists NUMBER := 0;
              l_type VARCHAR2(40) := CASE :type WHEN 'PACKAGE_SPEC' THEN 'PACKAGE' WHEN 'TYPE_SPEC' THEN 'TYPE' ELSE REPLACE(:type, '_', ' ') END;
            BEGIN
              h := DBMS_METADATA.OPEN(:type);
              BEGIN
                DBMS_METADATA.SET_FILTER(h, 'SCHEMA', l_owner);
                DBMS_METADATA.SET_FILTER(h, 'NAME', :name);
                th := DBMS_METADATA.ADD_TRANSFORM(h, 'DDL');
                DBMS_METADATA.SET_TRANSFORM_PARAM(th, 'SQLTERMINATOR', TRUE);
                l_ddl := DBMS_METADATA.FETCH_CLOB(h);
              EXCEPTION
                WHEN OTHERS THEN
                  BEGIN
                    DBMS_METADATA.CLOSE(h);
                  EXCEPTION
                    WHEN OTHERS THEN NULL;
                  END;
                  l_open := FALSE;
                  -- 없는 객체(와 볼 수 없는 SYS 객체)는 NULL 이 아니라 ORA-31603 예외로 온다. 그 밖의 오류는 그대로 올린다.
                  IF SQLCODE <> -31603 THEN
                    RAISE;
                  END IF;
              END;
              IF l_open THEN
                DBMS_METADATA.CLOSE(h);
              END IF;
              IF l_ddl IS NULL AND :type <> 'DB_LINK' THEN
                SELECT COUNT(*) INTO l_exists FROM all_objects WHERE owner = l_owner AND object_name = :name AND object_type = l_type;
              END IF;
              :out_owner := l_owner;
              :out_exists := l_exists;
              :out_ddl := l_ddl;
            END;
            """;

        public async Task<DdlRead> ReadAsync(string getDdlType, string? owner, string name, CancellationToken ct)
        {
            try
            {
                await using var cmd = Command();
                cmd.CommandText = ReadBlock;
                cmd.Parameters.Add(new OracleParameter("owner", OracleDbType.Varchar2) { Value = (object?)owner ?? DBNull.Value });
                cmd.Parameters.Add(new OracleParameter("type", OracleDbType.Varchar2) { Value = getDdlType });
                cmd.Parameters.Add(new OracleParameter("name", OracleDbType.Varchar2) { Value = name });
                var outOwner = cmd.Parameters.Add("out_owner", OracleDbType.Varchar2, 128, null, System.Data.ParameterDirection.Output);
                var outExists = cmd.Parameters.Add("out_exists", OracleDbType.Int32, null, System.Data.ParameterDirection.Output);
                var outDdl = cmd.Parameters.Add("out_ddl", OracleDbType.Clob, null, System.Data.ParameterDirection.Output);
                using (cmd.CancelOnToken(ct))
                {
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                var ownerText = OracleValues.ToText(outOwner.Value);
                var ddl = OracleValues.ToText(outDdl.Value);
                if (!string.IsNullOrEmpty(ddl))
                {
                    return new DdlRead(ddl, ownerText);
                }

                return OracleValues.ToInt(outExists.Value) > 0
                    ? new DdlRead(null, ownerText, InsufficientPrivileges, "ORA-31603: 객체는 있으나 DDL 을 읽을 수 없다(권한 부족 등)")
                    : new DdlRead(null, ownerText, ObjectNotFound, "ORA-31603: 객체를 찾지 못했다");
            }
            catch (OracleException ex)
            {
                return new DdlRead(null, null, ex.Number, FirstLine(ex.Message));
            }
        }

        private OracleCommand Command()
        {
            var cmd = connection.CreateCommand();
            cmd.BindByName = true;
            cmd.CommandTimeout = (int)ChangeCapture.Timeout.TotalSeconds;
            return cmd;
        }

        private static void Bind(OracleCommand cmd, string? owner, string name)
        {
            cmd.Parameters.Add(new OracleParameter("owner", OracleDbType.Varchar2) { Value = (object?)owner ?? DBNull.Value });
            cmd.Parameters.Add(new OracleParameter("name", OracleDbType.Varchar2) { Value = name });
        }

        private static string FirstLine(string message)
        {
            var line = message.Split('\n')[0].Trim();
            return line.Length <= 300 ? line : line[..300];
        }
    }
}
