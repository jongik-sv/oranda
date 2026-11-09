using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Execution.Providers;
using Oranda.Core.Sessions;
using Oranda.Tibero.Driver;
using Oranda.Tibero.Results;
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
/// 아직 없는 것(뒤 항목): 질의·결과 집합과 스크립트 실행(E4), 취소·시간 제한 세부(E2), 변경 일지(E5).
/// 등록(<c>[OrandaService(typeof(IExecutionService), Dbms = Dbms.Tibero)]</c>)은 질의 경로(E4)가 붙은 뒤에 단다 — 그 전에는 Tibero 세션 실행이
/// 디스패처에서 「구현 없음」(<see cref="DbmsNotSupportedException"/>)으로 끝난다.
/// </para>
/// </summary>
internal sealed partial class TiberoExecutionService : IExecutionService
{
    /// <summary>아직 지원하지 않는 실행(질의·결과 집합 등)의 앱 오류 코드.</summary>
    internal const string NotSupportedCode = "ORANDA-NOTSUPPORTED";

    /// <summary>
    /// 읽기 전용 프로필의 문장마다 보내는 블록. 최상위 <c>SET TRANSACTION READ ONLY</c> 는 TBR-7001 이고 <c>DBMS_TRANSACTION.READ_ONLY</c> 는 없다
    /// → PSM 블록 안에서만 된다(실측 2장). 앞 트랜잭션을 먼저 끝내 이전 스냅숏을 끌고 가지 않는다.
    /// </summary>
    internal const string ReadOnlyBlock = "BEGIN ROLLBACK; SET TRANSACTION READ ONLY; END;";

    /// <summary>미커밋 판정 문장. 트랜잭션이 없으면 NULL(실측 2장, Oracle 과 같은 문장).</summary>
    internal const string TransactionIdSql = "SELECT DBMS_TRANSACTION.LOCAL_TRANSACTION_ID FROM dual";

    private readonly IExecutionPolicy _policy;
    private readonly IExecutionInteraction _interaction;
    private readonly IEventBus _events;
    private readonly ISqlPlusCommandProcessor? _sqlPlus;
    private readonly ConditionalWeakTable<TiberoSession, SessionRuntime> _runtimes = new();

    public TiberoExecutionService(IExecutionPolicy policy, IExecutionInteraction interaction, IEventBus events,
                                  ISqlPlusCommandProcessor sqlPlus)
        : this(policy, interaction, events, sqlPlus, dbmsOutput: true)
    {
    }

