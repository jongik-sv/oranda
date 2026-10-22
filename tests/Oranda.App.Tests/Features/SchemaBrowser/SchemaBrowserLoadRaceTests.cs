using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Metadata;

namespace Oranda.App.Tests.Features.SchemaBrowser;

/// <summary>
/// 트리 읽기가 겹칠 때(TSK-13-10): 필터를 빨리 바꾸거나 접속 직후 필터가 와도 노드가 「…」 로 남거나 트리가 비지 않고,
/// 읽는 중 표시가 꺼진다. 조회는 문(gate)으로 막아 늦게 끝나게 하고, 이어지는 일은 실제 앱처럼 UI 스레드에서 돈다.
/// </summary>
public sealed class SchemaBrowserLoadRaceTests
{
    private static SchemaBrowserTests.Harness NewHarness()
    {
        var h = new SchemaBrowserTests.Harness();
        h.Meta.Objects.Add(new DbObjectRef("SCOTT", "DEPT_HIST", "VIEW"));
        return h;
    }

    private static SchemaNode Scott(SchemaBrowserTests.Harness h) => h.Vm.Roots.Single(r => r.Title == "SCOTT");

    private static string[] Titles(SchemaNode node) => [.. node.Children.Select(c => c.Title)];

    [Fact]
    public Task Overlapping_filter_applies_leave_the_schema_loaded_with_the_last_filter() => HeadlessHost.Run(async () =>
    {
        var h = NewHarness();
        await h.Vm.SetSessionAsync(h.Session);
        var gate = new TaskCompletionSource();
        h.Meta.ObjectsGate = gate;

        h.Vm.FilterText = "EMP";
        var first = h.Vm.ApplyFilterAsync();
        h.Vm.FilterText = "DEPT";
        var second = h.Vm.ApplyFilterAsync();
        h.Meta.ObjectsGate = null;
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(["테이블 (1)", "뷰 (1)"], Titles(Scott(h)));
        Assert.False(h.Vm.IsLoading);
    });

    [Fact]
    public Task A_filter_applied_while_the_schema_list_loads_does_not_empty_the_tree() => HeadlessHost.Run(async () =>
    {
        var h = NewHarness();
        var gate = new TaskCompletionSource();
        h.Meta.SchemasGate = gate;

        var load = h.Vm.SetSessionAsync(h.Session);
        h.Vm.FilterText = "DEPT";
        var apply = h.Vm.ApplyFilterAsync();
        h.Meta.SchemasGate = null;
        gate.SetResult();
        await Task.WhenAll(load, apply);

        Assert.Contains(h.Vm.Roots, r => r.Title == "HR");
        Assert.Equal(["테이블 (1)", "뷰 (1)"], Titles(Scott(h)));
        Assert.False(h.Vm.IsBusy);
        Assert.False(h.Vm.IsLoading);
    });

    [Fact]
    public Task A_filter_applied_while_the_own_schema_expands_reloads_it_with_the_filter() => HeadlessHost.Run(async () =>
    {
        var h = NewHarness();
        var gate = new TaskCompletionSource();
        h.Meta.ObjectsGate = gate;

        var load = h.Vm.SetSessionAsync(h.Session);
        Assert.Contains(h.Vm.Roots, r => r.Title == "SCOTT"); // 스키마 목록은 읽었고 내 스키마를 펼치는 중
        h.Vm.FilterText = "DEPT";
        var apply = h.Vm.ApplyFilterAsync();
        h.Meta.ObjectsGate = null;
        gate.SetResult();
        await Task.WhenAll(load, apply);

        Assert.True(Scott(h).IsExpanded);
        Assert.Equal(["테이블 (1)", "뷰 (1)"], Titles(Scott(h)));
        Assert.False(h.Vm.IsLoading);
    });

    [Fact]
    public Task Loading_is_shown_while_a_schema_is_read_and_cleared_after() => HeadlessHost.Run(async () =>
    {
        var h = NewHarness();
        await h.Vm.SetSessionAsync(h.Session);
        Assert.False(h.Vm.IsLoading);
        var gate = new TaskCompletionSource();
        h.Meta.ObjectsGate = gate;

        var hr = h.Vm.Roots.Single(r => r.Title == "HR");
        var expand = h.Vm.ExpandAsync(hr);
        Assert.True(h.Vm.IsLoading);

        h.Meta.ObjectsGate = null;
        gate.SetResult();
        await expand;
        Assert.False(h.Vm.IsLoading);
    });
}
