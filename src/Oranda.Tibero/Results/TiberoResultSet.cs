using System.Data.Common;
using Oranda.Core.Execution;
using Oranda.Core.Execution.Providers;
using Oranda.Core.Grid;
using Oranda.Tibero.Execution;

namespace Oranda.Tibero.Results;

/// <summary>한 셀을 그리드 값으로 읽는다(<c>TiberoValues</c>, 드라이버 어댑터를 쓰는 쪽). 한 행의 열 순서대로 불린다.</summary>
internal delegate object? TiberoCellReader(DbDataReader reader, int ordinal, ResultColumn column);

/// <summary>세션이 결과 집합에 건네는 연결점. 모두 선택이다(시험은 일부만 쓴다).</summary>
internal sealed class TiberoResultHooks
{
    /// <summary>페이지를 가져오는 동안 세션을 「바쁨」으로 표시한다.</summary>
    public Action<bool>? SetBusy { get; init; }

    /// <summary>드라이버 오류를 세션에 알린다(연결 끊김 감지 등).</summary>
    public Action<Exception>? ReportFailure { get; init; }

    /// <summary>서버에 가 있는 읽기를 끊는다(<c>DbCommand.Cancel()</c>). 하드 취소 때만 불린다.</summary>
    public Action? CancelServer { get; init; }

    /// <summary>드라이버 예외가 「취소됨」 분류인지. 기본 = <see cref="TiberoErrorClassifier"/>.</summary>
    public Func<Exception, bool>? IsCancelled { get; init; }
}

/// <summary>
/// 질의 결과(TRD 4.4). 커서는 앞으로만 간다: 지나간 페이지는 그리드 캐시가 보관하고, 마지막으로 준 페이지만 다시 준다.
/// 페이지 가져오기는 세션의 명령 잠금(<see cref="SessionCommandGate"/>, D-041)을 페이지마다 잡는다.
/// Tibero 드라이버(1.1.35)는 비동기 읽기가 동기라 페이지 읽기를 통째로 스레드 풀에서 돌리고(UI 를 막지 않음), 한 번에 가져오는 행 수
/// (FetchSize)는 조절할 수 없어 설정하지 않는다(docs/design/tibero-driver-values.md 1장·7장).
/// 사용자 취소는 행 사이에서 받아 이어 가져올 수 있게 하고, 서버 왕복이 늦으면 <see cref="TiberoResultHooks.CancelServer"/> 로 끊는다(하드 취소).
/// 하드 취소가 나간 결과는 더 가져오지 않는다(<see cref="FetchCancellation"/>): 취소 뒤 리더 상태를 보장할 수 없다.
/// </summary>
internal sealed class TiberoResultSet : IResultSet
{
    private readonly SessionCommandGate _gate;
    private readonly DbCommand? _command;
    private readonly DbDataReader _reader;
    private readonly TiberoCellReader _readCell;
    private readonly TiberoResultHooks _hooks;
    private readonly int _pageSize;
    private ResultPage? _last;
    private List<object?>[]? _partial; // 취소로 끊긴 페이지에 이미 읽은 행. 다음 요청이 이어서 채운다
    private int _nextPage;
    private long _rowsRead;
    private bool _complete;
    private bool _cut; // 하드 취소가 나갔다: 리더를 더 쓸 수 없다
    private int _disposed;

    /// <param name="command">리더를 만든 명령(REF CURSOR 면 null). 이 객체가 Dispose 한다.</param>
    public TiberoResultSet(SessionCommandGate gate, DbCommand? command, DbDataReader reader, IReadOnlyList<ResultColumn> columns,
                           TiberoCellReader readCell, int pageSize, TiberoResultHooks? hooks = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(readCell);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        _gate = gate;
        _command = command;
        _reader = reader;
        Columns = columns;
        _readCell = readCell;
        _pageSize = pageSize;
        _hooks = hooks ?? new TiberoResultHooks();
    }

