using System.Data;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Execution.Providers;
using Oranda.Core.Sessions;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Execution;

/// <summary>
/// 대량 실행 경로(접속 간 데이터 복사, 설계 8.3). 문장마다 정책을 한 번 평가하고(<see cref="PolicyGate"/>), 행 묶음을 바인드 배열로 실행한다.
/// <para>
/// <see cref="OracleExecutionService"/> 와 따로 둔다: 서비스 컨테이너는 등록마다 인스턴스를 따로 만들므로, 한 클래스에 두 등록을 붙이면
/// 세션별 상태가 두 벌로 갈린다. 세션 명령 잠금(<see cref="OracleSession.CommandGate"/>)과 바쁨 표시는 <c>RunAsync</c> 와 같게 잡는다.
/// </para>
/// <para>
/// 읽기 전용 프로필이라도 문장마다 새 읽기 전용 트랜잭션을 걸지 않는다: 원본 세션은 호출한 쪽이 연 <c>SET TRANSACTION READ ONLY</c>
/// 하나에서 모든 테이블을 같은 시점으로 읽는다(설계 8.1). 쓰기는 정책 관문의 읽기 전용 규칙이 막는다.
/// </para>
/// </summary>
[OrandaService(typeof(IBulkExecutionService), Dbms = Dbms.Oracle)]
internal sealed class OracleBulkExecutionService : IBulkExecutionService
{
    /// <summary>한 번에 가져올 행 수(FetchSize = RowSize × 이 값). LOB 은 위치자만 오고 값은 열마다 따로 읽는다.</summary>
    internal const int FetchRows = 1000;

    private const string CancelledMessage = "대량 읽기를 취소했습니다.";

    private readonly IExecutionPolicy _policy;
    private readonly IExecutionInteraction _interaction;
    private readonly IEventBus _events;

    public OracleBulkExecutionService(IExecutionPolicy policy, IExecutionInteraction interaction, IEventBus events)
    {
        _policy = policy;
        _interaction = interaction;
        _events = events;
    }

    public async IAsyncEnumerable<object?[]> StreamRowsAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds,
                                                             ExecutionOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(options);
        binds ??= new Dictionary<string, BindValue>();

        // 조회가 아니면 정책을 묻기 전에 끝낸다(확인 창이 뜨거나 드라이버로 가지 않게).
        // 호출한 쪽이 붙인 분류만 믿지 않고 글의 첫 낱말도 본다: 분류가 잘못 붙은 DELETE 가 리더로 실행되지 않게.
        var kind = ExecutionPolicy.EffectiveKind(query);
        var (first, _) = LeadingWords(query.Text);
        if (kind != StatementKind.Query || first is not ("SELECT" or "WITH"))
        {
            throw new BulkDeniedException(new PolicyDecision(PolicyDecisionKind.Deny, PolicyRule.None,
                $"대량 읽기는 조회(SELECT)만 실행할 수 있습니다. 받은 문장: {first ?? kind.ToString()}"));
        }

        var gate = await PolicyGate.DecideAsync(_policy, _interaction, _events, s, query, options, ct);
        switch (gate.Outcome)
        {
            case PolicyGateOutcome.Denied:
                throw new BulkDeniedException(gate.Decision);
            case PolicyGateOutcome.Cancelled:
                throw new OperationCanceledException("사용자가 정책 확인을 거절했습니다.");
        }

        var session = AsOracleSession(s);
        if (session.State is SessionState.Broken or SessionState.Closed)
        {
            throw new InvalidOperationException($"세션이 {session.State} 상태입니다. 다시 접속하세요.");
        }

