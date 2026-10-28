using System.Runtime.CompilerServices;
using System.Text.Json;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Tuning;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.Text;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>튜닝 실행 흐름(ai-ui u5): 요청 만들기, 제안은 편집기 제안으로만, 인덱스 문안은 새 탭에만, 이어 말하기는 ResumeId 로.</summary>
public class AiTuningFlowTests
{
    private const string Original = "SELECT * FROM tb_order WHERE stat_cd = '10'";

    private const string ResultJson = """
        {
          "summary": "TB_ORDER 를 FULL 로 읽습니다.",
          "rewrites": [ { "label": "NOT EXISTS 로 재작성", "sql": "SELECT o.* FROM tb_order o WHERE o.stat_cd = '10';", "rationale": "반조인을 쓰게 합니다.", "benchmark": "1.82 s → 0.21 s" } ],
          "index_suggestions": [ { "table": "TB_ORDER", "columns": ["STAT_CD", "ORDER_DT"], "ddl": "CREATE INDEX ix_order_stat ON tb_order (stat_cd, order_dt);", "rationale": "STAT_CD 로 시작하는 인덱스가 없습니다.", "risk": "쓰기 부담이 늘어납니다." } ],
          "evidence": [ { "tool": "get_cursor_plan", "finding": "TABLE ACCESS FULL" } ]
        }
        """;

    private sealed class FakeEditor(string statementText) : IEditorContext
    {
        public string StatementText { get; set; } = statementText;

        public List<TextEdit> Proposed { get; } = [];

        public bool Accept { get; set; } = true;

        public EditorSnapshot? GetActiveDocument() => null;

        public SqlStatement? GetCurrentStatement() => new(StatementText, new TextRange(10, StatementText.Length), StatementKind.Query);

        public Task<bool> ProposeEditAsync(TextEdit edit, CancellationToken ct)
        {
            Proposed.Add(edit);
            return Task.FromResult(Accept);
        }
    }

    private sealed class FakeDriver(params AiEvent[] events) : IAiDriver
    {
        public List<AiRequest> Requests { get; } = [];

        /// <summary>실행이 시작될 때 부르는 훅(실행 도중 사용자가 고치는 일을 흉내 낸다).</summary>
        public Action? OnRun { get; set; }

        public AiDriverCapabilities Capabilities { get; } = new(false, true, true, false, true);

