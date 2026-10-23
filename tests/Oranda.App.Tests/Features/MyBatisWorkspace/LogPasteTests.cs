using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.LogPaste;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.MyBatisWorkspace.Fakes;
using Oranda.Core.MyBatis;
using Oranda.Language.MyBatis;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>TSK-03-06: MyBatis 로그 붙여 넣기로 문장 찾기·값 맞추기·시나리오 저장. 소유 = TSK-03-06.</summary>
public class LogPasteTests
{
    private const string Logger = "com.acme.hr.EmpMapper";

    // 시안 selectEmpList 에서 empNo·ename·deptIds 2개를 모두 켠 모양(MyBatis 로그의 공백 모양).
    private const string SelectEmpListSql =
        "SELECT E.EMPNO, E.ENAME, E.JOB, E.SAL, D.DNAME FROM EMP E JOIN DEPT D ON D.DEPTNO = E.DEPTNO "
        + "WHERE E.EMPNO = ? AND E.ENAME LIKE '%' || ? || '%' AND E.DEPTNO IN ( ? , ? ) ORDER BY E.ENAME";

    private static string Pair(string? logger, string sql, string parameters) =>
        (logger is null ? "DEBUG" : $"14:05:01.123 [main] DEBUG {logger} -") + $" ==>  Preparing: {sql}\n"
        + (logger is null ? "DEBUG" : $"14:05:01.130 [main] DEBUG {logger} -") + $" ==> Parameters: {parameters}\n";

    private sealed class Fragments(IMyBatisMapperDocument document) : IMyBatisFragmentResolver
    {
        public MyBatisFragment? Resolve(string refid, string? currentNamespace)
        {
            var id = refid[(refid.LastIndexOf('.') + 1)..];
            var e = document.Elements.FirstOrDefault(x => x.Kind == MyBatisElementKind.Sql && x.Id == id);
            return e is null ? null : new MyBatisFragment(id, document.MapperNamespace, e.Text);
        }
    }

    private sealed class PickDialogs : IEditorDialogs
    {
        public List<IReadOnlyList<string>> Choices { get; } = [];

        public int Pick { get; set; }

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct)
        {
            Choices.Add(items);
            return Task.FromResult<string?>(Pick < 0 ? null : items[Pick]);
        }

        public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string initial, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class Env
    {
        public Env(string? xml = null)
        {
            var doc = new MapperParser().Parse(xml ?? MapperSamples.EmpMapper);
            Session = new FakeMyBatisWorkspaceSession
            {
                MapperPath = "/tmp/EmpMapper.xml",
                MapperNamespace = doc.MapperNamespace,
                Document = doc,
                Fragments = new Fragments(doc),
            };
            Host = new SqlEditorHost(dialogs: Dialogs);
            Host.MyBatisHooks = new MyBatisHooks(statementLocators: [new OpenMapperStatementLocator(() => [Session])]);
        }

        public SqlEditorHost Host { get; }

        public FakeMyBatisWorkspaceSession Session { get; }

        public FakeMyBatisScenarioStore Store { get; } = new();

        public PickDialogs Dialogs { get; } = new();

        public Task<MyBatisLogFlowResult?> RunAsync(string log) =>
            new MyBatisLogPasteFlow(Host, new MyBatisEvaluator(), Store, Dialogs, () => new DateTimeOffset(2026, 10, 23, 14, 5, 0, TimeSpan.Zero))
                .RunAsync(log, Session, CancellationToken.None);
    }

