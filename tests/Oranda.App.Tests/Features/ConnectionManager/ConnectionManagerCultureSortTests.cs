using System.Globalization;
using Oranda.App.Features.ConnectionManager;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.ConnectionManager;

// TSK-03-09: 접속 폴더·접속 이름순은 문화권과 무관한 고정 비교(대소문자 무시 서수)다.
// 문화권을 따르면 한국어(ko-KR)에서 한글이 영문보다 앞서 시험·화면 순서가 PC 마다 달라진다.
public sealed class ConnectionManagerCultureSortTests
{
    public static TheoryData<string> Cultures => new() { "ko-KR", "en-US", "de-DE", "sv-SE" };

    private static async Task<T> InCultureAsync<T>(string culture, Func<Task<T>> body)
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try { return await body(); }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    private static async Task<ConnectionManagerViewModel> SeedAsync(ServiceFixture fx, params (string Name, string? Group)[] items)
    {
        foreach (var (name, group) in items)
            await fx.Service.SaveProfileAsync(new ConnectionProfile(name, "u", "h:1521/S", Group: group), null, false, CancellationToken.None);
        var vm = new ConnectionManagerViewModel(fx.Service);
        await vm.LoadAsync();
        return vm;
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task Folders_and_connections_sort_the_same_in_every_culture(string culture)
    {
        var order = await InCultureAsync(culture, async () =>
        {
            using var fx = new ServiceFixture();
            var vm = await SeedAsync(fx, ("zeta", null), ("운영DB", "고객사"), ("sales", "ADB"), ("ft-dev", null), ("Alpha", "adb2"), ("beta", "Beta"));
            return vm.AllNodes().Select(n => n.IsFolder ? "[" + n.Title + "]" : n.Title).ToArray();
        });

        // 폴더(서수·대소문자 무시) 위, 폴더 안 접속 이름순, 그룹 없는 접속은 맨 아래
        Assert.Equal(["[ADB]", "sales", "[adb2]", "Alpha", "[Beta]", "beta", "[고객사]", "운영DB", "ft-dev", "zeta"], order);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task Name_order_ignores_case_and_flips_back_after_culture_is_restored(string culture)
    {
        var before = CultureInfo.CurrentCulture;
        var names = await InCultureAsync(culture, async () =>
        {
            using var fx = new ServiceFixture();
            var vm = await SeedAsync(fx, ("b", null), ("A", null), ("a2", null), ("한", null), ("Z", null));
            return vm.AllNodes().Select(n => n.Title).ToArray();
        });

        Assert.Equal(["A", "a2", "b", "Z", "한"], names);
        Assert.Same(before, CultureInfo.CurrentCulture);
    }

    [Fact]
    public async Task Empty_list_has_no_nodes_in_any_culture()
    {
        var count = await InCultureAsync("ko-KR", async () =>
        {
            using var fx = new ServiceFixture();
            var vm = await SeedAsync(fx);
            return vm.Tree.Count;
        });
        Assert.Equal(0, count);
    }
}
