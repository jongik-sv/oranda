using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Execution;
using Oranda.Core.Execution.Providers;
using Oranda.Core.Rollback;
using Oranda.Core.Sessions;
using Oranda.Oracle.Execution;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Rollback;

/// <summary>
/// Oracle 세션 연결 위의 되돌리기 통로(<see cref="IRollbackChannel"/>, 설계 5장·13.2). 기능 안쪽 문장을 정책 관문 없이 연결에 직접 보내고
/// 실행 이력·이벤트에 남기지 않는다. 두 벌이 있다.
/// <list type="bullet">
/// <item><see cref="Locked"/>: 호출한 쪽이 명령 잠금(<see cref="OracleSession.CommandGate"/>)을 쥔 채 쓴다(Begin·Complete·Probe·시점 읽기).</item>
/// <item><see cref="Gated"/>: 메서드마다 명령 잠금을 잡고 놓는다(Prepare, 정책 관문 앞).</item>
/// </list>
/// 서버 오류는 분류한 <see cref="DbError"/>(<see cref="DbError.Kind"/> 채움)를 담은 <see cref="RollbackChannelException"/>, 취소는 드라이버 명령을 취소한 뒤
/// <see cref="OperationCanceledException"/>. 서버 오류가 아닌 드라이버 예외(닫힌 연결 등)와 잠금 대기 초과(<see cref="SessionBusyException"/>)도
/// <see cref="RollbackChannelException"/>(<see cref="ClientErrorCode"/>)로 감싼다. 리더는 끝까지(또는 <c>maxRows</c> + 1 행까지) 읽고 닫은 뒤 돌려준다.
/// </summary>
internal sealed class OracleRollbackChannel : IRollbackChannel
{
    /// <summary>잠금 읽기·직후 값 읽기는 행이 많을 수 있다. 한 번 왕복에 최대 이만큼 행을 받는다(FetchSize = RowSize × 행 수).</summary>
    internal const int FetchRows = 1000;

    /// <summary>한 번 왕복의 버퍼 상한(바이트). 넓은 행(VARCHAR2(4000) 여러 열, AL32UTF8)이 큰 버퍼를 잡지 않게 한다.</summary>
    internal const long MaxFetchBytes = 8L * 1024 * 1024;

    /// <summary>드라이버가 서버 오류 밖의 예외(닫힌 연결 등)를 냈을 때 <see cref="RollbackChannelException"/> 에 싣는 코드.</summary>
    internal const string ClientErrorCode = "ORANDA-ROLLBACK-CHANNEL";

    private readonly OracleSession _session;
    private readonly bool _gated;

    private OracleRollbackChannel(OracleSession session, bool gated)
    {
        _session = session;
        _gated = gated;
    }

    /// <summary>명령 잠금을 쥔 호출자용 통로(잠금을 다시 잡지 않는다).</summary>
    public static OracleRollbackChannel Locked(OracleSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new OracleRollbackChannel(session, gated: false);
    }

    /// <summary>메서드마다 명령 잠금을 잡고 놓는 통로(정책 관문 앞 Prepare 단계).</summary>
    public static OracleRollbackChannel Gated(OracleSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new OracleRollbackChannel(session, gated: true);
    }

    /// <summary>메서드마다 잠금을 잡는 통로인지.</summary>
    internal bool IsGated => _gated;

