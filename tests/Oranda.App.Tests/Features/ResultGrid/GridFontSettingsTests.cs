using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Shell;
using Oranda.App.Shell.Appearance;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Grid;
using Oranda.Core.Settings;
using Oranda.Core.Shell;
using Oranda.Core.Storage;

namespace Oranda.App.Tests.Features.ResultGrid;

/// <summary>결과 그리드 글꼴·크기 설정: 이전 파일 호환, 설정 창 저장 → 열린 그리드, Ctrl+휠 저장, 글자 크게/작게가 포커스 영역을 따른다.</summary>
public sealed class GridFontSettingsTests
{
    private sealed class OneRowResult : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns { get; } = [new(0, "ID", "NUMBER", ColumnDataKind.Number)];

        public bool IsComplete => true;

        public long? KnownRowCount => 1;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct) =>
            Task.FromResult(new ResultPage(0, 0, 1, [new object?[] { 1m }]) { IsLast = true });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (Window Window, SqlResultPaneView View, ResultGridControl Grid, TextBox Other) ShowGrid()
    {
        var vm = new SqlResultPaneViewModel();
        vm.SetResult(new OneRowResult());
        var view = new SqlResultPaneView { DataContext = vm };
        var other = new TextBox();
        DockPanel.SetDock(other, Avalonia.Controls.Dock.Top);
        var window = new Window { Content = new DockPanel { Children = { other, view } }, Width = 800, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, view.FindControl<ResultGridControl>("GridControl")!, other);
    }

    [Fact]
    public Task Old_settings_json_without_grid_font_reads_defaults() => HeadlessHost.Run(async () =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-gridfont-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "settings.json"), "{\"schemaVersion\":1,\"grid\":{\"pageSize\":300}}");
            using var h = new ShellHarness(dir);
            var grid = (await h.Get<ISettingsStore>().LoadAsync(default)).Value.Grid;
            Assert.Equal(300, grid.PageSize);
            Assert.Null(grid.FontFamily);
            Assert.Equal(13, grid.FontSize);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    });

    [Fact]
    public Task Saving_settings_updates_every_open_grid() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var (window, _, grid, _) = ShowGrid();
        Assert.Equal(13, grid.GridFontSize);

        await h.Get<ICommandRegistry>().ExecuteAsync("oranda.tools.settings", h.Get<IContextKeys>());
        var vm = Assert.IsType<SettingsViewModel>(Assert.IsType<SettingsWindow>(h.Get<ShellController>().LastWindow).DataContext);
        Assert.Equal(13m, vm.GridFontSize);
        Assert.Equal(vm.FontChoices[0], vm.GridFontFamily);
        vm.GridFontSize = 18;
        vm.GridFontFamily = "Consolas";
        await vm.SaveAsync();

        await HeadlessHost.WaitUntil(() => grid.GridFontSize == 18);
        Assert.Equal(18, h.Get<SettingsService>().Current.Grid.FontSize);
        Assert.Equal("Consolas", h.Get<SettingsService>().Current.Grid.FontFamily);
        Assert.StartsWith("Consolas", grid.GridFontFamily.Name);
        window.Close();
    });

    [Fact]
    public Task Ctrl_wheel_saves_grid_font_size_to_settings() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var (window, _, grid, _) = ShowGrid();
        await HeadlessHost.WaitUntil(() => grid.Model is { LoadedRowCount: > 0 });

        var inside = grid.TranslatePoint(new Point(40, 40), window)!.Value;
        window.MouseWheel(inside, new Vector(0, 1), RawInputModifiers.Control);
        await HeadlessHost.WaitUntil(() => h.Get<SettingsService>().Current.Grid.FontSize == 14);
        Assert.Equal(14, grid.GridFontSize);
        Assert.Equal(13, h.Get<SettingsService>().Current.Editor.FontSize);
        window.Close();
    });

    [Fact]
    public Task Zoom_commands_follow_the_focused_area() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var registry = h.Get<ICommandRegistry>();
        var keys = h.Get<IContextKeys>();
        var settings = h.Get<SettingsService>();
        var (window, _, grid, other) = ShowGrid();
        var shell = h.Get<ShellController>();
        var previous = shell.MainWindow;
        shell.MainWindow = window;
        try
        {
            grid.Focus();
            Dispatcher.UIThread.RunJobs();
            await registry.ExecuteAsync("oranda.view.zoomIn", keys);
            Assert.Equal(14, settings.Current.Grid.FontSize);
            Assert.Equal(13, settings.Current.Editor.FontSize);

            other.Focus();
            Dispatcher.UIThread.RunJobs();
            await registry.ExecuteAsync("oranda.view.zoomIn", keys);
            Assert.Equal(14, settings.Current.Grid.FontSize);
            Assert.Equal(14, settings.Current.Editor.FontSize);

            grid.Focus();
            Dispatcher.UIThread.RunJobs();
            await registry.ExecuteAsync("oranda.view.zoomReset", keys);
            Assert.Equal(13, settings.Current.Grid.FontSize);
            Assert.Equal(14, settings.Current.Editor.FontSize);

            for (var i = 0; i < 40; i++)
            {
                await registry.ExecuteAsync("oranda.view.zoomOut", keys);
            }

            Assert.Equal(8, settings.Current.Grid.FontSize);
            for (var i = 0; i < 40; i++)
            {
                await registry.ExecuteAsync("oranda.view.zoomIn", keys);
            }

            Assert.Equal(32, settings.Current.Grid.FontSize);

            other.Focus();
            Dispatcher.UIThread.RunJobs();
            await registry.ExecuteAsync("oranda.view.zoomReset", keys);
            Assert.Equal(13, settings.Current.Editor.FontSize);
            Assert.Equal(32, settings.Current.Grid.FontSize);
        }
        finally
        {
            shell.MainWindow = previous;
            window.Close();
        }
    });
}