    [Fact]
    public async Task Mockup_selectEmpList_log_opens_statement_and_saves_scenario()
    {
        var env = new Env();

        var result = await env.RunAsync(Pair($"{Logger}.selectEmpList", SelectEmpListSql, "7499(Integer), AL(String), 10(Integer), 30(Integer)"));

        var opened = Assert.Single(result!.Opened);
        Assert.Equal("selectEmpList", opened.StatementId);
        Assert.Equal("로그 14:05", opened.ScenarioName);
        Assert.Equal(["selectEmpList"], env.Session.OpenedStatementIds);
        var scenario = Assert.Single(env.Store.All);
        Assert.Equal("selectEmpList", scenario.StatementId);
        Assert.Equal("로그 14:05", scenario.Name);
        Assert.Equal(new MyBatisScenarioValue(MyBatisScenarioValueKind.Number, "7499"), scenario.Values["empNo"]);
        Assert.Equal(new MyBatisScenarioValue(MyBatisScenarioValueKind.Text, "AL"), scenario.Values["ename"]);
        Assert.Equal(["10", "30"], scenario.Values["deptIds"].Items);
        Assert.Equal(MyBatisScenarioValueKind.Number, scenario.Values["deptIds"].ItemKind);
        Assert.Equal("E.ENAME", scenario.Values["orderBy"].Text); // ${} 치환은 로그 문장에서 되읽는다
        Assert.Equal(0, result.Fallback);
    }

    [Fact]
    public async Task Log_without_logger_finds_the_statement_by_sql_shape()
    {
        var env = new Env();

        var result = await env.RunAsync(Pair(null, "SELECT COUNT(*) FROM EMP", string.Empty));

        Assert.Equal("countEmp", Assert.Single(result!.Opened).StatementId);
        Assert.Empty(env.Dialogs.Choices);
    }

    [Fact]
    public async Task Several_same_shape_statements_ask_which_one()
    {
        var xml = """
            <mapper namespace="a.B">
              <select id="one">SELECT * FROM T WHERE X = #{x}</select>
              <select id="two">SELECT * FROM T WHERE X = #{y}</select>
            </mapper>
            """;
        var env = new Env(xml) { Dialogs = { Pick = 1 } };

        var result = await env.RunAsync(Pair(null, "SELECT * FROM T WHERE X = ?", "5(Integer)"));

        Assert.Single(env.Dialogs.Choices);
        Assert.Equal(["a.B.one  (EmpMapper.xml)", "a.B.two  (EmpMapper.xml)"], env.Dialogs.Choices[0]);
        Assert.Equal("two", Assert.Single(result!.Opened).StatementId);
        Assert.True(env.Store.All.Single().Values.ContainsKey("y"));
    }

    [Fact]
    public async Task Cancelling_the_candidate_box_stops_without_saving()
    {
        var xml = """
            <mapper namespace="a.B">
              <select id="one">SELECT 1 FROM T WHERE X = #{x}</select>
              <select id="two">SELECT 1 FROM T WHERE X = #{y}</select>
            </mapper>
            """;
        var env = new Env(xml) { Dialogs = { Pick = -1 } };

        var result = await env.RunAsync(Pair(null, "SELECT 1 FROM T WHERE X = ?", "5(Integer)"));

        Assert.True(result!.Cancelled);
        Assert.Empty(env.Store.All);
    }

    [Fact]
    public async Task Log_with_several_statements_saves_one_scenario_each()
    {
        var env = new Env();
        var log = Pair($"{Logger}.countEmp", "SELECT COUNT(*) FROM EMP", string.Empty)
            + Pair($"{Logger}.selectEmp", "SELECT E.EMPNO, E.ENAME, E.JOB, E.SAL, D.DNAME FROM EMP E JOIN DEPT D ON D.DEPTNO = E.DEPTNO WHERE E.EMPNO = ?", "7369(Long)");

        var result = await env.RunAsync(log);

        Assert.Equal(["countEmp", "selectEmp"], result!.Opened.Select(o => o.StatementId));
        Assert.Equal(2, env.Store.All.Count);
        Assert.Equal("7369", env.Store.All[1].Values["empNo"].Text);
    }

    [Fact]
    public async Task Foreach_item_count_follows_the_log_value_count()
    {
        var xml = """
            <mapper namespace="a.B">
              <select id="byIds">SELECT * FROM T WHERE K = #{k} AND ID IN <foreach collection="ids" item="i" open="(" separator="," close=")">#{i}</foreach> AND Z = #{z}</select>
            </mapper>
            """;
        var env = new Env(xml);

        await env.RunAsync(Pair("a.B.byIds", "SELECT * FROM T WHERE K = ? AND ID IN ( ? , ? , ? ) AND Z = ?", "K1(String), 1(Integer), 2(Integer), 3(Integer), Z9(String)"));

        var values = env.Store.All.Single().Values;
        Assert.Equal(["1", "2", "3"], values["ids"].Items);
        Assert.Equal("K1", values["k"].Text);
        Assert.Equal("Z9", values["z"].Text);
    }

