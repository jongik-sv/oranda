using Oranda.App.Features.GridEdit;

namespace Oranda.App.Tests.Features.GridEdit;

/// <summary>TSK-13-18 저장 효과: 편집 반영 성공·실패 때 상태 표시줄 알림이 한 번 나타난다.</summary>
public sealed partial class GridEditTests
{
    private static readonly Oranda.App.Shell.ShellContributions App =
        Oranda.App.Shell.ShellContributions.Scan(typeof(SaveStatusViewModel).Assembly);

    [Fact]
    public async Task Apply_success_shows_saved_notice_once_and_commit_says_so()
    {
        SaveFeedback.Clear();
        var s = await OpenAsync("SELECT empno, ename, sal FROM emp");
        GridEditCommands.Active = s;
        GridEditCommands.ConfirmPreview = (_, _, _) => Task.FromResult(true);
        var changes = 0;
        SaveFeedback.Changed += Count;
        try
        {
            s.SetCell(0, 1, "X");
            await Run("oranda.grid.edit.apply");

            Assert.Equal(new SaveNotice("1행 저장됨", SaveFeedbackKind.Success), SaveFeedback.Current);
            Assert.Equal(1, changes);

            s.SetCell(1, 1, "Y");
            await Run("oranda.grid.edit.applyAndCommit");

            Assert.Equal("1행 저장·커밋됨", SaveFeedback.Current?.Text);
            Assert.Equal(SaveFeedbackKind.Success, SaveFeedback.Current?.Kind);
        }
        finally
        {
            SaveFeedback.Changed -= Count;
            SaveFeedback.Clear();
        }

        void Count(object? sender, EventArgs e) => changes++;
    }

    [Fact]
    public async Task Apply_with_failed_row_shows_failure_notice()
    {
        SaveFeedback.Clear();
        var s = await OpenAsync("SELECT empno, ename, sal FROM emp");
        GridEditCommands.Active = s;
        GridEditCommands.ConfirmPreview = (_, _, _) => Task.FromResult(true);
        _exec.RowsFor = stmt => stmt.Text.Contains("UPDATE", StringComparison.Ordinal) ? 0 : 1;
        s.SetCell(0, 1, "X");
        try
        {
            await Run("oranda.grid.edit.apply");

            Assert.Equal(SaveFeedbackKind.Failure, SaveFeedback.Current?.Kind);
            Assert.Equal("1행 저장하지 못함", SaveFeedback.Current?.Text);
        }
        finally
        {
            SaveFeedback.Clear();
        }
    }

    [Fact]
    public async Task Saved_updates_flash_on_the_grid_the_session_is_bound_to()
    {
        SaveFeedback.Clear();
        var s = await OpenAsync("SELECT empno, ename, sal FROM emp");
        GridEditCommands.Active = s;
        GridEditCommands.ConfirmPreview = (_, _, _) => Task.FromResult(true);
        await using var model = new Oranda.App.Features.ResultGrid.ResultGridModel(new SaveTestResultSet());
        GridEditBinding.Attach(model, s);
        s.SetCell(1, 1, "X");

        await Run("oranda.grid.edit.apply");

        Assert.False(model.IsFlashing(0));
        Assert.True(model.IsFlashing(1));
        model.ClearFlash();
        Assert.False(model.IsFlashing(1));
        SaveFeedback.Clear();
    }

    [Fact]
    public async Task Flash_after_rerun_lands_on_the_new_grid_and_not_the_old_one()
    {
        SaveFeedback.Clear();
        var first = await OpenAsync("SELECT empno, ename, sal FROM emp");
        GridEditCommands.Active = first;
        GridEditCommands.ConfirmPreview = (_, _, _) => Task.FromResult(true);
        await using var oldModel = new Oranda.App.Features.ResultGrid.ResultGridModel(new SaveTestResultSet());
        GridEditBinding.Attach(oldModel, first);
        await using var newModel = new Oranda.App.Features.ResultGrid.ResultGridModel(new SaveTestResultSet());
        GridEditCommands.Rerun = async (_, _) =>
        {
            var reopened = await OpenAsync("SELECT empno, ename, sal FROM emp");
            GridEditBinding.Attach(newModel, reopened);
            GridEditCommands.Active = reopened;
        };
        first.SetCell(1, 1, "X");

        await Run("oranda.grid.edit.apply");

        Assert.True(newModel.IsFlashing(1));
        Assert.False(oldModel.HasFlash);
        SaveFeedback.Clear();
    }

    [Fact]
    public Task Status_item_is_registered_and_follows_the_notice_with_export_actions() => Oranda.App.Tests.Shell.Headless.HeadlessHost.Run(async () =>
    {
        var item = Assert.Single(App.StatusBarItems, i => i.Id == "oranda.status.saveNotice");
        Assert.Equal(9, item.Order);
        Assert.Equal(typeof(SaveStatusViewModel), item.ItemType);
        Assert.NotNull(typeof(SaveStatusViewModel).Assembly.GetType(typeof(SaveStatusViewModel).FullName!.Replace("ViewModel", "View", StringComparison.Ordinal)));

        SaveFeedback.Clear();
        try
        {
            using var vm = new SaveStatusViewModel();
            Assert.False(vm.IsVisible);

            SaveFeedback.ShowExported(1200, "/tmp/out/result.csv");
            await Oranda.App.Tests.Shell.Headless.HeadlessHost.WaitUntil(() => vm.IsVisible);

            Assert.Equal($"{1200:N0}행 내보냄: result.csv", vm.Text);
            Assert.Equal("/tmp/out/result.csv", vm.FilePath);

            SaveFeedback.ShowApplied(2, 0, committed: false);
            await Oranda.App.Tests.Shell.Headless.HeadlessHost.WaitUntil(() => vm.FilePath is null);

            Assert.Equal("2행 저장됨", vm.Text);
        }
        finally
        {
            SaveFeedback.Clear();
        }
    });

    private sealed class SaveTestResultSet : Oranda.Core.Grid.IResultSet
    {
        public IReadOnlyList<Oranda.Core.Grid.ResultColumn> Columns => EmpCols;

        public bool IsComplete => true;

        public long? KnownRowCount => 2;

        public Task<Oranda.Core.Grid.ResultPage> GetPageAsync(int pageIndex, CancellationToken ct) =>
            Task.FromResult(new Oranda.Core.Grid.ResultPage(pageIndex, 0, 2,
                [.. Enumerable.Range(0, 3).Select(c => (IReadOnlyList<object?>)[.. Data.Select(r => r[c])])]) { IsLast = true });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
