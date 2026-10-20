using Oranda.App.Features.ConnectionManager;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.ConnectionManager;

// 소유: TSK-03-04. 접속 관리자 사용성 개선(찾기·묶기·정렬, 행 동작·단축키, 접속 시험, 연결 문자열, 편집 상태, 배치) 시험.
// 서비스·가짜 저장소는 ConnectionManagerTests.cs 의 ServiceFixture 를 같이 쓴다.

public sealed class ConnectionManagerUxTests
{
    private static readonly DateTimeOffset Yesterday = DateTimeOffset.UtcNow.AddDays(-1);

    /// <summary>찾기·정렬 시험용 접속 넷: 그룹 둘(고객사·ADB), 그룹 없음 둘.</summary>
    private static async Task<ConnectionManagerViewModel> SeedAsync(ServiceFixture fx)
    {
        var s = fx.Service;
        var ct = CancellationToken.None;
        await s.SaveProfileAsync(new ConnectionProfile("ft-dev", "ft", "localhost:1522/FREEPDB1", LastConnectedAt: Yesterday), "pw", true, ct);
        await s.SaveProfileAsync(new ConnectionProfile("zeta", "hr", "db1.example.com:1521/HRPDB", LastConnectedAt: DateTimeOffset.UtcNow), null, false, ct);
        await s.SaveProfileAsync(new ConnectionProfile("운영DB", "scott", "prod.example.com:1521/ORCL", Environment: ConnectionEnvironment.Production, Group: "고객사"), null, false, ct);
        await s.SaveProfileAsync(new ConnectionProfile("sales", "admin", "SALES_HIGH", TnsAdmin: "/w", Group: "ADB"), null, false, ct);
        var vm = new ConnectionManagerViewModel(s);
        await vm.LoadAsync();
        return vm;
    }

    private static List<string> Titles(ConnectionManagerViewModel vm) =>
        [.. vm.AllNodes().Select(n => n.IsFolder ? "[" + n.Title + "]" : n.Title)];

    // ---------- 1. 찾기·묶기·정렬·마지막 접속 ----------

    [Fact]
    public async Task Groups_become_folders_on_top_and_ungrouped_profiles_stay_at_the_bottom()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);

        Assert.Equal(["[ADB]", "sales", "[고객사]", "운영DB", "ft-dev", "zeta"], Titles(vm));
        Assert.Equal("(1)", vm.Tree[0].Subtitle);
        Assert.All(vm.Tree.Where(n => n.IsFolder), f => Assert.True(f.IsExpanded));
    }

    [Theory]
    [InlineData("freepdb", "ft-dev")]
    [InlineData("SALES_high", "sales")]
    [InlineData("고객", "운영DB")]
    [InlineData("SCOTT", "운영DB")]
    [InlineData("db1.EXAMPLE", "zeta")]
    [InlineData("1522", "ft-dev")]
    public async Task Search_filters_by_name_user_host_service_alias_and_group_ignoring_case(string search, string expected)
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);

        vm.SearchText = search;

        var hit = Assert.Single(vm.AllNodes(), n => !n.IsFolder);
        Assert.Equal(expected, hit.Title);

        vm.SearchText = string.Empty;
        Assert.Equal(4, vm.AllNodes().Count(n => !n.IsFolder));
    }

    [Fact]
    public async Task Collapsed_folder_stays_collapsed_across_rebuilds_but_opens_while_searching()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);

        vm.Tree.Single(n => n.Title == "고객사").IsExpanded = false;
        vm.SortOrder = ConnectionSortOrder.RecentlyConnected;
        Assert.False(vm.Tree.Single(n => n.Title == "고객사").IsExpanded);

        vm.SearchText = "scott";
        Assert.True(vm.Tree.Single(n => n.Title == "고객사").IsExpanded);

        vm.SearchText = string.Empty;
        Assert.False(vm.Tree.Single(n => n.Title == "고객사").IsExpanded);
        Assert.True(vm.Tree.Single(n => n.Title == "ADB").IsExpanded);
    }

    [Fact]
    public async Task Sort_by_recent_connection_puts_latest_first_and_never_connected_last()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);
        Assert.Equal(["ft-dev", "zeta"], vm.Tree.Where(n => !n.IsFolder).Select(n => n.Title));

        vm.SortOrder = ConnectionSortOrder.RecentlyConnected;

        Assert.Equal(["zeta", "ft-dev"], vm.Tree.Where(n => !n.IsFolder).Select(n => n.Title));
        var zeta = vm.Tree.Single(n => n.Title == "zeta");
        Assert.Matches(@"^\d\d:\d\d$", zeta.LastConnectedText);
        Assert.NotNull(zeta.LastConnectedTip);
        Assert.Empty(vm.AllNodes().Single(n => n.Title == "sales").LastConnectedText);
    }

    [Fact]
    public void Last_connected_text_is_short_for_today_this_year_and_older()
    {
        var now = new DateTimeOffset(2026, 10, 20, 15, 0, 0, TimeSpan.FromHours(9));
        Assert.Equal("09:05", ConnectionTreeNode.FormatWhen(new DateTimeOffset(2026, 10, 20, 0, 5, 0, TimeSpan.Zero), now));
        Assert.Equal("03-02", ConnectionTreeNode.FormatWhen(new DateTimeOffset(2026, 3, 2, 3, 0, 0, TimeSpan.FromHours(9)), now));
        Assert.Equal("2025-12-31", ConnectionTreeNode.FormatWhen(new DateTimeOffset(2025, 12, 31, 3, 0, 0, TimeSpan.FromHours(9)), now));
    }

    [Fact]
    public async Task Successful_connect_records_last_connected_time_shown_after_reload()
    {
        using var fx = new ServiceFixture();
        var vm = await SeedAsync(fx);
        vm.AskPassword = _ => Task.FromResult<string?>("pw");
        vm.Selected = vm.AllNodes().Single(n => n.Title == "sales");
        Assert.Empty(vm.Selected!.LastConnectedText);

        var outcome = await vm.ConnectWithPromptsAsync(vm.Selected.Profile!);
        Assert.True(outcome?.IsConnected);

        await vm.LoadAsync();
        Assert.NotEmpty(vm.AllNodes().Single(n => n.Title == "sales").LastConnectedText);
    }
}
