using Oranda.Core.Grid;
using Oranda.Oracle.Execution;

namespace Oranda.Oracle.Tests.Execution;

// 설정 「한 번에 가져올 행」·「LOB 미리 읽기」 가 실행 서비스에 닿는지(DB 없이). 실제 FetchSize 는 통합 시험 몫.
public sealed class GridFetchSettingsTests
{
    private sealed class Settings : IGridFetchSettings
    {
        public int PageSize { get; set; } = 500;

        public int InitialLobFetchSize { get; set; } = 4000;
    }

    private static OracleExecutionService Service(IGridFetchSettings? grid, int pageSize = 500) =>
        new(null!, null!, null!, null, pageSize, dbmsOutput: true, grid);

    [Fact]
    public void Without_settings_the_constructor_page_size_and_default_lob_preview_apply()
    {
        var service = Service(null, pageSize: 250);

        Assert.Equal(250, service.CurrentPageSize);
        Assert.Equal(OracleValues.LobPreviewSize, service.CurrentLobFetchSize);
    }

    [Fact]
    public void The_page_size_setting_is_read_at_each_execution_so_a_change_applies_from_the_next_run()
    {
        var settings = new Settings();
        var service = Service(settings);
        Assert.Equal(500, service.CurrentPageSize);

        settings.PageSize = 5000;

        Assert.Equal(5000, service.CurrentPageSize);
    }

    [Fact]
    public void An_invalid_page_size_falls_back_to_the_constructor_value()
    {
        var service = Service(new Settings { PageSize = 0 }, pageSize: 300);

        Assert.Equal(300, service.CurrentPageSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4000)]
    [InlineData(1048576)]
    public void The_lob_prefetch_setting_applies_including_zero(int size)
    {
        var service = Service(new Settings { InitialLobFetchSize = size });

        Assert.Equal(size, service.CurrentLobFetchSize);
    }
}