        // 잠금은 열거가 끝나거나(Dispose) 예외가 날 때까지 쥔다. 정리(Dispose)가 실패해도 잠금은 반드시 푼다.
        await session.CommandGate.EnterAsync(ct);
        session.SetBusy(true);
        try
        {
            await foreach (var row in ReadRowsAsync(session, query, binds, ct))
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

    /// <summary>잠금을 쥔 채로 부른다. 리더·명령 정리는 여기서, 잠금 해제는 호출한 쪽 finally 에서 한다.</summary>
    private static async IAsyncEnumerable<object?[]> ReadRowsAsync(OracleSession session, SqlStatement query,
                                                                   IReadOnlyDictionary<string, BindValue> binds,
                                                                   [EnumeratorCancellation] CancellationToken ct)
    {
        OracleCommand? cmd = null;
        OracleDataReader? reader = null;
        try
        {
            PrepareConnection(session);
            cmd = session.Connection.CreateCommand();
            cmd.BindByName = true;
            cmd.CommandText = query.Text;
            BindBuilder.Build(cmd, query, binds);
            using var cancel = cmd.CancelOnToken(ct);

            BulkReadKind[] readers;
            try
            {
                reader = (OracleDataReader)await cmd.ExecuteReaderAsync(ct);
                reader.FetchSize = Math.Max(reader.RowSize, 1) * FetchRows;
                readers = ColumnReaders(reader);
            }
            catch (OracleException ex) when (OracleExecutionService.IsCancellation(ex, ct))
            {
                throw new OperationCanceledException(CancelledMessage, ex, ct); // ORA-01013 → 취소(실행기가 실패로 적지 않게)
            }
            catch (OracleException ex) when (Report(session, ex))
            {
                throw; // 도달하지 않는다(필터가 false)
            }

            while (true)
            {
                object?[] row;
                try
                {
                    if (!await reader.ReadAsync(ct))
                    {
                        break;
                    }

                    row = ReadRow(reader, readers);
                }
                catch (OracleException ex) when (OracleExecutionService.IsCancellation(ex, ct))
                {
                    throw new OperationCanceledException(CancelledMessage, ex, ct);
                }
                catch (OracleException ex) when (Report(session, ex))
                {
                    throw;
                }

                yield return row;
            }
        }
        finally
        {
            try
            {
                if (reader is not null)
                {
                    await reader.DisposeAsync();
                }
            }
            finally
            {
                if (cmd is not null)
                {
                    await cmd.DisposeAsync();
                }
            }
        }
    }

    /// <summary>
    /// <paramref name="columns"/> 가 비면(바인드 없는 문장) <paramref name="rows"/> 가 <c>[]</c> 든 <c>[[]]</c> 든 한 번 실행한다.
    /// 열이 있는데 <paramref name="rows"/> 가 비면 아무것도 하지 않는다(정책·확인 창·이벤트 없음, Succeeded·0).
    /// DDL·SQL*Plus 명령은 받지 않는다(<see cref="ArgumentException"/>): 변경 일지·처리기를 거치는 <see cref="IExecutionService"/> 로 보낸다.
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

        var gate = await PolicyGate.DecideAsync(_policy, _interaction, _events, s, stmt, options, ct);
        switch (gate.Outcome)
        {
            case PolicyGateOutcome.Denied:
                return Publish(s, stmt, options, new BulkResult(ExecutionStatus.Denied, 0) { Policy = gate.Decision }, TimeSpan.Zero, false);
            case PolicyGateOutcome.Cancelled:
                return Publish(s, stmt, options, new BulkResult(ExecutionStatus.Cancelled, 0) { Policy = gate.Decision }, TimeSpan.Zero, false);
        }

        // 읽기 전용 프로필 이중 방어: 이 경로는 문장마다 읽기 전용 트랜잭션을 다시 걸지 않으므로(호출한 쪽 스냅숏 유지),
        // 분류가 잘못 붙은 쓰기가 정책을 지나도 보내지 않는다. 글이 SET TRANSACTION·COMMIT·ROLLBACK 인 문장만 지난다(설계 4장).
        if (s.Profile.ReadOnly && !IsTransactionControlText(stmt.Text))
        {
            var denied = new PolicyDecision(PolicyDecisionKind.Deny, PolicyRule.ReadOnlyProfile,
                $"읽기 전용 접속 '{s.Profile.Name}' 에서는 대량 실행 경로로 SET TRANSACTION·COMMIT·ROLLBACK 만 실행할 수 있습니다.");
            return Publish(s, stmt, options, new BulkResult(ExecutionStatus.Denied, 0) { Policy = denied }, TimeSpan.Zero, false);
        }

        var applied = gate.Applied;

        var session = AsOracleSession(s);
        if (session.State is SessionState.Broken or SessionState.Closed)
        {
            var broken = new BulkResult(ExecutionStatus.Failed, 0)
            {
                Error = new DbError("ORANDA-SESSION", $"세션이 {session.State} 상태입니다. 다시 접속하세요."),
                Policy = applied,
            };
            return Publish(s, stmt, options, broken, TimeSpan.Zero, session.HasPendingTransaction);
        }

        try
        {
            await session.CommandGate.EnterAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return Publish(s, stmt, options, new BulkResult(ExecutionStatus.Cancelled, 0) { Policy = applied }, TimeSpan.Zero,
                session.HasPendingTransaction);
        }

        session.SetBusy(true);
        try
        {
            var (result, elapsed, pending) = await RunOnDriverAsync(session, stmt, columns, rows, applied, ct);
            return Publish(s, stmt, options, result, elapsed, pending);
        }
        finally
        {
            session.SetBusy(false);
            session.CommandGate.Release();
        }
    }

