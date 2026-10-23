using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.MyBatisWorkspace.Expanded;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.Binds;
using Oranda.App.Features.MyBatisWorkspace.Parameters;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Composition;
using Oranda.App.Tests.Features.MyBatisWorkspace.Fakes;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Execution;
using Oranda.Core.MyBatis;
using Oranda.Core.Shell;
using Oranda.Language.MyBatis;
using Oranda.Testing.MyBatis;
using Xunit.Abstractions;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// TSK-03-02 「펼친 SQL」(시나리오 콤보·명령 단추. TSK-03-10 부터 쿼리 탭 안 분할이고 바인드 · 치환 표는 없다)과 문장 파라미터의 「바인드 변수」 행 동기화
/// (소유 = TSK-03-02, docs/oranda1/design.md 「기능 Task 편집 지점」). 별도 「파라미터 · 시나리오」 창은 없다(2026-10-23 사용자 결정).
/// 계산기는 실제 <see cref="MyBatisEvaluator"/>, 저장소·세션·대화상자는 가짜다. 시안 Main 의 시나리오 4개(이름 검색·부서 목록·전체 조건·조건 없음)를 쓴다.
/// </summary>
public sealed partial class ParamsRunTests(ITestOutputHelper output)
{
    private const string StatementId = "selectEmpList";

    private static BindVariableRow RowOf(SqlEditorViewModel tab, string name) => tab.Binds.Rows.Single(r => r.Name == name);

    // ---- 바인드 변수 행 동기화 --------------------------------------------------------------

