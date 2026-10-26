using Oranda.App.Features.ResultGrid;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Features.Update;
using Oranda.Core.Grid;
using Oranda.Core.Settings;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.ResultGrid;

// 소유: 결과 그리드 설정 적용 수정. 설정 「한 번에 가져올 행」·「메모리 상한」·「LOB 미리 읽기」 가 실제 실행·그리드에 닿는다.
// (Oracle 쪽 실행 서비스는 tests/Oranda.Oracle.Tests/Execution/GridFetchSettingsTests.cs)
[Collection("GridEditStatics")]
public sealed class GridFetchSettingsTests
{
    private static async Task<SettingsService> LoadedAsync(Func<AppSettings, AppSettings>? change = null)
    {
        var settings = new SettingsService(new MemorySettingsStore());
        await settings.LoadAsync(default);
        if (change is not null)
        {
            await settings.UpdateAsync(change, default);
        }

        return settings;
    }

    private static AppSettings WithGrid(AppSettings s, Func<GridSettings, GridSettings> grid) => s with { Grid = grid(s.Grid) };

    [Fact]
    public async Task The_fetch_settings_follow_the_current_settings_without_a_restart()
    {
        var settings = await LoadedAsync();
        var fetch = new GridFetchSettings(settings);
        Assert.Equal(500, fetch.PageSize);
        Assert.Equal(4000, fetch.InitialLobFetchSize);

        await settings.UpdateAsync(s => WithGrid(s, g => g with { PageSize = 5000, InitialLobFetchSize = 100000 }), default);

        Assert.Equal(5000, fetch.PageSize);
        Assert.Equal(100000, fetch.InitialLobFetchSize);
    }

    [Fact]
    public void The_app_registers_the_fetch_settings_for_the_execution_service()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-gfs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var services = AppComposition.Create(new AppCompositionOptions { DataDirectory = dir });
            var fetch = services.GetService<IGridFetchSettings>();

            Assert.NotNull(fetch);
            Assert.IsType<GridFetchSettings>(fetch);
            Assert.Equal(services.GetRequiredService<SettingsService>().Current.Grid.PageSize, fetch.PageSize);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_result_pane_pages_by_the_size_the_result_set_was_made_with()
    {
        var result = FakeResultSet.FromText(["A"], [["1"]]);
        var big = new FakeResultSet(result.Columns, [[ "1" ], [ "2" ]], pageSize: 5000);
        var pane = new SqlResultPaneViewModel();

        pane.SetResult(big);

        Assert.Equal(5000, pane.Model!.PageSize);
    }

    [Fact]
    public void A_result_set_that_does_not_report_a_page_size_keeps_the_default()
    {
        var pane = new SqlResultPaneViewModel();

        pane.SetResult(new NoPageSize(FakeResultSet.FromText(["A"], [["1"]])));

        Assert.Equal(ResultGridModel.DefaultPageSize, pane.Model!.PageSize);
    }

    [Fact]
    public async Task The_memory_limit_setting_sets_the_page_cache_of_the_next_result()
    {
        var settings = await LoadedAsync(s => WithGrid(s, g => g with { MemoryLimitMb = 64 }));
        var previous = ResultGridModel.CacheOptionsProvider;
        try
        {
            await new ResultGridSettingsStartup(settings).RunAsync(default);

            await using var first = new ResultGridModel(FakeResultSet.FromText(["A"], [["1"]]));
            Assert.Equal(64L * 1024 * 1024, first.Cache.MaxMemoryBytes);

            await settings.UpdateAsync(s => WithGrid(s, g => g with { MemoryLimitMb = 1024 }), default);
            await using var next = new ResultGridModel(FakeResultSet.FromText(["A"], [["1"]]));
            Assert.Equal(1024L * 1024 * 1024, next.Cache.MaxMemoryBytes); // 설정을 바꾸면 다음 결과부터 따른다
            Assert.Equal(64L * 1024 * 1024, first.Cache.MaxMemoryBytes); // 이미 연 결과는 그대로
        }
        finally
        {
            ResultGridModel.CacheOptionsProvider = previous;
        }
    }

    [Fact]
    public async Task Without_the_provider_the_page_cache_keeps_its_default_limit()
    {
        var previous = ResultGridModel.CacheOptionsProvider;
        try
        {
            ResultGridModel.CacheOptionsProvider = null;
            await using var model = new ResultGridModel(FakeResultSet.FromText(["A"], [["1"]]));
            Assert.Equal(200L * 1024 * 1024, model.Cache.MaxMemoryBytes);
        }
        finally
        {
            ResultGridModel.CacheOptionsProvider = previous;
        }
    }

    /// <summary>페이지 크기를 알리지 않는 결과 집합(<see cref="IResultSet.PageSize"/> 기본 구현 = null).</summary>
    private sealed class NoPageSize(IResultSet inner) : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns => inner.Columns;

        public bool IsComplete => inner.IsComplete;

        public long? KnownRowCount => inner.KnownRowCount;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct) => inner.GetPageAsync(pageIndex, ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
