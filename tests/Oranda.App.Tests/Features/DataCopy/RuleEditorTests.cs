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
    public void Saved_period_columns_show_in_combo_before_columns_are_read()
    {
        // 저장된 작업을 열면 열 정보 읽기 전이라 후보 목록이 비어 있다. 저장된 열을 후보에 넣어야 콤보가 빈칸이 아니다.
        var rule = new TableRuleViewModel(TestJobs.Rule("ORDERS", RuleKind.Period) with
        {
            PeriodColumns = [new PeriodColumn("ORDER_DT", "YYYYMMDD"), new PeriodColumn("UPDATED_AT")],
        });

        Assert.Equal(["ORDER_DT", "UPDATED_AT"], rule.PeriodColumnOptions.Select(o => o.Column));
        Assert.Same(rule.PeriodColumnOptions[0], rule.FirstColumn);
        Assert.Same(rule.PeriodColumnOptions[1], rule.SecondColumn);
        Assert.Equal("YYYYMMDD", rule.FirstFormat);
        Assert.True(rule.FirstFormatVisible); // 형식 칸도 보인다
        Assert.False(rule.SecondFormatVisible); // 형식 없는 열(DATE·TIMESTAMP 로 저장됨)
        Assert.Equal(2, rule.ToRule().PeriodColumns.Count);

        // 열 정보를 읽으면 실제 열 항목으로 바뀐다(저장 형식 유지).
        rule.SetSource(TestJobs.Table("ORDERS", TestJobs.Column("ORDER_DT", "VARCHAR2", 1, length: 8), TestJobs.Column("UPDATED_AT", "DATE", 2)));
        Assert.Equal("VARCHAR2", rule.FirstColumn!.DataType);
        Assert.Equal("DATE", rule.SecondColumn!.DataType);
        Assert.Equal("YYYYMMDD", rule.FirstFormat);
        Assert.Equal(2, rule.PeriodColumnOptions.Count);
    }

    [Fact]
    public void Saved_parent_key_shows_in_combo_before_columns_are_read()
    {
        // 열 정보 읽기 전에는 FK 후보가 없다. 저장된 부모 키를 후보에 넣어야 「부모 테이블(FK)」 콤보가 빈칸이 아니다.
        var saved = new ParentLink("CUSTOMERS", ["CUST_ID"], ["CUST_ID"], "FK_ITEMS_CUST");
        var rule = new TableRuleViewModel(TestJobs.Rule("ORDER_ITEMS", RuleKind.ParentKey) with { Parent = saved });

        Assert.Same(saved, Assert.Single(rule.ParentCandidates));
        Assert.Same(rule.ParentCandidates[0], rule.Parent);

        // 열 정보를 읽으면 같은 FK 의 실제 후보로 바뀐다(저장된 임시 항목은 남지 않는다).
        var customers = new DbObjectRef("APP", "CUSTOMERS", "TABLE");
        var orders = new DbObjectRef("APP", "ORDERS", "TABLE");
        var source = TestJobs.Table("ORDER_ITEMS", TestJobs.Column("CUST_ID", "NUMBER", 1), TestJobs.Column("ORDER_ID", "NUMBER", 2)) with
        {
            Constraints =
            [
                new ConstraintInfo("FK_ITEMS_ORDERS", ConstraintKind.ForeignKey, ["ORDER_ID"]) { ReferencedTable = orders, ReferencedConstraint = "PK_ORDERS" },
                new ConstraintInfo("FK_ITEMS_CUST", ConstraintKind.ForeignKey, ["CUST_ID"]) { ReferencedTable = customers, ReferencedConstraint = "PK_CUST" },
            ],
        };
        var parents = new Dictionary<string, TableInfo>
        {
            ["CUSTOMERS"] = TestJobs.Table("CUSTOMERS") with { Constraints = [new ConstraintInfo("PK_CUST", ConstraintKind.PrimaryKey, ["CUST_ID"])] },
            ["ORDERS"] = TestJobs.Table("ORDERS") with { Constraints = [new ConstraintInfo("PK_ORDERS", ConstraintKind.PrimaryKey, ["ORDER_ID"])] },
        };
        rule.SetSource(source, parents);

        Assert.Equal(2, rule.ParentCandidates.Count);
        Assert.Contains(rule.Parent!, rule.ParentCandidates);
        Assert.NotSame(saved, rule.Parent);
        Assert.Equal("FK_ITEMS_CUST", rule.Parent!.ForeignKeyName);
        Assert.Equal("CUSTOMERS", rule.ToRule().Parent!.ParentTable);
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
    public async Task Added_table_starts_target_owner_from_the_target_connection_user()
    {
        var h = new DataCopyHarness();
        h.Metadata.Tables[new DbObjectRef("APP", "ORDERS", "TABLE")] = TestJobs.Table("ORDERS", TestJobs.Column("ORDER_ID", "NUMBER", 1));
        var vm = await h.OpenAsync(TestJobs.Job("소유자 기본값"));
        vm.Editor.SourceOwner = string.Empty;
        vm.Editor.TargetOwner = string.Empty;

        await vm.AddTableAsync(new DbObjectRef("APP", "ORDERS", "TABLE"), h.Source);

        Assert.Equal("APP", vm.Editor.SourceOwner);
        Assert.Equal("TGT_USER", vm.Editor.TargetOwner); // 원본 표 소유자가 아니라 대상 접속의 사용자(스키마)
    }

    [Fact]
    public async Task Table_added_before_choosing_a_target_takes_the_target_user_once_a_target_is_chosen()
    {
        var h = new DataCopyHarness();
        h.Metadata.Tables[new DbObjectRef("APP", "ORDERS", "TABLE")] = TestJobs.Table("ORDERS", TestJobs.Column("ORDER_ID", "NUMBER", 1));
        var vm = new DataCopyViewModel(h.Services());
        await vm.InitializeAsync();

        await vm.AddTableAsync(new DbObjectRef("APP", "ORDERS", "TABLE"), h.Source); // 스키마 브라우저: 새 작업, 대상 없음
        Assert.Null(vm.Editor.Target);
        Assert.Equal("APP", vm.Editor.TargetOwner);

        vm.Editor.Target = vm.Editor.TargetOptions.Single(o => o.Profile.Name == "TGT");
        Assert.Equal("TGT_USER", vm.Editor.TargetOwner);

        vm.Editor.TargetOwner = "MINE";
        vm.Editor.Target = vm.Editor.TargetOptions.Single(o => o.Profile.Name == "SRC");
        Assert.Equal("MINE", vm.Editor.TargetOwner); // 한 번 바꾼 뒤에는 사용자가 쓴 값을 덮지 않는다
    }

    [Fact]
    public void Extra_where_is_saved_only_for_period_rules_and_shown_in_the_condition_column()
    {
        var rule = PeriodRule(TestJobs.Column("ORDER_DT", "DATE", 1));
        rule.FirstColumn = rule.PeriodColumnOptions[0];
        rule.ExtraWhere = "  STATUS = 'DONE' ";

        Assert.Equal("STATUS = 'DONE'", rule.ToRule().ExtraWhere);
        Assert.Equal("ORDER_DT · 추가 조건 STATUS = 'DONE'", rule.ConditionText);

        rule.ChangeKind(RuleKind.All);
        Assert.Null(rule.ToRule().ExtraWhere); // 기간이 아닌 규칙에는 저장하지 않는다

        var loaded = new TableRuleViewModel(TestJobs.Rule("ORDERS", RuleKind.Period) with { ExtraWhere = "X = 1" });
        Assert.Equal("X = 1", loaded.ExtraWhere);
        Assert.True(loaded.HasExtraWhere);
    }

    [Fact]
    public async Task Condition_that_escapes_its_parentheses_is_shown_as_an_error_and_blocks_the_precheck()
    {
        var h = new DataCopyHarness();
        var vm = await h.OpenAsync(TestJobs.Job("조건 검사", rules: TestJobs.Rule("AUDIT", RuleKind.Custom) with { CustomWhere = "A = 1" }));
        var rule = vm.Editor.Rules[0];
        Assert.False(rule.HasConditionError);
        Assert.Null(vm.Editor.Validate());

        rule.CustomWhere = "A = 1) OR (1=1";
        Assert.True(rule.HasConditionError);
        Assert.Contains("AUDIT", vm.Editor.Validate(), StringComparison.Ordinal); // 사전 점검으로 넘어가지 않는다

        rule.ChangeKind(RuleKind.Period);
        Assert.False(rule.HasConditionError); // 기간 규칙은 추가 조건만 본다
        rule.ExtraWhere = "X = 1 -- 주석";
        Assert.True(rule.HasConditionError);
        rule.ExtraWhere = "NOTE = '--'";
        Assert.False(rule.HasConditionError);
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