    [Fact]
    public Task Every_statement_parameter_becomes_a_bind_row_and_stays_when_its_block_is_off() => Ui.Run(() =>
    {
        using var rig = Rig.Create();
        var eval = rig.Hub.Active!;
        var tab = rig.Tab;

        // 문장 파라미터 전부(처음 나온 순서). ${} 에만 쓰인 orderBy 는 앞에 $ 가 붙는다(행 이름은 창이 대문자로 바꾼다).
        Assert.Equal(["EMPNO", "ENAME", "DEPTIDS", "$ORDERBY"], tab.Binds.Rows.Select(r => r.Name));

        // 이름 검색 시나리오: ename 만 켜짐. 값 칸에는 시나리오 값이 칸 글로 들어간다.
        Assert.Equal("KIM", RowOf(tab, "ENAME").Text);
        Assert.Equal(string.Empty, RowOf(tab, "EMPNO").Text); // null = 빈 칸
        Assert.Equal("E.ENAME", RowOf(tab, "$ORDERBY").Text);

        // 조건 없음 시나리오: 블록이 모두 꺼져 펼친 SQL 에서 바인드가 사라져도 행은 남는다
        eval.SelectScenario(eval.Scenarios[3]);
        Assert.DoesNotContain("WHERE", eval.Result!.Sql, StringComparison.Ordinal);
        Assert.Empty(eval.Result.Binds);
        Assert.Equal(["EMPNO", "ENAME", "DEPTIDS", "$ORDERBY"], tab.Binds.Rows.Select(r => r.Name));

        // 부서 목록 시나리오: 목록은 「[10, 20]」, 빈 글자는 ''
        eval.SelectScenario(eval.Scenarios[1]);
        Assert.Equal("[10, 20]", RowOf(tab, "DEPTIDS").Text);
        Assert.Equal("''", RowOf(tab, "ENAME").Text);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Names_used_only_in_a_test_expression_also_get_a_row() => Ui.Run(() =>
    {
        using var rig = Rig.Create(withScenarios: false);
        rig.AddStatementTab("flagged", """<select id="flagged">SELECT 1 FROM DUAL <if test="debug == true">AND 1 = 1</if> WHERE X = #{x} ORDER BY ${col}</select>""");

        Assert.Equal(["DEBUG", "X", "$COL"], rig.Tab.Binds.Rows.Select(r => r.Name));
        Assert.Equal(BindDataType.Boolean, RowOf(rig.Tab, "DEBUG").Type); // 식에서 짐작한 형식
        return Task.CompletedTask;
    });

    [Fact]
    public Task Typing_cell_text_into_a_row_changes_the_expanded_sql_as_the_value_says() => Ui.Run(() =>
    {
        using var rig = Rig.Create(withScenarios: false);
        var expanded = MyBatisExpandedViewModel.For(rig.Hub);
        var eval = rig.Hub.Active!;
        var tab = rig.Tab;
        void Edit(string row, string text)
        {
            RowOf(tab, row).Text = text;
            eval.Flush();
        }

        // 빈 칸 = null: 조건이 모두 꺼진다
        Assert.DoesNotContain("WHERE", expanded.Sql, StringComparison.Ordinal);

        // '' = 빈 글자: empNo 는 null 이 아니라 test 가 참이고 바인드가 생긴다. ename != '' 는 거짓이라 ename 조건은 꺼진 채다.
        Edit("EMPNO", "''");
        Edit("ENAME", "''");
        Assert.Contains(Shown(expanded), b => b is { Name: ":empNo", ValueText: "''" });
        Assert.DoesNotContain(Shown(expanded), b => b.Name == ":ename");

        // 글자는 따옴표 없이 써도 글자
        Edit("ENAME", "SMITH");
        Assert.Contains(Shown(expanded), b => b is { Name: ":ename", ValueText: "'SMITH'" });

        // 목록: foreach IN 목록이 항목마다 바인드가 된다
        Edit("DEPTIDS", "['A', 'B']");
        Assert.Contains("IN", expanded.Sql, StringComparison.Ordinal);
        Assert.Contains(Shown(expanded), b => b is { Name: ":id_1", ValueText: "'A'" });
        Assert.Contains(Shown(expanded), b => b is { Name: ":id_2", ValueText: "'B'" });

        Edit("DEPTIDS", "10, 20");
        Assert.Contains(Shown(expanded), b => b is { Name: ":id_2", ValueText: "20" });

        // 빈 칸으로 되돌리면 다시 꺼진다
        Edit("DEPTIDS", string.Empty);
        Assert.DoesNotContain(Shown(expanded), b => b.Name.StartsWith(":id_", StringComparison.Ordinal));
        return Task.CompletedTask;
    });

    // ---- 시나리오 고르기 --------------------------------------------------------------------

    [Fact]
    public Task Picking_a_scenario_changes_rows_expanded_sql_binds_and_condition_count_together() => Ui.Run(() =>
    {
        using var rig = Rig.Create();
        var expanded = MyBatisExpandedViewModel.For(rig.Hub);
        var tab = rig.Tab;
        Assert.True(expanded.HasStatement);
        Assert.Equal(["이름 검색", "부서 목록", "전체 조건", "조건 없음"], expanded.Scenarios.Select(i => i.Name));
        Assert.Same(expanded.Scenarios[0], expanded.SelectedScenario);

        var seen = new List<string>();
        foreach (var item in expanded.Scenarios)
        {
            expanded.SelectedScenario = item;
            var binds = string.Join(", ", Shown(expanded).Select(b => $"{b.Name}[{b.TypeText}]={b.ValueText}"));
            seen.Add($"{item.Name} | {expanded.ConditionChip} | where={expanded.Sql.Contains("WHERE", StringComparison.Ordinal)} | {binds}");
            Assert.Contains(item.Name, expanded.Header, StringComparison.Ordinal);
        }

        output.WriteLine(string.Join(Environment.NewLine, seen));

        // 이름 검색: ename 만 켜짐
        expanded.SelectedScenario = expanded.Scenarios[0];
        Assert.Equal("KIM", RowOf(tab, "ENAME").Text);
        Assert.Contains("LIKE '%' || :ename || '%'", expanded.Sql, StringComparison.Ordinal);
        Assert.Contains(Shown(expanded), b => b is { Name: ":ename", ValueText: "'KIM'" });
        Assert.Contains(Shown(expanded), b => b is { Name: "${orderBy}", IsSubstitution: true, ValueText: "E.ENAME" });
        Assert.DoesNotContain("EMPNO = :empNo", expanded.Sql, StringComparison.Ordinal);
        Assert.Equal("켜진 조건 1/3", expanded.ConditionChip);

        // 부서 목록: 목록 값이 항목마다 바인드가 된다
        expanded.SelectedScenario = expanded.Scenarios[1];
        Assert.Equal("[10, 20]", RowOf(tab, "DEPTIDS").Text);
        Assert.Contains(Shown(expanded), b => b is { Name: ":id_1", ValueText: "10" });
        Assert.Contains(Shown(expanded), b => b is { Name: ":id_2", ValueText: "20" });

        // 조건 없음: where 가 사라지고 켜진 조건이 없다
        expanded.SelectedScenario = expanded.Scenarios[3];
        Assert.DoesNotContain("WHERE", expanded.Sql, StringComparison.Ordinal);
        Assert.Equal("켜진 조건 0/3", expanded.ConditionChip);

        // 전체 조건: 세 조건이 모두 켜진다
        expanded.SelectedScenario = expanded.Scenarios[2];
        Assert.Equal("켜진 조건 3/3", expanded.ConditionChip);

        // 시안: :empNo = NUMBER 7369. 숫자 시나리오 값이 추정 형식(Varchar2)을 이기고 바인드 창 형식 콤보도 따라간다.
        Assert.Contains(Shown(expanded), b => b is { Name: ":empNo", TypeText: "숫자", ValueText: "7369" });
        Assert.Equal(BindDataType.Number, RowOf(tab, "EMPNO").Type);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Scenario_number_values_set_the_row_type_unless_the_user_chose_one() => Ui.Run(() =>
    {
        using var rig = Rig.Create();
        var eval = rig.Hub.Active!;
        var tab = rig.Tab;
        Assert.Equal(BindDataType.Varchar2, RowOf(tab, "EMPNO").Type); // 이름 검색: empNo = null, test 식만으로는 글자

        eval.SelectScenario(eval.Scenarios[2]);
        Assert.Equal(BindDataType.Number, RowOf(tab, "EMPNO").Type);
        Assert.Equal(BindDataType.Number, eval.TypeOf("empNo"));
        eval.SelectScenario(eval.Scenarios[0]);
        Assert.Equal(BindDataType.Number, RowOf(tab, "EMPNO").Type); // null 값은 형식을 되돌리지 않는다

        // 사용자가 콤보로 고른 형식은 시나리오를 다시 골라도 그대로다.
        RowOf(tab, "EMPNO").Type = BindDataType.Varchar2;
        eval.SelectScenario(eval.Scenarios[2]);
        Assert.Equal(BindDataType.Varchar2, RowOf(tab, "EMPNO").Type);
        Assert.Equal<object?>("7369", Assert.Single(eval.Result!.Binds, b => b.Parameter == "empNo").Value);
        return Task.CompletedTask;
    });

    // ---- 시나리오 추가 · 이름 바꾸기 · 복제 · 지우기 -----------------------------------------

    [Fact]
    public Task Add_rename_duplicate_and_delete_reach_the_store_and_the_combo() => Ui.Run(async () =>
    {
        using var rig = Rig.Create();
        var vm = MyBatisExpandedViewModel.For(rig.Hub);
        Assert.Equal(4, vm.Scenarios.Count);

        // 추가: 「시나리오 n」 으로 목록 끝에 넣고 고른다(지금 값을 그대로 담는다)
        await vm.AddCommand.ExecuteAsync(null);
        Assert.Equal(5, rig.Store.All.Count);
        Assert.Equal(MyBatisText.Format("MyBatis.scenario.newName", "시나리오 {0}", 5), vm.Scenarios[^1].Name);
        Assert.Same(vm.Scenarios[^1], vm.SelectedScenario);
        Assert.Equal(rig.Store.All[^1].Name, vm.Scenarios[^1].Name);
        Assert.Equal("KIM", rig.Store.All[^1].Values["ename"].Text);

        // 이름 바꾸기: 작은 입력 대화(호스트의 대화 상자)로 묻는다
        vm.SelectedScenario = vm.Scenarios[1];
        rig.Dialogs.Prompt = "부서 둘";
        await vm.RenameCommand.ExecuteAsync(null);
        Assert.Equal("부서 둘", vm.Scenarios[1].Name);
        Assert.Equal("부서 둘", rig.Store.All.Single(s => s.Id == vm.Scenarios[1].Id).Name);
        Assert.Equal("부서 목록", rig.Dialogs.LastInitial);

        // 취소(null)면 아무것도 바뀌지 않는다
        rig.Dialogs.Prompt = null;
        await vm.RenameCommand.ExecuteAsync(null);
        Assert.Equal("부서 둘", vm.Scenarios[1].Name);

        // 복제: 「원래 이름 복사」 를 원본 바로 뒤에 두고 고른다
        await vm.DuplicateCommand.ExecuteAsync(null);
        Assert.Equal(6, rig.Store.All.Count);
        Assert.Equal(MyBatisText.Format("MyBatis.scenario.copyName", "{0} 복사", "부서 둘"), vm.Scenarios[2].Name);
        Assert.Same(vm.Scenarios[2], vm.SelectedScenario);
        Assert.Equal("[10, 20]", RowOf(rig.Tab, "DEPTIDS").Text);

        // 지우기(확인 없음): 저장소에서 빠지고 다음 시나리오가 골라진다
        var deletedId = vm.Scenarios[2].Id;
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(5, rig.Store.All.Count);
        Assert.DoesNotContain(rig.Store.All, s => s.Id == deletedId);
        Assert.DoesNotContain(vm.Scenarios, s => s.Id == deletedId);
        Assert.NotNull(vm.SelectedScenario);
    });

    [Fact]
    public Task A_failing_store_is_reported_in_one_line_and_does_not_throw() => Ui.Run(async () =>
    {
        using var rig = Rig.Create(withScenarios: false);
        var hub = new MyBatisEvalHub(MyBatisEvalHub.Services(new MyBatisEvaluator(), new ThrowingStore()))
        {
            Host = rig.Host,
            Debounce = TimeSpan.FromMilliseconds(40),
            Post = a => a(),
        };
        var vm = MyBatisExpandedViewModel.For(hub);
        Assert.True(vm.HasStatement);
        Assert.False(vm.HasNotice);

        await vm.AddCommand.ExecuteAsync(null);

        Assert.True(vm.HasNotice);
        Assert.Contains("디스크", vm.Notice, StringComparison.Ordinal);
        hub.Active?.Dispose();
    });

    // ---- 값 고침 · 디바운스 -----------------------------------------------------------------

    [Fact]
    public Task Editing_a_row_changes_the_expanded_sql_only_after_the_debounce_and_only_once() => Ui.Run(async () =>
    {
        using var rig = Rig.Create(debounceMs: 120);
        var expanded = MyBatisExpandedViewModel.For(rig.Hub);
        var eval = rig.Hub.Active!;
        var changes = 0;
        eval.Changed += (_, _) => changes++;

        var ename = RowOf(rig.Tab, "ENAME");
        ename.Text = "SMITH";
        ename.Text = "SMITHS"; // 연달아 고쳐도 한 번만 다시 계산한다

        // 디바운스 전: 행 값은 이미 바뀌었지만 펼친 SQL·바인드는 그대로
        Assert.Equal(0, changes);
        Assert.Contains(Shown(expanded), b => b is { Name: ":ename", ValueText: "'KIM'" });

        await HeadlessHost.WaitUntil(() => changes > 0);
        await Task.Delay(300);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, changes);
        Assert.Contains(Shown(expanded), b => b is { Name: ":ename", ValueText: "'SMITHS'" });

        // 고른 시나리오에도 저장된다
        Assert.Equal("SMITHS", rig.Store.All[0].Values["ename"].Text);
    });

    [Fact]
    public Task A_save_keeps_unedited_value_kinds_and_a_late_save_does_not_take_the_selection_back() => Ui.Run(async () =>
    {
        HeldStore? held = null;
        using var rig = Rig.Create(wrapStore: s => held = new HeldStore(s));
        var eval = rig.Hub.Active!;
        eval.SelectScenario(eval.Scenarios[2]); // 전체 조건: empNo = Number 7369

        // 고친 값의 저장이 끝나기 전에 다른 시나리오를 고른다(Sqlite 저장소는 백그라운드에서 쓴다)
        held!.Hold = true;
        RowOf(rig.Tab, "ENAME").Text = "SMITH";
        eval.Flush();
        eval.SelectScenario(eval.Scenarios[1]);
        held.Release();
        await HeadlessHost.WaitUntil(() => held.Saved);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("부서 목록", eval.SelectedScenario!.Name);
        Assert.Equal("''", RowOf(rig.Tab, "ENAME").Text);
        var all = rig.Store.All.Single(s => s.Name == "전체 조건");
        Assert.Equal("SMITH", all.Values["ename"].Text);
        Assert.Equal(MyBatisScenarioValueKind.Number, all.Values["empNo"].Kind); // 고치지 않은 값은 종류 그대로
        Assert.Equal(string.Empty, rig.Store.All.Single(s => s.Name == "부서 목록").Values["ename"].Text);
    });

    [Fact]
    public Task A_margin_override_right_after_an_edit_still_saves_the_edit() => Ui.Run(() =>
    {
        using var rig = Rig.Create(debounceMs: 10_000);
        var eval = rig.Hub.Active!;
        RowOf(rig.Tab, "ENAME").Text = "LEE";

        eval.SetOverride(eval.Result!.Blocks.First(b => b.Tag == "if").Range, true);

        Assert.Equal("LEE", rig.Store.All[0].Values["ename"].Text);
        Assert.Contains("'%' || :ename || '%'", eval.Result!.Sql, StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_row_the_user_added_is_adopted_without_saving_and_kept_when_the_parameter_goes() => Ui.Run(() =>
    {
        using var rig = Rig.Create();
        var eval = rig.Hub.Active!;
        var own = rig.Tab.Binds.Add("DEPTNO");
        own.Text = "10";
        var original = rig.Session.Elements[StatementId];

        rig.Session.Elements[StatementId] = original with { Text = original.Text.Replace("</select>", "  AND D.DEPTNO = #{deptNo}\n</select>", StringComparison.Ordinal) };
        eval.Recompute();
        eval.Flush();
        Assert.Equal("10", MyBatisValues.CellText(eval.Values["deptNo"]));
        Assert.DoesNotContain("deptNo", rig.Store.All[0].Values.Keys); // 가져온 값은 시나리오에 저장하지 않는다

        rig.Session.Elements[StatementId] = original;
        eval.Recompute();
        Assert.Contains(own, rig.Tab.Binds.Rows); // 사용자가 더한 행은 파라미터가 사라져도 남는다
        return Task.CompletedTask;
    });

    // ---- 화면 붙이기 · 명령 단추 ------------------------------------------------------------

    [Fact]
    public Task The_panel_renders_in_a_window_and_the_header_buttons_run_registry_commands() => Ui.Run(async () =>
    {
        using var rig = Rig.Create();
        var registry = new FakeRegistry();
        var expanded = MyBatisExpandedViewModel.For(rig.Hub, registry, new AlwaysKeys());
        var view = new MyBatisExpandedView { DataContext = expanded };
        var window = new Window { Width = 1100, Height = 400, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // 시나리오 콤보 4개 항목 + 아이콘 단추 4개(툴팁 필수)
        var combo = view.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ScenarioCombo");
        Assert.Equal(4, combo.ItemCount);
        Assert.Same(expanded.SelectedScenario, combo.SelectedItem);
        var actionBar = view.FindControl<StackPanel>("ActionBar")!;
        var scenarioButtons = view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("toolbar") && !actionBar.GetVisualDescendants().Contains(b)).ToList();
        Assert.Equal(4, scenarioButtons.Count);
        Assert.All(scenarioButtons, b => Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(b) as string)));

        // 읽기 전용 편집기에 같은 글, 바인드 표, 명령 단추 4개
        var sqlView = view.GetVisualDescendants().OfType<SqlCodeView>().Single();
        Assert.Equal(expanded.Sql, sqlView.TextEditorControl.Document.Text);
        Assert.True(sqlView.TextEditorControl.IsReadOnly);
        Assert.NotNull(sqlView.TextEditorControl.ContextMenu);
        Assert.Empty(view.GetVisualDescendants().OfType<ListBox>()); // 바인드 · 치환 입력 목록은 없다(TSK-03-10): 값은 「바인드 변수」 창에서만
        var buttons = actionBar.Children.OfType<Button>().ToList();
        Assert.Equal(MyBatisExpandedViewModel.ActionIds.Length, buttons.Count);
        foreach (var button in buttons)
        {
            Assert.True(button.Command!.CanExecute(null));
            button.Command.Execute(null);
        }

        await Task.WhenAll(expanded.Actions.Select(a => a.Command.LastRun ?? Task.CompletedTask));
        Assert.Equal(MyBatisExpandedViewModel.ActionIds, registry.Executed);

        // 콤보로 시나리오를 바꾸면 화면의 글도 바로 바뀐다
        combo.SelectedItem = expanded.Scenarios[3];
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain("WHERE", sqlView.TextEditorControl.Document.Text, StringComparison.Ordinal);
        window.Close();
    });

    [Fact]
    public Task Without_an_active_statement_the_panel_shows_only_the_hint() => Ui.Run(() =>
    {
        using var rig = Rig.Create();
        rig.Session.SelectedStatementId = null;
        var expanded = MyBatisExpandedViewModel.For(rig.Hub);

        Assert.False(expanded.HasStatement);
        Assert.True(expanded.HasNoStatement);
        Assert.Empty(expanded.Sql);
        Assert.Empty(Shown(expanded));
        Assert.Empty(expanded.Scenarios);
        Assert.False(expanded.AddCommand.CanExecute(null));

        // 서비스 없이 만든 창(디자이너·시험)도 안내 한 줄만 보인다
        Assert.False(new MyBatisExpandedViewModel().HasStatement);

        // 문장을 고르면 따라간다
        rig.Session.SelectedStatementId = StatementId;
        Assert.True(expanded.HasStatement);
        Assert.NotEmpty(expanded.Sql);
        Assert.Equal(4, expanded.Scenarios.Count);
        return Task.CompletedTask;
    });

    [Fact]
    public Task The_container_builds_the_tab_split_with_the_registry_and_does_not_wake_the_evaluator_by_itself() => Ui.Run(() =>
    {
        var services = AppComposition.Create(new AppCompositionOptions { DevBuild = true });
        try
        {
            // 셸은 편집기 View 공장을 컨테이너로 만들고, 공장이 쿼리 탭 안 「펼친 SQL」 에 컨테이너를 넘긴다(TSK-03-10).
            var factory = (Oranda.App.Features.MyBatisWorkspace.Editor.MyBatisEditorViewFactory)services.Create(typeof(Oranda.App.Features.MyBatisWorkspace.Editor.MyBatisEditorViewFactory));
            var session = new FakeMyBatisWorkspaceSession();
            var tab = new SqlEditorViewModel(Guid.NewGuid(), "<select id=\"byId\">select 1 from dual</select>");
            session.StatementIds[tab] = "byId";
            var view = (Oranda.App.Features.MyBatisWorkspace.Editor.MyBatisStatementEditorView)factory.CreateEditor(tab, session);
            var expanded = view.Expanded!;
            Assert.Same(tab, expanded.Tab);

            // 만든 것만으로는 허브도 계산기도 깨우지 않는다(시작 경로 보호)
            Assert.False(services.DeferredScanned, services.DeferredScanTrigger);

            expanded.Attach();
            Assert.Same(services.GetService<MyBatisEvalHub>(), services.GetService<MyBatisEvalHub>());
            Assert.Equal(MyBatisExpandedViewModel.ActionIds, expanded.Actions.Select(a => a.CommandId));
            Assert.All(expanded.Actions, a => Assert.False(string.IsNullOrWhiteSpace(a.Title)));
            output.WriteLine("DeferredScanned after attach = " + services.DeferredScanned + " (" + services.DeferredScanTrigger + ")");
            expanded.Dispose();
        }
        finally
        {
            services.Dispose();
        }

        return Task.CompletedTask;
    });

    // ---- 성능 -------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Perf")]
    public Task Recompute_after_a_value_edit_is_within_50_ms() => Ui.Run(() =>
    {
        // 계약 크기(문장 500줄)의 합성 문장: where 안에 if 160개(세 줄씩) = 파라미터 160개
        var xml = new StringBuilder("<select id=\"big\">\n  SELECT * FROM T\n  <where>\n");
        for (var i = 0; i < 160; i++)
        {
            xml.Append("    <if test=\"p").Append(i).Append(" != null\">\n      AND C").Append(i).Append(" = #{p").Append(i).Append("}\n    </if>\n");
        }

        xml.Append("  </where>\n</select>");
        output.WriteLine($"합성 문장 {xml.ToString().Split('\n').Length} 줄");

        foreach (var (name, big) in new[] { ("시안 selectEmpList", false), ("합성 500줄", true) })
        {
            using var rig = Rig.Create(debounceMs: 5000);
            if (big)
            {
                rig.AddStatementTab("big", xml.ToString());
            }

            var expanded = MyBatisExpandedViewModel.For(rig.Hub);
            var eval = rig.Hub.Active!;
            var row = RowOf(rig.Tab, big ? "P0" : "ENAME");

            // 데운다(JIT·첫 계산)
            row.Text = "WARM";
            eval.Flush();

            var best = double.MaxValue;
            var total = 0d;
            const int Rounds = 20;
            for (var i = 0; i < Rounds; i++)
            {
                row.Text = "V" + i;
                var watch = Stopwatch.StartNew();
                eval.Flush(); // 다시 계산 + 바인드 행·펼친 SQL 갱신(Changed 구독자)까지
                watch.Stop();
                best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
                total += watch.Elapsed.TotalMilliseconds;
                Assert.Contains(Shown(expanded), b => b.ValueText == "'V" + i + "'");
            }

            output.WriteLine($"[{name}] 값 고침 뒤 다시 계산(Flush): 최소 {best:F2} ms, 평균 {total / Rounds:F2} ms (한도 50 ms)");
            Assert.True(best <= 50, $"[{name}] 다시 계산이 {best:F2} ms 걸렸다(한도 50 ms)");
        }

        return Task.CompletedTask;
    });

    // ---- 시험 도구 --------------------------------------------------------------------------

    /// <summary>
    /// 펼친 SQL 의 바인드·치환 한 줄(예전 「바인드 · 치환」 표 모양). TSK-03-10 에 표는 화면에서 뺐고 값은 「바인드 변수」 창에서만 넣는다 —
    /// 시험은 계산 결과에서 같은 모양으로 읽어 펼친 SQL 이 쓰는 값을 단정한다.
    /// </summary>
    private sealed record ShownBind(string Name, string TypeText, string ValueText, bool IsSubstitution, bool IsNullValue);

    private static List<ShownBind> Shown(MyBatisExpandedViewModel vm)
    {
        vm.Attach(); // 화면에 붙인 것처럼(예전 표 속성은 읽을 때 붙었다)
        var rows = new List<ShownBind>();
        if (vm.Eval?.Result is not { } result)
        {
            return rows;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bind in result.Binds.Where(b => seen.Add(b.Name)))
        {
            rows.Add(new ShownBind(":" + bind.Name, bind.JdbcType ?? KindLabel(bind.Value), FormatValue(bind.Value), false, bind.Value is null));
        }

        rows.AddRange(result.Substitutions.Select(item => new ShownBind("${" + item.Name + "}", "치환", item.Value ?? "null", true, item.Value is null)));
        return rows;
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        string s => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string KindLabel(object? value) => value switch
    {
        null => "null",
        bool => "참거짓",
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => "숫자",
        _ => "글자",
    };

    /// <summary>저장을 붙잡아 둘 수 있는 저장소(백그라운드에서 늦게 끝나는 Sqlite 저장을 흉내 낸다).</summary>
    private sealed class HeldStore(FakeMyBatisScenarioStore inner) : IMyBatisScenarioStore
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Hold { get; set; }

        public bool Saved { get; private set; }

        public event EventHandler<MyBatisScenarioChangedEventArgs>? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public void Release() => _gate.TrySetResult();

        public Task<IReadOnlyList<MyBatisScenario>> ListAsync(string mapperPath, string? ns, string statementId, CancellationToken ct) =>
            inner.ListAsync(mapperPath, ns, statementId, ct);

        public async Task<MyBatisScenario> SaveAsync(MyBatisScenario scenario, CancellationToken ct)
        {
            if (Hold)
            {
                await _gate.Task;
            }

            var saved = await inner.SaveAsync(scenario, ct);
            Saved = true;
            return saved;
        }

        public Task RenameAsync(long id, string name, CancellationToken ct) => inner.RenameAsync(id, name, ct);

        public Task<MyBatisScenario> DuplicateAsync(long id, string newName, CancellationToken ct) => inner.DuplicateAsync(id, newName, ct);

        public Task DeleteAsync(long id, CancellationToken ct) => inner.DeleteAsync(id, ct);

        public Task ReorderAsync(IReadOnlyList<long> orderedIds, CancellationToken ct) => inner.ReorderAsync(orderedIds, ct);

        public Task<int> MoveStatementAsync(string mapperPath, string oldId, string newId, CancellationToken ct) =>
            inner.MoveStatementAsync(mapperPath, oldId, newId, ct);
    }

    private sealed class Rig : IDisposable
    {
        private static readonly IMyBatisMapperDocument Document = new MapperParser().Parse(MapperSamples.EmpMapper);

        private Rig(SqlEditorHost host, FakeMyBatisWorkspaceSession session, FakeMyBatisScenarioStore store, PromptDialogs dialogs, MyBatisEvalHub hub)
        {
            Host = host;
            Session = session;
            Store = store;
            Dialogs = dialogs;
            Hub = hub;
        }

        public SqlEditorHost Host { get; }

        public FakeMyBatisWorkspaceSession Session { get; }

        public FakeMyBatisScenarioStore Store { get; }

        public PromptDialogs Dialogs { get; }

        public MyBatisEvalHub Hub { get; }

        /// <summary>지금 보고 있는 문장 탭(바인드 변수 행이 달린 탭).</summary>
        public SqlEditorViewModel Tab { get; private set; } = null!;

        public static Rig Create(bool withScenarios = true, int debounceMs = 40, Func<FakeMyBatisScenarioStore, IMyBatisScenarioStore>? wrapStore = null)
        {
            var dialogs = new PromptDialogs();
            var host = new SqlEditorHost(null, dialogs);
            var workspace = host.EnsureWorkspace();
            var session = new FakeMyBatisWorkspaceSession { MapperPath = "/tmp/EmpMapper.xml", MapperNamespace = Document.MapperNamespace };
            session.Fragments = new DocumentFragments(Document);
            var element = Document.Find(StatementId)!;
            session.Elements[StatementId] = element;
            workspace.MyBatis = session;
            var tab = new SqlEditorViewModel(Guid.NewGuid(), element.Text) { Title = StatementId };
            tab.IsDirty = false;
            session.StatementIds[tab] = StatementId;
            host.AddMyBatisTab(workspace, tab);

            var store = new FakeMyBatisScenarioStore();
            var hub = new MyBatisEvalHub(MyBatisEvalHub.Services(new MyBatisEvaluator(), wrapStore?.Invoke(store) ?? store))
            {
                Host = host,
                Debounce = TimeSpan.FromMilliseconds(debounceMs),
                Post = a => a(),
            };
            var rig = new Rig(host, session, store, dialogs, hub);
            if (withScenarios)
            {
                rig.SeedScenarios();
            }

            rig.Tab = tab;
            session.SelectedStatementId = StatementId;
            return rig;
        }

        /// <summary>시안 Main 의 시나리오 4개.</summary>
        private void SeedScenarios()
        {
            Save("이름 검색", Null(), Text("KIM"), List(MyBatisScenarioValueKind.Number), Text("E.ENAME"));
            Save("부서 목록", Null(), Text(string.Empty), List(MyBatisScenarioValueKind.Number, "10", "20"), Text("E.DEPTNO, E.ENAME"));
            Save("전체 조건", new MyBatisScenarioValue(MyBatisScenarioValueKind.Number, "7369"), Text("SM"), List(MyBatisScenarioValueKind.Number, "20"), Text("E.EMPNO"));
            Save("조건 없음", Null(), Null(), Null(), Text("E.EMPNO"));
        }

        private void Save(string name, MyBatisScenarioValue empNo, MyBatisScenarioValue ename, MyBatisScenarioValue deptIds, MyBatisScenarioValue orderBy) =>
            Store.SaveAsync(
                new MyBatisScenario
                {
                    MapperPath = Session.MapperPath,
                    Namespace = Session.MapperNamespace,
                    StatementId = StatementId,
                    Name = name,
                    Values = new Dictionary<string, MyBatisScenarioValue>
                    {
                        ["empNo"] = empNo,
                        ["ename"] = ename,
                        ["deptIds"] = deptIds,
                        ["orderBy"] = orderBy,
                    },
                },
                default).GetAwaiter().GetResult();

        /// <summary>문장 하나를 더 두고 그 탭을 활성 탭·고른 문장으로 한다(원문 그대로). 허브가 새 문장을 따라간다.</summary>
        public void AddStatementTab(string id, string xml)
        {
            Session.Elements[id] = new MyBatisElement { Kind = MyBatisElementKind.Select, Name = "select", Id = id, Text = xml };
            var tab = new SqlEditorViewModel(Guid.NewGuid(), xml) { Title = id };
            tab.IsDirty = false;
            Session.StatementIds[tab] = id;
            Host.AddMyBatisTab(Host.ActiveWorkspace!, tab);
            Session.SelectedStatementId = id;
            Tab = tab;
            Hub.Refresh();
        }

        public void Dispose() => Hub.Active?.Dispose();

        private static MyBatisScenarioValue Null() => MyBatisScenarioValue.Null;

        private static MyBatisScenarioValue Text(string text) => new(MyBatisScenarioValueKind.Text, text);

        private static MyBatisScenarioValue List(MyBatisScenarioValueKind itemKind, params string[] items) =>
            new(MyBatisScenarioValueKind.List, Items: items, ItemKind: itemKind);
    }

    /// <summary>매퍼 문서의 sql 요소를 include 조각으로 돌려준다.</summary>
    private sealed class DocumentFragments(IMyBatisMapperDocument document) : IMyBatisFragmentResolver
    {
        public MyBatisFragment? Resolve(string refid, string? currentNamespace)
        {
            var ns = document.MapperNamespace;
            var id = ns is not null && refid.StartsWith(ns + ".", StringComparison.Ordinal) ? refid[(ns.Length + 1)..] : refid;
            return document.Find(id) is { Kind: MyBatisElementKind.Sql } element ? new MyBatisFragment(id, ns, element.Text) : null;
        }
    }

    private sealed class PromptDialogs : IEditorDialogs
    {
        public string? Prompt { get; set; }

        public string? LastInitial { get; private set; }

        public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string initial, CancellationToken ct)
        {
            LastInitial = initial;
            return Task.FromResult(Prompt);
        }

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct) => Task.FromResult(false);
    }

    /// <summary>목록은 비어 있고 쓰기는 모두 실패하는 저장소(디스크 오류 흉내).</summary>
    private sealed class ThrowingStore : IMyBatisScenarioStore
    {
        public event EventHandler<MyBatisScenarioChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<MyBatisScenario>> ListAsync(string mapperPath, string? ns, string statementId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MyBatisScenario>>([]);

        public Task<MyBatisScenario> SaveAsync(MyBatisScenario scenario, CancellationToken ct) => throw new IOException("디스크가 가득 찼습니다.");

        public Task RenameAsync(long id, string name, CancellationToken ct) => throw new IOException("디스크가 가득 찼습니다.");

        public Task<MyBatisScenario> DuplicateAsync(long id, string newName, CancellationToken ct) => throw new IOException("디스크가 가득 찼습니다.");

        public Task DeleteAsync(long id, CancellationToken ct) => throw new IOException("디스크가 가득 찼습니다.");

        public Task ReorderAsync(IReadOnlyList<long> orderedIds, CancellationToken ct) => Task.CompletedTask;

        public Task<int> MoveStatementAsync(string mapperPath, string oldId, string newId, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class AlwaysKeys : IContextKeys
    {
        public bool Evaluate(string? expression) => true;
    }

    /// <summary>명령 레지스트리 가짜: 단추 4개의 정의만 있고, 실행한 ID 를 순서대로 기록한다.</summary>
    private sealed class FakeRegistry : ICommandRegistry
    {
        private readonly List<CommandDefinition> _commands =
        [
            .. MyBatisExpandedViewModel.ActionIds.Select(id => new CommandDefinition { Id = id, Title = "Cmd." + id, Category = CommandCategory.Sql, Icon = "run" }),
        ];

        public List<string> Executed { get; } = [];

        public IReadOnlyList<CommandDefinition> Commands => _commands;

        public event EventHandler? CommandsChanged
        {
            add { }
            remove { }
        }

        public CommandDefinition? Find(string idOrAlias) => _commands.Find(c => c.Id == idOrAlias);

        public bool CanExecute(string idOrAlias, IContextKeys keys) => Find(idOrAlias) is not null;

        public Task<bool> ExecuteAsync(string idOrAlias, IContextKeys keys, object? parameter = null, CancellationToken cancellationToken = default)
        {
            Executed.Add(idOrAlias);
            return Task.FromResult(true);
        }

        public IDisposable Register(CommandDefinition definition, ICommandHandler handler) => throw new NotSupportedException();
    }
}