    internal TiberoExecutionService(IExecutionPolicy policy, IExecutionInteraction interaction, IEventBus events,
                                    ISqlPlusCommandProcessor? sqlPlus, bool dbmsOutput = true)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(events);
        _policy = policy;
        _interaction = interaction;
        _events = events;
        _sqlPlus = sqlPlus;
        DbmsOutputEnabled = dbmsOutput;
    }

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
    /// E4: 스크립트 실행은 질의 경로(결과 집합, tb-result R2)와 함께 붙인다. 그 전에는 「구현 없음」으로 던진다
    /// (디스패처가 열거 시작으로 미루므로 소비자에게는 <c>await foreach</c> 안에서 나온다).
    /// </summary>
    public IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode,
                                                                ExecutionOptions? options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(stmts);
        throw new DbmsNotSupportedException(Dbms.Tibero, typeof(IExecutionService)); // E4
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

            stmt = substituted;
        }

        // 2. 정책 관문. 거부면 드라이버를 부르지 않는다(SAFE-07). 미리 받은 확인은 치환 뒤 텍스트로 비교한다.
        var gate = await PolicyGate.DecideAsync(_policy, _interaction, _events, s, stmt, options, ct).ConfigureAwait(false);
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

        // E4: 질의(결과 집합)는 tb-result R2 의 결과 집합이 생긴 뒤 붙인다. 드라이버로 보내지 않고 끝낸다.
        if (stmt.Kind == StatementKind.Query)
        {
            return Failed(stmt, NotSupportedCode, "Tibero 질의 실행(결과 집합)은 아직 지원하지 않습니다.", applied);
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

        session.SetBusy(true);
        try
        {
            // 드라이버 호출은 동기로 돈다(실측 4장) → 부른 스레드(UI)를 막지 않게 드라이버 단계 전체를 스레드 풀로 옮긴다.
            return await Task.Run(() => RunOnDriverAsync(session, runtime, stmt, binds, applied, ct), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            session.SetBusy(false);
            session.CommandGate.Release();
        }
    }

    private async Task<ExecutionResult> RunOnDriverAsync(TiberoSession session, SessionRuntime runtime, SqlStatement stmt,
                                                         IReadOnlyDictionary<string, BindValue> binds, PolicyDecision? applied,
                                                         CancellationToken ct)
    {
        var kind = ExecutionPolicy.EffectiveKind(stmt);
        var sw = Stopwatch.StartNew();
        DbCommand? cmd = null;

        // dispatched = 사용자 문장을 드라이버에 넘겼다(그 전에 끝나면 걸린 시간은 0 — 준비 문장만 돈 시간은 사용자 문장 시간이 아니다).
        // executed = 사용자 문장 실행이 돌아왔다(그 뒤에 실패해도 트랜잭션 상태를 갱신한다).
        var dispatched = false;
        var executed = false;
        TimeSpan Elapsed() => dispatched ? sw.Elapsed : TimeSpan.Zero;
        try
        {
            await PrepareSessionAsync(session, runtime, ct).ConfigureAwait(false);

            // E5: 변경 일지(ChangeCapture + TiberoDdlSource)는 여기서 실행 직전에 이전 DDL 을 보관한다.

            cmd = session.CreateCommand(stmt.Text);
            IReadOnlyList<TiberoOutBind> outBinds = [];
            if (stmt.BindNames.Count > 0 || binds.Count > 0)
            {
                // 값이 없는 바인드는 여기서 InvalidOperationException → ORANDA-EXEC, 문장은 보내지 않는다.
                var driver = session.Driver ?? throw new InvalidOperationException("세션에 Tibero 드라이버 어댑터가 없어 바인드를 만들 수 없습니다.");
                outBinds = TiberoBindBuilder.Build(driver, cmd, stmt, binds);
            }

            if (outBinds.Any(o => o.Bind.Type == BindDataType.RefCursor))
            {
                // E4: REF CURSOR OUT 은 결과 집합(TiberoResultSet)으로 돌려줘야 한다. 그 전에는 보내지 않고 끝낸다(결과 없이 부수 효과만 남기지 않게).
                return Failed(stmt, NotSupportedCode, "Tibero REF CURSOR OUT 바인드는 아직 지원하지 않습니다.", applied);
            }

            long? rows = null;
            var returnedRows = false;

            // E2: 취소는 지금 토큰 → DbCommand.Cancel() 까지만 건다. 서버가 늦게 멈추는 동안의 화면 처리·시간 제한은 E2.
            using (cmd.CancelOnToken(ct))
            {
                dispatched = true;
                if (stmt.IsClassificationUncertain)
                {
                    // 분류가 불확실한 문장은 리더로 받는다(PSM 블록도 FieldCount=0 으로 된다, 실측 3장).
                    var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    executed = true;
                    await using (reader.ConfigureAwait(false))
                    {
                        returnedRows = reader.FieldCount > 0;
                        rows = !returnedRows && reader.RecordsAffected >= 0 ? reader.RecordsAffected : null;
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

            var outValues = ReadOutValues(session.Driver, outBinds);

            sw.Stop();

            if (returnedRows)
            {
                // E4: 결과 집합을 돌려줄 수 없다. 그러나 문장은 이미 돌았다 → 트랜잭션 상태를 늘 묻고(질의로 보고 건너뛰지 않는다),
                // 걸린 시간·안내 문구로 「보내지 않은 실패」와 구별되게 한다. App 의 SessionRunLog.WasSent 판정 보완은 따로 요청했다.
                var ranPending = await TrackTransactionAsync(session, stmt, kind == StatementKind.Query ? StatementKind.Dml : kind, ct)
                    .ConfigureAwait(false);
                // 돈 문장의 출력은 이 결과에 붙인다(안 거두면 다음 문장 결과로 잘못 간다). 순수 질의 뒤엔 Oracle 처럼 거두지 않는다.
                IReadOnlyList<string> ranOutput = IsPlainQuery(stmt, kind) ? [] : await SafeCollectAsync(session, runtime, ct).ConfigureAwait(false);
                return Failed(stmt, NotSupportedCode, "Tibero 질의 실행(결과 집합)은 아직 지원하지 않습니다. 문장은 실행되었습니다.", applied) with
                {
                    Elapsed = sw.Elapsed,
                    TransactionPending = ranPending,
                    DbmsOutput = ranOutput,
                };
            }

            var pending = await TrackTransactionAsync(session, stmt, kind, ct).ConfigureAwait(false);

            // 질의 뒤에는 숨은 문장(거두기)을 보내지 않는다(Oracle 과 같다): 직전 커서가 바뀌지 않게. 질의 안 함수가 낸 출력은 버퍼에 남아 다음 문장 뒤에 나온다.
            IReadOnlyList<string> output = IsPlainQuery(stmt, kind) ? [] : await SafeCollectAsync(session, runtime, ct).ConfigureAwait(false);

            if (kind == StatementKind.Ddl)
            {
                _events.Publish(new MetadataInvalidated(session.DbKey, []));
            }

            return new ExecutionResult(stmt, ExecutionStatus.Succeeded)
            {
                Elapsed = sw.Elapsed,
                RowsAffected = rows,
                Policy = applied,
                DbmsOutput = output,
                OutValues = outValues,
                TransactionPending = pending,
            };
        }
        catch (Exception ex) when (IsCancellation(ex, ct))
        {
            var pending = await SafeTrackAfterFailureAsync(session, stmt, kind).ConfigureAwait(false);
            return new ExecutionResult(stmt, ExecutionStatus.Cancelled) { Elapsed = Elapsed(), Policy = applied, TransactionPending = pending };
        }
        catch (Exception ex) when (TiberoErrorClassifier.IsDriverException(ex))
        {
            session.ReportFailure(ex);
            var pending = await SafeTrackAfterFailureAsync(session, stmt, kind).ConfigureAwait(false);
            // 실패 뒤에도 오류 앞에서 낸 출력을 거둔다(끊긴 세션은 빼고). 사용자 문장이 나가기 전의 실패(준비 문장)면 거둘 것이 없다.
            IReadOnlyList<string> output = !dispatched || session.State is SessionState.Broken or SessionState.Closed
                ? []
                : await SafeCollectAsync(session, runtime, ct).ConfigureAwait(false);
            return new ExecutionResult(stmt, ExecutionStatus.Failed)
            {
                Elapsed = Elapsed(),
                Error = TiberoErrorClassifier.ToDbError(ex, stmt.Text) ?? new DbError("TBR", ex.Message) { Dbms = Dbms.Tibero },
                Policy = applied,
                DbmsOutput = output,
                TransactionPending = pending,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or FormatException or InvalidCastException)
        {
            // 문장이 돈 뒤(OUT 값 읽기 등)의 실패면 트랜잭션이 이미 바뀌었을 수 있다 → 상태를 갱신한다.
            var failed = Failed(stmt, "ORANDA-EXEC", ex.Message, applied) with { Elapsed = Elapsed() };
            if (!executed)
            {
                return failed;
            }

            var pending = await SafeTrackAfterFailureAsync(session, stmt, kind).ConfigureAwait(false);
            var output = session.State is SessionState.Broken or SessionState.Closed
                ? []
                : await SafeCollectAsync(session, runtime, ct).ConfigureAwait(false);
            return failed with { TransactionPending = pending, DbmsOutput = output };
        }
        finally
        {
            await SafeDisposeAsync(cmd).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// OUT·INOUT·반환값을 R1b <see cref="TiberoValues.ReadOut"/> 로 읽는다(이름 → 그리드 값).
    /// 연결이 열린 동안(명령 잠금 안)에 부른다 — LOB 은 여기서 끝까지 읽는다.
    /// </summary>
    private static Dictionary<string, object?> ReadOutValues(TiberoDriver? driver, IReadOnlyList<TiberoOutBind> outBinds)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (outBinds.Count == 0 || driver is null)
        {
            return values;
        }

        foreach (var o in outBinds)
        {
            var value = TiberoValues.ReadOut(driver, o.Parameter, o.Bind);
            if (value is DbDataReader cursor)
            {
                // E4: REF CURSOR 값은 결과 집합으로 넘겨야 한다. REF CURSOR 로 선언한 바인드는 보내기 전에 막았고, 그래도 온 것은 닫아 새지 않게 한다.
                cursor.Dispose();
                value = null;
            }

            values[o.Name] = value;
        }

        return values;
    }

    /// <summary>
    /// 명령을 닫는다. 닫기 실패(드라이버 오류·이미 닫힌 연결)가 이미 만든 결과를 덮지 않게 삼킨다 — Oracle 처럼 결과는 늘 돌려준다.
    /// 그 밖의 예외(프로그램 오류)는 그대로 낸다.
    /// </summary>
    private static async ValueTask SafeDisposeAsync(DbCommand? cmd)
    {
        if (cmd is null)
        {
            return;
        }

        try
        {
            await cmd.DisposeAsync().ConfigureAwait(false);
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
    internal static async Task<bool> TrackTransactionAsync(TiberoSession session, SqlStatement stmt, StatementKind kind, CancellationToken ct)
    {
        if (session.Profile.ReadOnly)
        {
            session.SetPending(false);
            return false;
        }

        if (IsPlainQuery(stmt, kind))
        {
            return session.HasPendingTransaction;
        }

        var cmd = session.CreateCommand(TransactionIdSql);
        await using (cmd.ConfigureAwait(false))
        {
            var id = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            // 드라이버 NULL 은 DBNull 이 아니라 IsNull 인 공급자 값일 수 있다(실측 3장) → 어댑터로 판정한다.
            var pending = session.Driver is { } driver ? !driver.IsProviderNull(id) : id is not null and not DBNull;
            session.SetPending(pending);
            return pending;
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
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or FormatException or InvalidCastException)
        {
            // 거두기 실패(바인드·값 읽기)는 문장 결과에 영향이 없다. 거둔 데까지만 낸다.
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
