using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Tuning;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
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

        /// <summary>받아들이면 문장 글을 제안 글로 바꾼다(실제 편집기처럼).</summary>
        public bool ApplyOnAccept { get; set; }

        public EditorSnapshot? GetActiveDocument() => null;

        public SqlStatement? GetCurrentStatement() => new(StatementText, new TextRange(10, StatementText.Length), StatementKind.Query);

        public Task<bool> ProposeEditAsync(TextEdit edit, CancellationToken ct)
        {
            Proposed.Add(edit);
            if (Accept && ApplyOnAccept)
            {
                StatementText = edit.NewText;
            }

            return Task.FromResult(Accept);
        }

        /// <summary>비교 창 흉내: 제안을 기록하고 받아들이면 제안 글을, 거절하면 null 을 돌려준다(편집기 글은 바뀌지 않는다).</summary>
        public Task<string?> Preview(TextEdit edit, string before, CancellationToken ct)
        {
            Proposed.Add(edit);
            return Task.FromResult(Accept ? edit.NewText : null);
        }
    }

    private static Task<string?> PreviewAccept(TextEdit edit, string before, CancellationToken ct) => Task.FromResult<string?>(edit.NewText);

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
        var flow = new AiTuningFlow(editor, driver, chat, (t, x, _) => tabs.Add((t, x)), previewEdit: editor.Preview);

        await flow.StartAsync(new TuningStartChoice(true, true, "default"), Context);

        var edit = Assert.Single(editor.Proposed);
        Assert.Equal(new TextRange(10, Original.Length), edit.Range);
        Assert.DoesNotContain(';', edit.NewText); // 원문 범위에 ; 가 없으므로 맞춤
        Assert.Contains("NOT EXISTS", edit.Description, StringComparison.Ordinal);

        Assert.Equal(2, tabs.Count);
        Assert.Equal("SQL (튜닝)", tabs[0].Title); // 제안은 새 (튜닝) 탭으로, 인덱스 후보는 그 뒤 탭으로
        Assert.Contains("SELECT o.* FROM tb_order o", tabs[0].Text, StringComparison.Ordinal);
        var tab = tabs[1];
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
        var flow = new AiTuningFlow(new FakeEditor(Original), driver, new AiChatViewModel(), (_, _, _) => { }, previewEdit: PreviewAccept);

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
        Assert.False(request.Context.AllowRiskyFunctions);
    }

    [Fact]
    public void Prompt_with_index_ddl_carries_the_reuse_rules()
    {
        var prompt = AiTuningFlow.BuildPrompt(new TuningStartChoice(true, true, "default"), Original);

        Assert.Contains("check the existing indexes of that table", prompt, StringComparison.Ordinal); // (a)
        Assert.Contains("action=modify", prompt, StringComparison.Ordinal); // (b)
        Assert.Contains("leading column", prompt, StringComparison.Ordinal);
        Assert.Contains("DROP INDEX then CREATE INDEX", prompt, StringComparison.Ordinal);
        Assert.Contains("already covers", prompt, StringComparison.Ordinal); // (c)
        Assert.Contains("hint or a rewrite", prompt, StringComparison.Ordinal);
        Assert.Contains("condition and join columns only, at most 4", prompt, StringComparison.Ordinal); // (d)
        Assert.Contains("benchmark evidence", prompt, StringComparison.Ordinal);
        Assert.Contains("which other SQL that uses the existing index", prompt, StringComparison.Ordinal); // (e)
        Assert.DoesNotContain("may be suggested as text only", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Covering_index_is_allowed_only_when_the_benchmark_is_on()
    {
        var on = AiTuningFlow.BuildPrompt(new TuningStartChoice(true, true, "default"), Original);
        var off = AiTuningFlow.BuildPrompt(new TuningStartChoice(false, true, "default"), Original);

        Assert.Contains("allowed only with benchmark evidence", on, StringComparison.Ordinal);
        Assert.Contains("do not suggest one", off, StringComparison.Ordinal);
        Assert.DoesNotContain("Index rules", AiTuningFlow.BuildPrompt(new TuningStartChoice(true, false, "default"), Original), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_risky_functions_reaches_the_gate_context_for_the_start_and_every_follow_up()
    {
        var driver = Driver();
        var chat = new AiChatViewModel();
        var flow = new AiTuningFlow(new FakeEditor(Original), driver, chat, (_, _, _) => { }, previewEdit: PreviewAccept);

        await flow.StartAsync(new TuningStartChoice(true, false, "default", RunRiskyFunctions: true), Context);
        await flow.FollowUpAsync("인덱스는 빼고");

        Assert.All(driver.Requests, r => Assert.True(r.Context.AllowRiskyFunctions));
        Assert.Equal(2, driver.Requests.Count);
        Assert.Contains(chat.Entries.OfType<ChatUserEntry>(), e => e.Text.Contains("위험 함수도 실행", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Index_text_is_skipped_when_the_user_did_not_ask_for_it()
    {
        var tabs = new List<(string, string)>();
        var flow = new AiTuningFlow(new FakeEditor(Original), Driver(), new AiChatViewModel(), (t, x, _) => tabs.Add((t, x)), previewEdit: PreviewAccept);

        await flow.StartAsync(new TuningStartChoice(true, false, "default"), Context);

        Assert.Equal("SQL (튜닝)", Assert.Single(tabs).Item1); // 인덱스 탭은 없고 제안을 받아들인 (튜닝) 탭만 있다
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
        var flow = new AiTuningFlow(editor, driver, chat, (_, _, _) => { }, previewEdit: editor.Preview);

        await flow.StartAsync(new TuningStartChoice(true, true, "default"), Context);

        Assert.Empty(editor.Proposed);
        Assert.Contains(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.Contains("문장이 바뀌어", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_rejected_proposal_stays_as_a_card_that_reopens_the_same_comparison_and_turns_applied_when_accepted()
    {
        var editor = new FakeEditor(Original) { Accept = false };
        var chat = new AiChatViewModel();
        var flow = new AiTuningFlow(editor, Driver(), chat, (_, _, _) => { }, previewEdit: editor.Preview);

        await flow.StartAsync(new TuningStartChoice(true, false, "default"), Context);

        var card = Assert.Single(chat.Entries.OfType<ChatProposalEntry>());
        Assert.False(card.Applied);
        Assert.Contains("NOT EXISTS", card.Label, StringComparison.Ordinal);
        Assert.Equal("−88%", card.Change); // 벤치마크 「1.82 s → 0.21 s」 의 변화율
        Assert.DoesNotContain(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.StartsWith('[')); // 「[이름] 근거 (수치)」 꼬리표 줄은 카드가 대신한다
        Assert.True(chat.ReviewProposalCommand.CanExecute(card));

        editor.Accept = true;
        await chat.ReviewProposalCommand.ExecuteAsync(card);

        Assert.Equal(2, editor.Proposed.Count);
        Assert.Equal(editor.Proposed[0], editor.Proposed[1]); // 같은 범위·같은 글(시작 탭 기준)
        var applied = Assert.Single(chat.Entries.OfType<ChatProposalEntry>());
        Assert.True(applied.Applied);
        Assert.False(applied.CanReview);
        Assert.False(chat.ReviewProposalCommand.CanExecute(applied));
    }

    [Fact]
    public async Task An_accepted_proposal_card_says_applied_and_a_changed_statement_blocks_reopening_without_a_card_change()
    {
        var editor = new FakeEditor(Original);
        var chat = new AiChatViewModel();
        var flow = new AiTuningFlow(editor, Driver(), chat, (_, _, _) => { }, previewEdit: editor.Preview);
        await flow.StartAsync(new TuningStartChoice(true, false, "default"), Context);
        Assert.True(Assert.Single(chat.Entries.OfType<ChatProposalEntry>()).Applied);

        var rejected = new FakeEditor(Original) { Accept = false };
        var chat2 = new AiChatViewModel();
        await new AiTuningFlow(rejected, Driver(), chat2, (_, _, _) => { }, previewEdit: rejected.Preview).StartAsync(new TuningStartChoice(true, false, "default"), Context);
        var card = Assert.Single(chat2.Entries.OfType<ChatProposalEntry>());
        rejected.StatementText = Original + " AND 1 = 1"; // 닫은 뒤 사용자가 문장을 고침

        await chat2.ReviewProposalCommand.ExecuteAsync(card);

        Assert.Single(rejected.Proposed); // 다시 열지 않았다
        Assert.False(Assert.Single(chat2.Entries.OfType<ChatProposalEntry>()).Applied);
        Assert.Contains(chat2.Entries.OfType<ChatAssistantEntry>(), e => e.Text.Contains("문장이 바뀌어", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_applied_proposal_card_shows_the_original_and_the_applied_text_without_touching_the_editor()
    {
        var editor = new FakeEditor(Original) { ApplyOnAccept = true };
        var chat = new AiChatViewModel();
        var shown = new List<(string Title, string Before, string After)>();
        var flow = new AiTuningFlow(editor, Driver(), chat, (_, _, _) => { },
            showComparison: (title, before, after) => { shown.Add((title, before, after)); return Task.CompletedTask; }, previewEdit: editor.Preview);
        await flow.StartAsync(new TuningStartChoice(true, false, "default"), Context);
        var card = Assert.Single(chat.Entries.OfType<ChatProposalEntry>());
        Assert.True(card.CanView);
        Assert.False(card.CanReview);

        await chat.ViewProposalCommand.ExecuteAsync(card);

        var (title, before, after) = Assert.Single(shown);
        Assert.Equal(Original, before);
        Assert.Equal(Assert.Single(editor.Proposed).NewText, after);
        Assert.Contains("적용함", title, StringComparison.Ordinal);
        Assert.Single(editor.Proposed); // 보기만 했다
    }

    [Fact]
    public void Viewing_is_offered_only_for_applied_cards_with_a_viewer()
    {
        var chat = new AiChatViewModel();
        chat.AddProposal("a", "x", applied: false, () => Task.FromResult<bool?>(false), view: () => Task.CompletedTask);
        chat.AddProposal("b", "x", applied: true, () => Task.FromResult<bool?>(true));
        var cards = chat.Entries.OfType<ChatProposalEntry>().ToList();

        Assert.False(chat.ViewProposalCommand.CanExecute(cards[0]));
        Assert.False(chat.ViewProposalCommand.CanExecute(cards[1]));
    }

    [Fact]
    public void Reviewing_a_proposal_is_not_offered_while_a_run_is_in_progress()
    {
        var chat = new AiChatViewModel();
        chat.AddProposal("r", "x", applied: false, () => Task.FromResult<bool?>(false));
        var card = Assert.Single(chat.Entries.OfType<ChatProposalEntry>());

        chat.BeginTurn("이어 말하기");
        Assert.False(chat.ReviewProposalCommand.CanExecute(card));
        chat.EndTurn();
        Assert.True(chat.ReviewProposalCommand.CanExecute(card));
    }

    [Fact]
    public async Task Follow_up_resumes_with_the_previous_resume_id()
    {
        var driver = Driver();
        var chat = new AiChatViewModel();
        var flow = new AiTuningFlow(new FakeEditor(Original), driver, chat, (_, _, _) => { }, previewEdit: PreviewAccept);
        await flow.StartAsync(new TuningStartChoice(true, true, "default"), Context);

        await flow.FollowUpAsync("인덱스는 만들지 말고 힌트로만");

        Assert.Equal(2, driver.Requests.Count);
        Assert.Equal("sess-2", driver.Requests[1].ResumeId);
        Assert.StartsWith("인덱스는 만들지 말고 힌트로만", driver.Requests[1].Prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("xhigh", "xhigh")]
    [InlineData("default", null)]
    public async Task Chosen_effort_goes_into_the_first_request_and_every_follow_up(string effortId, string? expected)
    {
        var driver = Driver();
        var flow = new AiTuningFlow(new FakeEditor(Original), driver, new AiChatViewModel(), (_, _, _) => { }, previewEdit: PreviewAccept);
        await flow.StartAsync(new TuningStartChoice(true, true, "default", EffortId: effortId), Context);

        await flow.FollowUpAsync("다른 방법도");

        Assert.Equal(2, driver.Requests.Count);
        Assert.All(driver.Requests, r => Assert.Equal(expected, r.Effort));
    }

    [Fact]
    public async Task Response_style_rules_go_into_the_first_request_and_every_follow_up()
    {
        var driver = Driver();
        var flow = new AiTuningFlow(new FakeEditor(Original), driver, new AiChatViewModel(), (_, _, _) => { }, previewEdit: PreviewAccept);
        await flow.StartAsync(new TuningStartChoice(true, true, "default"), Context);

        await flow.FollowUpAsync("다른 방법도");

        Assert.All(driver.Requests, r =>
        {
            Assert.Contains(AiTuningFlow.ResponseStyle, r.Prompt, StringComparison.Ordinal);
            Assert.Contains("conclusion as one bold line", r.Prompt, StringComparison.Ordinal);
            Assert.Contains("지표 | 원문 | 제안", r.Prompt, StringComparison.Ordinal);
            Assert.Contains("3 to 5 bullets, each about 40 characters", r.Prompt, StringComparison.Ordinal);
            Assert.Contains("## 뺀 후보", r.Prompt, StringComparison.Ordinal);
            Assert.Contains("One line starting with >", r.Prompt, StringComparison.Ordinal);
            Assert.Contains("No [name] tags, no long remarks in parentheses", r.Prompt, StringComparison.Ordinal);
            Assert.Contains("Never write tool or function names (get_*, benchmark_queries, explain_plan and so on)", r.Prompt, StringComparison.Ordinal); // cf-2: 사람 말로
            Assert.Contains("벤치마크, 실행 계획, 튜닝 정보", r.Prompt, StringComparison.Ordinal);
            Assert.Contains("경과, 논리 읽기, 결과 행, 계획 해시", r.Prompt, StringComparison.Ordinal); // 지표 이름도 한국어
        });
    }

    [Fact]
    public async Task No_statement_at_the_caret_only_leaves_a_note()
    {
        var chat = new AiChatViewModel();
        var driver = Driver();
        var flow = new AiTuningFlow(new NoStatementEditor(), driver, chat, (_, _, _) => { });

        await flow.StartAsync(new TuningStartChoice(true, true, "default"), Context);

        Assert.Empty(driver.Requests);
        Assert.Contains("문장을 찾지 못했습니다", Assert.Single(chat.Entries.OfType<ChatAssistantEntry>()).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_while_a_run_is_in_progress_is_refused_without_touching_the_running_one()
    {
        var chat = new AiChatViewModel();
        var driver = Driver();
        var flow = new AiTuningFlow(new FakeEditor(Original), driver, chat, (_, _, _) => { }, previewEdit: PreviewAccept);
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
        var flow = new AiTuningFlow(new FakeEditor(Original), driver, new AiChatViewModel(), (_, _, _) => { }, sessionFactory: null,
            refreshContext: (old, _) => Task.FromResult<AiContext?>(old with { AccessLevel = AiAccessLevel.Metadata }), previewEdit: PreviewAccept);

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
        var flow = new AiTuningFlow(new FakeEditor(Original), driver, chat, (_, _, _) => { }, sessionFactory: null,
            refreshContext: (old, _) => Task.FromResult<AiContext?>(old with { AccessLevel = AiAccessLevel.Off }), previewEdit: PreviewAccept);

        await flow.StartAsync(new TuningStartChoice(true, true, "default"), Context);
        await flow.FollowUpAsync("계속");

        Assert.Single(driver.Requests);
        Assert.Contains(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.Contains("허용 수준이 바뀌어", StringComparison.Ordinal));
    }

    /// <summary>작은따옴표 문자열과 주석(힌트 제외)만 가리는 시험용 가림 구현(실제 구현은 Language 의 렉서 기반).</summary>
    private sealed class FakeMasker : ISqlLiteralMasker
    {
        public bool FailMask { get; set; }

        public LiteralVault CreateVault(string statementText) => new();

        public SqlMaskResult Mask(string sql, LiteralVault vault) => FailMask
            ? SqlMaskResult.Failed
            : SqlMaskResult.Ok(MaskText(sql, vault));

        private static string MaskText(string sql, LiteralVault vault) =>
            Regex.Replace(sql, @"'[^']*'|/\*(?!\+)[\s\S]*?\*/|--[^\n]*", m => m.Value[0] == '\'' ? vault.Add(m.Value) : vault.AddComment(m.Value));

        public SqlRestoreResult Restore(string sql, LiteralVault vault)
        {
            if (vault.TryGetStatement(sql, out var original))
            {
                return SqlRestoreResult.Ok(original);
            }

            string? unknown = null;
            var text = Regex.Replace(sql, @"/\*\s*c\d+\s*\*/|--\s*c\d+", m => vault.TryGetComment(m.Value, out var comment) ? comment : m.Value);
            text = Regex.Replace(text, @":v\d+", m =>
            {
                if (vault.TryGetRaw(m.Value, out var raw))
                {
                    return raw;
                }

                unknown ??= m.Value;
                return m.Value;
            });
            return unknown is null ? SqlRestoreResult.Ok(text) : SqlRestoreResult.Unknown(unknown);
        }

        public string MaskPredicate(string predicate, LiteralVault vault) => MaskText(predicate, vault);
    }

    private static AiTuningFlow MaskedFlow(FakeEditor editor, FakeDriver driver, AiChatViewModel chat, List<(string Title, string Text)> tabs, ISqlLiteralMasker? masker, List<LiteralVault?>? vaults = null) =>
        new(editor, driver, chat, (t, x, _) => tabs.Add((t, x)),
            sessionFactory: (_, vault) =>
            {
                vaults?.Add(vault);
                return null;
            },
            masker: masker,
            maskRequired: _ => true,
            previewEdit: editor.Preview);

    [Fact]
    public async Task Masked_start_sends_the_masked_statement_and_the_placeholder_note_and_hands_the_vault_to_the_session()
    {
        var driver = Driver();
        var vaults = new List<LiteralVault?>();
        var flow = MaskedFlow(new FakeEditor(Original), driver, new AiChatViewModel(), [], new FakeMasker(), vaults);

        await flow.StartAsync(new TuningStartChoice(true, true, "default"), Context);

        var request = Assert.Single(driver.Requests);
        Assert.DoesNotContain("'10'", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("stat_cd = :v1", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("placeholders such as :v1", request.Prompt, StringComparison.Ordinal);
        Assert.Equal(Original, request.Context.StatementText); // 원문은 문맥에 그대로(LLM 으로 가지 않는다)
        Assert.NotNull(Assert.Single(vaults));
    }

    [Fact]
    public async Task Masked_start_is_refused_without_a_masker_or_when_masking_fails()
    {
        var driver = Driver();
        var chat = new AiChatViewModel();
        await MaskedFlow(new FakeEditor(Original), driver, chat, [], masker: null).StartAsync(new TuningStartChoice(true, true, "default"), Context);
        await MaskedFlow(new FakeEditor(Original), driver, chat, [], new FakeMasker { FailMask = true }).StartAsync(new TuningStartChoice(true, true, "default"), Context);

        Assert.Empty(driver.Requests);
        var texts = chat.Entries.OfType<ChatAssistantEntry>().Select(e => e.Text).ToList();
        Assert.Contains(AiTuningFlow.MaskerMissingText, texts);
        Assert.Contains(AiTuningFlow.MaskFailedText, texts);
    }

    [Fact]
    public async Task Masked_result_is_restored_before_it_reaches_the_editor_and_the_index_tab()
    {
        const string result = """
            { "summary": "s", "rewrites": [ { "label": "r", "sql": "SELECT o.* FROM tb_order o WHERE o.stat_cd = :v1;", "rationale": "x" } ],
              "index_suggestions": [ { "table": "TB_ORDER", "columns": ["STAT_CD"], "ddl": "CREATE INDEX ix ON tb_order (stat_cd) WHERE stat_cd = :v1;", "rationale": "y" } ], "evidence": [] }
            """;
        var editor = new FakeEditor(Original);
        var tabs = new List<(string Title, string Text)>();
        var driver = new FakeDriver(new AiFinal(null, JsonDocument.Parse(result).RootElement.Clone(), "s"));
        var chat = new AiChatViewModel();
        var flow = MaskedFlow(editor, driver, chat, tabs, new FakeMasker());

        await flow.StartAsync(new TuningStartChoice(true, true, "default"), Context);

        var edit = Assert.Single(editor.Proposed);
        Assert.Contains("o.stat_cd = '10'", edit.NewText, StringComparison.Ordinal);
        Assert.DoesNotContain(":v1", edit.NewText, StringComparison.Ordinal);
        Assert.Equal(2, tabs.Count);
        Assert.Contains("o.stat_cd = '10'", tabs[0].Text, StringComparison.Ordinal);
        var tab = tabs[1].Text;
        Assert.Contains("WHERE stat_cd = '10';", tab, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE INDEX ix ON tb_order (stat_cd) WHERE stat_cd = :v1", tab, StringComparison.Ordinal);
        Assert.Contains("자리표시", tab, StringComparison.Ordinal); // 설명 줄은 되돌리지 않으므로 탭 머리에 안내
        Assert.Single(chat.Entries.OfType<ChatProposalEntry>());
        AssertChatHidesOriginal(chat);
    }

    /// <summary>대화 창(제목·사용자 줄·안내 줄) 어디에도 원래 리터럴이 없다.</summary>
    private static void AssertChatHidesOriginal(AiChatViewModel chat)
    {
        Assert.DoesNotContain("'10'", chat.Title, StringComparison.Ordinal);
        Assert.DoesNotContain(chat.Entries.OfType<ChatUserEntry>(), e => e.Text.Contains("'10'", StringComparison.Ordinal));
        Assert.DoesNotContain(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.Contains("'10'", StringComparison.Ordinal));
        Assert.DoesNotContain(chat.Entries.OfType<ChatProposalEntry>(), e => (e.Headline + e.Summary).Contains("'10'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_placeholder_in_a_result_is_not_put_in_the_editor_and_the_chat_says_so()
    {
        const string result = """
            { "summary": "s", "rewrites": [ { "label": "r", "sql": "SELECT 1 FROM dual WHERE x = :v9", "rationale": "x" } ],
              "index_suggestions": [ { "table": "T", "columns": ["C"], "ddl": "CREATE INDEX ix ON t (c) WHERE c = :v7", "rationale": "y" } ], "evidence": [] }
            """;
        var editor = new FakeEditor(Original);
        var tabs = new List<(string Title, string Text)>();
        var chat = new AiChatViewModel();
        var flow = MaskedFlow(editor, new FakeDriver(new AiFinal(null, JsonDocument.Parse(result).RootElement.Clone(), "s")), chat, tabs, new FakeMasker());

        await flow.StartAsync(new TuningStartChoice(true, true, "default"), Context);

        Assert.Empty(editor.Proposed);
        Assert.Empty(tabs);
        var texts = chat.Entries.OfType<ChatAssistantEntry>().Select(e => e.Text).ToList();
        Assert.Contains(texts, t => t.Contains(":v9", StringComparison.Ordinal) && t.Contains("literal_unknown", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains(":v7", StringComparison.Ordinal) && t.Contains("literal_unknown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Masked_follow_up_keeps_the_user_text_and_adds_only_the_placeholder_note()
    {
        var driver = Driver();
        var chat = new AiChatViewModel();
        var flow = MaskedFlow(new FakeEditor(Original), driver, chat, [], new FakeMasker());
        await flow.StartAsync(new TuningStartChoice(true, true, "default"), Context);

        await flow.FollowUpAsync("stat_cd 가 '20' 이면?");

        var prompt = driver.Requests[1].Prompt;
        Assert.StartsWith("stat_cd 가 '20' 이면?", prompt, StringComparison.Ordinal); // 사용자 글은 가리지 않는다
        Assert.Contains("placeholders such as :v1", prompt, StringComparison.Ordinal);
        Assert.Contains(chat.Entries.OfType<ChatUserEntry>(), e => e.Text == "stat_cd 가 '20' 이면?"); // 대화 창 줄에는 안내문이 없다
    }

    [Fact]
    public async Task Unmasked_flow_stays_as_before_without_a_masker()
    {
        var driver = Driver();
        var flow = new AiTuningFlow(new FakeEditor(Original), driver, new AiChatViewModel(), (_, _, _) => { }, maskRequired: _ => false, previewEdit: PreviewAccept);

        await flow.StartAsync(new TuningStartChoice(true, true, "default"), Context);

        Assert.Contains(Original, Assert.Single(driver.Requests).Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("placeholders", driver.Requests[0].Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Follow_up_hands_the_same_vault_to_the_new_session_and_a_new_unmasked_start_drops_it()
    {
        var driver = Driver();
        var vaults = new List<LiteralVault?>();
        var mask = true;
        var flow = new AiTuningFlow(new FakeEditor(Original), driver, new AiChatViewModel(), (_, _, _) => { },
            sessionFactory: (_, vault) =>
            {
                vaults.Add(vault);
                return null;
            },
            masker: new FakeMasker(),
            maskRequired: _ => mask, previewEdit: PreviewAccept);

        await flow.StartAsync(new TuningStartChoice(true, true, "default"), Context);
        await flow.FollowUpAsync("계속");
        mask = false;
        await flow.StartAsync(new TuningStartChoice(true, true, "default"), Context);

        Assert.NotNull(vaults[0]);
        Assert.Same(vaults[0], vaults[1]); // 이어 말하기 = 같은 대화의 같은 사전
        Assert.Null(vaults[2]); // 가림 없는 새 대화에는 앞 사전이 섞이지 않는다
    }

    [Fact]
    public async Task Follow_up_is_refused_when_the_connection_became_production_after_an_unmasked_start()
    {
        var driver = Driver();
        var chat = new AiChatViewModel();
        var production = false;
        var flow = new AiTuningFlow(new FakeEditor(Original), driver, chat, (_, _, _) => { }, sessionFactory: null,
            refreshContext: (old, _) => Task.FromResult<AiContext?>(old with { Environment = ConnectionEnvironment.Production }),
            masker: new FakeMasker(),
            maskRequired: ctx => production || ctx.Environment == ConnectionEnvironment.Production, previewEdit: PreviewAccept);

        await flow.StartAsync(new TuningStartChoice(true, true, "default"), Context);
        await flow.FollowUpAsync("계속");

        Assert.Single(driver.Requests); // 이어 말하기 요청은 가려진 적 없는 앞 글 위에 올라가므로 보내지 않는다
        Assert.Contains(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.Contains("다시 시작하세요", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refused_starts_do_not_advance_the_generation_but_a_confirmed_one_does()
    {
        var chat = new AiChatViewModel();
        var flow = MaskedFlow(new FakeEditor(Original), Driver(), chat, [], new FakeMasker { FailMask = true });
        await flow.StartAsync(new TuningStartChoice(true, true, "default"), Context);
        Assert.Equal(0, flow.Generation); // 가림 불가로 거부

        var noStatement = new AiTuningFlow(new NoStatementEditor(), Driver(), chat, (_, _, _) => { });
        await noStatement.StartAsync(new TuningStartChoice(true, true, "default"), Context);
        Assert.Equal(0, noStatement.Generation); // 문장 없음으로 거부

        var ok = new AiTuningFlow(new FakeEditor(Original), Driver(), chat, (_, _, _) => { }, previewEdit: PreviewAccept);
        await ok.StartAsync(new TuningStartChoice(true, true, "default"), Context);
        Assert.Equal(1, ok.Generation);
    }

    [Fact]
    public async Task Masked_conversation_shows_no_original_literal_in_the_chat()
    {
        var chat = new AiChatViewModel();
        var flow = MaskedFlow(new FakeEditor(Original), Driver(), chat, [], new FakeMasker());

        await flow.StartAsync(new TuningStartChoice(true, true, "default"), Context);

        AssertChatHidesOriginal(chat);
    }

    private sealed class NoStatementEditor : IEditorContext
    {
        public EditorSnapshot? GetActiveDocument() => null;

        public SqlStatement? GetCurrentStatement() => null;

        public Task<bool> ProposeEditAsync(TextEdit edit, CancellationToken ct) => Task.FromResult(false);
    }

    private const string Commented = "SELECT * /* 주문 상태 */ FROM tb_order -- 운영 점검\nWHERE stat_cd = '10'";

    private static FakeDriver RewriteDriver(string sql) => new(new AiFinal(
        null,
        JsonDocument.Parse($$"""{ "summary": "s", "rewrites": [ { "label": "r", "sql": {{JsonSerializer.Serialize(sql)}}, "rationale": "x" } ], "index_suggestions": [], "evidence": [] }""").RootElement.Clone(),
        "s"));

    private static List<string> AssistantTexts(AiChatViewModel chat) => [.. chat.Entries.OfType<ChatAssistantEntry>().Select(e => e.Text)];

    [Fact]
    public async Task Masked_start_sends_comment_placeholders_and_the_rewrite_gets_the_original_comments_back()
    {
        var editor = new FakeEditor(Commented);
        var driver = RewriteDriver("SELECT /*c1*/ o.* FROM tb_order o --c2\nWHERE o.stat_cd = :v1");
        var chat = new AiChatViewModel();

        await MaskedFlow(editor, driver, chat, [], new FakeMasker()).StartAsync(new TuningStartChoice(true, true, "default"), Context);

        var prompt = Assert.Single(driver.Requests).Prompt;
        Assert.Contains("SELECT * /*c1*/ FROM tb_order --c2", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("주문 상태", prompt, StringComparison.Ordinal); // 주석 본문은 보내지 않는다
        Assert.DoesNotContain("운영 점검", prompt, StringComparison.Ordinal);
        Assert.Contains("such as /*c1*/ and --c1", prompt, StringComparison.Ordinal);
        Assert.Contains(AiTuningFlow.KeepCommentsRule, prompt, StringComparison.Ordinal);
        Assert.Equal("SELECT /* 주문 상태 */ o.* FROM tb_order o -- 운영 점검\nWHERE o.stat_cd = '10'", Assert.Single(editor.Proposed).NewText);
        Assert.DoesNotContain(AssistantTexts(chat), t => t.Contains("빠짐", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Masked_rewrite_that_drops_a_comment_placeholder_is_still_proposed_and_the_chat_counts_the_missing_comment()
    {
        var editor = new FakeEditor(Commented);
        var chat = new AiChatViewModel();

        // /*c2*/ 는 줄 주석(--c2) 자리가 아니므로 되돌리지 않는다: 원래 주석 둘 다 빠진 것으로 센다.
        await MaskedFlow(editor, RewriteDriver("SELECT o.* FROM tb_order o /*c2*/ WHERE o.stat_cd = :v1"), chat, [], new FakeMasker())
            .StartAsync(new TuningStartChoice(true, true, "default"), Context);

        Assert.Equal("SELECT o.* FROM tb_order o /*c2*/ WHERE o.stat_cd = '10'", Assert.Single(editor.Proposed).NewText);
        Assert.Contains(AssistantTexts(chat), t => t.StartsWith("주석 2개 빠짐", StringComparison.Ordinal));
        Assert.DoesNotContain(AssistantTexts(chat), t => t.Contains("주문 상태", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unmasked_rewrite_that_drops_an_original_comment_is_still_proposed_and_the_chat_counts_it()
    {
        var kept = new FakeEditor(Commented);
        var dropped = new FakeEditor(Commented);
        var keptChat = new AiChatViewModel();
        var droppedChat = new AiChatViewModel();
        var keptDriver = RewriteDriver("SELECT /* 주문 상태 */ o.* FROM tb_order o -- 운영 점검\nWHERE o.stat_cd = '10'");

        await new AiTuningFlow(kept, keptDriver, keptChat, (_, _, _) => { }, masker: new FakeMasker(), maskRequired: _ => false, previewEdit: kept.Preview)
            .StartAsync(new TuningStartChoice(true, true, "default"), Context);
        await new AiTuningFlow(dropped, RewriteDriver("SELECT /* 주문 상태 */ o.* FROM tb_order o WHERE o.stat_cd = '10'"), droppedChat, (_, _, _) => { }, masker: new FakeMasker(), maskRequired: _ => false, previewEdit: dropped.Preview)
            .StartAsync(new TuningStartChoice(true, true, "default"), Context);

        Assert.Contains(AiTuningFlow.KeepCommentsRule, Assert.Single(keptDriver.Requests).Prompt, StringComparison.Ordinal);
        Assert.Single(kept.Proposed);
        Assert.DoesNotContain(AssistantTexts(keptChat), t => t.Contains("빠짐", StringComparison.Ordinal));
        Assert.Equal("SELECT /* 주문 상태 */ o.* FROM tb_order o WHERE o.stat_cd = '10'", Assert.Single(dropped.Proposed).NewText);
        Assert.Contains(AssistantTexts(droppedChat), t => t.StartsWith("주석 1개 빠짐", StringComparison.Ordinal));
    }
}
