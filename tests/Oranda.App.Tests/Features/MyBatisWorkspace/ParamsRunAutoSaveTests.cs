using Oranda.App.Features.Execution;
using Oranda.App.Features.MyBatisWorkspace.Expanded;
using Oranda.App.Features.MyBatisWorkspace.Parameters;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.MyBatis;
using Oranda.Language.MyBatis;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>D-044: 시나리오가 없는 문장의 바인드 값도 로컬 저장소에 남아 다시 열면 돌아온다.</summary>
public sealed partial class ParamsRunTests
{
    private static async Task<IReadOnlyList<MyBatisScenario>> ListOf(Rig rig) =>
        await rig.Store.ListAsync(rig.Session.MapperPath, rig.Session.MapperNamespace, StatementId, default);

    [Fact]
    public Task A_value_typed_into_a_statement_without_scenarios_comes_back_after_reopening() => HeadlessHost.Run(async () =>
    {
        using var rig = Rig.Create(withScenarios: false);
        var eval = rig.Hub.Active!;
        await eval.EnsureLoadedAsync();

        RowOf(rig.Tab, "ENAME").Text = "SMITH";
        eval.Flush();

        var saved = Assert.Single(await ListOf(rig));
        Assert.Equal(MyBatisStatementEval.AutoScenarioName, saved.Name);
        Assert.Equal(saved.Id, eval.SelectedScenario?.Id);
        Assert.Equal("SMITH", RowOf(rig.Tab, "ENAME").Text); // 저장했다고 칸을 다시 채우지 않는다

        // 앱을 다시 켠 것처럼: 같은 저장소를 보는 새 허브·새 세션 상태·새 탭
        var session = new Fakes.FakeMyBatisWorkspaceSession
        {
            MapperPath = rig.Session.MapperPath,
            MapperNamespace = rig.Session.MapperNamespace,
            Fragments = rig.Session.Fragments,
        };
        session.Elements[StatementId] = rig.Session.Elements[StatementId];
        var hub = new MyBatisEvalHub(MyBatisEvalHub.Services(new MyBatisEvaluator(), rig.Store)) { Host = rig.Host, Post = a => a() };
        var reopened = hub.For(session, StatementId);
        var tab = new SqlEditorViewModel(Guid.NewGuid(), rig.Tab.Text);
        using var track = reopened.Track(tab);
        await reopened.EnsureLoadedAsync();

        Assert.Equal(saved.Id, reopened.SelectedScenario?.Id);
        Assert.Equal("SMITH", RowOf(tab, "ENAME").Text);
    });

    [Fact]
    public Task A_later_edit_updates_the_auto_scenario_instead_of_making_another() => HeadlessHost.Run(async () =>
    {
        using var rig = Rig.Create(withScenarios: false);
        var eval = rig.Hub.Active!;
        await eval.EnsureLoadedAsync();

        RowOf(rig.Tab, "ENAME").Text = "SMITH";
        eval.Flush();
        RowOf(rig.Tab, "ENAME").Text = "JONES";
        eval.Flush();

        var saved = Assert.Single(await ListOf(rig));
        Assert.Equal("JONES", saved.Values["ename"].Text);
    });

    [Fact]
    public Task Letters_typed_while_the_auto_scenario_is_being_created_are_kept_and_saved() => HeadlessHost.Run(async () =>
    {
        HeldStore? held = null;
        using var rig = Rig.Create(withScenarios: false, wrapStore: s => held = new HeldStore(s));
        var eval = rig.Hub.Active!;
        await eval.EnsureLoadedAsync();

        held!.Hold = true;
        RowOf(rig.Tab, "ENAME").Text = "SMI";
        eval.Flush(); // 저장이 걸려 있다
        RowOf(rig.Tab, "ENAME").Text = "SMITH";
        eval.Flush(); // 만드는 중이라 두 번째 시나리오를 만들지 않는다
        Assert.Empty(await ListOf(rig));

        held.Release();
        await HeadlessHost.WaitUntil(() => eval.SelectedScenario?.Values["ename"].Text == "SMITH");

        Assert.Equal("SMITH", RowOf(rig.Tab, "ENAME").Text); // 입력한 글자가 덮이지 않았다
        var saved = Assert.Single(await ListOf(rig));
        Assert.Equal("SMITH", saved.Values["ename"].Text); // 뒤에 입력한 글자까지 저장됐다
    });

    [Fact]
    public Task Only_blank_values_create_no_scenario_and_a_value_from_the_tab_alone_creates_none() => HeadlessHost.Run(async () =>
    {
        using var rig = Rig.Create(withScenarios: false);
        var eval = rig.Hub.Active!;
        await eval.EnsureLoadedAsync();

        // 사용자가 쳤다가 지웠다
        RowOf(rig.Tab, "ENAME").Text = "X";
        RowOf(rig.Tab, "ENAME").Text = string.Empty;
        eval.Flush();
        Assert.Empty(await ListOf(rig));
        Assert.Null(eval.SelectedScenario);

        // 사용자가 고친 것이 아닌 값(탭에 복구된 행)만으로는 만들지 않는다
        eval.SetValue("ename", MyBatisValues.FromCellText("RESTORED"), byUser: false);
        eval.Flush();
        Assert.Empty(await ListOf(rig));
    });

