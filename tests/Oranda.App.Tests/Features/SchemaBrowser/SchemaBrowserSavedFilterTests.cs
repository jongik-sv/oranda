using Oranda.App.Features.SchemaBrowser;
using Oranda.Core.Metadata;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.SchemaBrowser;

/// <summary>스키마 브라우저 저장 필터(TSK-13-10): 고른 필터가 스키마·범주·객체를 거르고, 접속별 선택이 설정에 남는다.</summary>
public sealed class SchemaBrowserSavedFilterTests
{
    private static SchemaBrowserTests.Harness NewHarness()
    {
        var h = new SchemaBrowserTests.Harness();
        h.Meta.Schemas.Add("EMPTYUSER"); // 객체가 없는 스키마
        h.Meta.Objects.Add(new DbObjectRef("SCOTT", "DEPT_HIST", "VIEW"));
        return h;
    }

    private static string[] SchemaTitles(SchemaBrowserTests.Harness h) =>
        [.. h.Vm.Roots.Where(r => r.Kind == SchemaNodeKind.Schema).Select(r => r.Title)];

    private static SchemaNode Root(SchemaBrowserTests.Harness h, string name) => h.Vm.Roots.Single(r => r.Title == name);

    private static string[] Titles(SchemaNode node) => [.. node.Children.Select(c => c.Title)];

    private static async Task<SchemaBrowserFilter> SaveAndSelectAsync(SchemaBrowserTests.Harness h, SchemaBrowserFilter filter)
    {
        await h.Vm.SaveFiltersAsync([filter], filter.Id);
        return filter;
    }

    [Fact]
    public async Task Without_a_saved_filter_every_schema_is_shown()
    {
        var h = NewHarness();
        await h.Vm.SetSessionAsync(h.Session);

        Assert.Null(h.Vm.ActiveSavedFilter);
        Assert.Equal(["SCOTT", "HR", "EMPTYUSER"], SchemaTitles(h));
    }

    [Fact]
    public async Task Selected_schema_filter_keeps_only_those_schemas()
    {
        var h = NewHarness();
        await h.Vm.SetSessionAsync(h.Session);

        await SaveAndSelectAsync(h, new SchemaBrowserFilter { Name = "HR만", Scope = SchemaFilterScope.Selected, Schemas = ["HR"] });

        Assert.Equal(["HR"], SchemaTitles(h));
        Assert.Equal("HR만", h.Vm.ActiveSavedFilter?.Name);
    }

    [Fact]
    public async Task Logon_schema_filter_keeps_the_connected_user_expanded()
    {
        var h = NewHarness();
        await h.Vm.SetSessionAsync(h.Session);

        await SaveAndSelectAsync(h, new SchemaBrowserFilter { Name = "나", Scope = SchemaFilterScope.LogonSchema });

        Assert.Equal(["SCOTT"], SchemaTitles(h));
        Assert.True(Root(h, "SCOTT").IsExpanded);
        Assert.Contains(Root(h, "SCOTT").Children, c => c.CategoryType == "TABLE");
        Assert.EndsWith(" · 필터 「나」", h.Vm.StatusText, StringComparison.Ordinal); // 스키마를 다시 읽은 뒤에도 필터 이름이 보인다
    }

    [Fact]
    public async Task Pattern_schema_filter_keeps_only_matching_schemas_and_survives_reconnect()
    {
        var h = NewHarness();
        h.Meta.Schemas.Add("APP_USER");
        h.Meta.Schemas.Add("EAI_USER");
        await h.Vm.SetSessionAsync(h.Session);

        var filter = await SaveAndSelectAsync(h, new SchemaBrowserFilter { Name = "AP", Scope = SchemaFilterScope.Pattern, SchemaPattern = "_user$" });

        Assert.Equal(["APP_USER", "EAI_USER"], SchemaTitles(h));
        Assert.Contains("필터 「AP」 · 스키마 2/5개", h.Vm.StatusText, StringComparison.Ordinal);
        Assert.Equal("_user$", h.Settings.Current.SchemaBrowser.Filters.Single().SchemaPattern);

        await h.Vm.SetSessionAsync(h.Session); // 다시 접속해도 설정에서 같은 필터를 골라 그대로 거른다
        Assert.Equal(filter.Id, h.Vm.ActiveSavedFilter?.Id);
        Assert.Equal(["APP_USER", "EAI_USER"], SchemaTitles(h));
    }