    [Fact]
    public async Task Dynamic_branch_off_in_log_is_found()
    {
        var env = new Env();

        await env.RunAsync(Pair($"{Logger}.selectEmpList", SelectEmpListSql.Replace("WHERE E.EMPNO = ? AND E.ENAME LIKE '%' || ? || '%' AND E.DEPTNO IN ( ? , ? )", "WHERE E.ENAME LIKE '%' || ? || '%'"), "AL(String)"));

        var values = env.Store.All.Single().Values;
        Assert.Equal("AL", values["ename"].Text);
        Assert.False(values.ContainsKey("empNo"));
    }

    [Fact]
    public async Task Unmatchable_log_falls_back_to_a_plain_sql_tab()
    {
        var env = new Env();

        var result = await env.RunAsync(Pair($"{Logger}.selectEmp", "SELECT * FROM SOMEWHERE_ELSE WHERE A = ?", "1(Integer)"));

        Assert.Empty(result!.Opened);
        Assert.Equal(1, result.Fallback);
        Assert.Empty(env.Store.All);
        Assert.Empty(env.Session.OpenedStatementIds);
        var tab = Assert.Single(env.Host.Tabs);
        Assert.Equal("SELECT * FROM SOMEWHERE_ELSE WHERE A = :p1", tab.Text);
    }

    [Fact]
    public async Task Secret_values_are_not_kept()
    {
        var xml = """
            <mapper namespace="a.B">
              <select id="login">SELECT 1 FROM USERS WHERE ID = #{id} AND PWD = #{userPwd}</select>
            </mapper>
            """;
        var env = new Env(xml);

        await env.RunAsync(Pair("a.B.login", "SELECT 1 FROM USERS WHERE ID = ? AND PWD = ?", "scott(String), tiger(String)"));

        var values = env.Store.All.Single().Values;
        Assert.Equal("scott", values["id"].Text);
        Assert.Equal(MyBatisScenarioValue.Null, values["userPwd"]);
    }

    [Fact]
    public async Task Inconsistent_values_for_one_parameter_do_not_match()
    {
        var xml = """
            <mapper namespace="a.B">
              <select id="twice">SELECT 1 FROM T WHERE A = #{x} OR B = #{x}</select>
            </mapper>
            """;
        var env = new Env(xml);

        var result = await env.RunAsync(Pair("a.B.twice", "SELECT 1 FROM T WHERE A = ? OR B = ?", "1(Integer), 2(Integer)"));

        Assert.Empty(result!.Opened);
        Assert.Equal(1, result.Fallback);
    }

    [Fact]
    public async Task Log_text_without_pairs_returns_null()
    {
        Assert.Null(await new Env().RunAsync("select 1"));
    }

    [Fact]
    public async Task Open_mapper_locator_finds_exact_and_partial()
    {
        var locator = new OpenMapperStatementLocator(() => [new Env().Session]);

        var exact = await locator.FindAsync($"{Logger}.countEmp", exact: true, CancellationToken.None);
        var partial = await locator.FindAsync("emp", exact: false, CancellationToken.None);
        var none = await locator.FindAsync($"{Logger}.nothing", exact: true, CancellationToken.None);

        Assert.Equal(new MyBatisStatementLocation("/tmp/EmpMapper.xml", Logger, "countEmp", "open"), Assert.Single(exact));
        Assert.True(partial.Count >= 5);
        Assert.Empty(none);
    }

