using Oranda.App.Features.SchemaBrowser;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.SchemaBrowser;

/// <summary>저장 필터 편집 대화상자 모델(TSK-13-10, 오렌지 Schema Browser Filter): 새로 만들기·저장·삭제, 스키마·객체·옵션 탭.</summary>
public sealed class SchemaFilterEditorTests
{
    private static readonly string[] AllSchemas = ["EAI_USER", "HR", "APP_USER", "SCOTT"];

    private static readonly SchemaBrowserFilter ApUser = new()
    {
        Id = "f1",
        Name = "APUser",
        Scope = SchemaFilterScope.Selected,
        Schemas = ["APP_USER"],
        HiddenTypes = ["JOB"],
        NamePatterns = new Dictionary<string, string> { ["TABLE"] = "TB*" },
        UnderscoreIsWildcard = true,
    };

    private static SchemaFilterEditorViewModel Make(string? selectedId, params SchemaBrowserFilter[] filters) =>
        new(filters, selectedId, AllSchemas);

    private static FilterTypeRow Row(SchemaFilterEditorViewModel vm, string type) => vm.Selected!.Types.Single(t => t.Type == type);

    [Fact]
    public void Opens_on_the_chosen_filter_with_its_values()
    {
        var vm = Make("f1", new SchemaBrowserFilter { Id = "f0", Name = "모두" }, ApUser);

        var d = vm.Selected!;
        Assert.Equal("APUser", d.Name);
        Assert.True(d.IsSelectedScope);
        Assert.False(d.IsAllScope);
        Assert.Equal(["APP_USER"], d.Schemas);
        Assert.False(Row(vm, "JOB").IsShown);
        Assert.True(Row(vm, "VIEW").IsShown);
        Assert.Equal("TB*", Row(vm, "TABLE").Pattern);
        Assert.True(d.UnderscoreIsWildcard);
        Assert.True(d.WrapWithPercent);
        Assert.Equal("테이블", Row(vm, "TABLE").Title);
    }

    [Fact]
    public void Without_saved_filters_nothing_is_selected_until_new()
    {
        var vm = Make(null);
        Assert.Null(vm.Selected);
        Assert.False(vm.HasSelection);

        vm.New();
        vm.New();

        Assert.Equal(["새 필터", "새 필터 (2)"], vm.Filters.Select(f => f.Name));
        Assert.Same(vm.Filters[1], vm.Selected);
        Assert.True(vm.Selected!.IsAllScope);
        Assert.True(vm.HasSelection);
    }

    [Fact]
    public void Schemas_are_added_once_and_removed()
    {
        var vm = Make(null);
        vm.New();
        vm.Selected!.IsSelectedScope = true;

        vm.SchemaToAdd = "HR";
        vm.AddSchema();
        vm.AddSchema();
        vm.SchemaToAdd = "SCOTT";
        vm.AddSchema();
        Assert.Equal(["HR", "SCOTT"], vm.Selected.Schemas);

        vm.RemoveSchema("HR");
        Assert.Equal(["SCOTT"], vm.Selected.Schemas);
        Assert.Equal(SchemaFilterScope.Selected, vm.Selected.Scope);
    }

    [Fact]
    public void Select_all_deselect_all_assign_globally_and_reset()
    {
        var vm = Make("f1", ApUser);

        vm.DeselectAll();
        Assert.All(vm.Selected!.Types, t => Assert.False(t.IsShown));
        vm.SelectAll();
        Assert.All(vm.Selected.Types, t => Assert.True(t.IsShown));

        vm.GlobalPattern = "M30*";
        vm.AssignGlobally();
        Assert.All(vm.Selected.Types, t => Assert.Equal("M30*", t.Pattern));

        vm.DeselectAll();
        vm.Reset();
        Assert.All(vm.Selected.Types, t => Assert.True(t.IsShown));
        Assert.All(vm.Selected.Types, t => Assert.Equal("", t.Pattern));
        Assert.Equal("", vm.GlobalPattern);
    }

    [Fact]
    public void Result_turns_drafts_back_into_filters_keeping_ids()
    {
        var vm = Make("f1", ApUser);
        vm.Selected!.Name = "AP 계정";
        Row(vm, "VIEW").IsShown = false;
        Row(vm, "INDEX").Pattern = "  ";

        Assert.True(vm.Validate());
        var f = Assert.Single(vm.BuildFilters());
        Assert.Equal("f1", f.Id);
        Assert.Equal("AP 계정", f.Name);
        Assert.Equal(SchemaFilterScope.Selected, f.Scope);
        Assert.Equal(["APP_USER"], f.Schemas);
        Assert.Equal(["VIEW", "JOB"], f.HiddenTypes);
        Assert.Equal(new Dictionary<string, string> { ["TABLE"] = "TB*" }, f.NamePatterns);
        Assert.True(f.UnderscoreIsWildcard);
        Assert.Equal("f1", vm.SelectedId);
    }

