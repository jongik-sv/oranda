using System.Data.Common;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Execution.Providers;
using Oranda.Core.Rollback;
using Oranda.Core.Sessions;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Results;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Rollback;

/// <summary>
/// Tibero 세션 연결 위의 되돌리기 통로(<see cref="IRollbackChannel"/>, 설계 13.2·15장). 실행 서비스가 만든다.
/// <list type="bullet">
/// <item><see cref="Locked"/>: Begin·Complete·Probe·시점 읽기 단계. 부른 쪽(실행 서비스)이 이미 명령 잠금을 쥐고 있어 잠금을 잡지 않는다(재진입하면 교착).</item>
/// <item><see cref="Gated"/>: Prepare 단계(명령 잠금 밖). 메서드마다 <see cref="TiberoSession.CommandGate"/> 를 잡고 놓는다.</item>
/// </list>
/// 드라이버 호출은 동기로 돌므로 스레드 풀(<c>Task.Run</c>)에서 부른다. 리더는 <c>maxRows</c> + 1 행까지 읽고(넘침 확인) 닫은 뒤 돌려준다.
/// 서버 오류는 분류한 <see cref="DbError"/> 로 <see cref="RollbackChannelException"/>, 끊김이면 세션을 Broken 으로 돌린다(<see cref="TiberoSession.ReportFailure"/>).
/// 서버 오류가 아닌 예외(드라이버 어댑터 없음, 바인드·값 변환, 닫힌 연결)와 잠금 대기 초과(<see cref="SessionBusyException"/>)도
/// <see cref="ClientErrorCode"/> 로 감싼다(Oracle 통로와 같은 규칙, RB-10a). 취소는 토큰 → <c>DbCommand.Cancel()</c>(실행 서비스와 같다) 뒤 <see cref="OperationCanceledException"/>.
/// </summary>
internal sealed class TiberoRollbackChannel : IRollbackChannel
{
    /// <summary>서버 오류가 아닌 실패를 <see cref="RollbackChannelException"/> 에 싣는 코드(Oracle 통로와 같다).</summary>
    internal const string ClientErrorCode = "ORANDA-ROLLBACK-CHANNEL";

    private readonly TiberoSession _session;
    private readonly bool _gated;

    private TiberoRollbackChannel(TiberoSession session, bool gated)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _gated = gated;
    }

    /// <summary>부른 쪽이 명령 잠금을 쥔 동안 쓰는 통로(잠금을 다시 잡지 않는다).</summary>
    public static TiberoRollbackChannel Locked(TiberoSession session) => new(session, gated: false);

    /// <summary>메서드마다 잠금을 잡는 통로인지.</summary>
    internal bool IsGated => _gated;

    /// <summary>이 통로로 문장을 한 번이라도 보냈다(시작 단계가 내부 저장점·잠금 읽기·시점 읽기를 했는지 실행 서비스가 본다).</summary>
    internal bool HasSent { get; private set; }

    /// <summary>메서드마다 명령 잠금을 잡고 놓는 통로(Prepare 단계).</summary>
    public static TiberoRollbackChannel Gated(TiberoSession session) => new(session, gated: true);

    public Task<RollbackRows> QueryAsync(string sql, IReadOnlyDictionary<string, BindValue> binds, int maxRows, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRows);
        binds ??= new Dictionary<string, BindValue>();
        return RunAsync(sql, async cmd =>
        {
            if (binds.Count > 0)
            {
                var driver = _session.Driver ?? throw new InvalidOperationException("세션에 Tibero 드라이버 어댑터가 없어 바인드를 만들 수 없습니다.");
                foreach (var (name, value) in binds)
                {
                    cmd.Parameters.Add(TiberoBindBuilder.Create(driver, cmd, name, value));
                }
            }

            var rows = new List<object?[]>();
            var truncated = false;
            var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (rows.Count >= maxRows)
                    {
                        truncated = true;
                        break;
                    }

                    rows.Add(ReadRow(reader));
                }
            }

            return new RollbackRows(rows, truncated);
        }, ct);
    }

    public Task ExecuteAsync(string sql, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        return RunAsync(sql, async cmd => await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false), ct);
    }

    private async Task<T> RunAsync<T>(string sql, Func<DbCommand, Task<T>> run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_session.State is SessionState.Broken or SessionState.Closed)
        {
            throw new RollbackChannelException(new DbError(ClientErrorCode, $"세션이 {_session.State} 상태입니다.")
            {
                Dbms = Dbms.Tibero,
                Kind = DbErrorKind.Disconnected,
            });
        }

        if (_gated)
        {
            try
            {
                await _session.CommandGate.EnterAsync(ct).ConfigureAwait(false);
            }
            catch (SessionBusyException ex)
            {
                throw new RollbackChannelException(new DbError(ClientErrorCode, ex.Message) { Dbms = Dbms.Tibero, Kind = DbErrorKind.ResourceBusy }, ex);
            }
        }

        try
        {
            HasSent = true;
            return await Task.Run(async () =>
            {
                var cmd = _session.CreateCommand(sql);
                await using (cmd.ConfigureAwait(false))
                {
                    try
                    {
                        using (cmd.CancelOnToken(ct))
                        {
                            return await run(cmd).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (TiberoExecutionService.IsCancellation(ex, ct))
                    {
                        throw new OperationCanceledException("되돌리기 통로 문장을 취소했습니다.", ex, ct);
                    }
                    catch (Exception ex) when (TiberoErrorClassifier.IsDriverException(ex))
                    {
                        _session.ReportFailure(ex);
                        var error = TiberoErrorClassifier.ToDbError(ex, sql) ?? new DbError("TBR", ex.Message) { Dbms = Dbms.Tibero };
                        throw new RollbackChannelException(error, ex);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or InvalidCastException or OverflowException
                                               or FormatException or ArgumentException or NotSupportedException)
                    {
                        // 서버 오류가 아닌 실패도 엔진이 한 갈래로 받게 계약 예외로 감싼다. 종류는 Other(「만들지 않음 · 실패」).
                        throw new RollbackChannelException(new DbError(ClientErrorCode, ex.Message) { Dbms = Dbms.Tibero, Kind = DbErrorKind.Other }, ex);
                    }
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (_gated)
            {
                _session.CommandGate.Release();
            }
        }
    }

    /// <summary>한 행. BINARY_FLOAT·BINARY_DOUBLE 은 .NET float·double(방언의 리터럴이 R 형식으로 쓴다), 그 밖은 드라이버 값(대개 읽는 식의 글자). 공급자 NULL 도 null.</summary>
    private object?[] ReadRow(DbDataReader reader)
    {
        var row = new object?[reader.FieldCount];
        for (var i = 0; i < row.Length; i++)
        {
            if (reader.IsDBNull(i))
            {
                continue;
            }

            var type = reader.GetFieldType(i);
            object? value = type == typeof(double) ? reader.GetDouble(i)
                : type == typeof(float) ? reader.GetFloat(i)
                : type == typeof(string) ? reader.GetString(i)
                : reader.GetValue(i);
            row[i] = _session.Driver is { } driver && driver.IsProviderNull(value) ? null : value;
        }

        return row;
    }
}