    [Fact]
    public async Task Parameter_used_only_in_a_test_falls_back_because_the_scenario_would_not_replay()
    {
        var xml = """
            <mapper namespace="a.B">
              <select id="gated">SELECT 1 FROM T WHERE 1 = 1 <if test="mode == 'Y'">AND D = #{d}</if></select>
            </mapper>
            """;
        var env = new Env(xml);

        var result = await env.RunAsync(Pair("a.B.gated", "SELECT 1 FROM T WHERE 1 = 1 AND D = ?", "5(Integer)"));

        Assert.Empty(result!.Opened);
        Assert.Equal(1, result.Fallback);
    }

    [Fact]
    public async Task Logger_found_but_values_unmatched_does_not_jump_to_another_statement()
    {
        var env = new Env();

        // selectEmp 로 찾았지만 SQL 이 countEmp 모양: 모양 훑기로 넘어가지 않고 일반 탭으로 간다.
        var result = await env.RunAsync(Pair($"{Logger}.selectEmp", "SELECT COUNT(*) FROM EMP", string.Empty));

        Assert.Empty(result!.Opened);
        Assert.Equal(1, result.Fallback);
    }

    [Fact]
    public async Task Empty_foreach_in_log_matches_with_zero_items()
    {
        var xml = """
            <mapper namespace="a.B">
              <select id="maybe">SELECT 1 FROM T WHERE K = #{k} <foreach collection="ids" item="i" open="AND ID IN (" separator="," close=")">#{i}</foreach></select>
            </mapper>
            """;
        var env = new Env(xml);

        await env.RunAsync(Pair("a.B.maybe", "SELECT 1 FROM T WHERE K = ?", "K1(String)"));

        Assert.Equal("K1", env.Store.All.Single().Values["k"].Text);
        // 값이 없으면 계산기가 바인드 자리 하나로 펼치므로(검사용 가정) 0개 로그는 빈 목록으로 남긴다.
        var ids = env.Store.All.Single().Values["ids"];
        Assert.Equal((MyBatisScenarioValueKind.List, 0), (ids.Kind, ids.Items!.Count));
    }

    // null 만 거르는 if 안의 foreach: 그 절이 없는 로그는 「값 없음」 으로 맞춘다(빈 목록이면 ids != null 이 참이 되어 절이 붙는다).
    [Fact]
    public async Task A_foreach_behind_a_null_check_matches_a_log_without_that_clause_as_no_value()
    {
        var xml = """
            <mapper namespace="a.B">
              <select id="maybe">SELECT 1 FROM T WHERE K = #{k}<if test="ids != null"> AND ID IN <foreach collection="ids" item="i" open="(" separator="," close=")">#{i}</foreach></if></select>
            </mapper>
            """;
        var env = new Env(xml);

        await env.RunAsync(Pair("a.B.maybe", "SELECT 1 FROM T WHERE K = ?", "K1(String)"));

        var values = env.Store.All.Single().Values;
        Assert.Equal("K1", values["k"].Text);
        Assert.False(values.TryGetValue("ids", out var ids) && ids.Kind != MyBatisScenarioValueKind.Null);
    }

    [Fact]
    public Task Real_session_opens_the_found_statement_tab_and_uses_unsaved_text() => Oranda.App.Tests.Features.SqlEditor.Ui.Run(async () =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-logpaste-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "EmpMapper.xml");
            await File.WriteAllTextAsync(path, MapperSamples.EmpMapper);
            var host = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler()) };
            await host.OpenWorkspaceFileAsync(path, default);
            var session = host.ActiveWorkspace!.MyBatis!;
            host.MyBatisHooks = new MyBatisHooks(
                openHandler: new MyBatisOpenHandler(), statementLocators: [new OpenMapperStatementLocator(() => [session])]);
            var store = new FakeMyBatisScenarioStore();

            var result = await new MyBatisLogPasteFlow(host, new MyBatisEvaluator(), store, new PickDialogs())
                .RunAsync(Pair($"{Logger}.countEmp", "SELECT COUNT(*) FROM EMP", string.Empty), session, CancellationToken.None);

            Assert.Equal("countEmp", Assert.Single(result!.Opened).StatementId);
            Assert.Equal("countEmp", host.Active!.Title);
            Assert.Equal("countEmp", Assert.Single(store.All).StatementId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    });
}
