using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Composition;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.History;
using Oranda.Core.Sessions;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Execution;

/// <summary>
/// 실행 엔진(TRD 4.3, 4.9). 파이프라인: 치환 변수 → 정책 관문 → (SQL*Plus 명령이면 처리기) → 바인드 → 드라이버 실행
/// → 트랜잭션 추적(<c>LOCAL_TRANSACTION_ID</c>) → DBMS_OUTPUT 수거 → 이벤트. internal(TC-05). 소유: TSK-06-01.
/// <para>
/// 세션마다 실행을 한 줄로 세운다(결과 집합의 페이지 가져오기도 같은 줄). 연결은 자동 커밋·파이프라이닝을 끈다.
/// </para>
/// </summary>
[OrandaService(typeof(IExecutionService))]
internal sealed partial class OracleExecutionService : IExecutionService
{
    /// <summary>ORA-01013: 사용자가 현재 작업을 취소함.</summary>
    internal const int CancelledNumber = 1013;

    private readonly IExecutionPolicy _policy;
    private readonly IExecutionInteraction _interaction;
    private readonly IEventBus _events;
    private readonly ISqlPlusCommandProcessor? _sqlPlus;
    private readonly ConditionalWeakTable<OracleSession, SessionRuntime> _runtimes = new();

    public OracleExecutionService(IExecutionPolicy policy, IExecutionInteraction interaction, IEventBus events,
                                  ISqlPlusCommandProcessor sqlPlus)
        : this(policy, interaction, events, sqlPlus, pageSize: 500)
    {
    }

    internal OracleExecutionService(IExecutionPolicy policy, IExecutionInteraction interaction, IEventBus events,
                                    ISqlPlusCommandProcessor? sqlPlus, int pageSize = 500, bool dbmsOutput = true)
    {
        _policy = policy;
        _interaction = interaction;
        _events = events;
        _sqlPlus = sqlPlus;
        PageSize = pageSize;
        DbmsOutputEnabled = dbmsOutput;
    }

    /// <summary>결과 페이지 크기(<c>grid.pageSize</c>). FetchSize = RowSize × 이 값.</summary>
    internal int PageSize { get; }

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
            var result = await ExecuteAsync(s, stmt, empty, options, ct);
            yield return result;

            if (result.Status == ExecutionStatus.Cancelled)
            {
                yield break; // 취소 또는 사용자가 확인을 거절했다.
            }

