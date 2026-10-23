using System.Text.RegularExpressions;
using Oranda.App.Features.Binds;
using Oranda.App.Features.Execution;
using Oranda.App.Features.MyBatisWorkspace.Parameters;
using Oranda.App.Features.MyBatisWorkspace.Run;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Tests.Features.Execution;
using Oranda.App.Tests.Features.MyBatisWorkspace.Fakes;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.MyBatis;
using Oranda.Core.Text;
using Oranda.Language.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// TSK-03-02 「시나리오 값으로 실행」·복사 2종: 펼친 SQL·바인드가 탭 실행과 같은 경로(<see cref="ExecutionController.RunPreparedAsync"/>)로 가는지,
/// 바인드 변환(jdbcType → BindInput), <c>${}</c> 확인, 복사 글을 본다. 실행 엔진·대화상자·세션은 가짜, 계산기·언어 서비스는 실제다.
/// </summary>
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class ParamsRunExecutionTests
{
    private const string EmpSelect = """
        <select id="selectEmpList" parameterType="map">
          SELECT E.EMPNO, E.ENAME FROM EMP E
          <where>
            <if test="empNo != null">
              AND E.EMPNO = #{empNo,jdbcType=NUMERIC}
            </if>
            <if test="ename != null and ename != ''">
              AND E.ENAME LIKE '%' || #{ename} || '%'
            </if>
            <if test="deptIds != null and deptIds.size() > 0">
              AND E.DEPTNO IN
              <foreach collection="deptIds" item="id" open="(" separator="," close=")">
                #{id}
              </foreach>
            </if>
          </where>
          ORDER BY ${orderBy}
        </select>
        """;

    private static readonly MyBatisEvaluator Evaluator = new();

    private static readonly ConnectionProfile Production =
        new("prod", "scott", "localhost:1521/FREEPDB1") { Environment = ConnectionEnvironment.Production };

    /// <summary>MyBatis 문장 탭 하나(가짜 세션) + 활성 문장 계산 상태 + 실행 준비를 가진 시험 장비.</summary>
    private sealed class Env : IDisposable
    {
        public Env(string xml = EmpSelect, ConnectionProfile? profile = null)
        {
            Rig = new Rig(profile);
            var ws = Rig.Host.EnsureWorkspace();
            Session = new FakeMyBatisWorkspaceSession();
            ws.MyBatis = Session;
            Tab = new SqlEditorViewModel(Guid.NewGuid(), "select 1 from dual") { Title = "stmt" };
            Tab.IsDirty = false;
            Rig.Host.AddMyBatisTab(ws, Tab);
            Rig.Host.Active = Tab;
            Session.StatementIds[Tab] = "s";
            Session.Elements["s"] = new MyBatisElement { Kind = MyBatisElementKind.Select, Name = "select", Id = "s", Text = xml };
            Session.SelectedStatementId = "s";
            Hub = new MyBatisEvalHub(MyBatisEvalHub.Services(Evaluator, null)) { Host = Rig.Host, Post = a => a() };
            Hub.Refresh();
            Eval = Hub.Active!;
            Runner = new MyBatisRunner(Rig.Controller, Rig.LanguageForTests(), () => Rig.Dialogs);
        }

        public Rig Rig { get; }

        public FakeMyBatisWorkspaceSession Session { get; }

        public SqlEditorViewModel Tab { get; }

        public MyBatisEvalHub Hub { get; }

        public MyBatisStatementEval Eval { get; }

        public MyBatisRunner Runner { get; }

        /// <summary>명령 처리기와 같이 행 형식을 넘겨 실행한다.</summary>
        public Task<bool> RunAsync() => Runner.RunAsync(Tab, Result, Eval.RowTypeOf);

        public EvalResult Result
        {
            get
            {
                Eval.Flush();
                return Eval.Result!;
            }
        }

        /// <summary>시안 「부서 목록 + 이름 검색」 값: empNo 7369, ename O'NEIL, deptIds 10·20, orderBy E.ENAME.</summary>
        public Env WithFullValues()
        {
            Eval.SetValue("empNo", MyBatisValues.FromCellText("7369"));
            Eval.SetValue("ename", MyBatisValues.FromCellText("O'NEIL"));
            Eval.SetValue("deptIds", MyBatisValues.FromCellText("[10, 20]"));
            Eval.SetValue("orderBy", MyBatisValues.FromCellText("E.ENAME"));
            return this;
        }

        public void Dispose() => Rig.Dispose();
    }

    private static MyBatisBind Bind(string name, object? value, string? jdbcType = null) => new(name, name, jdbcType, value, default);

    // ---- 탭 실행과 같음 ----------------------------------------------------------------------

    [Fact]
    public Task Run_sends_the_expanded_sql_and_binds_like_a_tab_run() => Ui.Run(async () =>
    {
        using var env = new Env().WithFullValues();
        var result = env.Result;

        var ok = await env.Runner.RunAsync(env.Tab, result);

        Assert.True(ok);
        var stmt = Assert.Single(env.Rig.Exec.Statements);
        var expected = Assert.Single(Rig.LanguageForTests().Split(result.Sql, SplitOptions.Default));
        Assert.Equal(expected.Text, stmt.Text);
        Assert.Equal(StatementKind.Query, stmt.Kind);
        Assert.Equal(default, stmt.Range); // 매퍼 탭에 강조·오류 위치가 찍히지 않는다
        Assert.Equal(expected.BindNames, stmt.BindNames);
        var binds = env.Rig.Exec.LastBinds!;
        Assert.Equal(result.Binds.Select(b => b.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(), binds.Keys.Order());
        Assert.Equal(new BindValue(BindDataType.Number, 7369m), binds["empNo"]); // jdbcType NUMERIC
        Assert.Equal(new BindValue(BindDataType.Varchar2, "O'NEIL"), binds["ename"]);
        Assert.Equal(new BindValue(BindDataType.Number, 10m), binds["id_1"]); // jdbcType 없음: 값의 형
        Assert.Equal(new BindValue(BindDataType.Number, 20m), binds["id_2"]);
        var results = (ResultTabsViewModel)env.Tab.ResultPane!; // 일반 실행처럼 탭의 결과 창에 보인다
        Assert.Single(results.Tabs);
    });

    [Fact]
    public Task Statement_classification_comes_from_the_language_service() => Ui.Run(async () =>
    {
        using var env = new Env("""<delete id="s">DELETE FROM EMP</delete>""");

        await env.RunAsync();

        var stmt = Assert.Single(env.Rig.Exec.Statements);
        Assert.Equal(StatementKind.Dml, stmt.Kind);
        Assert.True(stmt.IsUnfilteredDml); // 정책 관문이 WHERE 없는 DML 로 알아보도록 손대지 않는다
    });

    [Theory]
    [InlineData("""<select id="s">SELECT 1 FROM DUAL; SELECT 2 FROM DUAL</select>""", "문장이 2개")]
    [InlineData("""<select id="s"><if test="x != null">SELECT 1 FROM DUAL</if></select>""", "비어 있어")]
    public Task Run_needs_exactly_one_statement(string xml, string expectedMessage) => Ui.Run(async () =>
    {
        using var env = new Env(xml);

        var ok = await env.RunAsync();

        Assert.False(ok);
        Assert.Empty(env.Rig.Exec.Statements);
        Assert.Contains(env.Rig.Log.Messages, m => m.Kind == MessageKind.Warning && m.Text.Contains(expectedMessage, StringComparison.Ordinal));
    });

    [Fact]
    public Task Run_prepared_goes_through_the_same_denied_path_as_a_tab_run() => Ui.Run(async () =>
    {
        using var env = new Env("""<delete id="s">DELETE FROM EMP WHERE EMPNO = #{empNo}</delete>""");
        env.Eval.SetValue("empNo", MyBatisValues.FromCellText("7369"));
        var denied = new PolicyDecision(PolicyDecisionKind.Deny, PolicyRule.ReadOnlyProfile, "읽기 전용 접속입니다.");
        env.Rig.Exec.Handler = (s, _) => Task.FromResult(new ExecutionResult(s, ExecutionStatus.Denied) { Policy = denied });

        await env.RunAsync();

        var viaMyBatis = Assert.Single(((ResultTabsViewModel)env.Tab.ResultPane!).Tabs);
        Assert.Single(env.Rig.Exec.Statements); // 정책 관문(실행 엔진)을 거쳤다
        Assert.Equal("거부됨", viaMyBatis.Title);
        Assert.Equal("읽기 전용 접속입니다.", viaMyBatis.Message);
        Assert.Contains(env.Rig.Log.Messages, m => m.Kind == MessageKind.Warning && m.Text.Contains("거부됨", StringComparison.Ordinal));

        var plain = env.Rig.Host.NewDocument("delete from emp where empno = 7369");
        await env.Rig.Controller.RunAsync(RunMode.Statement);
        var viaTab = Assert.Single(((ResultTabsViewModel)plain.ResultPane!).Tabs);
        Assert.Equal((viaTab.Title, viaTab.Message), (viaMyBatis.Title, viaMyBatis.Message));
    });

    [Fact]
    public Task A_busy_connection_is_not_run_again() => Ui.Run(async () =>
    {
        using var env = new Env().WithFullValues();
        var gate = new TaskCompletionSource<ExecutionResult>();
        env.Rig.Exec.Handler = (_, _) => gate.Task;
        var first = env.RunAsync();

        var second = await env.RunAsync();

        Assert.False(second);
        gate.SetResult(new ExecutionResult(new SqlStatement("select 1 from dual", default, StatementKind.Query), ExecutionStatus.Succeeded));
        Assert.True(await first);
        Assert.Single(env.Rig.Exec.Statements); // 두 번째는 실행 엔진에 가지 않았다
    });

    [Fact]
    public Task Literal_with_an_ampersand_does_not_ask_for_substitution_variables() => Ui.Run(async () =>
    {
        // 펼친 SQL 의 글자 리터럴 안 & 는 일반 탭과 같이 치환 변수로 보지 않는다.
        using var env = new Env("""<select id="s">SELECT 'A&amp;B' AS X FROM DUAL WHERE 1 = #{n}</select>""");
        env.Eval.SetValue("n", MyBatisValues.FromCellText("1"));

        await env.RunAsync();

        var stmt = Assert.Single(env.Rig.Exec.Statements);
        Assert.Contains("'A&B'", stmt.Text, StringComparison.Ordinal);
        Assert.Empty(stmt.SubstitutionNames);
    });

    // ---- 바인드 변환 -----------------------------------------------------------------------

    [Theory]
    [InlineData("DATE", BindDataType.Date, "2026-10-23")]
    [InlineData("date", BindDataType.Date, "2026-10-23 14:05:09")]
    [InlineData("TIMESTAMP", BindDataType.Timestamp, "2026-10-23 14:05:09.123")]
    [InlineData("NUMERIC", BindDataType.Number, "1.5")]
    [InlineData("VARCHAR", BindDataType.Varchar2, "x")]
    public void Jdbc_type_picks_the_format_and_bind_input_converts_the_text(string jdbcType, BindDataType type, string text)
    {
        var value = MyBatisRunBinds.ToBindValue(Bind("d", text, jdbcType), out var error);

        var expected = new BindInput("d") { Type = type, Text = text }.ToBindValue(out var expectedError);
        Assert.Null(error);
        Assert.Null(expectedError);
        Assert.Equal(expected, value); // 일반 탭 바인드 값 칸과 같은 변환
    }

    [Theory]
    [InlineData("Y", 1)]
    [InlineData("false", 0)]
    public void Boolean_format_reads_the_text_like_a_plain_tab_and_binds_number_one_or_zero(string text, int expected)
    {
        // jdbcType=BOOLEAN 과 행 형식 참거짓 모두 11g 에서도 도는 숫자 1/0(값 넣은 글 복사와 같다).
        Assert.Equal(new BindValue(BindDataType.Number, (decimal)expected), MyBatisRunBinds.ToBindValue(Bind("d", text, "BOOLEAN"), out var error));
        Assert.Null(error);
        Assert.Equal(new BindValue(BindDataType.Number, (decimal)expected), MyBatisRunBinds.ToBindValue(Bind("d", text, null), out error, _ => BindDataType.Boolean));
        Assert.Null(error);
    }

    [Fact]
    public Task Unreadable_value_reports_an_error_and_nothing_is_run() => Ui.Run(async () =>
    {
        using var env = new Env().WithFullValues();
        env.Eval.SetType("empNo", BindDataType.Number);
        env.Eval.SetValue("empNo", MyBatisValues.FromCellText("abc")); // 계산기는 글자로 넘기고, 변환에서 걸린다

        var ok = await env.RunAsync();

        Assert.False(ok);
        Assert.Empty(env.Rig.Exec.Statements);
        Assert.Contains(env.Rig.Log.Messages, m => m.Kind == MessageKind.Error && m.Text.Contains(":empNo", StringComparison.Ordinal));
    });

    [Fact]
    public void Without_jdbc_type_the_value_type_decides()
    {
        Assert.Equal(new BindValue(BindDataType.Varchar2, "x"), MyBatisRunBinds.ToBindValue(Bind("a", "x"), out _));
        Assert.Equal(new BindValue(BindDataType.Number, 5m), MyBatisRunBinds.ToBindValue(Bind("a", 5), out _));
        Assert.Equal(new BindValue(BindDataType.Number, 5m), MyBatisRunBinds.ToBindValue(Bind("a", 5L), out _));
        Assert.Equal(new BindValue(BindDataType.Number, 1.25m), MyBatisRunBinds.ToBindValue(Bind("a", 1.25d), out _));
        Assert.Equal(new BindValue(BindDataType.Number, 1m), MyBatisRunBinds.ToBindValue(Bind("a", true), out _)); // 11g SQL 에는 BOOLEAN 이 없다
        Assert.Equal(new BindValue(BindDataType.Number, 0m), MyBatisRunBinds.ToBindValue(Bind("a", false), out _));
        Assert.Equal(new BindValue(BindDataType.Varchar2, null), MyBatisRunBinds.ToBindValue(Bind("a", null), out _));
        Assert.Null(MyBatisRunBinds.ToBindValue(Bind("a", new List<object?> { 1 }), out var error));
        Assert.Contains(":a", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_value_with_a_jdbc_type_keeps_the_type()
    {
        Assert.Equal(new BindValue(BindDataType.Date, null), MyBatisRunBinds.ToBindValue(Bind("d", null, "DATE"), out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Binds_with_the_same_name_merge_and_the_last_value_wins()
    {
        var binds = MyBatisRunBinds.Build([Bind("empNo", 1m), Bind("EMPNO", 2m), Bind("ename", "x")], out var error);

        Assert.Null(error);
        Assert.Equal(2, binds!.Count);
        Assert.Equal(new BindValue(BindDataType.Number, 2m), binds["empNo"]);
    }

    // ---- 행 형식 (「바인드 변수」 창 행이 형식을 정한다) ---------------------------------------

    [Fact]
    public Task A_date_row_type_converts_the_cell_text_like_a_tab_bind() => Ui.Run(async () =>
    {
        using var env = new Env("""<select id="s">SELECT 1 FROM DUAL WHERE D = #{d}</select>""");
        env.Eval.SetType("d", BindDataType.Date);
        env.Eval.SetValue("d", MyBatisValues.FromCellText("2024-01-31"));

        Assert.True(await env.RunAsync());

        var expected = new BindInput("d") { Type = BindDataType.Date, Text = "2024-01-31" }.ToBindValue(out _);
        var bind = env.Rig.Exec.LastBinds!["d"];
        Assert.Equal(expected, bind);
        Assert.Equal(new DateTime(2024, 1, 31), bind.Value);
    });

    [Fact]
    public Task The_chosen_row_type_wins_over_the_jdbc_type() => Ui.Run(async () =>
    {
        using var env = new Env("""<select id="s">SELECT 1 FROM DUAL WHERE N = #{n,jdbcType=NUMERIC}</select>""");
        env.Eval.SetType("n", BindDataType.Varchar2);
        env.Eval.SetValue("n", MyBatisValues.FromCellText("007"));

        Assert.True(await env.RunAsync());

        Assert.Equal(new BindValue(BindDataType.Varchar2, "007"), env.Rig.Exec.LastBinds!["n"]); // jdbcType 대로면 7
    });

    [Fact]
    public Task Empty_string_and_blank_cell_are_different_binds() => Ui.Run(async () =>
    {
        using var env = new Env("""<select id="s">SELECT 1 FROM DUAL WHERE A = #{a} AND B = #{b}</select>""");
        env.Eval.SetValue("a", MyBatisValues.FromCellText("''"));
        env.Eval.SetValue("b", MyBatisValues.FromCellText(string.Empty));

        Assert.True(await env.RunAsync());

        var binds = env.Rig.Exec.LastBinds!;
        Assert.Equal(new BindValue(BindDataType.Varchar2, string.Empty), binds["a"]); // '' = 빈 글자
        Assert.Equal(new BindValue(BindDataType.Varchar2, null), binds["b"]); // 빈 칸 = null
    });

    // ---- ${} 확인 -----

    [Fact]
    public Task Production_with_a_substitution_asks_and_a_refusal_runs_nothing() => Ui.Run(async () =>
    {
        using var env = new Env(profile: Production).WithFullValues();
        env.Rig.Dialogs.ConfirmAnswer = false;

        var ok = await env.RunAsync();

        Assert.False(ok);
        Assert.Empty(env.Rig.Exec.Statements);
        var asked = Assert.Single(env.Rig.Dialogs.Confirms);
        Assert.Contains("${orderBy} = E.ENAME", asked.Decision.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionEnvironment.Production, asked.Profile?.Environment);
        Assert.Contains(env.Rig.Log.Messages, m => m.Text == "${orderBy} = E.ENAME");
    });

    [Fact]
    public Task Production_with_a_substitution_runs_after_a_yes() => Ui.Run(async () =>
    {
        using var env = new Env(profile: Production).WithFullValues();

        var ok = await env.RunAsync();

        Assert.True(ok);
        Assert.Single(env.Rig.Dialogs.Confirms);
        Assert.Single(env.Rig.Exec.Statements);
    });

    [Fact]
    public Task Development_shows_the_substitution_without_asking() => Ui.Run(async () =>
    {
        using var env = new Env().WithFullValues();

        var ok = await env.RunAsync();

        Assert.True(ok);
        Assert.Empty(env.Rig.Dialogs.Confirms);
        Assert.Contains(env.Rig.Log.Messages, m => m.Kind == MessageKind.Info && m.Text == "${orderBy} = E.ENAME");
    });

    [Fact]
    public Task Production_without_a_substitution_does_not_ask() => Ui.Run(async () =>
    {
        using var env = new Env("""<select id="s">SELECT 1 FROM DUAL WHERE 1 = #{n}</select>""", Production);
        env.Eval.SetValue("n", MyBatisValues.FromCellText("1"));

        Assert.True(await env.RunAsync());

        Assert.Empty(env.Rig.Dialogs.Confirms);
    });

    // ---- 복사 2종 -------------------------------------------------------------------------

    [Fact]
    public Task Copy_texts_are_the_bind_form_and_the_value_form() => Ui.Run(() =>
    {
        using var env = new Env().WithFullValues();
        var result = env.Result;
        var binds = MyBatisRunBinds.Build(result.Binds, out _, env.Eval.RowTypeOf)!;

        var withValues = MyBatisRunText.WithValues(result.Sql, binds);

        Assert.Contains(":empNo", result.Sql, StringComparison.Ordinal); // 바인드 그대로
        Assert.Contains("E.EMPNO = 7369", withValues, StringComparison.Ordinal);
        Assert.Contains("'%' || 'O''NEIL' || '%'", withValues, StringComparison.Ordinal);
        Assert.Contains("IN(10,20)", Regex.Replace(withValues, @"\s+", string.Empty), StringComparison.Ordinal);
        Assert.DoesNotContain(":", withValues, StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Fact]
    public void Only_bind_tokens_are_replaced_literals_and_comments_stay()
    {
        var binds = new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = new(BindDataType.Number, 1.50m),
            ["n"] = new(BindDataType.Varchar2, null),
        };

        var text = MyBatisRunText.WithValues("SELECT ':a' FROM T WHERE x = :A AND y = :n AND z = :other -- :a\n", binds);

        Assert.Equal("SELECT ':a' FROM T WHERE x = 1.50 AND y = NULL AND z = :other -- :a\n", text);
    }

    [Fact]
    public void Literals_follow_the_bind_variable_window_rules()
    {
        var date = new DateTime(2026, 10, 23, 14, 5, 9, DateTimeKind.Unspecified);
        Assert.Equal("'it''s'", MyBatisRunText.Literal(new BindValue(BindDataType.Varchar2, "it's")));
        Assert.Equal("N'x'", MyBatisRunText.Literal(new BindValue(BindDataType.NVarchar2, "x")));
        Assert.Equal("NULL", MyBatisRunText.Literal(new BindValue(BindDataType.Date, null)));
        Assert.Equal("1", MyBatisRunText.Literal(new BindValue(BindDataType.Boolean, true)));
        Assert.Equal("0", MyBatisRunText.Literal(new BindValue(BindDataType.Number, 0m)));
        Assert.Equal("TO_DATE('2026-10-23 14:05:09', 'YYYY-MM-DD HH24:MI:SS')", MyBatisRunText.Literal(new BindValue(BindDataType.Date, date)));
        Assert.StartsWith("TO_TIMESTAMP('2026-10-23 14:05:09.0000000'", MyBatisRunText.Literal(new BindValue(BindDataType.Timestamp, date)), StringComparison.Ordinal);
        Assert.Null(MyBatisRunText.Literal(new BindValue(BindDataType.Number, null, BindDirection.Out)));
    }

    [Fact]
    public void A_date_bind_literal_matches_the_bind_variable_window()
    {
        var bind = MyBatisRunBinds.ToBindValue(Bind("d", "2026-10-23 14:05:09", "DATE"), out _)!;
        var row = new BindVariableRow("d") { Type = BindDataType.Date, Text = "2026-10-23 14:05:09" };

        Assert.Equal(BindVariableStore.ToLiteral(row), MyBatisRunText.Literal(bind));
    }

    // ---- 탭 찾기 · 시작 ---------------------------------------------------------------------

    [Fact]
    public Task The_statement_tab_is_found_even_when_another_tab_is_active() => Ui.Run(() =>
    {
        using var env = new Env();
        var other = env.Rig.Host.NewDocument("select 2 from dual");
        Assert.Same(other, env.Rig.Host.Active);

        Assert.Same(env.Tab, MyBatisRunner.TabOf(env.Eval, env.Rig.Host));
        env.Rig.Host.Active = env.Tab;
        Assert.Same(env.Tab, MyBatisRunner.TabOf(env.Eval, env.Rig.Host));
        return Task.CompletedTask;
    });

    [Fact]
    public void The_handler_is_created_without_waking_the_language_assembly()
    {
        using var container = AppComposition.Create(new AppCompositionOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "oranda-mbrun-" + Guid.NewGuid().ToString("N")) });

        Assert.NotNull(container.Create(typeof(MyBatisRunCommands)));

        Assert.False(container.DeferredScanned, "시작 경로에서 지연 어셈블리를 깨웠다: " + container.DeferredScanTrigger);
    }
}
