using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Grid;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Execution;

/// <summary>
/// 질의 결과(TRD 4.4). 커서는 앞으로만 간다: 이미 지나간 페이지는 다시 줄 수 없고(그리드 캐시가 보관), 마지막으로 준 페이지만 다시 준다.
/// FetchSize = RowSize × 페이지 크기. 페이지 가져오기는 그 세션의 실행 줄을 함께 쓴다(같은 연결을 동시에 쓰지 않게).
/// 페이지 가져오기 취소는 행 사이에서 받아 다음 페이지를 이어 가져올 수 있게 한다. 서버 왕복이 늦으면 드라이버를 취소하고(하드 취소),
/// 그 결과는 더 가져올 수 없다(<see cref="FetchCancellation"/>).
/// </summary>
internal sealed class OracleResultSet : IResultSet
{
    private readonly OracleSession _session;
    private readonly SemaphoreSlim _gate;
    private readonly OracleCommand? _command;
    private readonly OracleDataReader _reader;
    private readonly int _pageSize;
    private ResultPage? _last;
    private List<object?>[]? _partial; // 취소로 끊긴 페이지에 이미 읽은 행. 다음 요청이 이어서 채운다
    private int _nextPage;
    private long _rowsRead;
    private bool _complete;
    private bool _cut; // 하드 취소가 나갔다: 리더를 더 쓸 수 없다
    private int _disposed;

    /// <param name="command">리더를 만든 명령(REF CURSOR 면 null). 이 객체가 Dispose 한다.</param>
    public OracleResultSet(OracleSession session, SemaphoreSlim gate, OracleCommand? command, OracleDataReader reader, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        _session = session;
        _gate = gate;
        _command = command;
        _reader = reader;
        _pageSize = pageSize;
        Columns = OracleValues.ReadColumns(reader);
        if (reader.RowSize > 0)
        {
            reader.FetchSize = reader.RowSize * pageSize;
        }
    }

    public IReadOnlyList<ResultColumn> Columns { get; }

    public bool IsComplete => Volatile.Read(ref _complete);

    public long? KnownRowCount => IsComplete ? Interlocked.Read(ref _rowsRead) : null;

    /// <summary>시험용: 리더에 설정한 FetchSize.</summary>
    internal long FetchSize => _reader.FetchSize;

    /// <summary>시험용: 다음에 읽을 페이지 번호.</summary>
    internal int NextPageIndex => _nextPage;

    /// <summary>시험용: 하드 취소 뒤 늦은 브레이크(ORA-01013)를 받아 다시 한 왕복 수.</summary>
    internal int LateBreaksAbsorbed { get; private set; }

    public async Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        await _gate.WaitAsync(ct);
        _session.SetBusy(true);
        try
        {
            if (_last is { } last && last.PageIndex == pageIndex)
            {
                return last;
            }

            if (pageIndex < _nextPage)
            {
                throw new InvalidOperationException($"결과 커서는 앞으로만 갑니다. 페이지 {pageIndex} 는 이미 지났습니다.");
            }

            if (_cut)
            {
                // 서버에 가지 않는다: 드라이버 리더가 취소 상태라 어차피 ORA-01013 이다.
                throw new InvalidOperationException("취소로 결과가 끊겼습니다. 다시 실행하세요.");
            }

            using var cancel = new FetchCancellation(FetchCancellation.HardCancelDelay, ct);
            try
            {
                ResultPage page;
                do
                {
                    page = await ReadPageAsync(ct, cancel.DriverToken);
                }
                while (page.PageIndex < pageIndex);

                return page;
            }
            finally
            {
                if (cancel.HardCancelled)
                {
                    _cut = true;
                    _partial = null;
                    await AbsorbLateBreakAsync();
                }
            }
        }
        catch (OracleException ex)
        {
            _session.ReportFailure(ex);
            if (ex.Number == OracleExecutionService.CancelledNumber && ct.IsCancellationRequested)
            {
                throw new OperationCanceledException("페이지 가져오기를 취소했습니다.", ex, ct);
            }

            throw;
        }
        finally
        {
            _session.SetBusy(false);
            _gate.Release();
        }
    }

    private async Task<ResultPage> ReadPageAsync(CancellationToken ct, CancellationToken driverCt)
    {
        var index = _nextPage;
        var first = Interlocked.Read(ref _rowsRead);
        var buffers = _partial ?? NewBuffers();
        _partial = null;
        var count = buffers.Length == 0 ? 0 : buffers[0].Count;
        var sharer = new PageValueSharer(buffers.Length);
        try
        {
            while (!_complete && count < _pageSize)
            {
                // 사용자 취소는 행 사이에서 받는다: 진행 중인 왕복(FetchSize, 약 한 페이지)이 끝난 뒤라 커서가 이어진다.
                // 드라이버에는 하드 취소 토큰만 넘긴다(왕복이 늦을 때만 취소됨, FetchCancellation).
                ct.ThrowIfCancellationRequested();
                var more = await _reader.ReadAsync(driverCt);
                if (!more)
                {
                    Volatile.Write(ref _complete, true);
                    break;
                }

                for (var c = 0; c < buffers.Length; c++)
                {
                    buffers[c].Add(sharer.Share(c, OracleValues.ReadCell(_reader, c, Columns[c].Kind)));
                }

                count++;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or OracleException { Number: OracleExecutionService.CancelledNumber })
        {
            // 행 사이에서 끊었으므로 커서는 이어진다. 읽은 행을 버리면 다음 페이지부터 행이 빠지고 밀린다.
            _partial = buffers;
            throw;
        }

        foreach (var buffer in buffers)
        {
            buffer.TrimExcess();
        }

        Interlocked.Add(ref _rowsRead, count);
        _nextPage = index + 1;
        var page = new ResultPage(index, first, count, buffers) { IsLast = _complete };
        _last = page;
        return page;
    }

    /// <summary>
    /// 하드 취소의 브레이크가 왕복이 막 끝난 뒤 나갔으면(ODP 는 m_bFetching 확인과 Break 사이에 잠금이 없다) 서버가 그 브레이크를
    /// 같은 연결의 다음 호출에 ORA-01013 으로 돌려준다. 실행 줄을 쥔 채 가벼운 왕복으로 먼저 받아 다음 문장으로 새지 않게 한다.
    /// </summary>
    private async Task AbsorbLateBreakAsync()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await using var cmd = _session.Connection.CreateCommand();
                cmd.CommandText = "BEGIN NULL; END;";
                await cmd.ExecuteNonQueryAsync(CancellationToken.None);
                return;
            }
            catch (Exception ex) when (ex is OperationCanceledException or OracleException { Number: OracleExecutionService.CancelledNumber })
            {
                // 늦은 브레이크를 받았다: 한 번 더 왕복해 연결이 깨끗한지 본다.
                LateBreaksAbsorbed++;
            }
            catch (Exception ex) when (ex is OracleException or InvalidOperationException)
            {
                // 연결 문제는 다음 문장이 알린다(취소 예외를 가리지 않는다).
                return;
            }
        }
    }

    private List<object?>[] NewBuffers()
    {
        var buffers = new List<object?>[Columns.Count];
        for (var c = 0; c < buffers.Length; c++)
        {
            buffers[c] = new List<object?>(Math.Min(_pageSize, 1024));
        }

        return buffers;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            await _reader.DisposeAsync();
            if (_command is not null)
            {
                await _command.DisposeAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