    private static async Task<(BulkResult Result, TimeSpan Elapsed, bool Pending)> RunOnDriverAsync(
        OracleSession session, SqlStatement stmt, IReadOnlyList<BulkColumn> columns, IReadOnlyList<object?[]> rows,
        PolicyDecision? applied, CancellationToken ct)
    {
        var kind = ExecutionPolicy.EffectiveKind(stmt);
        var sw = Stopwatch.StartNew();
        OracleCommand? cmd = null;
        try
        {
            PrepareConnection(session);
            cmd = session.Connection.CreateCommand();
            cmd.BindByName = true;
            cmd.CommandText = stmt.Text;
            if (columns.Count > 0)
            {
                cmd.ArrayBindCount = rows.Count;
                for (var c = 0; c < columns.Count; c++)
                {
                    var values = new object[rows.Count];
                    for (var r = 0; r < rows.Count; r++)
                    {
                        values[r] = rows[r][c] ?? DBNull.Value;
                    }

                    cmd.Parameters.Add(new OracleParameter(columns[c].BindName, BulkBinds.ToDbType(columns[c].ServerType))
                    {
                        Direction = ParameterDirection.Input,
                        Value = values,
                    });
                }
            }

            long n;
            using (cmd.CancelOnToken(ct))
            {
                n = await cmd.ExecuteNonQueryAsync(ct);
                ct.ThrowIfCancellationRequested();
            }

            sw.Stop();
            var pending = await OracleExecutionService.TrackTransactionAsync(session, stmt, kind, ct);
            return (new BulkResult(ExecutionStatus.Succeeded, n >= 0 ? n : 0) { Policy = applied }, sw.Elapsed, pending);
        }
        catch (Exception ex) when (OracleExecutionService.IsCancellation(ex, ct))
        {
            var pending = await OracleExecutionService.SafeTrackAfterFailureAsync(session, stmt, kind);
            return (new BulkResult(ExecutionStatus.Cancelled, 0) { Policy = applied }, sw.Elapsed, pending);
        }
        catch (OracleException ex)
        {
            session.ReportFailure(ex);
            var pending = await OracleExecutionService.SafeTrackAfterFailureAsync(session, stmt, kind);
            return (new BulkResult(ExecutionStatus.Failed, 0) { Error = OracleExecutionService.ToDbError(ex, stmt.Text), Policy = applied },
                sw.Elapsed, pending);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or FormatException or InvalidCastException)
        {
            return (new BulkResult(ExecutionStatus.Failed, 0) { Error = new DbError("ORANDA-EXEC", ex.Message), Policy = applied },
                sw.Elapsed, session.HasPendingTransaction);
        }
        finally
        {
            if (cmd is not null)
            {
                await cmd.DisposeAsync();
            }
        }
    }

    /// <summary>호출 한 번에 <see cref="StatementExecuted"/> 하나. 이력 저장 여부는 <see cref="ExecutionOptions.SkipHistory"/> 를 따른다.</summary>
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

