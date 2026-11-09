using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Execution.Providers;
using Oranda.Core.Sessions;
using Oranda.Tibero.Driver;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Execution;

/// <summary>
/// Tibero 대량 실행 경로(접속 간 데이터 복사, Oracle <c>OracleBulkExecutionService</c> 대응, TB1 설계 1·3장). 문장마다 정책을 한 번 평가하고
/// 행 묶음을 바인드 배열로 실행한다. 세션 명령 잠금(<see cref="TiberoSession.CommandGate"/>)·바쁨 표시·트랜잭션 추적·취소 판정은
/// <see cref="TiberoExecutionService"/> 와 같은 것을 쓴다.
/// <para>
/// 값은 열마다 바인드 글자 배열(<c>string?[]</c>, <see cref="TiberoBulkValueCodec.ToBindText"/>)로 <b>잠금 전에</b> 바꾼다. 형식이 맞지 않거나 지원하지 않는 열,
/// DATE·TIMESTAMP·RAW 처럼 값 자리를 변환 함수로 감싸야 하는 열이 감싸지지 않은 글이면 아무것도 보내지 않고 <c>ORANDA-EXEC</c>
/// (감싸지 않으면 NLS 기본 서식으로 시각을 잃는 조용한 손상, TB1 설계 0장).
/// </para>
/// <para>
/// 경로(TB1 설계 3.1): 열 없음 = 한 번 실행, 행 1 = 스칼라 바인드(서버 오류가 그대로 나와 실패 행의 이유가 된다), 행 &gt; 1 = 배열(ArrayBindCount).
/// BLOB 열이 있는 행 &gt; 1 묶음은 스칼라 한 행씩: 행마다 새 명령·매개변수를 만들어 실행하고 닫으며(재사용하면 큰 BLOB 뒤 TBR-590727, 실측), 행 사이에 취소를 확인한다.
/// 드라이버 예외·INSERT 0 행(<see cref="PartialCode"/>, 몇 번째 행인지 글에 넣음)이면 거기서 멈추고 남은 행은 보내지 않는다(앞 행은 열린 트랜잭션에 남는다). Tibero 배열 DML 은 오류 없이 앞 k 행만 넣고 멈출 수 있다(실측 3.1) →
/// INSERT … VALUES·MERGE 는 반환 행 수 k 가 보낸 행 수 n 과 같을 때만 성공한다: k &lt; n(앞 k 행만 넣고 멈춤)이든 k &gt; n(WHEN MATCHED UPDATE 가 대상 키가
/// 유일하지 않아 한 원천 행에 여러 대상 행을 바꿈 — 어디서 멈췄는지 숨는다)이든 <see cref="PartialCode"/> 로 실패시킨다(RowsAffected 0, 앞 행은 열린 트랜잭션에 남는다 —
/// 호출한 쪽이 세이브포인트로 되돌리고 한 행씩 다시 넣는다). 스칼라 한 행은 INSERT 와 WHEN MATCHED 가 있는 MERGE(upsert)의 0 행이 실패이고, 키 열만 있는 MERGE(WHEN NOT MATCHED INSERT 만)의 0 행은 성공이다(실측 3.8).
/// </para>
/// <para>
/// 읽기 전용 프로필이라도 문장마다 읽기 전용 트랜잭션을 걸지 않는다(원본 스냅숏은 호출한 쪽이 <see cref="TiberoExecutionService.ReadOnlyBlock"/> 으로 연다 —
/// 읽기 전용 프로필에서는 그 글만 이중 방어를 지난다). DBMS_OUTPUT·변경 일지도 거치지 않는다.
/// 등록하지 않는다: Tibero 복사(TI1b)가 붙을 때 DBMS 키로 등록한다(TiberoDataTransferService 와 같은 순서).
/// </para>
/// </summary>
internal sealed partial class TiberoBulkExecutionService : IBulkExecutionService
{
    /// <summary>배열 DML 이 오류 없이 행을 덜 넣었을 때의 오류 코드(TB1 설계 3.2).</summary>
    internal const string PartialCode = "ORANDA-ARRAY-PARTIAL";

    private const string ExecCode = "ORANDA-EXEC";

    private readonly IExecutionPolicy _policy;
    private readonly IExecutionInteraction _interaction;
    private readonly IEventBus _events;

