using System.Runtime.CompilerServices;
using System.Text.Json;
using Oranda.App.Features.Ai;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Tuning;
using Oranda.App.Features.Ai.Tuning.Commands;
using Oranda.App.Features.MyBatisWorkspace.Ai;
using Oranda.App.Features.MyBatisWorkspace.Parameters;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Tests.Features.MyBatisWorkspace.Fakes;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.MyBatis;
using Oranda.Core.Text;
using Oranda.Language.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// TSK-15-08 MyBatis 펼친 SQL 튜닝·설명(설계 §3.4, 결정 AE-5): AI 가 매퍼 탭의 원문 대신 지금 값으로 펼친 SQL 을 보고, 바인드 값은 실행과 같게 담고,
/// 재작성 제안은 새 SQL 탭으로만 가고(매퍼 탭 글은 그대로), 카드에 기준 한 줄이 붙는지. 활성 식·문맥 키·까닭 툴팁도 본다.
/// 계산기는 실제, 세션·드라이버는 가짜다.
/// </summary>
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class MyBatisAiTests
{
    private const string Xml = """
        <select id="s">
          SELECT E.EMPNO, E.ENAME FROM EMP E
          <where>
            <if test="empNo != null">AND E.EMPNO = #{empNo,jdbcType=NUMERIC}</if>
          </where>
        </select>
        """;

    private static readonly Guid ConnectionId = Guid.Parse("15081508-1508-1508-1508-150815081508");

    private static readonly AiContext Dev = new("DEV-ERP", ConnectionEnvironment.Development, AiAccessLevel.QueryRows);

    /// <summary>계산은 실제와 같고 <see cref="Fail"/> 이면 펼치기에서 예외를 낸다(펼치기 실패).</summary>
    private sealed class SwitchEvaluator : IMyBatisEvaluator
    {
        private readonly MyBatisEvaluator _real = new();

        public bool Fail { get; set; }

        public EvalResult Evaluate(MyBatisElement statement, IMyBatisFragmentResolver fragments, IReadOnlyDictionary<string, object?> values, IReadOnlyDictionary<TextRange, bool>? blockOverrides, MyBatisEvalOptions? options = null) =>
            Fail ? throw new InvalidOperationException("펼치기 실패(시험)") : _real.Evaluate(statement, fragments, values, blockOverrides, options);

        public IReadOnlyList<MyBatisParameter> ExtractParameters(MyBatisElement statement, IMyBatisFragmentResolver fragments) => _real.ExtractParameters(statement, fragments);

        public IReadOnlyList<ConditionCombination> Combinations(EvalResult allOn, int fullLimit = 8) => _real.Combinations(allOn, fullLimit);
    }

    /// <summary>요청을 모으고 구조 결과(튜닝 결과 모양)를 돌려주는 드라이버. 실제 드라이버처럼 튜닝 요청에는 도구 범위(Session)가 있어야 한다.</summary>
    private sealed class ScriptDriver : IAiDriver
    {
        public List<AiRequest> Requests { get; } = [];

        public JsonElement? Result { get; set; }

        /// <summary>답을 내기 전에 부른다(AI 가 일하는 사이 사용자가 값을 고친 경우를 흉내).</summary>
        public Action? WhileRunning { get; set; }

        public AiDriverCapabilities Capabilities { get; } = new(false, true, true, false, true);

        public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            if (request.Session is null)
            {
                yield return new AiError("도구 범위가 없습니다.", "session_required");
                yield break;
            }

            yield return new AiStarted("s-" + Requests.Count);
            await Task.Yield();
            WhileRunning?.Invoke();
            yield return new AiFinal("답", request.OutputSchema is null ? null : Result, "s-" + Requests.Count);
        }
    }

    private sealed class Env : IDisposable
    {
        private readonly SqlEditorHost _previous = SqlEditorHost.Current;

        public Env(string xml = Xml)
        {
            SqlEditorHost.Current = Host;
            var ws = Host.EnsureWorkspace();
            ws.MyBatis = Session;
            Tab = new SqlEditorViewModel(Guid.NewGuid(), "<select id=\"s\">XML-ORIGINAL</select>") { Title = "stmt", ConnectionProfileId = ConnectionId };
            Tab.IsDirty = false;
            Host.AddMyBatisTab(ws, Tab);
            Host.Active = Tab;
            Session.StatementIds[Tab] = "s";
            Session.Elements["s"] = new MyBatisElement { Kind = MyBatisElementKind.Select, Name = "select", Id = "s", Text = xml };
            Session.SelectedStatementId = "s";
            Hub = new MyBatisEvalHub(MyBatisEvalHub.Services(Evaluator, null)) { Host = Host, Post = a => a() };
            Hub.Refresh();
            Source = MyBatisAiSqlSource.For(Hub);
            Editor = new AiSqlSourceEditor(new EditorContext { Host = Host }, new HostEditorTabs(), [Source]);
        }

        public SqlEditorHost Host { get; } = new();

        public FakeMyBatisWorkspaceSession Session { get; } = new();

        public SwitchEvaluator Evaluator { get; } = new();

        public SqlEditorViewModel Tab { get; }

        public MyBatisEvalHub Hub { get; }

        public MyBatisAiSqlSource Source { get; }

        public AiSqlSourceEditor Editor { get; }

        public MyBatisStatementEval Eval => Hub.ForTab(Tab)!;

        public Env WithEmpNo(string value)
        {
            Eval.SetValue("empNo", MyBatisValues.FromCellText(value));
            Eval.Flush();
            return this;
        }

        public void Dispose() => SqlEditorHost.Current = _previous;
    }

    // ---- AI 가 보는 글 -------------------------------------------------------------------------

    [Fact]
    public Task The_ai_sees_the_expanded_sql_not_the_mapper_text() => Ui.Run(() =>
    {
        using var env = new Env().WithEmpNo("7369");

        var statement = env.Editor.GetCurrentStatement();
        var document = env.Editor.GetActiveDocument();

        Assert.NotNull(statement);
        Assert.Contains("FROM EMP E", statement.Text, StringComparison.Ordinal);
        Assert.Contains(":empNo", statement.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("<if", statement.Text, StringComparison.Ordinal);
        Assert.Equal(["empNo"], statement.BindNames);
        Assert.NotNull(document);
        Assert.Equal(env.Tab.TabId, document.TabId);
        Assert.Equal(ConnectionId, document.ConnectionProfileId); // 접속 확인(지금 접속의 탭인지)은 원래 탭 그대로
        Assert.DoesNotContain("XML-ORIGINAL", document.Text, StringComparison.Ordinal);
        Assert.Equal(new BindValue(BindDataType.Number, 7369m), env.Editor.BindsOf(env.Tab.TabId)!["empNo"]); // 실행과 같은 변환(jdbcType NUMERIC)
    });

    [Fact]
    public Task A_plain_sql_tab_is_passed_through_untouched() => Ui.Run(() =>
    {
        using var env = new Env();
        var plain = env.Host.NewDocument("select 'X' from dual");
        env.Host.Active = plain;

        Assert.Equal("select 'X' from dual", env.Editor.GetCurrentStatement()?.Text);
        Assert.Null(env.Editor.BindsOf(plain.TabId)); // 일반 탭은 탭의 바인드 목록을 쓴다
        Assert.Null(env.Editor.ProposalNote(plain.TabId));
        Assert.True(env.Editor.ReplaceText(plain.TabId, "select 2 from dual"));
        Assert.Equal("select 2 from dual", plain.Text);
    });

    [Fact]
    public Task Writes_to_the_mapper_tab_are_refused() => Ui.Run(async () =>
    {
        using var env = new Env().WithEmpNo("7369");
        var before = env.Tab.Text;

        Assert.False(env.Editor.ReplaceText(env.Tab.TabId, "SELECT 1 FROM DUAL"));
        Assert.False(await env.Editor.ProposeEditAsync(new TextEdit(new TextRange(0, 1), "X"), CancellationToken.None));
        Assert.Equal(before, env.Tab.Text); // 매퍼 XML 은 AI 경로로 바뀌지 않는다(AE-5)
        Assert.Equal(MyBatisAiSqlSource.Note, env.Editor.ProposalNote(env.Tab.TabId));
    });

    [Fact]
    public Task A_failed_expansion_gives_no_statement() => Ui.Run(() =>
    {
        using var env = new Env();
        env.Evaluator.Fail = true;
        env.Eval.Recompute();

        Assert.Null(env.Editor.GetCurrentStatement());
        Assert.Equal(string.Empty, env.Editor.GetActiveDocument()?.Text); // 원문으로 되돌아가 보내지 않는다
        Assert.Empty(env.Editor.BindsOf(env.Tab.TabId)!);
    });

    // ---- 활성 식·문맥 키 -----------------------------------------------------------------------

    [Fact]
    public Task Keys_follow_the_active_tab_and_the_expansion() => Ui.Run(() =>
    {
        using var env = new Env();
        var keys = new ContextKeyService();
        _ = env.Eval.EnsureLoadedAsync();
        new MyBatisAiKeys(keys).Attach(env.Hub);

        Assert.True(keys.Evaluate(MyBatisAiKeys.TabKey));
        Assert.True(keys.Evaluate(MyBatisAiKeys.ReadyKey));

        env.Evaluator.Fail = true;
        env.Eval.Recompute();
        Assert.True(keys.Evaluate(MyBatisAiKeys.TabKey));
        Assert.False(keys.Evaluate(MyBatisAiKeys.ReadyKey)); // 펼치기 실패

        env.Evaluator.Fail = false;
        env.Eval.Recompute();
        Assert.True(keys.Evaluate(MyBatisAiKeys.ReadyKey)); // 고치면 다시 켜진다

        env.Host.Active = env.Host.NewDocument("select 1 from dual");
        Assert.False(keys.Evaluate(MyBatisAiKeys.TabKey));
        Assert.False(keys.Evaluate(MyBatisAiKeys.ReadyKey));
    });

    [Fact]
    public void Tune_and_explain_open_in_mybatis_only_with_an_expanded_sql_and_say_why_not()
    {
        var keys = new ContextKeyService();
        var text = new ShellText(language: "ko");
        foreach (var key in new[] { "aiConfigured", "aiAccessOn", "hasSession", "mybatisDocActive" })
        {
            keys.Set(key, true);
        }

        string? Reason(string expression) => ContextKeyReasons.Explain(expression, keys.Evaluate, text.TryLookup); // 주 메뉴와 같은 문구 조회

        Assert.False(keys.Evaluate(AiCommands.ExpandedSqlEnablement));
        Assert.Contains("MyBatis 문장 탭", Reason(AiCommands.ExpandedSqlEnablement), StringComparison.Ordinal);

        keys.Set(MyBatisAiKeys.TabKey, true);
        Assert.Contains("펼친 SQL 을 만들지 못했습니다", Reason(AiCommands.ExpandedSqlEnablement), StringComparison.Ordinal);

        keys.Set(MyBatisAiKeys.ReadyKey, true);
        Assert.True(keys.Evaluate(AiCommands.ExpandedSqlEnablement));
        Assert.False(keys.Evaluate(ExplainPlanCommand.Enablement)); // 실행 계획 해설·오류 고치기는 MyBatis 에서 계속 닫힌다
        Assert.False(keys.Evaluate(FixErrorCommand.Enablement));

        keys.Set("mybatisDocActive", false);
        keys.Set(MyBatisAiKeys.TabKey, false);
        keys.Set(MyBatisAiKeys.ReadyKey, false);
        Assert.True(keys.Evaluate(AiCommands.ExpandedSqlEnablement)); // 일반 SQL 탭은 전과 같다
    }

    [Fact]
    public void Tune_tune_statement_and_explain_sql_use_the_expanded_sql_expression()
    {
        var commands = ShellContributions.Scan(typeof(ShellContributions).Assembly).Commands
            .Select(c => c.Definition).ToDictionary(c => c.Id);

        Assert.Equal(AiCommands.ExpandedSqlEnablement, commands[AiCommands.Tune].Enablement);
        Assert.Equal(AiCommands.ExpandedSqlEnablement, commands[AiCommands.TuneStatement].Enablement);
        Assert.Equal(AiCommands.ExpandedSqlEnablement, commands[AiCommands.ExplainSql].Enablement);
        Assert.Equal(ExplainPlanCommand.Enablement, commands[AiCommands.ExplainPlan].Enablement);
        Assert.Equal(FixErrorCommand.Enablement, commands[AiCommands.FixError].Enablement);
    }

    // ---- 튜닝 흐름 끝까지 --------------------------------------------------------------------

    private static AiTuningFlow FlowFor(Env env, ScriptDriver driver, AiChatViewModel chat) => new(
        env.Editor, driver, chat, (_, _, _) => throw new InvalidOperationException("탭 접근이 있으면 쓰지 않는다"),
        sessionFactory: (context, vault) => new AgentSession(new AgentRunScope("run-t", new ConnectionProfile("DEV-ERP", "scott", "h/s", Id: ConnectionId), context) { Literals = vault }),
        tabs: env.Editor,
        previewEdit: (edit, _, _) => Task.FromResult<string?>(edit.NewText));

    [Fact]
    public Task Tuning_sends_the_expanded_sql_and_puts_the_rewrite_in_a_new_sql_tab_only() => Ui.Run(async () =>
    {
        using var env = new Env().WithEmpNo("7369");
        var mapperText = env.Tab.Text;
        var driver = new ScriptDriver
        {
            Result = JsonDocument.Parse("""
                {
                  "summary": "인덱스를 타게 했습니다.",
                  "rewrites": [ { "label": "열 줄임", "sql": "SELECT E.EMPNO FROM EMP E WHERE E.EMPNO = :empNo", "rationale": "필요한 열만" } ],
                  "index_suggestions": [ { "table": "EMP", "columns": ["EMPNO"], "ddl": "CREATE INDEX ix_emp ON emp (empno);", "rationale": "r" } ],
                  "evidence": []
                }
                """).RootElement.Clone(),
        };
        var chat = new AiChatViewModel();
        var flow = FlowFor(env, driver, chat);

        await flow.StartAsync(new TuningStartChoice(Benchmark: false, IndexDdl: true, "default"), Dev);

        var request = Assert.Single(driver.Requests);
        Assert.Contains("FROM EMP E", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<if", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("XML-ORIGINAL", request.Prompt, StringComparison.Ordinal);
        Assert.Equal(mapperText, env.Tab.Text); // 매퍼 탭은 그대로
        var tuned = Assert.Single(env.Host.Tabs, t => t.Title == "stmt (튜닝)");
        Assert.Equal("SELECT E.EMPNO FROM EMP E WHERE E.EMPNO = :empNo", tuned.Text.Trim());
        Assert.Null(env.Host.WorkspaceOf(tuned)?.MyBatis); // 일반 SQL 작업 공간의 새 탭
        Assert.Equal(ConnectionId, tuned.ConnectionProfileId); // 매퍼 탭과 같은 접속
        Assert.Equal("7369", tuned.Binds.Find("empNo")?.Text); // 펼친 바인드 값을 옮겨 바로 실행할 수 있다
        var card = Assert.Single(chat.Entries.OfType<ChatProposalEntry>());
        Assert.Equal(MyBatisAiSqlSource.Note, card.Note);
        Assert.True(card.HasNote);
        Assert.Single(env.Host.Tabs, t => t.Title == "AI 인덱스 후보"); // 인덱스 제안은 그대로 쓴다
    });

    [Fact]
    public Task A_rewrite_is_not_placed_when_the_values_changed_meanwhile() => Ui.Run(async () =>
    {
        using var env = new Env().WithEmpNo("7369");
        var chat = new AiChatViewModel();
        var driver = new ScriptDriver
        {
            Result = JsonDocument.Parse("""
                { "summary": "s", "rewrites": [ { "label": "L", "sql": "SELECT 1 FROM DUAL", "rationale": "r" } ], "index_suggestions": [], "evidence": [] }
                """).RootElement.Clone(),
            WhileRunning = () => // AI 가 일하는 사이 사용자가 매퍼 원문을 고쳐 펼친 SQL 이 달라졌다
            {
                env.Session.Elements["s"] = new MyBatisElement { Kind = MyBatisElementKind.Select, Name = "select", Id = "s", Text = Xml.Replace("E.ENAME", "E.JOB", StringComparison.Ordinal) };
                env.Eval.Recompute();
            },
        };

        await FlowFor(env, driver, chat).StartAsync(new TuningStartChoice(false, false, "default"), Dev);

        Assert.DoesNotContain(env.Host.Tabs, t => t.Title.EndsWith("(튜닝)", StringComparison.Ordinal)); // 옛 펼친 SQL 기준 제안을 넣지 않는다
        Assert.Contains(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.Contains("문장이 바뀌어", StringComparison.Ordinal));
    });

    [Fact]
    public Task Explain_sql_sends_the_expanded_sql_without_tools_changing_the_mapper() => Ui.Run(async () =>
    {
        using var env = new Env().WithEmpNo("7369");
        var driver = new ScriptDriver();
        var chat = new AiChatViewModel();
        var flow = new AiTuningFlow(
            env.Editor, driver, chat, (_, _, _) => throw new InvalidOperationException(),
            sessionFactory: (context, vault) => new AgentSession(new AgentRunScope("run-e", new ConnectionProfile("DEV-ERP", "scott", "h/s", Id: ConnectionId), context)),
            tabs: env.Editor);
        var target = new AiCommandTarget(env.Editor.GetCurrentStatement()!, TabId: env.Tab.TabId);

        await flow.StartCommandAsync(ExplainSqlCommand.Instance, target, Dev);

        var request = Assert.Single(driver.Requests);
        Assert.Contains("FROM EMP E", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<if", request.Prompt, StringComparison.Ordinal);
        Assert.Equal("<select id=\"s\">XML-ORIGINAL</select>", env.Tab.Text);
    });

    // ---- 바인드 값·카드 그리기 ---------------------------------------------------------------

    [Fact]
    public void Source_binds_are_copied_for_the_benchmark_without_secret_looking_names()
    {
        var binds = new TuningStartBinds();
        var before = binds.Capture(null, null);

        var previous = binds.Use(new Dictionary<string, BindValue>
        {
            ["empNo"] = new(BindDataType.Number, 7369m),
            ["userPwd"] = new(BindDataType.Varchar2, "s3cr3t"),
        });

        Assert.Empty(previous);
        Assert.Equal(new BindValue(BindDataType.Number, 7369m), binds.Current["EMPNO"]); // 이름은 대소문자 무시
        Assert.False(binds.Current.ContainsKey("userPwd")); // 비밀로 보이는 이름은 벤치마크에도 싣지 않는다(탭 값과 같은 규칙)
        binds.Restore(before);
        Assert.Empty(binds.Current); // 시작이 거부되면 앞 값으로 되돌린다
    }

    [Fact]
    public Task The_proposal_card_shows_the_expanded_sql_note_only_when_given() => Ui.Run(() =>
    {
        var vm = new AiChatViewModel();
        vm.StartConversation("튜닝 — SELECT …");
        vm.AddProposal("열 줄임", "필요한 열만", applied: false, () => Task.FromResult<bool?>(true), note: MyBatisAiSqlSource.Note);
        vm.AddProposal("힌트", "일반 탭 제안", applied: false, () => Task.FromResult<bool?>(true));
        var window = new Avalonia.Controls.Window { Width = 460, Height = 760, Content = new AiChatView { DataContext = vm } };
        window.Show();
        try
        {
            Oranda.App.Tests.Shell.Headless.HeadlessHost.Pump(4);
            window.UpdateLayout();

            var notes = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.TextBlock>()
                .Where(t => t.Name == "ProposalNote" && t.IsEffectivelyVisible).ToList();
            var note = Assert.Single(notes); // 기준이 없는 카드에는 줄이 없다
            Assert.Equal("펼친 SQL 기준입니다. XML 은 바뀌지 않습니다.", note.Text);
        }
        finally
        {
            window.Close();
        }
    });
}