        public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            OnRun?.Invoke();
            foreach (var e in events)
            {
                ct.ThrowIfCancellationRequested();
                yield return e;
                await Task.Yield();
            }
        }
    }

    private static readonly AiContext Context = new("DEV-ERP", ConnectionEnvironment.Development, AiAccessLevel.Query, SqlId: "7h35uxf5uhmm1");

    private static FakeDriver Driver() => new(
        new AiStarted("sess-1"),
        new AiToolCall("c1", "get_cursor_plan", "sql_id=7h35"),
        new AiToolResult("c1", "get_cursor_plan", 1400, AiToolJudgement.Allowed),
        new AiFinal(null, JsonDocument.Parse(ResultJson).RootElement.Clone(), "sess-2"));

    [Fact]
    public async Task Rewrite_goes_to_the_editor_as_a_proposal_and_index_ddl_only_to_a_new_tab()
    {
        var editor = new FakeEditor(Original);
        var driver = Driver();
        var chat = new AiChatViewModel();
        var tabs = new List<(string Title, string Text)>();
        var flow = new AiTuningFlow(editor, driver, chat, (t, x) => tabs.Add((t, x)));

        await flow.StartAsync(new TuningStartChoice(true, true, "default"), Context);

        var edit = Assert.Single(editor.Proposed);
        Assert.Equal(new TextRange(10, Original.Length), edit.Range);
        Assert.DoesNotContain(';', edit.NewText); // 원문 범위에 ; 가 없으므로 맞춤
        Assert.Contains("NOT EXISTS", edit.Description, StringComparison.Ordinal);

        var tab = Assert.Single(tabs);
        Assert.Contains("CREATE INDEX ix_order_stat", tab.Text, StringComparison.Ordinal);
        Assert.StartsWith("-- AI 가 제안한 인덱스 후보", tab.Text, StringComparison.Ordinal);

        Assert.False(chat.IsRunning);
        Assert.Equal("sess-2", chat.ResumeId);
        Assert.Single(chat.Entries.OfType<ChatToolEntry>());
        Assert.NotNull(flow.LastResult);
    }

    [Fact]
    public async Task Request_carries_the_schema_statement_model_and_the_choice_flags()
    {
        var driver = Driver();
        var flow = new AiTuningFlow(new FakeEditor(Original), driver, new AiChatViewModel(), (_, _) => { });

        await flow.StartAsync(new TuningStartChoice(false, false, "sonnet"), Context);

        var request = Assert.Single(driver.Requests);
        Assert.Equal("sonnet", request.Model);
        Assert.NotNull(request.OutputSchema);
        Assert.Equal(Original, request.Context.StatementText);
        Assert.Equal("7h35uxf5uhmm1", request.Context.SqlId);
        Assert.Null(request.ResumeId);
        Assert.Contains("Do not benchmark", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("Do not suggest indexes", request.Prompt, StringComparison.Ordinal);
        Assert.Contains(Original, request.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Index_text_is_skipped_when_the_user_did_not_ask_for_it()
    {
        var tabs = new List<(string, string)>();
        var flow = new AiTuningFlow(new FakeEditor(Original), Driver(), new AiChatViewModel(), (t, x) => tabs.Add((t, x)));

        await flow.StartAsync(new TuningStartChoice(true, false, "default"), Context);

        Assert.Empty(tabs);
    }

    [Fact]
    public async Task Edited_statement_during_the_run_blocks_the_proposal()
    {
        var editor = new FakeEditor(Original);
        var chat = new AiChatViewModel();
        var driver = new FakeDriver(new AiFinal(null, JsonDocument.Parse(ResultJson).RootElement.Clone(), "s"))
        {
            OnRun = () => editor.StatementText = Original + " AND 1 = 1", // 실행 도중 사용자가 문장을 고침
        };
        var flow = new AiTuningFlow(editor, driver, chat, (_, _) => { });

        await flow.StartAsync(new TuningStartChoice(true, true, "default"), Context);

        Assert.Empty(editor.Proposed);
        Assert.Contains(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.Contains("문장이 바뀌어", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Follow_up_resumes_with_the_previous_resume_id()
    {
        var driver = Driver();
        var chat = new AiChatViewModel();
        var flow = new AiTuningFlow(new FakeEditor(Original), driver, chat, (_, _) => { });
        await flow.StartAsync(new TuningStartChoice(true, true, "default"), Context);

        await flow.FollowUpAsync("인덱스는 만들지 말고 힌트로만");

        Assert.Equal(2, driver.Requests.Count);
        Assert.Equal("sess-2", driver.Requests[1].ResumeId);
        Assert.Equal("인덱스는 만들지 말고 힌트로만", driver.Requests[1].Prompt);
    }

    [Fact]
    public async Task No_statement_at_the_caret_only_leaves_a_note()
    {
        var chat = new AiChatViewModel();
        var driver = Driver();
        var flow = new AiTuningFlow(new NoStatementEditor(), driver, chat, (_, _) => { });

        await flow.StartAsync(new TuningStartChoice(true, true, "default"), Context);

        Assert.Empty(driver.Requests);
        Assert.Contains("문장을 찾지 못했습니다", Assert.Single(chat.Entries.OfType<ChatAssistantEntry>()).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_while_a_run_is_in_progress_is_refused_without_touching_the_running_one()
    {
        var chat = new AiChatViewModel();
        var driver = Driver();
        var flow = new AiTuningFlow(new FakeEditor(Original), driver, chat, (_, _) => { });
        var inner = Task.CompletedTask;
        driver.OnRun = () => inner = flow.StartAsync(new TuningStartChoice(true, true, "default"), Context); // 실행이 도는 동안 다시 시작

        await flow.StartAsync(new TuningStartChoice(true, true, "default"), Context);
        await inner;

        Assert.Single(driver.Requests);
        Assert.Contains(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.Contains("진행 중", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Follow_up_rebuilds_the_context_from_the_current_connection_settings()
    {
        var driver = Driver();
        var seen = new List<AiAccessLevel>();
        var flow = new AiTuningFlow(new FakeEditor(Original), driver, new AiChatViewModel(), (_, _) => { }, sessionFactory: null,
            refreshContext: (old, _) => Task.FromResult<AiContext?>(old with { AccessLevel = AiAccessLevel.Metadata }));

        await flow.StartAsync(new TuningStartChoice(true, true, "default"), Context);
        await flow.FollowUpAsync("다른 방법도 보여줘");

        seen.AddRange(driver.Requests.Select(r => r.Context.AccessLevel));
        Assert.Equal([AiAccessLevel.Query, AiAccessLevel.Metadata], seen);
    }

    [Fact]
    public async Task Follow_up_is_refused_when_the_connection_no_longer_allows_ai()
    {
        var chat = new AiChatViewModel();
        var driver = Driver();
        var flow = new AiTuningFlow(new FakeEditor(Original), driver, chat, (_, _) => { }, sessionFactory: null,
            refreshContext: (old, _) => Task.FromResult<AiContext?>(old with { AccessLevel = AiAccessLevel.Off }));

        await flow.StartAsync(new TuningStartChoice(true, true, "default"), Context);
        await flow.FollowUpAsync("계속");

        Assert.Single(driver.Requests);
        Assert.Contains(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.Contains("허용 수준이 바뀌어", StringComparison.Ordinal));
    }

    private sealed class NoStatementEditor : IEditorContext
    {
        public EditorSnapshot? GetActiveDocument() => null;

        public SqlStatement? GetCurrentStatement() => null;

        public Task<bool> ProposeEditAsync(TextEdit edit, CancellationToken ct) => Task.FromResult(false);
    }
}
