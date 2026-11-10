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
    /// <summary>
    /// 한 번에 가져올 행 수(FetchSize = RowSize × 이 값). LOB 미리 받기(<see cref="ExecutionOptions.LobPrefetchSize"/>)가 꺼져 있으면
    /// LOB 은 위치자만 오고 값은 열마다 따로 읽는다(LOB 하나마다 서버 왕복).
    /// </summary>
    internal const int FetchRows = 1000;

    /// <summary>
    /// LOB 미리 받기를 켠 읽기에서 서버 왕복 한 번에 받을 양의 상한(바이트, 드라이버 행 버퍼 = RowSize × 행 수).
    /// <para>
    /// 근거(ODP.NET 문서 「Obtaining LOB Data」·<c>OracleDataReader.RowSize</c>): <c>InitialLOBFetchSize</c> 를 0 보다 크게 두면 LOB 마다 그 크기까지
    /// (CLOB·NCLOB 은 글자, BLOB 은 바이트) 행과 함께 받아 리더 버퍼에 두고, <c>RowSize</c> 에 그 크기가 들어간다. 그래서 LOB 열이 있는 질의에
    /// 「RowSize × <see cref="FetchRows"/>」를 그대로 쓰면 버퍼가 미리 받기 크기 × 1000 행만큼 커질 수 있다(64K 글자 CLOB 하나면 수백 MB).
    /// 이 상한으로 한 번에 받는 행 수를 줄인다(적어도 1행, 많아야 <see cref="FetchRows"/>). LOB 열이 없는 질의·미리 받기를 끈 읽기는 옛 계산 그대로다.
    /// </para>
    /// </summary>
    internal const long LobPrefetchFetchBytes = 32L * 1024 * 1024;

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
            await foreach (var row in ReadRowsAsync(session, query, binds, options, ct))
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
                                                                   IReadOnlyDictionary<string, BindValue> binds, ExecutionOptions options,
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
            var prefetch = ApplyLobPrefetch(cmd, options);
            BindBuilder.Build(cmd, query, binds);
            using var cancel = cmd.CancelOnToken(ct);

            BulkReadKind[] readers;
            try
            {
                reader = (OracleDataReader)await cmd.ExecuteReaderAsync(ct);
                readers = ColumnReaders(reader);
                prefetch = prefetch && HasLob(readers);
                reader.FetchSize = FetchSizeFor(reader.RowSize, prefetch); // 첫 Read 전에 둔다(첫 왕복부터 먹게)
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

                    row = ReadRow(reader, readers, prefetch);
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

    /// <summary>
    /// 대량 읽기 명령에 LOB 미리 받기 크기를 둔다(<see cref="ExecutionOptions.LobPrefetchSize"/> 가 0 보다 클 때만, 실행 전). 두었으면 true.
    /// <para>
    /// ODP.NET(Managed) <c>OracleCommand.InitialLOBFetchSize</c> 의 뜻(문서 「Obtaining LOB Data」, 12.1.0.2 이후 동작): 0(기본) = 위치자만 받고
    /// 값은 꺼낼 때 LOB 마다 따로 받는다. 0 보다 크면 LOB 마다 그 크기까지(CLOB·NCLOB 글자, BLOB 바이트) <c>Read</c> 때 행과 함께 받고, 더 긴 값은
    /// 꺼낼 때(형식 접근자) 나머지를 서버에서 더 받는다 — 값은 언제나 끝까지 오고 기본 키·ROWID 가 없어도 된다. -1 = 크기 상한 없이 LOB 전체를
    /// 행 버퍼에 받는다(최대 2GB).
    /// </para>
    /// <para>
    /// 복사는 -1 을 쓰지 않는다: 한 번에 받는 행 수만큼 LOB 전체가 드라이버 버퍼에 쌓여 아주 큰 LOB(수백 MB) 이 있으면 메모리 상한이 없다.
    /// 상한 있는 크기면 그보다 큰 값만 옛 방식(나머지 따로 읽기)으로 가고, 큰 값은 왕복 한 번보다 옮기는 시간이 훨씬 길어 손해가 작다.
    /// 문서도 「대부분(80%) 행의 LOB 보다 큰 값」을 권한다. 크기는 호출한 쪽(복사 실행기)이 정한다.
    /// </para>
    /// </summary>
    internal static bool ApplyLobPrefetch(OracleCommand cmd, ExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentNullException.ThrowIfNull(options);
        if (options.LobPrefetchSize is not { } size || size <= 0)
        {
            return false;
        }

        cmd.InitialLOBFetchSize = size;
        return true;
    }

    /// <summary>
    /// 리더의 FetchSize(바이트). 미리 받기를 켠 LOB 질의는 왕복 한 번이 <see cref="LobPrefetchFetchBytes"/> 를 넘지 않게 행 수를 줄이고(적어도 1행),
    /// 그 밖에는 옛 계산(RowSize × <see cref="FetchRows"/>) 그대로다.
    /// </summary>
    internal static long FetchSizeFor(long rowSize, bool lobPrefetch)
    {
        var size = Math.Max(rowSize, 1);
        var rows = lobPrefetch ? Math.Clamp(LobPrefetchFetchBytes / size, 1, FetchRows) : FetchRows;
        return size * rows;
    }

    private static bool HasLob(BulkReadKind[] readers) => Array.Exists(readers, k => k is BulkReadKind.Clob or BulkReadKind.Blob);

    /// <summary>
    /// 행 하나. LOB 은 끝까지 읽는다(그리드의 미리보기 경로 <c>OracleValues.ReadCell</c> 을 쓰지 않는다).
    /// <paramref name="lobPrefetched"/> 면 LOB 을 위치자 대신 값 접근자로 꺼낸다(<see cref="ReadPrefetchedClob"/>·<see cref="ReadPrefetchedBlob"/>).
    /// </summary>
    private static object?[] ReadRow(OracleDataReader reader, BulkReadKind[] readers, bool lobPrefetched)
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
                BulkReadKind.Clob => lobPrefetched ? ReadPrefetchedClob(reader, i) : ReadClob(reader, i),
                BulkReadKind.Blob => lobPrefetched ? ReadPrefetchedBlob(reader, i) : ReadBlob(reader, i),
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

    /// <summary>
    /// 미리 받은 CLOB·NCLOB. 형식 접근자 <c>GetString</c> 은 행과 함께 온 앞부분을 쓰고, 미리 받기 크기보다 긴 값이면 나머지를 서버에서 더 받아
    /// 끝까지 돌려준다(문서). 위치자(<c>GetOracleClob</c>)를 만들지 않으므로 미리 받은 값은 LOB 마다 왕복 없이 쓰인다.
    /// NULL 은 부른 쪽이 이미 걸렀다(IsDBNull): null 이 오면 빈 값(EMPTY_CLOB)으로 본다.
    /// </summary>
    private static string ReadPrefetchedClob(OracleDataReader reader, int i) => reader.GetString(i) ?? string.Empty;

    /// <summary>
    /// 미리 받은 BLOB. <c>GetOracleBinary</c> 는 BLOB 열에 쓸 수 있는 형식 접근자이고 긴 값은 나머지를 더 받는다(문서).
    /// 빈 BLOB(EMPTY_BLOB) 을 미리 받으면 값 없음으로 온다는 드라이버 보고가 있어, NULL 을 이미 거른 이 자리에서는 빈 배열로 본다.
    /// </summary>
    private static byte[] ReadPrefetchedBlob(OracleDataReader reader, int i)
    {
        var value = reader.GetOracleBinary(i);
        return value.IsNull ? [] : value.Value ?? [];
    }
}