            if (result.Status is ExecutionStatus.Failed or ExecutionStatus.Denied)
            {
                // 스크립트 안의 WHENEVER SQLERROR EXIT 는 설정(묻기·계속)보다 앞선다(SQL*Plus 와 같다).
                if (_sqlPlus is SqlPlus.SqlPlusCommandProcessor processor && processor.ExitOnError(s))
                {
                    await processor.ApplyOnErrorTransactionAsync(s, ct);
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

        // 2. 정책 관문. 거부면 드라이버를 부르지 않는다(SAFE-07: 문자열 자체를 보내지 않음).
        var ctx = new PolicyContext(s.Profile, s.Server, options.Source) { RequiredLicense = options.RequiredLicense };
        var decision = await _policy.EvaluateAsync(ctx, stmt, ct);
        _events.Publish(new PolicyDecisionMade(s.Id, stmt, decision));
        PolicyDecision? applied = null;
        switch (decision.Kind)
        {
            case PolicyDecisionKind.Deny:
                return new ExecutionResult(stmt, ExecutionStatus.Denied) { Policy = decision };
            case PolicyDecisionKind.Confirm:
                if (!await _interaction.ConfirmAsync(stmt, decision, ct))
                {
                    return new ExecutionResult(stmt, ExecutionStatus.Cancelled) { Policy = decision };
                }

                applied = decision;
                break;
        }

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

        try
        {
            await runtime.Gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return new ExecutionResult(stmt, ExecutionStatus.Cancelled) { Policy = applied };
        }

        session.SetBusy(true);
        try
        {
            return await RunOnDriverAsync(session, runtime, stmt, binds, applied, ct);
        }
        finally
        {
            session.SetBusy(false);
            runtime.Gate.Release();
        }
    }

    private async Task<ExecutionResult> RunOnDriverAsync(OracleSession session, SessionRuntime runtime, SqlStatement stmt,
                                                         IReadOnlyDictionary<string, BindValue> binds, PolicyDecision? applied,
                                                         CancellationToken ct)
    {
        var conn = session.Connection;
        var kind = ExecutionPolicy.EffectiveKind(stmt);
        var sw = Stopwatch.StartNew();
        OracleCommand? cmd = null;
        OracleDataReader? reader = null;
        try
        {
            await PrepareSessionAsync(session, runtime, ct);

            cmd = conn.CreateCommand();
            cmd.BindByName = true;
            cmd.CommandText = stmt.Text;
            var parameters = BindBuilder.Build(cmd, stmt, binds);

            IResultSet? resultSet = null;
            long? rows = null;
            using (ct.Register(static c => ((OracleCommand)c!).Cancel(), cmd))
            {
                // 질의와 분류가 불확실한 문장은 리더로 받는다(결과가 있으면 결과 집합).
                if (stmt.Kind == StatementKind.Query || stmt.IsClassificationUncertain)
                {
                    reader = (OracleDataReader)await cmd.ExecuteReaderAsync(ct);
                    if (reader.FieldCount > 0)
                    {
                        resultSet = new OracleResultSet(session, runtime.Gate, cmd, reader, PageSize);
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

                ct.ThrowIfCancellationRequested();
            }

            var outValues = parameters.Count == 0
                ? new Dictionary<string, object?>()
                : OracleValues.ReadOutValues(session, runtime.Gate, parameters, PageSize);

            sw.Stop();
            var pending = await TrackTransactionAsync(session, stmt, kind, ct);
            var output = await CollectDbmsOutputAsync(session, runtime, ct);

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
            };
        }
        catch (Exception ex) when (IsCancellation(ex, ct))
        {
            var pending = await SafeTrackAfterFailureAsync(session, stmt, kind);
            return new ExecutionResult(stmt, ExecutionStatus.Cancelled) { Elapsed = sw.Elapsed, Policy = applied, TransactionPending = pending };
        }
        catch (OracleException ex)
        {
            session.ReportFailure(ex);
            var pending = await SafeTrackAfterFailureAsync(session, stmt, kind);
            var output = session.State is SessionState.Broken ? [] : await SafeCollectAsync(session, runtime);
            return new ExecutionResult(stmt, ExecutionStatus.Failed)
            {
                Elapsed = sw.Elapsed,
                Error = ToDbError(ex, stmt.Text),
                Policy = applied,
                DbmsOutput = output,
                TransactionPending = pending,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or FormatException or InvalidCastException)
        {
            return Failed(stmt, "ORANDA-EXEC", ex.Message, applied) with { Elapsed = sw.Elapsed };
        }
        finally
        {
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
    private static async Task<bool> TrackTransactionAsync(OracleSession session, SqlStatement stmt, StatementKind kind, CancellationToken ct)
    {
        if (session.Profile.ReadOnly)
        {
            session.SetPending(false);
            return false;
        }

        if (kind == StatementKind.Query && !ForUpdateRegex().IsMatch(stmt.Text))
        {
            return session.HasPendingTransaction;
        }

        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = "SELECT DBMS_TRANSACTION.LOCAL_TRANSACTION_ID FROM dual";
        var id = await cmd.ExecuteScalarAsync(ct);
        var pending = id is not null and not DBNull;
        session.SetPending(pending);
        return pending;
    }

    private static async Task<bool> SafeTrackAfterFailureAsync(OracleSession session, SqlStatement stmt, StatementKind kind)
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
        var processor = _sqlPlus as SqlPlus.SqlPlusCommandProcessor;
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
        return stmt with { Text = text };
    }

    private static async Task ExecSqlAsync(OracleConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static bool IsCancellation(Exception ex, CancellationToken ct) =>
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

        return new DbError($"ORA-{ex.Number:D5}", ex.Message) { Number = ex.Number, Offset = offset, Line = line, Column = column };
    }

    [GeneratedRegex(@"\bFOR\s+UPDATE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForUpdateRegex();

    /// <summary>세션별 실행 상태(실행 줄, 준비 여부, && 치환 값).</summary>
    internal sealed class SessionRuntime
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool Prepared { get; set; }

        public Dictionary<string, string> Defines { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