    [Fact]
    public void Blank_or_duplicate_names_are_rejected()
    {
        var vm = Make("f1", ApUser);
        vm.Selected!.Name = "  ";
        Assert.False(vm.Validate());
        Assert.Equal("필터 이름을 넣어 주세요", vm.Error);

        vm.Selected.Name = "APUser";
        vm.New();
        vm.Selected!.Name = "apuser";
        Assert.False(vm.Validate());
        Assert.Equal("같은 이름의 필터가 있습니다: apuser", vm.Error);

        vm.Selected.Name = "다른 이름";
        Assert.True(vm.Validate());
        Assert.Null(vm.Error);
    }

    [Fact]
    public void Delete_removes_the_selected_filter_and_picks_a_neighbour()
    {
        var vm = Make("f1", new SchemaBrowserFilter { Id = "f0", Name = "모두" }, ApUser);

        vm.Delete();
        Assert.Equal(["모두"], vm.Filters.Select(f => f.Name));
        Assert.Equal("모두", vm.Selected?.Name);

        vm.Delete();
        Assert.Empty(vm.Filters);
        Assert.Null(vm.Selected);
        Assert.Null(vm.SelectedId);
    }

    [Fact]
    public async Task Save_keeps_the_dialog_open_and_hands_over_the_list()
    {
        IReadOnlyList<SchemaBrowserFilter>? saved = null;
        var vm = new SchemaFilterEditorViewModel([ApUser], "f1", AllSchemas, list =>
        {
            saved = list;
            return Task.CompletedTask;
        });
        vm.Selected!.Name = "AP";

        Assert.True(await vm.SaveAsync());
        Assert.Equal("AP", Assert.Single(saved!).Name);

        vm.Selected.Name = "";
        Assert.False(await vm.SaveAsync());
        Assert.Equal("AP", Assert.Single(saved!).Name);
    }

    [Fact]
    public void Pattern_scope_previews_matching_schemas_and_round_trips()
    {
        var vm = Make(null);
        vm.New();
        vm.Selected!.IsPatternScope = true;
        vm.Selected.SchemaPattern = "_USER$";

        Assert.Equal(SchemaFilterScope.Pattern, vm.Selected.Scope);
        Assert.Equal(["EAI_USER", "APP_USER"], vm.Selected.PatternMatches);
        Assert.Equal("맞는 스키마 2/4개", vm.Selected.PatternMatchCountText);
        Assert.True(vm.CanConfirm);

        vm.Selected.SchemaPattern = "^hr$";
        Assert.Equal(["HR"], vm.Selected.PatternMatches);

        var f = Assert.Single(vm.BuildFilters());
        Assert.Equal(SchemaFilterScope.Pattern, f.Scope);
        Assert.Equal("^hr$", f.SchemaPattern);
        Assert.Equal("^hr$", FilterDraft.From(f, AllSchemas).SchemaPattern);
    }

    [Fact]
    public async Task Invalid_pattern_blocks_save_and_confirm_until_fixed()
    {
        var saved = 0;
        var vm = new SchemaFilterEditorViewModel([], null, AllSchemas, _ =>
        {
            saved++;
            return Task.CompletedTask;
        });
        vm.New();
        vm.Selected!.IsPatternScope = true;
        vm.Selected.SchemaPattern = "(_USER";

        Assert.True(vm.Selected.HasPatternError);
        Assert.NotNull(vm.Selected.PatternError);
        Assert.Empty(vm.Selected.PatternMatches);
        Assert.False(vm.CanConfirm);
        Assert.False(vm.Validate());
        Assert.StartsWith("스키마 패턴이 올바르지 않습니다", vm.Error, StringComparison.Ordinal);
        Assert.False(await vm.SaveAsync());
        Assert.Equal(0, saved);

        vm.Selected.SchemaPattern = "_USER$";
        Assert.True(vm.CanConfirm);
        Assert.True(await vm.SaveAsync());
        Assert.Equal(1, saved);
    }

    [Fact]
    public void Leaving_pattern_scope_clears_the_error()
    {
        var vm = Make(null);
        vm.New();
        vm.Selected!.IsPatternScope = true;
        vm.Selected.SchemaPattern = "[";
        Assert.False(vm.CanConfirm);

        vm.Selected.IsAllScope = true;

        Assert.True(vm.CanConfirm);
        Assert.Null(vm.Selected.PatternError);
        Assert.True(vm.Validate());
    }
}
