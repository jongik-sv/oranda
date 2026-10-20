using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Tests.Features.Navigation;
using Oranda.Core.Metadata;

namespace Oranda.App.Tests.Features.SchemaBrowser;

/// <summary>스키마 브라우저에서 찾기(<c>SchemaBrowserViewModel.RevealAsync</c>): 지연 로딩으로 스키마·범주를 펼쳐 노드를 고른다.</summary>
public class SchemaBrowserRevealTests
{
    [Fact]
    public async Task Reveal_selects_the_object_in_the_users_schema_and_expands_its_ancestors()
    {
        var h = new NavBrowserHarness();
        await h.Vm.SetSessionAsync(h.Session);
        SchemaNode? announced = null;
        h.Vm.RevealRequested += (_, n) => announced = n;

        Assert.True(await h.Vm.RevealAsync(new DbObjectRef("SCOTT", "V_EMP", "VIEW")));

        var node = h.Vm.SelectedNode!;
        Assert.Equal(new DbObjectRef("SCOTT", "V_EMP", "VIEW"), node.Ref);
        Assert.Same(node, announced);
        Assert.True(h.Vm.IsNodeSelected);
        var category = h.Vm.Roots.Single(r => r.Title == "SCOTT").Children.Single(c => c.CategoryType == "VIEW");
        Assert.True(category.IsExpanded);
        Assert.True(h.Vm.Roots.Single(r => r.Title == "SCOTT").IsExpanded);
        Assert.Contains(node, category.Children);
    }

    [Fact]
    public async Task Reveal_loads_another_schema_lazily()
    {
        var h = new NavBrowserHarness();
        await h.Vm.SetSessionAsync(h.Session);
        var hr = h.Vm.Roots.Single(r => r.Title == "HR");
        Assert.Equal(SchemaNodeKind.Placeholder, hr.Children.Single().Kind); // 아직 안 읽음

        Assert.True(await h.Vm.RevealAsync(new DbObjectRef("HR", "JOBS", "TABLE")));

        Assert.Equal("JOBS", h.Vm.SelectedNode!.Title);
        Assert.True(hr.IsExpanded);
        Assert.Contains(h.Vm.SelectedNode, hr.Children.Single(c => c.CategoryType == "TABLE").Children);
    }

    [Fact]
    public async Task Reveal_picks_the_schema_tree_node_not_the_favorites_copy()
    {
        var h = new NavBrowserHarness();
        await h.Vm.SetSessionAsync(h.Session);
        var emp = new DbObjectRef("SCOTT", "EMP", "TABLE");
        await h.Favorites.SetAsync(h.Session.Profile.Id, SchemaSql.FavoriteKey(emp), true, default);
        await h.Vm.LoadAsync();
        await h.Vm.ExpandAsync(h.Vm.Roots[0]);
        Assert.Contains(h.Vm.Roots[0].Children, c => c.Ref == emp);

        Assert.True(await h.Vm.RevealAsync(emp));

        var selected = h.Vm.SelectedNode!;
        Assert.Equal(emp, selected.Ref);
        Assert.DoesNotContain(selected, h.Vm.Roots[0].Children);
        Assert.Contains(selected, h.Vm.Roots.Single(r => r.Title == "SCOTT").Children.Single(c => c.CategoryType == "TABLE").Children);
    }

    [Fact]
    public async Task Reveal_pages_through_large_categories_until_the_object_is_shown()
    {
        var h = new NavBrowserHarness();
        for (var i = 0; i < 1_200; i++)
        {
            h.Meta.Objects.Add(new DbObjectRef("BIG", $"T{i:D5}", "TABLE"));
        }

        h.Meta.Schemas.Add("BIG");
        await h.Vm.SetSessionAsync(h.Session);

        Assert.True(await h.Vm.RevealAsync(new DbObjectRef("BIG", "T01100", "TABLE")));

        Assert.Equal("T01100", h.Vm.SelectedNode!.Title);
        var cat = h.Vm.Roots.Single(r => r.Title == "BIG").Children.Single();
        Assert.True(cat.Children.Count > 2 * SchemaBrowserViewModel.PageSize);
    }