    [Fact]
    public Task A_statement_that_has_scenarios_gets_no_auto_scenario() => HeadlessHost.Run(async () =>
    {
        using var rig = Rig.Create();
        var eval = rig.Hub.Active!;
        await eval.EnsureLoadedAsync();

        RowOf(rig.Tab, "ENAME").Text = "SMITH";
        eval.Flush();

        var list = await ListOf(rig);
        Assert.Equal(4, list.Count);
        Assert.DoesNotContain(list, s => s.Name == MyBatisStatementEval.AutoScenarioName);
        Assert.Equal("SMITH", list[0].Values["ename"].Text); // 고른 시나리오에 저장
    });

    [Fact]
    public Task Deleting_the_auto_scenario_does_not_bring_it_back_until_a_new_value_is_typed() => HeadlessHost.Run(async () =>
    {
        using var rig = Rig.Create(withScenarios: false);
        var eval = rig.Hub.Active!;
        await eval.EnsureLoadedAsync();

        RowOf(rig.Tab, "ENAME").Text = "SMITH";
        eval.Flush();
        await eval.DeleteScenarioAsync(eval.SelectedScenario!);

        Assert.Empty(await ListOf(rig));
        Assert.Null(eval.SelectedScenario);

        RowOf(rig.Tab, "ENAME").Text = "JONES";
        eval.Flush();
        Assert.Equal("JONES", Assert.Single(await ListOf(rig)).Values["ename"].Text);
    });

    [Fact]
    public Task Clearing_values_in_the_bind_panel_empties_the_selected_scenario_and_keeps_the_rows() => HeadlessHost.Run(async () =>
    {
        using var rig = Rig.Create();
        var eval = rig.Hub.Active!;
        await eval.EnsureLoadedAsync();
        eval.SelectScenario(eval.Scenarios[2]); // 전체 조건: 네 값이 모두 있다
        var types = rig.Tab.Binds.Rows.Select(r => r.Type).ToList();
        var panel = BindVariablesViewModel.ForHost(null, rig.Host);

        panel.ClearValuesCommand.Execute(null);
        eval.Flush();

        Assert.Equal(["EMPNO", "ENAME", "DEPTIDS", "$ORDERBY"], rig.Tab.Binds.Rows.Select(r => r.Name));
        Assert.All(rig.Tab.Binds.Rows, r => Assert.Equal(string.Empty, r.Text));
        Assert.Equal(types, rig.Tab.Binds.Rows.Select(r => r.Type));

        var list = await ListOf(rig);
        Assert.All(list.Single(s => s.Name == "전체 조건").Values.Values, v => Assert.Equal(MyBatisScenarioValueKind.Null, v.Kind));
        Assert.Equal("KIM", list.Single(s => s.Name == "이름 검색").Values["ename"].Text); // 고르지 않은 시나리오는 그대로
        Assert.DoesNotContain(list, s => s.Name == MyBatisStatementEval.AutoScenarioName);
    });

    [Fact]
    public Task Clearing_values_of_a_statement_without_scenarios_creates_no_auto_scenario() => HeadlessHost.Run(async () =>
    {
        using var rig = Rig.Create(withScenarios: false);
        var eval = rig.Hub.Active!;
        await eval.EnsureLoadedAsync();
        RowOf(rig.Tab, "ENAME").Text = "SMITH";
        eval.Flush();
        var panel = BindVariablesViewModel.ForHost(null, rig.Host);

        panel.ClearValuesCommand.Execute(null);
        eval.Flush();

        var saved = Assert.Single(await ListOf(rig)); // 이미 만든 자동 저장 시나리오의 값만 비운다
        Assert.Equal(MyBatisStatementEval.AutoScenarioName, saved.Name);
        Assert.Equal(MyBatisScenarioValueKind.Null, saved.Values["ename"].Kind);
        Assert.Equal(string.Empty, RowOf(rig.Tab, "ENAME").Text);
    });

    [Fact]
    public Task A_read_only_store_leaves_the_values_alone_and_selects_nothing() => HeadlessHost.Run(async () =>
    {
        using var rig = Rig.Create(withScenarios: false, wrapStore: _ => new ReadOnlyStore());
        var expanded = MyBatisExpandedViewModel.For(rig.Hub);
        var eval = rig.Hub.Active!;
        await eval.EnsureLoadedAsync();

        RowOf(rig.Tab, "ENAME").Text = "SMITH";
        eval.Flush();

        Assert.Null(eval.SelectedScenario);
        Assert.Empty(expanded.Scenarios);
        Assert.Equal("SMITH", RowOf(rig.Tab, "ENAME").Text);
        Assert.Null(eval.Error);
    });

    // 실제 저장소의 읽기 전용 동작: 쓰기는 건너뛰고 ID 0 을 돌려주며 Changed 를 내지 않는다.
    private sealed class ReadOnlyStore : IMyBatisScenarioStore
    {
        public event EventHandler<MyBatisScenarioChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<MyBatisScenario>> ListAsync(string mapperPath, string? ns, string statementId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MyBatisScenario>>([]);

        public Task<MyBatisScenario> SaveAsync(MyBatisScenario scenario, CancellationToken ct) => Task.FromResult(scenario);

        public Task RenameAsync(long id, string name, CancellationToken ct) => Task.CompletedTask;

        public Task<MyBatisScenario> DuplicateAsync(long id, string newName, CancellationToken ct) => throw new NotSupportedException();

        public Task DeleteAsync(long id, CancellationToken ct) => Task.CompletedTask;

        public Task ReorderAsync(IReadOnlyList<long> orderedIds, CancellationToken ct) => Task.CompletedTask;

        public Task<int> MoveStatementAsync(string mapperPath, string oldId, string newId, CancellationToken ct) => Task.FromResult(0);
    }
}
