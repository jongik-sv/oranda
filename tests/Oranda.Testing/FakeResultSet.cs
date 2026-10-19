using Oranda.Core.Grid;

namespace Oranda.Testing;

/// <summary>메모리 행으로 만든 <see cref="IResultSet"/>. 페이지를 요청한 만큼만 「가져온」 것으로 친다.</summary>
public sealed class FakeResultSet : IResultSet
{
    private readonly IReadOnlyList<IReadOnlyList<object?>> _rows;
    private readonly int _pageSize;
    private int _fetchedPages;

    /// <param name="columns">열 정의</param>
    /// <param name="rows">행 지향 값(<c>rows[행][열]</c>)</param>
    /// <param name="pageSize">페이지당 행 수</param>
    public FakeResultSet(IReadOnlyList<ResultColumn> columns, IReadOnlyList<IReadOnlyList<object?>> rows, int pageSize = 500)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        Columns = columns;
        _rows = rows;
        _pageSize = pageSize;
    }

    /// <summary>문자열 열만 있는 결과를 간단히 만든다.</summary>
    public static FakeResultSet FromText(IReadOnlyList<string> columnNames, params object?[][] rows) =>
        new(columnNames.Select((n, i) => new ResultColumn(i, n, "VARCHAR2", ColumnDataKind.Text)).ToList(), rows);

    public IReadOnlyList<ResultColumn> Columns { get; }

    public bool IsComplete => (long)_fetchedPages * _pageSize >= _rows.Count;

    public long? KnownRowCount => IsComplete ? _rows.Count : null;

    public int PageRequests { get; private set; }

    public bool IsDisposed { get; private set; }

    public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        PageRequests++;
        _fetchedPages = Math.Max(_fetchedPages, pageIndex + 1);

        var first = pageIndex * _pageSize;
        var count = Math.Clamp(_rows.Count - first, 0, _pageSize);
        var columns = Columns
            .Select(c => (IReadOnlyList<object?>)Enumerable.Range(first, count).Select(r => _rows[r][c.Ordinal]).ToList())
            .ToList();

        return Task.FromResult(new ResultPage(pageIndex, first, count, columns) { IsLast = first + count >= _rows.Count });
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}
