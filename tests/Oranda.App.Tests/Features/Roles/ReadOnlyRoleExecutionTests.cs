using Oranda.App.Features.Execution;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Tests.Features.Execution;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Features;
using Oranda.Core.History;
using Oranda.Core.Language;
using Oranda.Core.Sessions;
using Oranda.Testing;
using FakeSession = Oranda.App.Tests.Features.Execution.FakeSession;

namespace Oranda.App.Tests.Features.Roles;

/// <summary>
/// TSK-07-05 조회 전용 Role 실행 차단(TRD 10.7.4, D-006) — 앱 쪽.
/// <list type="bullet">
/// <item>실제 분류기(언어 서비스 <c>Split</c>)로 가른 문장을 정책 관문에 넣는 경계 사례: 여러 문장 스크립트·주석 앞머리·대소문자·WITH·문자열 안의 DML</item>
/// <item>실행 컨트롤러: 가짜 엔진이 실제 정책 관문을 거쳐 거부면 「드라이버」를 부르지 않는다(OracleExecutionService 2단계와 같은 순서). 메시지 창·결과 탭에 이유</item>
/// <item>실제 실행 엔진: 거부는 드라이버 단계 전(가짜 세션으로도 거부 결과가 나온다)</item>
/// <item>앱 조립·대체 등록이 정책 관문에 게이트를 넘김, <c>readOnlySession</c> 키와 쓰기 명령 흐림·게이트 Changed 되돌림</item>
/// </list>
/// </summary>
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class ReadOnlyRoleExecutionTests
{
    private static readonly SplitOptions Split = SplitOptions.Default;

    /// <summary>쓰기 명령 흐림 시험에서 참으로 두는 다른 키(readOnlySession 만 이유가 되게).</summary>
    private static readonly string[] TrueKeys = ["hasSession", "dedicatedSession", "schemaNodeSelected", "recyclebinNodeSelected", "sqlDocActive", "schemaFocus", "plsqlEditorFocus"];

    private static ExecutionPolicy ViewerPolicy() => new(FakeFeatureGate.ForRoles(RoleIds.Viewer));

    private static PolicyContext Ctx(bool readOnly = false) =>
        new(TestData.Profile(readOnly: readOnly), TestData.Server(), HistorySource.Editor);

    private static PolicyDecision Eval(ExecutionPolicy policy, SqlStatement stmt, bool readOnly = false) =>
        policy.EvaluateAsync(Ctx(readOnly), stmt, default).AsTask().GetAwaiter().GetResult();

    private static IReadOnlyList<SqlStatement> Parse(string text) => Rig.LanguageForTests().Split(text, Split);

    // ---------- 실제 분류기 + 정책 관문 ----------

    [Theory]
    [InlineData("select * from emp")]
    [InlineData("SELECT * FROM emp")]
    [InlineData("  -- 주석 줄\n/* 블록 주석 */ SeLeCt 1 FROM dual")]
    [InlineData("with x as (select 1 a from dual) select * from x")]
    [InlineData("(select 1 from dual)")]
    [InlineData("select 'update emp set sal = 0' as s from dual")] // 문자열 안의 DML 은 질의다
    [InlineData("-- update emp set sal = 0\nselect 1 from dual")] // 주석 안의 DML 도
    [InlineData("select * from emp for update")] // 행 잠금은 읽기 전용 프로필과 같이 질의로 본다(TRD 10.7.4 같은 기준)
    [InlineData("commit")]
    [InlineData("ROLLBACK")]
    [InlineData("savepoint a")]
    [InlineData("set transaction read only")]
    [InlineData("explain plan for update emp set sal = 1 where id = 1")] // 계획만 세우고 UPDATE 는 돌지 않는다
    [InlineData("set serveroutput on")]
    [InlineData("desc emp")]
    public void Reads_pass_with_the_real_classifier(string text)
    {
        var stmt = Assert.Single(Parse(text));

        Assert.Equal(PolicyDecisionKind.Allow, Eval(ViewerPolicy(), stmt).Kind);
    }

    [Theory]
    [InlineData("update emp set sal = 1 where id = 1", "UPDATE")]
    [InlineData("UpDaTe emp SET sal = 1", "UPDATE")]
    [InlineData("/* 힌트 */ delete from emp where id = 1", "DELETE")]
    [InlineData("-- 주석\n-- 두 줄\nINSERT INTO emp VALUES (1)", "INSERT")]
    [InlineData("insert into t with x as (select 1 a from dual) select * from x", "INSERT")]
    [InlineData("merge into emp e using dual on (1 = 1) when matched then update set sal = 1", "MERGE")]
    [InlineData("call p()", "CALL")]
    [InlineData("lock table emp in exclusive mode", "LOCK")]
    [InlineData("create table t (x number)", "CREATE")]
    [InlineData("alter system kill session '1,2'", "ALTER SYSTEM")]
    [InlineData("ALTER SESSION SET nls_date_format = 'YYYY'", "ALTER SESSION")]
    [InlineData("drop table emp", "DROP")]
    [InlineData("truncate table emp", "TRUNCATE")]
    [InlineData("grant select on emp to u", "GRANT")]
    [InlineData("comment on table emp is 'x'", "COMMENT")]
    [InlineData("flashback table emp to before drop", "FLASHBACK")]
    [InlineData("purge recyclebin", "PURGE")]
    [InlineData("begin null; end;", "BEGIN")]
    [InlineData("declare x number; begin x := 1; end;", "DECLARE")]
    [InlineData("create or replace procedure p as begin null; end;", "CREATE")]
    public void Writes_are_denied_with_the_real_classifier(string text, string verb)
    {
        var stmt = Assert.Single(Parse(text));

        var d = Eval(ViewerPolicy(), stmt);

        Assert.Equal(PolicyDecisionKind.Deny, d.Kind);
        Assert.Equal(PolicyRule.ReadOnlyRole, d.Rule);
        Assert.Contains(verb, d.Message, StringComparison.Ordinal);
        Assert.Contains("조회 전용 Role", d.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("table emp")] // 분류기가 확신하지 못함 → DML 로 본다
    [InlineData("foo bar")]
    public void Uncertain_statements_are_denied(string text)
    {
        var stmt = Assert.Single(Parse(text));
        Assert.True(stmt.IsClassificationUncertain);

        Assert.Equal(PolicyRule.ReadOnlyRole, Eval(ViewerPolicy(), stmt).Rule);
    }

    [Fact]
    public void With_then_insert_is_left_to_the_server()
    {
        // Oracle 에는 WITH … INSERT 문법이 없다(서버가 ORA-00928 로 거절해 쓰지 못한다). 분류기는 첫 낱말로 질의라 하고,
        // 정책 관문은 분류기를 따른다(새 분류기를 만들지 않는다 — TSK-07-05). 같은 문장을 읽기 전용 프로필도 통과시킨다.
        var stmt = Assert.Single(Parse("with x as (select 1 a from dual) insert into t select * from x"));

        Assert.Equal(StatementKind.Query, stmt.Kind);
        Assert.Equal(PolicyDecisionKind.Allow, Eval(ViewerPolicy(), stmt).Kind);
        Assert.Equal(PolicyDecisionKind.Allow, Eval(new ExecutionPolicy(), stmt, readOnly: true).Kind);
    }

    [Fact]
    public void Script_is_judged_statement_by_statement()
    {
        const string script = """
            -- 조회 전용 점검 스크립트
            select * from emp;
            UPDATE emp SET sal = 1 WHERE id = 1;
            with x as (select 1 a from dual) select * from x;
            begin
              null;
            end;
            /
            create table t (x number);
            commit;
            """;

        var decisions = Parse(script).Select(s => (s.Verb, Eval(ViewerPolicy(), s).Kind)).ToList();

        Assert.Equal(
            [
                ("SELECT", PolicyDecisionKind.Allow),
                ("UPDATE", PolicyDecisionKind.Deny),
                ("WITH", PolicyDecisionKind.Allow),
                ("BEGIN", PolicyDecisionKind.Deny),
                ("CREATE", PolicyDecisionKind.Deny),
                ("COMMIT", PolicyDecisionKind.Allow),
            ],
            decisions);
    }

    [Fact]
    public void Exec_runs_as_a_plsql_block_and_is_denied()
    {
        // SQL*Plus EXEC 줄 자체는 SQL*Plus 문장이라 통과하지만, 처리기가 같은 엔진에 다시 넣는 블록(SqlPlusCommandProcessor.ExecAsync)이 거부된다.
        var exec = Assert.Single(Parse("exec p"));
        Assert.Equal(StatementKind.SqlPlus, exec.Kind);
        Assert.Equal(PolicyDecisionKind.Allow, Eval(ViewerPolicy(), exec).Kind);

        var inner = new SqlStatement("BEGIN p; END;", default, StatementKind.PlSql) { Verb = "BEGIN" };
        Assert.Equal(PolicyRule.ReadOnlyRole, Eval(ViewerPolicy(), inner).Rule);
    }

    // ---------- 실행 컨트롤러(가짜 엔진 + 실제 정책 관문) ----------

    /// <summary>가짜 엔진이 OracleExecutionService 2단계처럼 정책 관문을 먼저 보고, 거부가 아니면 「드라이버」(목록)에 넘긴다.</summary>
    private static List<SqlStatement> UsePolicy(Rig rig, ExecutionPolicy policy)
    {
        var driver = new List<SqlStatement>();
        rig.Exec.Handler = async (s, ct) =>
        {
            var d = await policy.EvaluateAsync(new PolicyContext(rig.Session.Profile, rig.Session.Server, HistorySource.Editor), s, ct);
            if (d.Kind == PolicyDecisionKind.Deny)
            {
                return new ExecutionResult(s, ExecutionStatus.Denied) { Policy = d };
            }

            driver.Add(s);
            return s.Kind == StatementKind.Query ? Rig.Query(s) : new ExecutionResult(s, ExecutionStatus.Succeeded) { RowsAffected = 0 };
        };
        return driver;
    }

    [Fact]
    public Task Editor_update_is_denied_before_the_driver_and_the_reason_is_shown() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var driver = UsePolicy(rig, ViewerPolicy());
        var tab = rig.Host.NewDocument("update emp set sal = 1 where id = 1");

        await rig.Controller.RunAsync(RunMode.Statement);

        Assert.Empty(driver);
        Assert.Empty(rig.Dialogs.Confirms); // 확인 창 없이 거부
        var item = Assert.Single(((ResultTabsViewModel)tab.ResultPane!).Tabs);
        Assert.Equal("거부됨", item.Title);
        Assert.Contains("조회 전용 Role", item.Message, StringComparison.Ordinal);
        Assert.Contains("설정 > 기능", item.Message, StringComparison.Ordinal);
        Assert.Contains(rig.Log.Messages, m => m.Kind == MessageKind.Warning && m.Text.Contains("조회 전용 Role", StringComparison.Ordinal));
        Assert.Equal(0, rig.Session.Commits);
    });

    [Fact]
    public Task Editor_select_still_runs() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var driver = UsePolicy(rig, ViewerPolicy());
        rig.Host.NewDocument("select * from emp");

        await rig.Controller.RunAsync(RunMode.Statement);

        Assert.Equal("SELECT", Assert.Single(driver).Verb);
        Assert.DoesNotContain(rig.Log.Messages, m => m.Kind == MessageKind.Warning);
    });

    [Fact]
    public Task Script_f5_sends_only_reads_to_the_driver() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var driver = UsePolicy(rig, ViewerPolicy());
        rig.State.ErrorMode = ScriptErrorMode.Continue;
        rig.Host.NewDocument("""
            select 1 from dual;
            /* 주석 */ delete from emp where id = 1;
            begin
              null;
            end;
            /
            alter session set nls_date_format = 'YYYY';
            select 2 from dual;
            commit;
            """);

        await rig.Controller.RunAsync(RunMode.Script);

        Assert.Equal(["SELECT", "SELECT", "COMMIT"], driver.Select(s => s.Verb).ToArray());
        Assert.Equal(6, rig.Exec.Statements.Count); // 엔진은 여섯 문장 모두 받았고 셋은 정책 관문에서 멈췄다
        Assert.Equal(3, rig.Log.Messages.Count(m => m.Kind == MessageKind.Warning && m.Text.Contains("조회 전용 Role", StringComparison.Ordinal)));
    });

    [Fact]
    public Task Script_stop_mode_stops_at_the_first_denial() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var driver = UsePolicy(rig, ViewerPolicy());
        rig.State.ErrorMode = ScriptErrorMode.Stop;
        rig.Host.NewDocument("select 1 from dual;\nupdate emp set sal = 1;\nselect 2 from dual;");

        await rig.Controller.RunAsync(RunMode.Script);

        Assert.Equal("SELECT", Assert.Single(driver).Verb);
        Assert.Equal(2, rig.Exec.Statements.Count);
    });

    [Fact]
    public Task Viewer_with_another_role_is_not_denied() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var driver = UsePolicy(rig, new ExecutionPolicy(FakeFeatureGate.ForRoles(RoleIds.Viewer, RoleIds.SqlDev)));
        rig.State.ErrorMode = ScriptErrorMode.Continue;
        rig.Host.NewDocument("update emp set sal = 1 where id = 1;\ncreate table t (x number);\nbegin null; end;\n/");

        await rig.Controller.RunAsync(RunMode.Script);

        Assert.Equal(3, driver.Count);
        Assert.DoesNotContain(rig.Log.Messages, m => m.Kind == MessageKind.Warning);
    });

    [Fact]
    public Task Read_only_profile_message_comes_first() => Ui.Run(async () =>
    {
        using var rig = new Rig(new ConnectionProfile("ro", "scott", "localhost:1521/FREEPDB1", ReadOnly: true));
        var driver = UsePolicy(rig, ViewerPolicy());
        var tab = rig.Host.NewDocument("update emp set sal = 1 where id = 1");

        await rig.Controller.RunAsync(RunMode.Statement);

        Assert.Empty(driver);
        var item = Assert.Single(((ResultTabsViewModel)tab.ResultPane!).Tabs);
        Assert.Contains("읽기 전용 접속", item.Message, StringComparison.Ordinal);
    });

    [Fact]
    public Task Choosing_another_role_lets_the_next_run_through() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var gate = FakeFeatureGate.ForRoles(RoleIds.Viewer);
        var driver = UsePolicy(rig, new ExecutionPolicy(gate));
        rig.Host.NewDocument("update emp set sal = 1 where id = 1");

        await rig.Controller.RunAsync(RunMode.Statement);
        Assert.Empty(driver);

        gate.ReadOnlyRole = false; // 설정에서 R1 을 더 고름
        gate.RaiseChanged();
        await rig.Controller.RunAsync(RunMode.Statement);
        Assert.Equal("UPDATE", Assert.Single(driver).Verb);
    });

    // ---------- 실제 실행 엔진 ----------

    [Fact]
    public async Task Real_engine_denies_before_reaching_the_driver()
    {
        // 가짜 세션은 OracleSession 이 아니라서 드라이버 단계에 닿으면 ArgumentException 이 난다. 거부 결과가 나오면 그 전에 멈춘 것이다.
        var bus = new EventBus();
        var decisions = new List<PolicyDecision>();
        bus.Subscribe<PolicyDecisionMade>(this, e => decisions.Add(e.Decision));
        using var container = AppComposition.Create(new AppCompositionOptions { DataDirectory = NewDataDir() });
        container.AddInstance<IFeatureGate>(FakeFeatureGate.ForRoles(RoleIds.Viewer));
        container.AddInstance<IEventBus>(bus);
        var engine = container.GetRequiredService<IExecutionService>(); // 앱 조립의 실제 엔진(OracleExecutionService)과 정책 관문
        var session = new FakeSession(TestData.Profile());
        var update = Assert.Single(Parse("update emp set sal = 1 where id = 1"));

        var r = await engine.ExecuteAsync(session, update, new Dictionary<string, BindValue>(), null, default);

        Assert.Equal(ExecutionStatus.Denied, r.Status);
        Assert.Equal(PolicyRule.ReadOnlyRole, r.Policy?.Rule);
        Assert.Equal(PolicyRule.ReadOnlyRole, Assert.Single(decisions).Rule);

        var select = Assert.Single(Parse("select 1 from dual"));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.ExecuteAsync(session, select, new Dictionary<string, BindValue>(), null, default));
    }

    [Fact]
    public async Task Real_engine_script_continues_past_denials_in_continue_mode()
    {
        using var container = AppComposition.Create(new AppCompositionOptions { DataDirectory = NewDataDir() });
        container.AddInstance<IFeatureGate>(FakeFeatureGate.ForRoles(RoleIds.Viewer));
        var engine = container.GetRequiredService<IExecutionService>();
        var session = new FakeSession(TestData.Profile());
        var stmts = Parse("update emp set sal = 1;\ndrop table emp;\nbegin null; end;\n/");

        var results = new List<ExecutionResult>();
        await foreach (var r in engine.ExecuteScriptAsync(session, stmts, ScriptErrorMode.Continue, null, default))
        {
            results.Add(r);
        }

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal(PolicyRule.ReadOnlyRole, r.Policy?.Rule));
    }

    // ---------- 앱 조립·대체 등록 ----------

    private static string NewDataDir() => Path.Combine(Path.GetTempPath(), "oranda-rorole-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void App_composition_hands_the_registered_gate_to_the_policy()
    {
        using var container = AppComposition.Create(new AppCompositionOptions { DataDirectory = NewDataDir() });
        container.AddInstance<IFeatureGate>(FakeFeatureGate.ForRoles(RoleIds.Viewer));

        var policy = Assert.IsType<ExecutionPolicy>(container.GetRequiredService<IExecutionPolicy>());

        Assert.Equal(PolicyRule.ReadOnlyRole, Eval(policy, Assert.Single(Parse("delete from emp where id = 1"))).Rule);
    }

    [Fact]
    public void App_composition_with_the_default_gate_denies_nothing()
    {
        using var container = AppComposition.Create(new AppCompositionOptions { DataDirectory = NewDataDir() });

        var policy = Assert.IsType<ExecutionPolicy>(container.GetRequiredService<IExecutionPolicy>());

        Assert.Same(PolicyDecision.Allowed, Eval(policy, Assert.Single(Parse("delete from emp where id = 1"))));
    }

    [Fact]
    public void Fallback_registration_passes_the_gate()
    {
        using var container = new ServiceContainer();
        container.AddInstance<IFeatureGate>(FakeFeatureGate.ForRoles(RoleIds.Viewer));

        ExecutionCommands.EnsureEngineServices(container, new ExecutionState());

        var policy = Assert.IsType<ExecutionPolicy>(container.GetRequiredService<IExecutionPolicy>());
        Assert.Equal(PolicyRule.ReadOnlyRole, Eval(policy, Assert.Single(Parse("create table t (x number)"))).Rule);
    }

    [Fact]
    public void Fallback_registration_without_a_gate_keeps_the_old_rules()
    {
        using var container = new ServiceContainer();

        ExecutionCommands.EnsureEngineServices(container, new ExecutionState());

        var policy = Assert.IsType<ExecutionPolicy>(container.GetRequiredService<IExecutionPolicy>());
        Assert.Same(PolicyDecision.Allowed, Eval(policy, Assert.Single(Parse("create table t (x number)"))));
    }

    [Fact]
    public Task Controller_resolution_wires_the_gate_into_the_shared_state_without_waking_deferred_assemblies() => Ui.Run(() =>
    {
            using var container = AppComposition.Create(new AppCompositionOptions { DataDirectory = NewDataDir() });
            var gate = FakeFeatureGate.ForRoles(RoleIds.Viewer);
            container.AddInstance<IFeatureGate>(gate);

            var controller = ExecutionCommands.Resolve(container);

            Assert.True(controller.State.ReadOnlyRole);
            Assert.True(container.GetRequiredService<ContextKeyService>().Get("readOnlySession"));
            Assert.False(container.DeferredScanned, "시작 경로에서 지연 어셈블리를 깨웠다: " + container.DeferredScanTrigger);
    });

    // ---------- readOnlySession 키·쓰기 명령 흐림 ----------

    private static ExecutionState StateWith(ContextKeyService keys, IFeatureGate? gate, ConnectionProfile? profile = null, EventBus? bus = null)
    {
        var state = new ExecutionState(bus);
        state.UseKeys(keys);
        state.UseFeatures(gate);
        if (bus is not null && profile is not null)
        {
            var session = new FakeSession(profile);
            bus.Publish(new SessionOpened(session.Id, profile, session.Server, SessionPurpose.Work));
        }

        return state;
    }

    [Fact]
    public Task Viewer_only_sets_read_only_session_even_without_a_connection() => Ui.Run(() =>
    {
            var keys = new ContextKeyService();

            var state = StateWith(keys, FakeFeatureGate.ForRoles(RoleIds.Viewer));

            Assert.True(state.ReadOnlyRole);
            Assert.True(state.ReadOnlySessionKey);
            Assert.True(keys.Get("readOnlySession"));
    });

    [Fact]
    public Task Viewer_with_another_role_or_no_gate_leaves_the_key_to_the_profile() => Ui.Run(() =>
    {
            var keys = new ContextKeyService();
            StateWith(keys, FakeFeatureGate.ForRoles(RoleIds.Viewer, RoleIds.Tuner));
            Assert.False(keys.Get("readOnlySession"));

            var keys2 = new ContextKeyService();
            StateWith(keys2, null);
            Assert.False(keys2.Get("readOnlySession"));

            var keys3 = new ContextKeyService();
            var bus = new EventBus();
            StateWith(keys3, new FakeFeatureGate(), new ConnectionProfile("ro", "scott", "localhost:1521/FREEPDB1", ReadOnly: true), bus);
            Assert.True(keys3.Get("readOnlySession")); // 읽기 전용 프로필은 Role 과 상관없이 그대로
    });

    [Fact]
    public Task Gate_change_recomputes_the_key_both_ways() => Ui.Run(() =>
    {
            var keys = new ContextKeyService();
            var gate = FakeFeatureGate.ForRoles(RoleIds.Viewer);
            var state = StateWith(keys, gate);
            var raised = new List<string?>();
            state.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            gate.ReadOnlyRole = false;
            gate.RaiseChanged();
            Assert.False(keys.Get("readOnlySession"));
            Assert.Contains(nameof(ExecutionState.ReadOnlyRole), raised);

            gate.ReadOnlyRole = true;
            gate.RaiseChanged();
            Assert.True(keys.Get("readOnlySession"));
    });

    [Fact]
    public Task Replacing_the_gate_unsubscribes_the_old_one() => Ui.Run(() =>
    {
            var keys = new ContextKeyService();
            var old = FakeFeatureGate.ForRoles(RoleIds.Viewer);
            var state = StateWith(keys, old);
            var next = FakeFeatureGate.ForRoles(RoleIds.SqlDev);

            state.UseFeatures(next);
            Assert.False(keys.Get("readOnlySession"));

            old.RaiseChanged(); // 옛 게이트의 사건은 더 이상 키를 바꾸지 않는다(옛 게이트는 여전히 R6)
            Assert.False(keys.Get("readOnlySession"));

            state.UseFeatures(null); // null 은 무시(게이트를 떼지 않는다)
            next.ReadOnlyRole = true;
            next.RaiseChanged();
            Assert.True(keys.Get("readOnlySession"));
    });

    [Fact]
    public Task Gate_change_from_a_background_thread_reaches_the_ui_thread() => HeadlessHost.Run(async () =>
    {
        var keys = new ContextKeyService();
        var gate = FakeFeatureGate.ForRoles(RoleIds.SqlDev);
        StateWith(keys, gate);
        Assert.False(keys.Get("readOnlySession"));

        await Task.Run(() =>
        {
            gate.ReadOnlyRole = true;
            gate.RaiseChanged();
        });

        await HeadlessHost.WaitUntil(() => keys.Get("readOnlySession"));
        Assert.True(Avalonia.Threading.Dispatcher.UIThread.CheckAccess());
    });

    [Fact]
    public Task Write_commands_are_dimmed_with_a_reason_for_viewer_only() => Ui.Run(() =>
    {
            var keys = new ContextKeyService();
            StateWith(keys, FakeFeatureGate.ForRoles(RoleIds.Viewer));
            foreach (var key in TrueKeys)
            {
                keys.Set(key, true);
            }

            var text = new ShellText(language: "ko");
            var writes = ShellTestHelpers.AppContributions.Commands
                .Select(c => c.Definition)
                .Where(d => d.Enablement?.Contains("!readOnlySession", StringComparison.Ordinal) == true)
                .ToList();

            Assert.Contains(writes, d => d.Id == "oranda.txn.toggleAutoCommit");
            Assert.Contains(writes, d => d.Id == "oranda.tools.import");
            Assert.Contains(writes, d => d.Id == "oranda.exec.runForEdit");
            Assert.All(writes, d =>
            {
                Assert.False(keys.Evaluate(d.Enablement), d.Id);
                Assert.False(string.IsNullOrEmpty(Oranda.App.Shell.Menus.MenuVisibility.Reason(d.Enablement, keys, text.TryLookup)), d.Id);
            });

            // R1 을 더 고르면 같은 명령이 다시 켜진다(세션 있음).
            var keys2 = new ContextKeyService();
            StateWith(keys2, FakeFeatureGate.ForRoles(RoleIds.Viewer, RoleIds.SqlDev));
            keys2.Set("hasSession", true);
            Assert.True(keys2.Evaluate(writes.Single(d => d.Id == "oranda.txn.toggleAutoCommit").Enablement));
    });
}