    [Fact]
    public async Task Reveal_builds_an_empty_tree_first_with_the_last_work_session()
    {
        var h = new NavBrowserHarness();
        Assert.Empty(h.Vm.Roots); // 도구 창을 방금 만든 상태

        Assert.True(await h.Vm.RevealAsync(new DbObjectRef("SCOTT", "EMP", "TABLE")));

        Assert.Same(h.Session, h.Vm.CurrentSession);
        Assert.Equal("EMP", h.Vm.SelectedNode!.Title);
    }

    [Fact]
    public async Task Reveal_switches_the_tree_to_the_given_session_when_its_database_differs()
    {
        var h = new NavBrowserHarness();
        await h.Vm.SetSessionAsync(h.Session);
        var other = new NavFakeSession("HR", "OTHER/DB/HR");

        Assert.True(await h.Vm.RevealAsync(new DbObjectRef("HR", "JOBS", "TABLE"), other));

        Assert.Same(other, h.Vm.CurrentSession);
        Assert.Equal("JOBS", h.Vm.SelectedNode!.Title);
        Assert.Equal("HR", h.Vm.Roots[1].Title); // 그 접속의 사용자 스키마가 맨 위
    }

    [Fact]
    public async Task Reveal_clears_a_filter_that_hides_the_object()
    {
        var h = new NavBrowserHarness();
        await h.Vm.SetSessionAsync(h.Session);
        h.Vm.FilterText = "DEPT";
        h.Vm.TypeFilter = "TABLE";
        await h.Vm.ApplyFilterAsync();
        Assert.DoesNotContain(h.Vm.Roots.Single(r => r.Title == "SCOTT").Children, c => c.CategoryType == "VIEW");

        Assert.True(await h.Vm.RevealAsync(new DbObjectRef("SCOTT", "V_EMP", "VIEW")));

        Assert.Equal("V_EMP", h.Vm.SelectedNode!.Title);
        Assert.Equal(string.Empty, h.Vm.FilterText);
        Assert.Null(h.Vm.TypeFilter);
        Assert.True(h.Vm.Filter.IsEmpty);
    }

    [Fact]
    public async Task Reveal_of_a_body_falls_back_to_the_spec_and_unknown_objects_report_failure()
    {
        var h = new NavBrowserHarness();
        await h.Vm.SetSessionAsync(h.Session);

        // 본문 범주가 있으면 본문, 없으면 명세 노드를 고른다.
        Assert.True(await h.Vm.RevealAsync(new DbObjectRef("SCOTT", "EMP_PKG", "PACKAGE BODY")));
        Assert.Equal("PACKAGE BODY", h.Vm.SelectedNode!.Ref!.ObjectType);
        Assert.True(await h.Vm.RevealAsync(new DbObjectRef("HR", "EMP_API", "PACKAGE BODY")));
        Assert.Equal(new DbObjectRef("HR", "EMP_API", "PACKAGE"), h.Vm.SelectedNode!.Ref);

        Assert.False(await h.Vm.RevealAsync(new DbObjectRef("SCOTT", "NOPE", "TABLE")));
        Assert.Contains("SCOTT.NOPE", h.Vm.StatusText, StringComparison.Ordinal);
        Assert.False(await h.Vm.RevealAsync(new DbObjectRef("NOBODY", "EMP", "TABLE")));
    }

    [Fact]
    public async Task Reveal_ignores_the_sub_name_of_the_target()
    {
        var h = new NavBrowserHarness();
        await h.Vm.SetSessionAsync(h.Session);

        Assert.True(await h.Vm.RevealAsync(new DbObjectRef("SCOTT", "EMP", "TABLE", "ENAME")));
        Assert.Equal(new DbObjectRef("SCOTT", "EMP", "TABLE"), h.Vm.SelectedNode!.Ref);
    }
}