    [Fact]
    public async Task Pattern_schema_filter_with_a_broken_pattern_warns_in_the_status_line()
    {
        var h = NewHarness();
        await h.Vm.SetSessionAsync(h.Session);

        await SaveAndSelectAsync(h, new SchemaBrowserFilter { Name = "깨짐", Scope = SchemaFilterScope.Pattern, SchemaPattern = "(" });

        Assert.Empty(SchemaTitles(h));
        Assert.Contains("스키마 패턴이 올바르지 않아", h.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Owning_schema_filter_drops_schemas_without_objects()
    {
        var h = NewHarness();
        await h.Vm.SetSessionAsync(h.Session);

        await SaveAndSelectAsync(h, new SchemaBrowserFilter { Name = "객체 있음", Scope = SchemaFilterScope.SchemasWithObjects });

        Assert.Equal(["SCOTT", "HR"], SchemaTitles(h));
    }

    [Fact]
    public async Task Hidden_types_drop_their_category_and_type_patterns_filter_within_it()
    {
        var h = NewHarness();
        await h.Vm.SetSessionAsync(h.Session);

        await SaveAndSelectAsync(h, new SchemaBrowserFilter
        {
            Name = "표만 D",
            HiddenTypes = ["VIEW", "PROCEDURE"],
            NamePatterns = new Dictionary<string, string> { ["TABLE"] = "D*" },
        });

        var scott = Root(h, "SCOTT");
        Assert.Equal(["테이블 (1)"], Titles(scott));
        var tables = scott.Children.Single();
        await h.Vm.ExpandAsync(tables);
        Assert.Equal(["DEPT"], Titles(tables));
    }

    [Fact]
    public async Task Quick_name_filter_and_saved_filter_both_apply()
    {
        var h = NewHarness();
        await h.Vm.SetSessionAsync(h.Session);
        await SaveAndSelectAsync(h, new SchemaBrowserFilter { Name = "뷰 숨김", HiddenTypes = ["VIEW"] });

        h.Vm.FilterText = "DEPT";
        await h.Vm.ApplyFilterAsync();

        Assert.Equal(["테이블 (1)"], Titles(Root(h, "SCOTT")));
    }

    [Fact]
    public async Task Switching_filters_keeps_an_expanded_schema_expanded_and_rereads_it()
    {
        var h = NewHarness();
        await h.Vm.SetSessionAsync(h.Session);
        var hr = Root(h, "HR");
        hr.IsExpanded = true;
        await h.Vm.ExpandAsync(hr);
        var hidden = new SchemaBrowserFilter { Name = "표 숨김", HiddenTypes = ["TABLE"] };
        var all = new SchemaBrowserFilter { Name = "모두" };
        await h.Vm.SaveFiltersAsync([hidden, all], hidden.Id);
        Assert.Equal(["해당하는 객체가 없다"], Titles(Root(h, "HR")));

        await h.Vm.SelectSavedFilterAsync(all.Id);

        Assert.Same(hr, Root(h, "HR"));
        Assert.True(hr.IsExpanded);
        Assert.Equal(["테이블 (1)"], Titles(hr));
    }

    [Fact]
    public async Task The_choice_is_remembered_per_connection_and_survives_a_rename()
    {
        var h = NewHarness();
        await h.Vm.SetSessionAsync(h.Session);
        var f = await SaveAndSelectAsync(h, new SchemaBrowserFilter { Name = "HR만", Scope = SchemaFilterScope.Selected, Schemas = ["HR"] });

        Assert.Equal(f.Id, h.Settings.Current.SchemaBrowser.ActiveFilters[h.Session.Profile.Id.ToString()]);

        await h.Vm.SaveFiltersAsync([f with { Name = "인사" }], f.Id);
        Assert.Equal("인사", h.Vm.ActiveSavedFilter?.Name);

        // 같은 설정으로 새로 연 창도 그 접속의 필터를 고른 채로 시작한다.
        var again = new SchemaBrowserViewModel(new SchemaBrowserTests.Services(h)) { PostToUi = a => a() };
        await again.SetSessionAsync(h.Session);
        Assert.Equal("인사", again.ActiveSavedFilter?.Name);
        Assert.Equal(["HR"], again.Roots.Where(r => r.Kind == SchemaNodeKind.Schema).Select(r => r.Title));
    }

    [Fact]
    public async Task Deleting_the_chosen_filter_falls_back_to_no_filter()
    {
        var h = NewHarness();
        await h.Vm.SetSessionAsync(h.Session);
        await SaveAndSelectAsync(h, new SchemaBrowserFilter { Name = "HR만", Scope = SchemaFilterScope.Selected, Schemas = ["HR"] });

        await h.Vm.SaveFiltersAsync([], null);

        Assert.Null(h.Vm.ActiveSavedFilter);
        Assert.Empty(h.Settings.Current.SchemaBrowser.ActiveFilters);
        Assert.Equal(["SCOTT", "HR", "EMPTYUSER"], SchemaTitles(h));
    }

    [Fact]
    public async Task Choosing_no_filter_clears_the_connection_choice()
    {
        var h = NewHarness();
        await h.Vm.SetSessionAsync(h.Session);
        var f = await SaveAndSelectAsync(h, new SchemaBrowserFilter { Name = "HR만", Scope = SchemaFilterScope.Selected, Schemas = ["HR"] });

        await h.Vm.SelectSavedFilterAsync(null);

        Assert.Null(h.Vm.ActiveSavedFilter);
        Assert.Single(h.Vm.SavedFilters, x => x.Id == f.Id);
        Assert.Equal(["SCOTT", "HR", "EMPTYUSER"], SchemaTitles(h));
    }

    [Fact]
    public async Task Edit_filters_command_is_registered_and_asks_the_view_to_open_the_editor()
    {
        Assert.NotNull(Oranda.App.Tests.Shell.ShellTestHelpers.CreateCommandRegistry().Find("oranda.schema.editFilters"));
        var h = NewHarness();
        await h.Vm.SetSessionAsync(h.Session);
        var asked = 0;
        h.Vm.FilterEditorRequested += (_, _) => asked++;

        await new SchemaBrowserCommandHandler(h.Vm).ExecuteAsync(
            new Oranda.Core.Shell.CommandContext("oranda.schema.editFilters", Oranda.App.Tests.Shell.FakeContextKeys.AllTrue), CancellationToken.None);

        Assert.Equal(1, asked);
        Assert.Equal(["SCOTT", "HR", "EMPTYUSER"], h.Vm.AllSchemaNames.OrderBy(n => n == "SCOTT" ? 0 : 1));
    }

    [Fact]
    public async Task Saving_from_the_editor_keeps_the_current_choice_when_it_still_exists()
    {
        var h = NewHarness();
        await h.Vm.SetSessionAsync(h.Session);
        var f = await SaveAndSelectAsync(h, new SchemaBrowserFilter { Name = "HR만", Scope = SchemaFilterScope.Selected, Schemas = ["HR"] });
        var other = new SchemaBrowserFilter { Name = "다른" };

        await h.Vm.SaveFiltersKeepingChoiceAsync([f, other]);
        Assert.Equal(f.Id, h.Vm.ActiveSavedFilter?.Id);

        await h.Vm.SaveFiltersKeepingChoiceAsync([other]);
        Assert.Null(h.Vm.ActiveSavedFilter);
    }

    [Fact]
    public async Task Reveal_lifts_a_saved_filter_that_hides_the_object()
    {
        var h = NewHarness();
        await h.Vm.SetSessionAsync(h.Session);
        await SaveAndSelectAsync(h, new SchemaBrowserFilter { Name = "HR만", Scope = SchemaFilterScope.Selected, Schemas = ["HR"] });

        Assert.True(await h.Vm.RevealAsync(new DbObjectRef("SCOTT", "EMP", "TABLE")));

        Assert.Null(h.Vm.ActiveSavedFilter);
        Assert.Equal(new DbObjectRef("SCOTT", "EMP", "TABLE"), h.Vm.SelectedNode?.Ref);
    }
}