    public IReadOnlyList<ResultColumn> Columns { get; }

    public int? PageSize => _pageSize;

    public bool IsComplete => Volatile.Read(ref _complete);

    public long? KnownRowCount => IsComplete ? Interlocked.Read(ref _rowsRead) : null;

    /// <summary>시험용: 다음에 읽을 페이지 번호.</summary>
    internal int NextPageIndex => _nextPage;

    /// <summary>시험용: 하드 취소로 끊겼는지.</summary>
    internal bool IsCut => _cut;

    public async Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        await _gate.EnterAsync(ct).ConfigureAwait(false);
        _hooks.SetBusy?.Invoke(true);
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
                // 서버에 가지 않는다: 취소된 리더는 이어 읽을 수 없다.
                throw new InvalidOperationException("취소로 결과가 끊겼습니다. 다시 실행하세요.");
            }

            using var cancel = new FetchCancellation(FetchCancellation.HardCancelDelay, ct);
            using var hard = cancel.DriverToken.Register(static s => ((TiberoResultSet)s!).CancelServer(), this);
            try
            {
                // 드라이버 읽기는 동기라 호출 스레드를 막는다: 스레드 풀에서 돌린다. 사용자 취소는 행 사이에서 받는다.
                return await Task.Run(() => ReadUntil(pageIndex, ct), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && IsCancelled(ex))
            {
                _hooks.ReportFailure?.Invoke(ex);
                throw new OperationCanceledException("페이지 가져오기를 취소했습니다.", ex, ct);
            }
            finally
            {
                if (cancel.HardCancelled)
                {
                    _cut = true;
                    _partial = null;
                }
            }
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or InvalidOperationException or ObjectDisposedException))
        {
            _hooks.ReportFailure?.Invoke(ex);
            throw;
        }
        finally
        {
            _hooks.SetBusy?.Invoke(false);
            _gate.Release();
        }
    }

    private void CancelServer()
    {
        try
        {
            _hooks.CancelServer?.Invoke();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or DbException)
        {
            // 이미 끝났거나 닫힌 명령: 취소할 것이 없다.
        }
    }

    private bool IsCancelled(Exception ex) =>
        _hooks.IsCancelled?.Invoke(ex) ?? TiberoErrorClassifier.ToDbError(ex, null)?.Kind == DbErrorKind.Cancelled;

    private ResultPage ReadUntil(int pageIndex, CancellationToken ct)
    {
        ResultPage page;
        do
        {
            page = ReadPage(ct);
        }
        while (page.PageIndex < pageIndex);

        return page;
    }

    private ResultPage ReadPage(CancellationToken ct)
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
                ct.ThrowIfCancellationRequested();
                if (!_reader.Read())
                {
                    Volatile.Write(ref _complete, true);
                    break;
                }

                for (var c = 0; c < buffers.Length; c++)
                {
                    buffers[c].Add(sharer.Share(c, _readCell(_reader, c, Columns[c])));
                }

                count++;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException || IsCancelled(ex))
        {
            // 행 사이에서 끊었거나 서버가 읽기를 끊었다. 읽은 행을 버리면 다음 페이지부터 행이 빠지고 밀리니 보관한다(하드 취소면 어차피 버려진다).
            // 행 한가운데서 끊긴 경우를 피하려고 완성된 행만 보관한다.
            _partial = TrimToWholeRows(buffers);
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

    private static List<object?>[] TrimToWholeRows(List<object?>[] buffers)
    {
        if (buffers.Length == 0)
        {
            return buffers;
        }

        var whole = buffers.Min(b => b.Count);
        foreach (var buffer in buffers)
        {
            if (buffer.Count > whole)
            {
                buffer.RemoveRange(whole, buffer.Count - whole);
            }
        }

        return buffers;
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

        await _gate.EnterForCleanupAsync().ConfigureAwait(false);
        try
        {
            await _reader.DisposeAsync().ConfigureAwait(false);
            if (_command is not null)
            {
                await _command.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