    public TiberoBulkExecutionService(IExecutionPolicy policy, IExecutionInteraction interaction, IEventBus events)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(events);
        _policy = policy;
        _interaction = interaction;
        _events = events;
    }

    /// <summary>
    /// 조회 결과를 행마다 낸다(TB1 설계 1.1·1.2). 조회가 아니면(실효 분류가 Query 가 아니거나 첫 낱말이 SELECT·WITH 가 아니면) 정책을 묻기 전에
    /// <see cref="BulkDeniedException"/>. 정책 거부 = <see cref="BulkDeniedException"/>, 확인 거절 = <see cref="OperationCanceledException"/>.
    /// 세션 명령 잠금은 열거가 끝나거나 Dispose(조기 중단·예외 포함)될 때까지 쥔다(같은 세션의 다른 명령은 기다린다). 실행·읽기는 <see cref="TiberoBulkReader"/>.
    /// </summary>
    public async IAsyncEnumerable<object?[]> StreamRowsAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds,
                                                             ExecutionOptions options,
                                                             [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(options);
        binds ??= new Dictionary<string, BindValue>();

        // 조회가 아니면 정책을 묻기 전에 끝낸다(확인 창·드라이버로 가지 않게). 분류가 잘못 붙은 DELETE 가 리더로 실행되지 않게 첫 낱말도 본다(Oracle 과 같다).
        var kind = ExecutionPolicy.EffectiveKind(query);
        var (first, _) = LeadingWords(query.Text);
        if (kind != StatementKind.Query || first is not ("SELECT" or "WITH"))
        {
            throw new BulkDeniedException(new PolicyDecision(PolicyDecisionKind.Deny, PolicyRule.None,
                $"대량 읽기는 조회(SELECT)만 실행할 수 있습니다. 받은 문장: {first ?? kind.ToString()}"));
        }

        var gate = await PolicyGate.DecideAsync(_policy, _interaction, _events, s, query, options, ct).ConfigureAwait(false);
        switch (gate.Outcome)
        {
            case PolicyGateOutcome.Denied:
                throw new BulkDeniedException(gate.Decision);
            case PolicyGateOutcome.Cancelled:
                throw new OperationCanceledException("사용자가 정책 확인을 거절했습니다.");
        }

        var session = AsTiberoSession(s);
        if (session.State is SessionState.Broken or SessionState.Closed)
        {
            throw new InvalidOperationException($"세션이 {session.State} 상태입니다. 다시 접속하세요.");
        }

        await session.CommandGate.EnterAsync(ct).ConfigureAwait(false);
        session.SetBusy(true);
        try
        {
            await foreach (var row in TiberoBulkReader.ReadAsync(session, query, binds, ct).ConfigureAwait(false))
            {
                yield return row;
            }
        }
        finally
        {
            session.SetBusy(false);
            session.CommandGate.Release();
        }
    }

    /// <summary>
    /// <paramref name="columns"/> 가 비면(바인드 없는 문장) <paramref name="rows"/> 가 <c>[]</c> 든 <c>[[]]</c> 든 한 번 실행한다.
    /// 열이 있는데 <paramref name="rows"/> 가 비면 아무것도 하지 않는다(정책·확인 창·이벤트 없음, Succeeded·0).
    /// DDL·SQL*Plus 명령은 받지 않는다(<see cref="ArgumentException"/>). 호출마다 <see cref="StatementExecuted"/> 하나.
    /// </summary>
    public async Task<BulkResult> ExecuteArrayAsync(ISession s, SqlStatement stmt, IReadOnlyList<BulkColumn> columns,
                                                    IReadOnlyList<object?[]> rows, ExecutionOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(stmt);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(options);
        Validate(stmt, columns, rows);

        if (rows.Count == 0 && columns.Count > 0)
        {
            return new BulkResult(ExecutionStatus.Succeeded, 0);
        }

        if (ct.IsCancellationRequested)
        {
            return Publish(s, stmt, options, new BulkResult(ExecutionStatus.Cancelled, 0), TimeSpan.Zero, s.HasPendingTransaction);
        }

        var gate = await PolicyGate.DecideAsync(_policy, _interaction, _events, s, stmt, options, ct).ConfigureAwait(false);
        switch (gate.Outcome)
        {
            case PolicyGateOutcome.Denied:
                return Publish(s, stmt, options, new BulkResult(ExecutionStatus.Denied, 0) { Policy = gate.Decision }, TimeSpan.Zero, false);
            case PolicyGateOutcome.Cancelled:
                return Publish(s, stmt, options, new BulkResult(ExecutionStatus.Cancelled, 0) { Policy = gate.Decision }, TimeSpan.Zero, false);
        }

        // 읽기 전용 프로필 이중 방어(Oracle 과 같다): 분류가 잘못 붙은 쓰기가 정책을 지나도 보내지 않는다.
        // 글이 SET TRANSACTION·COMMIT·ROLLBACK 이거나 읽기 전용 트랜잭션 블록(TiberoExecutionService.ReadOnlyBlock)인 문장만 지난다.
        if (s.Profile.ReadOnly && !IsReadOnlySafeText(stmt.Text))
        {
            var denied = new PolicyDecision(PolicyDecisionKind.Deny, PolicyRule.ReadOnlyProfile,
                $"읽기 전용 접속 '{s.Profile.Name}' 에서는 대량 실행 경로로 SET TRANSACTION·COMMIT·ROLLBACK 만 실행할 수 있습니다.");
            return Publish(s, stmt, options, new BulkResult(ExecutionStatus.Denied, 0) { Policy = denied }, TimeSpan.Zero, false);
        }

        var applied = gate.Applied;

        var session = AsTiberoSession(s);
        if (session.State is SessionState.Broken or SessionState.Closed)
        {
            var broken = Failed("ORANDA-SESSION", $"세션이 {session.State} 상태입니다. 다시 접속하세요.", applied);
            return Publish(s, stmt, options, broken, TimeSpan.Zero, session.HasPendingTransaction);
        }

        // 값 → 바인드 글자(잠금 전). 지원 밖 형식·감싸지 않은 값 자리·형식이 맞지 않는 값이면 아무것도 보내지 않는다.
        if (!TryPrepare(stmt, columns, rows, out var plan, out var refusal))
        {
            return Publish(s, stmt, options, Failed(ExecCode, refusal, applied), TimeSpan.Zero, session.HasPendingTransaction);
        }

        try
        {
            await session.CommandGate.EnterAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Publish(s, stmt, options, new BulkResult(ExecutionStatus.Cancelled, 0) { Policy = applied }, TimeSpan.Zero,
                session.HasPendingTransaction);
        }

        session.SetBusy(true);
        try
        {
            // 드라이버 호출은 동기로 돈다(실측 4장) → 부른 스레드를 막지 않게 드라이버 단계 전체를 스레드 풀로 옮긴다(E1 과 같다).
            var (result, elapsed, pending) = await Task.Run(() => RunOnDriverAsync(session, stmt, plan, applied, ct), CancellationToken.None)
                .ConfigureAwait(false);
            return Publish(s, stmt, options, result, elapsed, pending);
        }
        finally
        {
            session.SetBusy(false);
            session.CommandGate.Release();
        }
    }

    private static async Task<(BulkResult Result, TimeSpan Elapsed, bool Pending)> RunOnDriverAsync(
        TiberoSession session, SqlStatement stmt, BindPlan plan, PolicyDecision? applied, CancellationToken ct)
    {
        var kind = ExecutionPolicy.EffectiveKind(stmt);
        var check = RowCountCheckOf(stmt.Text);
        var sw = Stopwatch.StartNew();
        DbCommand? cmd = null;
        var executed = false; // 서버로 문장을 보내기 시작했는가(그 뒤의 실패는 트랜잭션이 이미 바뀌었을 수 있다)
        try
        {
            var driver = session.Driver ?? throw new InvalidOperationException("세션에 Tibero 드라이버 어댑터가 없어 바인드를 만들 수 없습니다.");
            var n = plan.RowCount;
            var array = plan.Columns.Count > 0 && n > 1 && !plan.RowByRow;
            foreach (var column in plan.Columns)
            {
                if (column.Values.Length != n)
                {
                    throw new InvalidOperationException($"바인드 배열 {column.BindName} 의 길이({column.Values.Length})가 행 수({n})와 다릅니다.");
                }
            }

            long k;
            string? shortfall;
            if (plan.RowByRow)
            {
                // 한 행씩은 행마다 새 명령을 만든다(ExecuteRowByRowAsync) — 명령 하나를 재사용하지 않는다.
                executed = true;
                (k, shortfall) = await ExecuteRowByRowAsync(session, driver, stmt, plan, check, ct).ConfigureAwait(false);
            }
            else
            {
                cmd = BuildCommand(session, driver, stmt, plan, array, 0);
                executed = true;
                using (cmd.CancelOnToken(ct))
                {
                    (k, shortfall) = await ExecuteOnceAsync(cmd, check, array, n, ct).ConfigureAwait(false);
                }
            }

            sw.Stop();
            var pending = await TiberoExecutionService.TrackTransactionAsync(session, stmt, kind, ct).ConfigureAwait(false);
            if (shortfall is not null)
            {
                // 앞 k 행은 트랜잭션에 남았다(pending) — 호출한 쪽이 세이브포인트로 되돌리고 한 행씩 다시 넣어 이유를 얻는다.
                return (Failed(PartialCode, shortfall, applied), sw.Elapsed, pending);
            }

            return (new BulkResult(ExecutionStatus.Succeeded, k >= 0 ? k : 0) { Policy = applied }, sw.Elapsed, pending);
        }
        catch (Exception ex) when (TiberoExecutionService.IsCancellation(ex, ct))
        {
            var pending = await TiberoExecutionService.SafeTrackAfterFailureAsync(session, stmt, kind).ConfigureAwait(false);
            return (new BulkResult(ExecutionStatus.Cancelled, 0) { Policy = applied }, sw.Elapsed, pending);
        }
        catch (Exception ex) when (TiberoErrorClassifier.IsDriverException(ex))
        {
            session.ReportFailure(ex);
            var pending = await TiberoExecutionService.SafeTrackAfterFailureAsync(session, stmt, kind).ConfigureAwait(false);
            var error = TiberoErrorClassifier.ToDbError(ex, stmt.Text) ?? new DbError("TBR", ex.Message) { Dbms = Dbms.Tibero };
            return (new BulkResult(ExecutionStatus.Failed, 0) { Error = error, Policy = applied }, sw.Elapsed, pending);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or FormatException
                                       or InvalidCastException or OverflowException)
        {
            // 문장이 나간 뒤의 실패(행 수 해석 등)면 트랜잭션이 이미 바뀌었을 수 있다 → 상태를 갱신한다(E1 과 같다).
            var pending = executed
                ? await TiberoExecutionService.SafeTrackAfterFailureAsync(session, stmt, kind).ConfigureAwait(false)
                : session.HasPendingTransaction;
            return (Failed(ExecCode, ex.Message, applied), sw.Elapsed, pending);
        }
        finally
        {
            await TiberoExecutionService.SafeDisposeAsync(cmd).ConfigureAwait(false);
        }
    }

    /// <summary>한 번 실행(배열 또는 스칼라 1행). 실행 뒤 취소를 확인하고, 행 수 부족이면 그 글을 함께 돌려준다.</summary>
    private static async Task<(long Count, string? Shortfall)> ExecuteOnceAsync(
        DbCommand cmd, RowCountCheck check, bool array, int n, CancellationToken ct)
    {
        var k = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return (k, Shortfall(check, array, n, k));
    }

    /// <summary>
    /// BLOB 열이 있는 행 &gt; 1 묶음(TB1 설계 3.1): 행마다 새 명령·새 매개변수 한 벌을 만들어 실행하고 바로 닫는다.
    /// 명령 하나를 두고 <c>Value</c> 만 바꿔 재사용하면, 큰 BLOB(1 MB) 행 다음 행의 실행이 TBR-590727 "Communication protocol error" 로 실패한다(win144 실측,
    /// 같은 행을 새 명령으로 보내면 정상인 TBR-10007 이 나온다). 그래서 명령 재사용을 하지 않는다.
    /// 명령마다 취소 연결(<c>CancelOnToken</c>)을 건다. 행마다 실행 뒤 취소를 확인한다. 예외가 나면 그대로 올라가 거기서 멈춘다(남은 행은 보내지 않는다).
    /// INSERT·upsert MERGE(WHEN MATCHED 있음)가 0 행이면 그 행에서 멈추고
    /// 몇 번째 행인지 넣은 행 수 부족 글을 돌려준다(키 열만 있는 MERGE 의 0 은 성공). 돌려주는 행 수는 지금까지 합이다 — 앞 행은 열린 트랜잭션에 남는다.
    /// </summary>
    private static async Task<(long Count, string? Shortfall)> ExecuteRowByRowAsync(
        TiberoSession session, TiberoDriver driver, SqlStatement stmt, BindPlan plan, RowCountCheck check, CancellationToken ct)
    {
        long total = 0;
        for (var r = 0; r < plan.RowCount; r++)
        {
            ct.ThrowIfCancellationRequested();
            DbCommand? cmd = null;
            try
            {
                cmd = BuildCommand(session, driver, stmt, plan, false, r);
                long k;
                using (cmd.CancelOnToken(ct))
                {
                    k = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                ct.ThrowIfCancellationRequested();
                if (Shortfall(check, false, 1, k) is { } message)
                {
                    return (total, $"{(r + 1).ToString(CultureInfo.InvariantCulture)}번째 행 (앞 {r.ToString(CultureInfo.InvariantCulture)}행은 넣음): {message}");
                }

                total += k >= 0 ? k : 0;
            }
            finally
            {
                await TiberoExecutionService.SafeDisposeAsync(cmd).ConfigureAwait(false);
            }
        }

        return (total, null);
    }

    /// <summary>
    /// 명령 하나와 매개변수 한 벌을 만든다. 배열이면 열 전체를 배열 바인드로, 아니면 <paramref name="row"/> 번째 행의 스칼라 값만 담는다.
    /// </summary>
    private static DbCommand BuildCommand(TiberoSession session, TiberoDriver driver, SqlStatement stmt, BindPlan plan, bool array, int row)
    {
        var cmd = session.CreateCommand(stmt.Text);
        try
        {
            if (array)
            {
                driver.SetArrayBindCount(cmd, plan.RowCount);
            }

            foreach (var column in plan.Columns)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = column.BindName;
                driver.SetParameterKind(p, column.Kind);
                p.Direction = ParameterDirection.Input;
                p.Value = array ? column.Values : ScalarAt(column, row);
                cmd.Parameters.Add(p);
            }

            return cmd;
        }
        catch
        {
            cmd.Dispose();
            throw;
        }
    }

    /// <summary>스칼라 바인드 값: <paramref name="row"/> 번째 행의 글(BLOB 은 byte[]), NULL 은 <see cref="DBNull.Value"/>.</summary>
    private static object ScalarAt(PreparedColumn column, int row) => column.Values.GetValue(row) ?? DBNull.Value;

    /// <summary>
    /// 행 수 판정(TB1 설계 3.2). 배열: INSERT … VALUES·MERGE 는 돌려준 행 수 k 가 n 과 같을 때만 성공 — k &lt; n(앞 k 행만 넣고 멈춤)·음수는 물론
    /// k &gt; n(WHEN MATCHED UPDATE 가 대상 키가 유일하지 않아 한 원천 행에 여러 대상 행을 바꿈, 어디서 멈췄는지 숨음)도 실패.
    /// 스칼라: INSERT 와 upsert MERGE(WHEN MATCHED 있음)의 0 행이 실패, 키 열만 있는 MERGE 의 0 행(이미 있음)은 성공(실측 3.8).
    /// UPDATE·DELETE 는 보지 않는다(복사가 보내지 않고, Tibero 는 이 경우 위반을 알 수 없다).
    /// </summary>
    private static string? Shortfall(RowCountCheck check, bool array, int n, long k)
    {
        if (check == RowCountCheck.None)
        {
            return null;
        }

        if (!array)
        {
            return check is RowCountCheck.Insert or RowCountCheck.Merge && k < 1
                ? $"{(check == RowCountCheck.Insert ? "INSERT" : "MERGE")} 가 {Math.Max(k, 0).ToString(CultureInfo.InvariantCulture)}행을 "
                  + $"{(check == RowCountCheck.Insert ? "넣었습니다" : "처리했습니다")}. 드라이버가 오류를 알리지 않아 이유를 알 수 없습니다."
                : null;
        }

        if (k == n)
        {
            return null;
        }

        var total = n.ToString(CultureInfo.InvariantCulture);
        if (k < 0)
        {
            return $"배열 DML 이 {total}행의 처리 행 수를 알리지 않았습니다(드라이버 반환 {k.ToString(CultureInfo.InvariantCulture)}). "
                   + "드라이버가 오류를 알리지 않아 이유는 한 행씩 다시 넣을 때 나옵니다.";
        }

        var done = k.ToString(CultureInfo.InvariantCulture);
        if (k > n)
        {
            return $"배열 DML 의 처리 행 수가 보낸 행 수와 일치하지 않습니다(처리 {done}행, 보낸 {total}행). "
                   + "대상 키가 유일하지 않아 한 행이 여러 행을 바꿨을 수 있어 어디서 멈췄는지 알 수 없으므로 한 행씩 다시 넣습니다.";
        }

        var where = check == RowCountCheck.Insert ? $" {(k + 1).ToString(CultureInfo.InvariantCulture)}번째 행에서" : "";
        return $"배열 DML 이 {total}행 중 앞 {done}행만 넣고{where} 멈췄습니다. 드라이버가 오류를 알리지 않아 이유는 한 행씩 다시 넣을 때 나옵니다.";
    }

    /// <summary>행 수를 검사하는 문장: 첫 낱말 INSERT 이고 VALUES 꼴, 또는 MERGE(CopySqlBuilder 가 만드는 두 모양: WHEN MATCHED 가 있으면 upsert, 없으면 키 열만).</summary>
    private static RowCountCheck RowCountCheckOf(string text) => LeadingWords(text).First switch
    {
        "INSERT" when ValuesKeyword().IsMatch(text) => RowCountCheck.Insert,
        "MERGE" => WhenMatchedKeyword().IsMatch(text) ? RowCountCheck.Merge : RowCountCheck.MergeKeyOnly,
        _ => RowCountCheck.None,
    };

    /// <summary>
    /// 열마다 모양을 보고 값을 바인드 배열로 바꾼다. 지원 밖 형식, 감싸야 하는 값 자리(<see cref="TiberoBulkShape.IsWrapped"/>)가 글에 없는 열,
    /// 바꿀 수 없는 값이면 false 와 이유(열·행 번호).
    /// </summary>
    private static bool TryPrepare(SqlStatement stmt, IReadOnlyList<BulkColumn> columns, IReadOnlyList<object?[]> rows,
                                   out BindPlan plan, out string refusal)
    {
        var prepared = new List<PreparedColumn>(columns.Count);
        var rowByRow = false;
        plan = new BindPlan(prepared, rows.Count, false);
        refusal = string.Empty;
        for (var c = 0; c < columns.Count; c++)
        {
            var column = columns[c];
            TiberoBulkShape shape;
            string? expected = null;
            try
            {
                shape = TiberoBulkTypes.ShapeOf(column.ServerType);
                if (shape.IsSupported && shape.ParamKind is not null && shape.IsWrapped)
                {
                    expected = TiberoBulkValueCodec.BindExpression(column.BindName, column.ServerType);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException or FormatException
                                           or InvalidCastException or OverflowException)
            {
                refusal = $"{(c + 1).ToString(CultureInfo.InvariantCulture)}번째 열 {column.BindName}({column.ServerType})의 형식을 해석할 수 없습니다: {ex.Message}";
                return false;
            }

            if (!shape.IsSupported || shape.ParamKind is not { } paramKind)
            {
                refusal = $"데이터 복사가 지원하지 않는 형식입니다: {column.BindName} ({column.ServerType})";
                return false;
            }

            if (expected is not null && !IsWrappedEverywhere(stmt.Text, expected, column.BindName))
            {
                refusal = $"{column.BindName} 열({column.ServerType})의 값 자리가 {expected} 로 감싸져 있지 않아 보내지 않았습니다"
                          + "(감싸지 않으면 서버 기본 서식으로 바뀌어 값이 상합니다).";
                return false;
            }

            var blob = shape.Type == TiberoBulkType.Blob;
            rowByRow |= !shape.ArrayBind && rows.Count > 1;
            Array values = blob ? new byte[]?[rows.Count] : new string?[rows.Count];
            for (var r = 0; r < rows.Count; r++)
            {
                try
                {
                    values.SetValue(blob
                        ? TiberoBulkValueCodec.ToBindBytes(rows[r][c], column.ServerType)
                        : TiberoBulkValueCodec.ToBindText(rows[r][c], column.ServerType), r);
                }
                catch (Exception ex) when (ex is InvalidCastException or NotSupportedException or InvalidOperationException or FormatException
                                               or OverflowException or ArgumentException)
                {
                    refusal = $"{(c + 1).ToString(CultureInfo.InvariantCulture)}번째 열 {column.BindName}({column.ServerType})의 "
                              + $"{(r + 1).ToString(CultureInfo.InvariantCulture)}번째 행 값을 바인드할 수 없습니다: {ex.Message}";
                    return false;
                }
            }

            if (shape.Type is TiberoBulkType.Clob or TiberoBulkType.NClob && rows.Count > 1 && HasLongText((string?[])values))
            {
                rowByRow = true;
            }

            prepared.Add(new PreparedColumn(column.BindName, paramKind, values));
        }

        plan = new BindPlan(prepared, columns.Count == 0 ? 1 : rows.Count, rowByRow);
        return true;
    }

    /// <summary>
    /// 배열로 보내도 되는 CLOB·NCLOB 값 길이(글자)의 상한. 이보다 긴 값이 하나라도 있는 묶음은 한 행씩 보낸다. 실측(win144 #74·#81·#82): 100000자 CLOB 행이
    /// 다른 열과 함께 배열에 들어가면 서버가 연결을 끊는다(TBR-90405 End Of Stream). 4000자로 자른 같은 혼합 묶음, CLOB 열만 있는 100000자 배열은 됨.
    /// 스칼라 바인드는 100000자도 됨.
    /// </summary>
    internal const int ArrayClobMaxChars = 4000;

    private static bool HasLongText(string?[] values)
    {
        foreach (var value in values)
        {
            if (value is { Length: > ArrayClobMaxChars })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 글에 감싼 값 자리(<paramref name="expected"/>)가 들어 있고, 그것을 모두 지운 나머지에 같은 바인드 <c>:이름</c>(뒤가 식별자 글자가 아닌 것, 예: <c>:C1</c> 은 <c>:C10</c> 이 아님)이
    /// 더 남아 있지 않으면 참. 같은 바인드가 감싸지 않은 채 한 번 더 나오면 그 자리는 서버 기본 서식으로 읽는다.
    /// </summary>
    private static bool IsWrappedEverywhere(string text, string expected, string bindName)
    {
        if (!text.Contains(expected, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = text.Replace(expected, string.Empty, StringComparison.OrdinalIgnoreCase);
        return !Regex.IsMatch(rest, ":" + Regex.Escape(bindName) + "(?![A-Za-z0-9_$#])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    /// <summary>호출 한 번에 <see cref="StatementExecuted"/> 하나(Oracle 과 같다).</summary>
    private BulkResult Publish(ISession s, SqlStatement stmt, ExecutionOptions options, BulkResult result, TimeSpan elapsed, bool pending)
    {
        var executed = new ExecutionResult(stmt, result.Status)
        {
            Elapsed = elapsed,
            RowsAffected = result.Status == ExecutionStatus.Succeeded ? result.RowsAffected : null,
            Error = result.Error,
            Policy = result.Policy,
            TransactionPending = pending,
        };
        _events.Publish(new StatementExecuted(s.Id, stmt, executed, options));
        return result;
    }

    private static BulkResult Failed(string code, string message, PolicyDecision? applied) =>
        new(ExecutionStatus.Failed, 0) { Error = new DbError(code, message) { Dbms = Dbms.Tibero }, Policy = applied };

    private static void Validate(SqlStatement stmt, IReadOnlyList<BulkColumn> columns, IReadOnlyList<object?[]> rows)
    {
        // 분류가 불확실하면 EffectiveKind 는 DML 이 되므로, 붙은 분류와 실효 분류를 모두 본다.
        var rejected = stmt.Kind is StatementKind.Ddl or StatementKind.SqlPlus ? stmt.Kind : ExecutionPolicy.EffectiveKind(stmt);
        if (rejected is StatementKind.Ddl or StatementKind.SqlPlus)
        {
            throw new ArgumentException($"대량 실행 경로는 {rejected} 문장을 받지 않습니다. IExecutionService 로 실행하세요.", nameof(stmt));
        }

        if (columns.Count == 0 && rows.Count > 1)
        {
            throw new ArgumentException("바인드 열이 없는 문장은 한 번만 실행합니다.", nameof(rows));
        }

        for (var r = 0; r < rows.Count; r++)
        {
            if (rows[r] is null || rows[r].Length != columns.Count)
            {
                throw new ArgumentException($"{r}번째 행의 값 수가 열 수({columns.Count})와 다릅니다.", nameof(rows));
            }
        }
    }

    /// <summary>
    /// 읽기 전용 프로필에서 지나는 글: SET TRANSACTION·COMMIT·ROLLBACK(<see cref="IsTransactionControlText"/>), 또는 앞뒤 공백을 뺀 글이
    /// <see cref="TiberoExecutionService.ReadOnlyBlock"/> 과 글자 그대로 같은 것(복사 방언이 그 글을 그대로 보낸다).
    /// </summary>
    internal static bool IsReadOnlySafeText(string text) =>
        IsTransactionControlText(text) || string.Equals(text.Trim(), TiberoExecutionService.ReadOnlyBlock, StringComparison.Ordinal);

    /// <summary>SET TRANSACTION, COMMIT, ROLLBACK(ROLLBACK TO SAVEPOINT 포함). Oracle <c>OracleBulkExecutionService</c> 와 같은 규칙(TB1 설계 3.4: 복사).</summary>
    internal static bool IsTransactionControlText(string text) => LeadingWords(text) switch
    {
        ("SET", "TRANSACTION") => true,
        ("COMMIT" or "ROLLBACK", _) => true,
        _ => false,
    };

    /// <summary>
    /// 글의 앞 두 낱말(대문자). 앞의 공백·주석(<c>--</c>, <c>/* */</c>)·여는 괄호를 건너뛴다. 낱말이 없으면 null.
    /// 분류(<see cref="SqlStatement.Kind"/>)를 한 번 더 확인하는 데만 쓴다(Oracle 과 같은 규칙).
    /// </summary>
    internal static (string? First, string? Second) LeadingWords(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var i = 0;
        var first = NextWord(text, ref i);
        var second = first is null ? null : NextWord(text, ref i);
        return (first, second);
    }

    private static string? NextWord(string text, ref int i)
    {
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c == '(')
            {
                i++;
            }
            else if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                var nl = text.IndexOf('\n', i);
                i = nl < 0 ? text.Length : nl + 1;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? text.Length : close + 2;
            }
            else
            {
                break;
            }
        }

        var start = i;
        while (i < text.Length && (char.IsLetter(text[i]) || text[i] == '_'))
        {
            i++;
        }

        return i > start ? text[start..i].ToUpperInvariant() : null;
    }

    private static TiberoSession AsTiberoSession(ISession s) =>
        s as TiberoSession ?? throw new ArgumentException("Oranda Tibero 세션 관리자가 연 세션이 아닙니다.", nameof(s));

    [GeneratedRegex(@"\bVALUES\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ValuesKeyword();

    /// <summary><c>WHEN MATCHED</c>(<c>WHEN NOT MATCHED</c> 는 아님).</summary>
    [GeneratedRegex(@"\bWHEN\s+MATCHED\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WhenMatchedKeyword();

    private enum RowCountCheck
    {
        None,
        Insert,

        /// <summary>WHEN MATCHED 가 있는 MERGE(upsert): 스칼라 0 행은 실패.</summary>
        Merge,

        /// <summary>WHEN NOT MATCHED INSERT 만 있는 MERGE(키 열만): 스칼라 0 행(이미 있음)은 성공.</summary>
        MergeKeyOnly,
    }

    /// <summary>바인드 열 하나: 이름(쌍점 없음)·매개변수 종류·행마다의 값(<c>string?[]</c>, BLOB 은 <c>byte[]?[]</c>).</summary>
    private sealed record PreparedColumn(string BindName, TiberoParamKind Kind, Array Values);

    /// <summary>실행 계획: 열·행 수(열이 없으면 1)·스칼라 한 행씩 여부(BLOB 열이 있는 행 &gt; 1).</summary>
    private sealed record BindPlan(IReadOnlyList<PreparedColumn> Columns, int RowCount, bool RowByRow);
}