    public async Task<RollbackRows> QueryAsync(string sql, IReadOnlyDictionary<string, BindValue> binds, int maxRows, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRows);
        binds ??= new Dictionary<string, BindValue>();
        return await WithGateAsync(() => RunQueryAsync(sql, binds, maxRows, ct), ct);
    }

    public async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        await WithGateAsync(async () =>
        {
            await RunAsync(async cmd =>
            {
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(ct);
                return 0;
            }, ct);
            return 0;
        }, ct);
    }

    private async Task<RollbackRows> RunQueryAsync(string sql, IReadOnlyDictionary<string, BindValue> binds, int maxRows, CancellationToken ct) =>
        await RunAsync(async cmd =>
        {
            cmd.CommandText = sql;
            foreach (var (name, bind) in binds)
            {
                cmd.Parameters.Add(BindBuilder.Create(name, bind));
            }

            await using var reader = (OracleDataReader)await cmd.ExecuteReaderAsync(ct);
            if (FetchSizeFor(reader.RowSize, maxRows) is { } fetchSize)
            {
                reader.FetchSize = fetchSize;
            }

            var rows = new List<object?[]>();
            var truncated = false;
            while (await reader.ReadAsync(ct))
            {
                if (rows.Count == maxRows)
                {
                    truncated = true; // maxRows 보다 많다. 나머지는 읽지 않고 리더를 닫는다.
                    break;
                }

                rows.Add(ReadRow(reader));
            }

            return new RollbackRows(rows, truncated);
        }, ct);

    /// <summary>
    /// 한 왕복 버퍼 크기: <c>RowSize × min(maxRows + 1, FetchRows)</c>(maxRows + 1 = 넘침 확인 행), <see cref="MaxFetchBytes"/> 를 넘지 않게(적어도 한 행).
    /// RowSize 를 모르면 null(드라이버 기본값).
    /// </summary>
    internal static long? FetchSizeFor(long rowSize, int maxRows)
    {
        if (rowSize <= 0)
        {
            return null;
        }

        var rows = Math.Min((long)maxRows + 1, FetchRows);
        rows = Math.Max(1, Math.Min(rows, MaxFetchBytes / rowSize));
        return rowSize * rows;
    }

    /// <summary>
    /// 한 행. NUMBER 는 decimal(28자리) 안이면 decimal, 넘으면 드라이버 원시 글(정밀도 보존, <see cref="OracleValues.FromDecimal"/>).
    /// 그 밖은 .NET 기본 형식(<see cref="OracleValues.Normalize"/>). DB NULL 은 null.
    /// 정밀도가 중요한 값은 방언의 읽는 식(<c>TO_CHAR</c> 등)으로 글자로 읽으므로 여기서 바꿀 일은 거의 없다.
    /// </summary>
    internal static object?[] ReadRow(OracleDataReader reader)
    {
        var row = new object?[reader.FieldCount];
        for (var i = 0; i < row.Length; i++)
        {
            if (reader.IsDBNull(i))
            {
                continue;
            }

            row[i] = reader.GetFieldType(i) == typeof(decimal)
                ? OracleValues.FromDecimal(reader.GetOracleDecimal(i))
                : OracleValues.Normalize(reader.GetValue(i));
        }

        return row;
    }

    private async Task<T> WithGateAsync<T>(Func<Task<T>> body, CancellationToken ct)
    {
        if (!_gated)
        {
            return await body();
        }

        try
        {
            await _session.CommandGate.EnterAsync(ct);
        }
        catch (SessionBusyException ex)
        {
            throw new RollbackChannelException(new DbError(ClientErrorCode, ex.Message) { Kind = DbErrorKind.ResourceBusy }, ex);
        }

        try
        {
            return await body();
        }
        finally
        {
            _session.CommandGate.Release();
        }
    }

    /// <summary>명령 하나를 만들어 보내고, 드라이버 오류를 통로 계약(취소·분류한 오류)으로 바꾼다. 끊김이면 세션을 Broken 으로 돌린다.</summary>
    private async Task<T> RunAsync<T>(Func<OracleCommand, Task<T>> send, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var cmd = _session.Connection.CreateCommand();
            cmd.BindByName = true;
            using (cmd.CancelOnToken(ct))
            {
                var result = await send(cmd);
                ct.ThrowIfCancellationRequested();
                return result;
            }
        }
        catch (OracleException ex) when (OracleExecutionService.IsCancellation(ex, ct))
        {
            throw new OperationCanceledException("되돌리기 통로 명령을 취소했습니다.", ex, ct);
        }
        catch (OracleException ex)
        {
            _session.ReportFailure(ex);
            throw new RollbackChannelException(OracleErrorClassifier.ToDbError(ex), ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or InvalidCastException or OverflowException
                                   or FormatException or ArgumentException)
        {
            // 서버 오류가 아닌 드라이버 예외(닫힌 연결, 값 변환)도 엔진이 한 갈래로 받게 계약 예외로 감싼다. 종류는 Other(「만들지 않음 · 실패」).
            throw new RollbackChannelException(new DbError(ClientErrorCode, ex.Message) { Kind = DbErrorKind.Other }, ex);
        }
    }
}
