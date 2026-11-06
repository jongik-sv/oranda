using Oranda.App.Features.DataCopy;
using Oranda.App.Tests.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Tests.Features.DataCopy;

/// <summary>데이터 복사 1단계(규칙 편집) 화면 모델: 대상 목록·기간 열 고르기·기간 날짜 표시·명령 등록.</summary>
public sealed class RuleEditorTests
{
    private static RuleEditorViewModel Editor(DataCopyHarness? harness = null) => new((harness ?? new DataCopyHarness()).Services());

    private static TableRuleViewModel PeriodRule(params ColumnInfo[] columns)
    {
        var rule = new TableRuleViewModel(TestJobs.Rule("ORDERS", RuleKind.Period));
        rule.SetSource(TestJobs.Table("ORDERS", columns));
        return rule;
    }

    [Fact]
    public void Target_list_disables_production_with_reason()
    {
        var h = new DataCopyHarness();
        var editor = Editor(h);
        editor.SetConnections([h.Source, h.Target, h.Production]);

        var prod = Assert.Single(editor.TargetOptions, o => o.Profile.Name == "PRODDB");
        Assert.False(prod.IsEnabled);
        Assert.False(string.IsNullOrWhiteSpace(prod.Tooltip));
        Assert.All(editor.TargetOptions.Where(o => o.Profile.Name != "PRODDB"), o => Assert.True(o.IsEnabled));
        Assert.All(editor.SourceOptions, o => Assert.True(o.IsEnabled)); // 원본으로는 운영도 읽는다(읽기 전용)
    }

    [Fact]
    public void Period_rule_lists_created_at_columns_after_business_dates()
    {
        var rule = PeriodRule(
            TestJobs.Column("CREATED_AT", "TIMESTAMP(6)", 1),
            TestJobs.Column("NAME", "VARCHAR2", 2, length: 50),
            TestJobs.Column("ORDER_DT", "DATE", 3));

        Assert.Equal(["ORDER_DT", "CREATED_AT"], rule.PeriodColumnOptions.Select(o => o.Column));
        Assert.DoesNotContain("생성 시각", rule.PeriodColumnOptions[0].Display, StringComparison.Ordinal);
        Assert.Contains("생성 시각", rule.PeriodColumnOptions[1].Display, StringComparison.Ordinal);
    }

    [Fact]
    public void Period_rule_defaults_string_format_from_length()
    {
        var rule = PeriodRule(TestJobs.Column("ORDER_DT", "VARCHAR2", 1, length: 8), TestJobs.Column("MONTH_YM", "CHAR", 2, length: 6));

        rule.FirstColumn = rule.PeriodColumnOptions.Single(o => o.Column == "ORDER_DT");
        Assert.Equal("YYYYMMDD", rule.FirstFormat);

        rule.FirstColumn = rule.PeriodColumnOptions.Single(o => o.Column == "MONTH_YM");
        Assert.Equal("YYYYMM", rule.FirstFormat);
    }

    [Fact]
    public void Second_period_column_is_optional_and_max_two()
    {
        var rule = PeriodRule(
            TestJobs.Column("ORDER_DT", "DATE", 1), TestJobs.Column("CREATED_AT", "TIMESTAMP", 2), TestJobs.Column("UPDATED_DT", "DATE", 3));
        rule.FirstColumn = rule.PeriodColumnOptions[0];

        Assert.Single(rule.ToRule().PeriodColumns); // 두 번째 열은 선택
        Assert.True(rule.CanAddSecondColumn);

        rule.AddSecondColumn();
        var two = rule.ToRule().PeriodColumns;
        Assert.Equal(2, two.Count);
        Assert.NotEqual(two[0].Column, two[1].Column);
        Assert.False(rule.CanAddSecondColumn); // 최대 두 개

        rule.RemoveSecondColumn();
        Assert.Single(rule.ToRule().PeriodColumns);
    }

    [Fact]
    public void Recreate_action_visible_only_for_development_target()
    {
        var h = new DataCopyHarness();
        var editor = Editor(h);
        editor.SetConnections([h.Source, h.Target]);

        editor.Target = editor.TargetOptions.Single(o => o.Profile.Name == "SRC"); // 개발
        Assert.True(editor.CanRecreate);
        Assert.Contains(TargetAction.Recreate, editor.AvailableActions);

        editor.Target = editor.TargetOptions.Single(o => o.Profile.Name == "TGT"); // 테스트
        Assert.False(editor.CanRecreate);
        Assert.DoesNotContain(TargetAction.Recreate, editor.AvailableActions);
    }

