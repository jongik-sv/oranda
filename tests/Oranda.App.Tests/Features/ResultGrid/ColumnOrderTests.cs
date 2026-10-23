using Oranda.App.Features.DataTransfer;
using Oranda.App.Features.ResultGrid;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.ResultGrid;

/// <summary>TSK-13-17 열 순서: 복사·내보내기는 화면 순서, 서버 재조회 내보내기는 감싼 SELECT 의 열 순서, 감쌀 수 없으면 원래 문장.</summary>
public sealed class ColumnOrderTests
{
    private static readonly ResultColumn[] Cols =
    [
        new(0, "A", "NUMBER", ColumnDataKind.Number),
        new(1, "B", "VARCHAR2", ColumnDataKind.Text),
        new(2, "C", "NUMBER", ColumnDataKind.Number),
    ];

    private sealed class Rows : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns => Cols;

        public bool IsComplete => true;

        public long? KnownRowCount => 2;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct) =>
            Task.FromResult(new ResultPage(pageIndex, 0, 2,
                [[(object?)1m, 2m], ["x", "y"], [10m, 20m]])
            { IsLast = true });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<ResultGridModel> LoadAsync()
    {
        var m = new ResultGridModel(new Rows());
        await m.LoadFirstPageAsync();
        return m;
    }

    [Fact]
    public async Task Copy_with_headers_follows_the_screen_column_order()
    {
        await using var m = await LoadAsync();
        Assert.True(m.MoveColumn(2, 0)); // C 를 맨 앞으로: C, A, B
        m.SelectAll();

        var tsv = await m.BuildCopyTextAsync(CopyFormat.Tsv, includeHeaders: true);

        var lines = tsv.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("C\tA\tB", lines[0]);
        Assert.Equal("10\t1\tx", lines[1]);
        var insert = await m.BuildCopyTextAsync(CopyFormat.Insert);
        Assert.Contains("(C, A, B)", insert, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Excel_export_from_the_grid_follows_the_screen_column_order()
    {
        await using var m = await LoadAsync();
        m.MoveColumn(2, 0);
        m.SelectAll();

        var data = ExcelExportPlan.FromGrid(m, ExcelExportScope.LoadedRows);

        Assert.Equal(["C", "A", "B"], data.Columns.Select(c => c.Name));
    }

    [Fact]
    public void Reorder_wraps_the_original_statement_and_projects_by_name()
    {
        var sql = Oranda_Reorder("SELECT a, b, c FROM t WHERE x = :x;  ", [2, 0, 1]);

        Assert.Equal("SELECT \"C\", \"A\", \"B\" FROM (" + Environment.NewLine + "SELECT a, b, c FROM t WHERE x = :x" + Environment.NewLine + ")", sql);
    }

    [Fact]
    public void Reorder_returns_null_when_nothing_changes_or_cannot_be_wrapped()
    {
        Assert.Null(Oranda_Reorder("SELECT a, b, c FROM t", [0, 1, 2])); // 그대로
        Assert.Null(Oranda_Reorder("SELECT a, b, c FROM t FOR UPDATE", [2, 0, 1]));
        Assert.Null(ResultQueryWrapper.Reorder("SELECT a, a FROM t", [new(0, "A", "NUMBER", ColumnDataKind.Number), new(1, "A", "NUMBER", ColumnDataKind.Number)], [1, 0])); // 열 이름 중복
        Assert.Null(ResultQueryWrapper.Reorder("SELECT rowid, a FROM t", [new(0, "ROWID", "ROWID", ColumnDataKind.Text), new(1, "A", "NUMBER", ColumnDataKind.Number)], [1, 0]));
    }

    [Theory]
    [InlineData("SELECT * FROM t FOR UPDATE", true)]
    [InlineData("select * from t for\n  update of x nowait", true)]
    [InlineData("SELECT 'for update' FROM t", false)]
    [InlineData("SELECT * FROM t -- for update", false)]
    [InlineData("SELECT * FROM t /* for update */", false)]
    [InlineData("SELECT \"FOR UPDATE\" FROM t", false)]
    [InlineData("SELECT before_update FROM t", false)]
    public void HasForUpdate_ignores_literals_comments_and_identifiers(string sql, bool expected) =>
        Assert.Equal(expected, ResultQueryWrapper.HasForUpdate(sql));

    [Fact]
    public void Trailing_line_comment_cannot_swallow_the_closing_parenthesis()
    {
        var sql = Oranda_Reorder("SELECT a, b, c FROM t -- all rows", [1, 0, 2]);

        Assert.EndsWith(Environment.NewLine + ")", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_source_uses_the_wrapped_query_only_when_the_order_changed()
    {
        var session = (ISession)null!;
        var query = new SqlStatement("SELECT a, b, c FROM t", default, StatementKind.Query) { Verb = "SELECT" };
        var binds = new Dictionary<string, BindValue>();

        var same = new ExportSource(session, query, binds) { Columns = Cols, ColumnOrder = [0, 1, 2] };
        var moved = new ExportSource(session, query, binds) { Columns = Cols, ColumnOrder = [2, 0, 1] };
        var plain = new ExportSource(session, query, binds);

        Assert.Same(query, same.QueryInColumnOrder());
        Assert.Same(query, plain.QueryInColumnOrder());
        Assert.StartsWith("SELECT \"C\", \"A\", \"B\" FROM (", moved.QueryInColumnOrder().Text, StringComparison.Ordinal);
        Assert.Equal(query.Verb, moved.QueryInColumnOrder().Verb);
    }

    private static string? Oranda_Reorder(string sql, int[] order) => ResultQueryWrapper.Reorder(sql, Cols, order);
}