    /// <summary>읽기 전용 프로필에서 지나는 글: SET TRANSACTION, COMMIT, ROLLBACK(ROLLBACK TO SAVEPOINT 포함).</summary>
    internal static bool IsTransactionControlText(string text) => LeadingWords(text) switch
    {
        ("SET", "TRANSACTION") => true,
        ("COMMIT" or "ROLLBACK", _) => true,
        _ => false,
    };

    /// <summary>
    /// 글의 앞 두 낱말(대문자). 앞의 공백·주석(<c>--</c>, <c>/* */</c>)·여는 괄호를 건너뛴다. 낱말이 없으면 null.
    /// 분류(<see cref="SqlStatement.Kind"/>)를 한 번 더 확인하는 데만 쓴다.
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

    private static OracleSession AsOracleSession(ISession s) =>
        s as OracleSession ?? throw new ArgumentException("Oranda 세션 관리자가 연 세션이 아닙니다.", nameof(s));

    /// <summary>자동 커밋·파이프라이닝을 끈다(SAFE-04). 커밋 단위는 호출한 쪽이 정한다. 서버 왕복 없음.</summary>
    private static void PrepareConnection(OracleSession session)
    {
        session.Connection.AutoCommit = false;
        session.Connection.Pipelining = false;
    }

    /// <summary>예외 필터용: 끊김이면 세션 상태를 바꾸고, 잡지는 않는다.</summary>
    private static bool Report(OracleSession session, OracleException ex)
    {
        session.ReportFailure(ex);
        return false;
    }

    /// <summary>열마다 읽는 방법을 한 번 정한다(<see cref="BulkBinds.ReadKind"/>). 1차 지원 밖 형식이면 <see cref="NotSupportedException"/>.</summary>
    private static BulkReadKind[] ColumnReaders(OracleDataReader reader)
    {
        var result = new BulkReadKind[reader.FieldCount];
        for (var i = 0; i < result.Length; i++)
        {
            var type = reader.GetDataTypeName(i);
            try
            {
                result[i] = BulkBinds.ReadKind(type);
            }
            catch (NotSupportedException)
            {
                throw new NotSupportedException($"데이터 복사가 지원하지 않는 열 형식입니다: {reader.GetName(i)} ({type})");
            }
        }

        return result;
    }

    /// <summary>행 하나. LOB 은 끝까지 읽는다(그리드의 미리보기 경로 <c>OracleValues.ReadCell</c> 을 쓰지 않는다).</summary>
    private static object?[] ReadRow(OracleDataReader reader, BulkReadKind[] readers)
    {
        var row = new object?[readers.Length];
        for (var i = 0; i < readers.Length; i++)
        {
            if (reader.IsDBNull(i))
            {
                continue;
            }

            row[i] = readers[i] switch
            {
                BulkReadKind.Clob => ReadClob(reader, i),
                BulkReadKind.Blob => ReadBlob(reader, i),
                BulkReadKind.Raw => reader.GetOracleBinary(i).Value,
                BulkReadKind.BinaryFloat => reader.GetFloat(i),
                BulkReadKind.BinaryDouble => reader.GetDouble(i),
                BulkReadKind.Number => reader.GetOracleDecimal(i),
                BulkReadKind.Text => reader.GetString(i),
                BulkReadKind.Date => reader.GetOracleDate(i),
                BulkReadKind.Timestamp => reader.GetOracleTimeStamp(i),
                BulkReadKind.TimestampTz => reader.GetOracleTimeStampTZ(i),
                BulkReadKind.TimestampLtz => reader.GetOracleTimeStampLTZ(i),
                BulkReadKind.IntervalDs => reader.GetOracleIntervalDS(i),
                _ => reader.GetOracleIntervalYM(i),
            };
        }

        return row;
    }

    private static string ReadClob(OracleDataReader reader, int i)
    {
        using var clob = reader.GetOracleClob(i);
        return clob.Value;
    }

    private static byte[] ReadBlob(OracleDataReader reader, int i)
    {
        using var blob = reader.GetOracleBlob(i);
        return blob.Value;
    }
}