    [Fact]
    public void Merge_note_is_visible_only_for_merge_action()
    {
        var rule = new TableRuleViewModel(TestJobs.Rule("CODES")); // 전체 규칙 → PK 없으면 비우고 넣기
        rule.Action = TargetAction.DeleteRangeInsert;
        Assert.False(rule.IsMerge);

        rule.Action = TargetAction.MergeByPk;
        Assert.True(rule.IsMerge);
    }

    [Fact]
    public void Consistent_read_toggle_defaults_on()
    {
        var h = new DataCopyHarness();
        var editor = Editor(h);
        Assert.True(editor.ConsistentRead);

        var job = TestJobs.Job("읽기 일관성");
        editor.SetConnections([h.Source, h.Target]);
        editor.Load(job, h.Source, h.Target);
        Assert.True(editor.ConsistentRead);
        Assert.True(editor.BuildJob().ConsistentRead);

        editor.ConsistentRead = false;
        Assert.False(editor.BuildJob().ConsistentRead);
    }

    [Fact]
    public void Calculated_dates_are_always_shown()
    {
        var editor = Editor(); // 가짜 시계: 2026-11-06
        Assert.Equal("기간 없음", editor.PeriodText);

        editor.PeriodChoice = editor.PeriodKinds.Single(k => k.Kind == PeriodKind.LastMonth);
        Assert.Equal("2026-10-07 ~ 2026-11-07", editor.PeriodText);

        editor.PeriodChoice = editor.PeriodKinds.Single(k => k.Kind == PeriodKind.PreviousMonth);
        Assert.Equal("2026-10-01 ~ 2026-11-01", editor.PeriodText);
    }

    [Fact]
    public void Condition_cell_shows_column_and_format()
    {
        var rule = PeriodRule(TestJobs.Column("ORDER_DT", "VARCHAR2", 1, length: 8));
        rule.FirstColumn = rule.PeriodColumnOptions[0];

        Assert.Equal("ORDER_DT · YYYYMMDD 문자열", rule.ConditionText);
    }

    [Fact]
    public async Task Add_to_data_copy_from_schema_adds_table_rule()
    {
        var h = new DataCopyHarness();
        h.Metadata.Tables[new DbObjectRef("APP", "ORDERS", "TABLE")] =
            TestJobs.Table("ORDERS", TestJobs.Column("ORDER_ID", "NUMBER", 1), TestJobs.Column("ORDER_DT", "DATE", 2));
        var vm = new DataCopyViewModel(h.Services());
        await vm.InitializeAsync();

        await vm.AddTableAsync(new DbObjectRef("APP", "ORDERS", "TABLE"), h.Source);

        Assert.True(vm.HasJob); // 열린 작업이 없으면 새로 만든다
        var added = Assert.Single(vm.Editor.Rules);
        Assert.Equal("ORDERS", added.Table);
        Assert.True(added.HasColumns); // 원본에서 열 정보를 읽어 왔다
        Assert.Equal(["ORDER_DT"], added.PeriodColumnOptions.Select(o => o.Column));
        Assert.True(Assert.Single(h.Opened).ReadOnly); // 열 정보를 읽는 원본 세션도 읽기 전용
        Assert.Equal("SRC", vm.Editor.Source?.Profile.Name);
        Assert.Equal("APP", vm.Editor.SourceOwner);

        await vm.AddTableAsync(new DbObjectRef("APP", "ORDERS", "TABLE"), h.Source); // 같은 표를 다시 추가해도 규칙은 하나
        Assert.Single(vm.Editor.Rules);
    }

    [Fact]
    public void Commands_are_listed_in_commands_md()
    {
        var doc = File.ReadAllText(FindCommandsDoc());
        Assert.Contains("`oranda.tools.dataCopy`", doc, StringComparison.Ordinal);
        Assert.Contains("`oranda.schema.addToDataCopy`", doc, StringComparison.Ordinal);

        var registered = ShellTestHelpers.AppContributions.Commands.ToDictionary(c => c.Definition.Id);
        Assert.Equal(typeof(DataCopyCommandHandler), registered["oranda.tools.dataCopy"].HandlerType);
        Assert.Contains("menu:tools/dataCopy@41", registered["oranda.tools.dataCopy"].Definition.Placements);
        Assert.Equal("schemaFocus", registered["oranda.schema.addToDataCopy"].Definition.When);
        Assert.Equal("schemaNodeSelected", registered["oranda.schema.addToDataCopy"].Definition.Enablement);
        Assert.Contains("context:schema/dataCopy@46", registered["oranda.schema.addToDataCopy"].Definition.Placements);
    }

    private static string FindCommandsDoc()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "docs", "design", "commands.md");
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException("docs/design/commands.md 를 찾지 못했다.");
    }
}
