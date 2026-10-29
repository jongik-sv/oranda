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
        Assert.False(request.Context.AllowRiskyFunctions);
    }

    [Fact]
    public async Task Run_risky_functions_reaches_the_gate_context_for_the_start_and_every_follow_up()
    {
        var driver = Driver();
        var chat = new AiChatViewModel();
        var flow = new AiTuningFlow(new FakeEditor(Original), driver, chat, (_, _) => { });

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
    public async Task A_rejected_proposal_stays_as_a_card_that_reopens_the_same_comparison_and_turns_applied_when_accepted()
    {
        var editor = new FakeEditor(Original) { Accept = false };
        var chat = new AiChatViewModel();
        var flow = new AiTuningFlow(editor, Driver(), chat, (_, _) => { });

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
        var flow = new AiTuningFlow(editor, Driver(), chat, (_, _) => { });
        await flow.StartAsync(new TuningStartChoice(true, false, "default"), Context);
        Assert.True(Assert.Single(chat.Entries.OfType<ChatProposalEntry>()).Applied);

        var rejected = new FakeEditor(Original) { Accept = false };
        var chat2 = new AiChatViewModel();
        await new AiTuningFlow(rejected, Driver(), chat2, (_, _) => { }).StartAsync(new TuningStartChoice(true, false, "default"), Context);
        var card = Assert.Single(chat2.Entries.OfType<ChatProposalEntry>());
        rejected.StatementText = Original + " AND 1 = 1"; // 닫은 뒤 사용자가 문장을 고침

        await chat2.ReviewProposalCommand.ExecuteAsync(card);

        Assert.Single(rejected.Proposed); // 다시 열지 않았다
        Assert.False(Assert.Single(chat2.Entries.OfType<ChatProposalEntry>()).Applied);
        Assert.Contains(chat2.Entries.OfType<ChatAssistantEntry>(), e => e.Text.Contains("문장이 바뀌어", StringComparison.Ordinal));
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
        var flow = new AiTuningFlow(new FakeEditor(Original), driver, chat, (_, _) => { });
        await flow.StartAsync(new TuningStartChoice(true, true, "default"), Context);

        await flow.FollowUpAsync("인덱스는 만들지 말고 힌트로만");

        Assert.Equal(2, driver.Requests.Count);
        Assert.Equal("sess-2", driver.Requests[1].ResumeId);
        Assert.StartsWith("인덱스는 만들지 말고 힌트로만", driver.Requests[1].Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Response_style_rules_go_into_the_first_request_and_every_follow_up()
    {
        var driver = Driver();
        var flow = new AiTuningFlow(new FakeEditor(Original), driver, new AiChatViewModel(), (_, _) => { });
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
        });
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

    /// <summary>작은따옴표 문자열만 가리는 시험용 가림 구현(실제 구현은 Language 의 렉서 기반).</summary>
    private sealed class FakeMasker : ISqlLiteralMasker
    {
        public bool FailMask { get; set; }

        public LiteralVault CreateVault(string statementText) => new();

        public SqlMaskResult Mask(string sql, LiteralVault vault) => FailMask
            ? SqlMaskResult.Failed
            : SqlMaskResult.Ok(Regex.Replace(sql, "'[^']*'", m => vault.Add(m.Value)));

        public SqlRestoreResult Restore(string sql, LiteralVault vault)
        {
            if (vault.TryGetStatement(sql, out var original))
            {
                return SqlRestoreResult.Ok(original);
            }

            string? unknown = null;
            var text = Regex.Replace(sql, @":v\d+", m =>
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

        public string MaskPredicate(string predicate, LiteralVault vault) => Regex.Replace(predicate, "'[^']*'", m => vault.Add(m.Value));
    }

    private static AiTuningFlow MaskedFlow(FakeEditor editor, FakeDriver driver, AiChatViewModel chat, List<(string Title, string Text)> tabs, ISqlLiteralMasker? masker, List<LiteralVault?>? vaults = null) =>
        new(editor, driver, chat, (t, x) => tabs.Add((t, x)),
            sessionFactory: (_, vault) =>
            {
                vaults?.Add(vault);
                return null;
            },
            masker: masker,
            maskRequired: _ => true);

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
        var tab = Assert.Single(tabs).Text;
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
        var flow = new AiTuningFlow(new FakeEditor(Original), driver, new AiChatViewModel(), (_, _) => { }, maskRequired: _ => false);

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
        var flow = new AiTuningFlow(new FakeEditor(Original), driver, new AiChatViewModel(), (_, _) => { },
            sessionFactory: (_, vault) =>
            {
                vaults.Add(vault);
                return null;
            },
            masker: new FakeMasker(),
            maskRequired: _ => mask);

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
        var flow = new AiTuningFlow(new FakeEditor(Original), driver, chat, (_, _) => { }, sessionFactory: null,
            refreshContext: (old, _) => Task.FromResult<AiContext?>(old with { Environment = ConnectionEnvironment.Production }),
            masker: new FakeMasker(),
            maskRequired: ctx => production || ctx.Environment == ConnectionEnvironment.Production);

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

        var noStatement = new AiTuningFlow(new NoStatementEditor(), Driver(), chat, (_, _) => { });
        await noStatement.StartAsync(new TuningStartChoice(true, true, "default"), Context);
        Assert.Equal(0, noStatement.Generation); // 문장 없음으로 거부

        var ok = new AiTuningFlow(new FakeEditor(Original), Driver(), chat, (_, _) => { });
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
}
