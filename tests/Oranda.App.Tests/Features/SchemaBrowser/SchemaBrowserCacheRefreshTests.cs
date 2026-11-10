using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Tests.Shell;
using Oranda.Core.Metadata;
using Oranda.Core.Shell;
using static Oranda.App.Tests.Features.SchemaBrowser.SchemaBrowserTests;

namespace Oranda.App.Tests.Features.SchemaBrowser;

/// <summary>
/// MC1: 스키마 브라우저의 객체 목록 캐시 새로 고침. 유형 범주 F5 = 그 유형만 서버에서 다시 받기, 「메타데이터 캐시 초기화」, 상태 줄의 「목록 기준 시각」.
/// </summary>
public class SchemaBrowserCacheRefreshTests
{
    [Fact]
    public async Task F5_on_a_type_folder_reloads_that_type_from_the_server_and_shows_new_objects()
    {
        var h = new Harness();
        h.Meta.Objects.Add(new DbObjectRef("SCOTT", "F1", "FUNCTION"));
        h.Meta.ServerOnly.Add(new DbObjectRef("SCOTT", "F_NEW", "FUNCTION")); // 캐시를 만든 뒤 다른 도구로 만든 함수
        await h.Vm.SetSessionAsync(h.Session);
        var functions = Category(h, "FUNCTION");
        Assert.Equal("함수 (1)", functions.Title);
        await h.Vm.ExpandAsync(functions);
        functions.IsExpandedSilently = true;
        h.Vm.SelectedNode = functions;

        await new SchemaBrowserCommandHandler(h.Vm).ExecuteAsync(new CommandContext("oranda.schema.refresh", FakeContextKeys.AllTrue), CancellationToken.None);

        Assert.Equal([("SCOTT", "FUNCTION")], h.Meta.TypeRefreshes);
        var again = Category(h, "FUNCTION");
        Assert.Equal("함수 (2)", again.Title);
        Assert.Equal(["F1", "F_NEW"], again.Children.Select(c => c.Title));
        Assert.Same(again, h.Vm.SelectedNode); // 이어서 F5 를 눌러도 같은 범주
        Assert.Contains("SCOTT FUNCTION", h.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task F5_on_a_schema_node_rebuilds_the_tree_without_a_type_refresh()
    {
        var h = new Harness();
        await h.Vm.SetSessionAsync(h.Session);
        var calls = h.Meta.ListObjectsCalls;
        h.Vm.SelectedNode = h.Vm.Roots.Single(r => r.Title == "SCOTT");

        await new SchemaBrowserCommandHandler(h.Vm).ExecuteAsync(new CommandContext("oranda.schema.refresh", FakeContextKeys.AllTrue), CancellationToken.None);

        Assert.Empty(h.Meta.TypeRefreshes);
        Assert.True(h.Meta.ListObjectsCalls > calls); // 트리를 처음부터 다시 만들었다
    }

    [Fact]
    public async Task Reset_metadata_cache_reloads_the_tree_and_shows_the_new_list_time()
    {
        var reg = ShellTestHelpers.CreateCommandRegistry();
        var command = reg.Find("oranda.schema.resetMetadataCache");
        Assert.NotNull(command);
        Assert.Contains("context:schema/refresh@12", command!.Placements);
        Assert.Equal("hasSession && !noImplMetadata", command.Enablement);

        var h = new Harness();
        await h.Vm.SetSessionAsync(h.Session);
        Assert.Equal(string.Empty, h.Vm.ListBaseTimeText); // 기준 시각을 모르면 비운다
        h.Meta.ServerOnly.Add(new DbObjectRef("SCOTT", "F_NEW", "FUNCTION"));

        await new SchemaBrowserCommandHandler(h.Vm).ExecuteAsync(new CommandContext("oranda.schema.resetMetadataCache", FakeContextKeys.AllTrue), CancellationToken.None);

        Assert.Equal(1, h.Meta.ResetCalls);
        Assert.Contains(Category(h, "FUNCTION").Items, o => o.Name == "F_NEW");
        Assert.Equal(SchemaBrowserViewModel.ListBaseTimeLabel(h.Meta.ResetTime), h.Vm.ListBaseTimeText);
    }

    [Fact]
    public async Task List_base_time_is_local_and_follows_the_object_list_refreshed_event()
    {
        var first = new DateTimeOffset(2026, 11, 5, 1, 21, 6, TimeSpan.Zero);
        var h = new Harness();
        h.Meta.RefreshedAt = first;
        await h.Vm.SetSessionAsync(h.Session);
        Assert.Equal(SchemaBrowserViewModel.ListBaseTimeLabel(first), h.Vm.ListBaseTimeText);
        Assert.EndsWith(first.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), h.Vm.ListBaseTimeText, StringComparison.Ordinal);

        var later = first.AddDays(5);
        h.Bus.Publish(new ObjectListRefreshed("OTHER/-/X", later, ObjectListChange.None)); // 다른 접속은 무시
        Assert.Equal(SchemaBrowserViewModel.ListBaseTimeLabel(first), h.Vm.ListBaseTimeText);

        h.Bus.Publish(new ObjectListRefreshed(h.Session.DbKey, later, new ObjectListChange(1, 0, 0)));
        Assert.Equal(SchemaBrowserViewModel.ListBaseTimeLabel(later), h.Vm.ListBaseTimeText);
    }

    [Fact]
    public async Task F5_on_a_type_folder_keeps_another_category_picked_while_the_type_was_loading()
    {
        var h = new Harness();
        h.Meta.Objects.Add(new DbObjectRef("SCOTT", "F1", "FUNCTION"));
        await h.Vm.SetSessionAsync(h.Session);
        h.Vm.SelectedNode = Category(h, "FUNCTION");
        h.Meta.DuringTypeRefresh = () => h.Vm.SelectedNode = Category(h, "PROCEDURE"); // 긴 F5 동안 다른 범주를 골랐다

        await new SchemaBrowserCommandHandler(h.Vm).ExecuteAsync(new CommandContext("oranda.schema.refresh", FakeContextKeys.AllTrue), CancellationToken.None);

        Assert.Equal([("SCOTT", "FUNCTION")], h.Meta.TypeRefreshes);
        Assert.Same(Category(h, "PROCEDURE"), h.Vm.SelectedNode); // F5 한 범주로 되돌리지 않는다
    }

    [Fact]
    public async Task List_base_time_of_the_previous_tree_is_cleared_while_rebuilding_and_after_a_failed_build()
    {
        var first = new DateTimeOffset(2026, 11, 5, 1, 21, 6, TimeSpan.Zero);
        var h = new Harness();
        h.Meta.RefreshedAt = first;
        await h.Vm.SetSessionAsync(h.Session);
        Assert.Equal(SchemaBrowserViewModel.ListBaseTimeLabel(first), h.Vm.ListBaseTimeText);

        h.Meta.SchemasGate = new TaskCompletionSource();
        var load = h.Vm.LoadAsync();
        Assert.Equal(string.Empty, h.Vm.ListBaseTimeText); // 짓는 동안 이전 시각을 남기지 않는다
        h.Meta.SchemasGate.SetException(new InvalidOperationException("ORA-03113"));
        await load;

        Assert.Equal(string.Empty, h.Vm.ListBaseTimeText);
        Assert.Contains("ORA-03113", h.Vm.StatusText, StringComparison.Ordinal);
    }

    private static SchemaNode Category(Harness h, string type)
        => h.Vm.Roots.Single(r => r.Title == "SCOTT").Children.Single(c => c.CategoryType == type);
}
