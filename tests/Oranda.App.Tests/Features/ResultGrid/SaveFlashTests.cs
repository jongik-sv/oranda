using Avalonia.Controls;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Grid;

namespace Oranda.App.Tests.Features.ResultGrid;

/// <summary>TSK-13-18 저장된 행 강조: 모델이 강조를 들고 있다가 컨트롤이 붙은 뒤에도 일정 시간 뒤 지운다.</summary>
public sealed class SaveFlashTests
{
    private sealed class Rows : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns { get; } = [new(0, "ID", "NUMBER", ColumnDataKind.Number)];

        public bool IsComplete => true;

        public long? KnownRowCount => 3;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct) =>
            Task.FromResult(new ResultPage(pageIndex, 0, 3, [[(object?)1m, 2m, 3m]]) { IsLast = true });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Flash_marks_only_given_rows_and_clear_removes_it()
    {
        await using var m = new ResultGridModel(new Rows());
        m.FlashSavedRows([1]);

        Assert.True(m.IsFlashing(1));
        Assert.False(m.IsFlashing(0));
        Assert.True(m.HasFlash);
        m.ClearFlash();
        Assert.False(m.HasFlash);
        m.FlashSavedRows([]);
        Assert.False(m.HasFlash);
    }

    [Fact]
    public Task Control_bound_after_the_flash_started_still_clears_it() => HeadlessHost.Run(async () =>
    {
        var old = ResultGridControl.FlashDuration;
        ResultGridControl.FlashDuration = TimeSpan.FromMilliseconds(60);
        try
        {
            await using var m = new ResultGridModel(new Rows());
            m.FlashSavedRows([0, 2]);
            var grid = new ResultGridControl { Model = m };
            var window = new Window { Content = grid, Width = 300, Height = 200 };
            window.Show();
            try
            {
                Assert.True(m.IsFlashing(0));
                await HeadlessHost.WaitUntil(() => !m.HasFlash);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            ResultGridControl.FlashDuration = old;
        }
    });
}
