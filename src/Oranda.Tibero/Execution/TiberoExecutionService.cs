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
/// 질의와 분류가 불확실한 문장은 리더로 받아, 행이 있으면 <see cref="TiberoResultSet"/>(E4), REF CURSOR OUT 도 결과 집합으로 돌려준다.
/// 결과 집합이 명령·리더를 소유하고 페이지마다 명령 잠금을 다시 잡는다(실행은 리더를 연 뒤 잠금을 놓는다, Oracle 과 같다).
/// 취소(E2)는 Oracle 과 같다: 토큰 → <c>DbCommand.Cancel()</c>(<see cref="CommandCancellation.CancelOnToken"/>)이고, 드라이버가 돌아올 때까지
/// (Tibero 서버는 10ms~58초 뒤에 멈춘다, 실측 4장) 이 호출이 끝나지 않으므로 호출한 쪽의 「취소 중」 상태가 그만큼 이어진다 — 서비스는 별도 이벤트를 내지 않는다.
/// 문장 시간 제한은 Oracle 서비스에도 없어 두지 않는다(드라이버가 CommandTimeout 을 무시하므로 필요해지면 연결 취소 토큰으로 건다).
/// 변경 일지(E5, D4): ALTER·DROP·CREATE OR REPLACE·RENAME 직전에 <see cref="TiberoDdlSource"/> 로 <see cref="ChangeCapture"/> 를 돌려
/// 「보관 안 함 + 이유」 행을 <see cref="ChangeSnapshotCaptured"/> 로 알린다(서버에 아무것도 보내지 않는다). 실제 이전 DDL 보관은 T3.
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
    private readonly ConditionalWeakTable<TiberoSession, SessionRuntime> _runtimes = new();

    public TiberoExecutionService(IExecutionPolicy policy, IExecutionInteraction interaction, IEventBus events,
                                  ISqlPlusCommandProcessor sqlPlus)
        : this(policy, interaction, events, sqlPlus, dbmsOutput: true)
    {
    }

    internal TiberoExecutionService(IExecutionPolicy policy, IExecutionInteraction interaction, IEventBus events,
                                    ISqlPlusCommandProcessor? sqlPlus, bool dbmsOutput = true, int pageSize = 500,
                                    IGridFetchSettings? grid = null)
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
    }

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
                return await Task.Run(() => RunOnDriverAsync(session, runtime, stmt, binds, applied, orphans, ct), CancellationToken.None)
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
                                                         List<IAsyncDisposable> orphans, CancellationToken ct)
    {
        var kind = ExecutionPolicy.EffectiveKind(stmt);
        var sw = Stopwatch.StartNew();
        DbCommand? cmd = null;
        DbDataReader? reader = null; // 결과 집합에 넘기기 전까지 이 메서드가 소유한다

        // dispatched = 사용자 문장을 드라이버에 넘겼다(그 전에 끝나면 걸린 시간은 0 — 준비 문장만 돈 시간은 사용자 문장 시간이 아니다).
        // executed = 사용자 문장 실행이 돌아왔다(그 뒤에 실패해도 트랜잭션 상태를 갱신한다).
        var dispatched = false;
        var executed = false;
        TimeSpan Elapsed() => dispatched ? sw.Elapsed : TimeSpan.Zero;
        IReadOnlyList<TiberoOutBind> outBinds = [];
        IReadOnlyList<string>? collected = null; // 이미 거둔 DBMS_OUTPUT(버퍼는 비었다) — 뒤 단계가 실패하면 그 결과에 붙인다
        try
        {
            await PrepareSessionAsync(session, runtime, ct).ConfigureAwait(false);

            await CaptureBeforeChangeAsync(session, stmt, ct).ConfigureAwait(false);

            cmd = session.CreateCommand(stmt.Text);
            if (stmt.BindNames.Count > 0 || binds.Count > 0)
            {
                // 값이 없는 바인드는 여기서 InvalidOperationException → ORANDA-EXEC, 문장은 보내지 않는다.
                var driver = session.Driver ?? throw new InvalidOperationException("세션에 Tibero 드라이버 어댑터가 없어 바인드를 만들 수 없습니다.");
                outBinds = TiberoBindBuilder.Build(driver, cmd, stmt, binds);
            }

            var pageSize = CurrentPageSize; // 실행마다 한 번 읽는다: 한 결과 안에서 페이지 크기가 바뀌지 않게
            var lobFetchSize = CurrentLobFetchSize;
            long? rows = null;
            var hasRows = false;

            // 취소: 토큰 → DbCommand.Cancel()(Oracle 과 같다). 드라이버 호출이 돌아올 때까지(서버가 멈출 때까지) 이 줄에서 기다린다.
            // 호출이 끝나면 등록을 푼다 — 늦은 취소가 같은 연결의 다음 문장으로 새지 않게.
            using (cmd.CancelOnToken(ct))
            {
                dispatched = true;
                if (stmt.Kind == StatementKind.Query || stmt.IsClassificationUncertain)
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
            var outValues = ReadOutValues(session, outBinds, pageSize, lobFetchSize, orphans);

            sw.Stop();
            var pending = await TrackTransactionAsync(session, stmt, kind, ct).ConfigureAwait(false);

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
                OutValues = outValues,
                TransactionPending = pending,
            };
        }
        catch (Exception ex) when (IsCancellation(ex, ct))
        {
            if (executed)
            {
                await CloseCursorOutsAsync(session, outBinds).ConfigureAwait(false); // 서버가 연 REF CURSOR 를 감싸지 않은 채 남기지 않는다.
            }

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
            var output = collected ?? (session.State is SessionState.Broken or SessionState.Closed
                ? []
                : await SafeCollectAsync(session, runtime, ct).ConfigureAwait(false));
            return failed with { TransactionPending = pending, DbmsOutput = output };
        }
        finally
        {
            // 결과 집합에 넘기지 않은 리더·명령만 여기서 닫는다(넘겼으면 null).
            await SafeDisposeAsync(reader).ConfigureAwait(false);
            await SafeDisposeAsync(cmd).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 변경 일지(Oracle <c>CaptureBeforeChangeAsync</c> 와 같은 자리·규칙): 정책·확인을 통과한 DDL 대상마다 일지 행을 알린다.
    /// T2 의 원천 <see cref="TiberoDdlSource"/> 는 서버를 읽지 않고 「보관 안 함 + 이유」 행만 만든다. 일지는 부가 기능 — 실행을 막지 않고, 사용자 취소만 전파한다.
    /// </summary>
    private async Task CaptureBeforeChangeAsync(TiberoSession session, SqlStatement stmt, CancellationToken ct)
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
                StatementSummary = ChangeCapture.Summarize(stmt.Text),
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
