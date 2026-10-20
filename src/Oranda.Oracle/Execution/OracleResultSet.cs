using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Grid;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Execution;

/// <summary>
/// 질의 결과(TRD 4.4). 커서는 앞으로만 간다: 이미 지나간 페이지는 다시 줄 수 없고(그리드 캐시가 보관), 마지막으로 준 페이지만 다시 준다.
/// FetchSize = RowSize × 페이지 크기. 페이지 가져오기는 그 세션의 실행 줄을 함께 쓴다(같은 연결을 동시에 쓰지 않게).
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

            using var reg = _command is null ? default : _command.CancelOnToken(ct);
            ResultPage page;
            do
            {
                page = await ReadPageAsync(ct);
            }
            while (page.PageIndex < pageIndex);

            return page;
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

    private async Task<ResultPage> ReadPageAsync(CancellationToken ct)
    {
        var index = _nextPage;
        var first = Interlocked.Read(ref _rowsRead);
        var buffers = _partial ?? NewBuffers();
        _partial = null;
        var count = buffers.Length == 0 ? 0 : buffers[0].Count;
        try
        {
            while (!_complete && count < _pageSize)
            {
                if (!await _reader.ReadAsync(ct))
                {
                    Volatile.Write(ref _complete, true);
                    break;
                }

                for (var c = 0; c < buffers.Length; c++)
                {
                    buffers[c].Add(OracleValues.ReadCell(_reader, c, Columns[c].Kind));
                }

                count++;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or OracleException { Number: OracleExecutionService.CancelledNumber })
        {
            // 취소 뒤에도 커서는 이어진다(TSK-06-11 실측). 읽은 행을 버리면 다음 페이지부터 행이 빠지고 밀린다.
            _partial = buffers;
            throw;
        }

        Interlocked.Add(ref _rowsRead, count);
        _nextPage = index + 1;
        var page = new ResultPage(index, first, count, buffers) { IsLast = _complete };
        _last = page;
        return page;
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
