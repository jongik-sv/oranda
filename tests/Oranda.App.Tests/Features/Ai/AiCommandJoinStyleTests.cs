using System.Runtime.CompilerServices;
using System.Text.Json;
using Oranda.App.Features.Ai;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Tuning;
using Oranda.App.Features.Ai.Tuning.Commands;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.Text;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// oracle-join-style: AI 가 제안하는 SQL 은 Oracle 조인 문법으로 쓰라는 프롬프트 규칙이 처음 요청과 이어 말하기 양쪽에 있다.
/// 튜닝은 <c>OracleJoinRule</c>(ANSI 도 바꿈), 오류 고치기는 <c>FixErrorJoinRule</c>(자기가 쓰거나 고치는 조인만). 제안 SQL 이 없는 SQL 설명·실행 계획 해설에는 둘 다 없다. 규칙 글만 확인한다(실제 모델이 따르는지는 이 시험으로 알 수 없다).
/// </summary>
public class AiCommandJoinStyleTests
{
    private const string Sql = "SELECT * FROM tb_order o LEFT JOIN tb_cust c ON c.id = o.cust_id AND c.use_yn = 'Y' WHERE o.qty > zz";

    private static readonly AiContext Full = new("DEV-ERP", ConnectionEnvironment.Development, AiAccessLevel.QueryRows);

    private sealed class Editor : IEditorContext
    {
        public EditorSnapshot? GetActiveDocument() => null;

        public SqlStatement? GetCurrentStatement() => Statement();

        public Task<bool> ProposeEditAsync(TextEdit edit, CancellationToken ct) => Task.FromResult(true);

        public static Task<string?> Preview(TextEdit edit, string before, CancellationToken ct) => Task.FromResult<string?>(edit.NewText);
    }

    private sealed class Driver : IAiDriver
    {
        public List<AiRequest> Requests { get; } = [];

        public AiDriverCapabilities Capabilities { get; } = new(false, true, true, false, true);

        public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            yield return new AiStarted("s-" + Requests.Count);
            await Task.Yield();
            yield return new AiFinal("답 " + Requests.Count, null, "s-" + Requests.Count);
        }
    }

    private sealed class Rig
    {
        public Driver Driver { get; } = new();

        public AiChatViewModel Chat { get; } = new();

        public AiTuningFlow Flow { get; }

        public Rig()
        {
            var editor = new Editor();
            Flow = new AiTuningFlow(
                editor, Driver, Chat, (_, _, _) => { },
                refreshContext: (context, _) => Task.FromResult<AiContext?>(context with { AccessLevel = AiAccessLevel.QueryRows }),
                previewEdit: Editor.Preview);
        }
    }

    private static SqlStatement Statement() => new(Sql, new TextRange(0, Sql.Length), StatementKind.Query);

    private static async Task<(string First, string FollowUp)> RunAsync(Func<Rig, Task> start)
    {
        var rig = new Rig();
        await start(rig);
        await rig.Flow.FollowUpAsync("조금 더 설명해 줘");
        Assert.Equal(2, rig.Driver.Requests.Count);
        return (rig.Driver.Requests[0].Prompt, rig.Driver.Requests[1].Prompt);
    }

    private static int Count(string text, string part) => text.Split(part).Length - 1;

    [Fact]
    public void Both_rules_share_the_syntax_the_exception_and_the_keep_text_and_carry_the_thin_fixed_phrases()
    {
        foreach (var rule in new[] { AiTuningFlow.OracleJoinRule, FixErrorCommand.FixErrorJoinRule })
        {
            Assert.Contains(AiTuningFlow.OracleJoinSyntaxRule, rule, StringComparison.Ordinal); // 공통 글은 const 로 한 곳에서 온다
            Assert.Contains(AiTuningFlow.OracleJoinExceptionRule, rule, StringComparison.Ordinal);
            Assert.Contains(AiTuningFlow.OracleJoinKeepRule, rule, StringComparison.Ordinal);
            Assert.Contains("(+)", rule, StringComparison.Ordinal);
            Assert.Contains("FROM clause", rule, StringComparison.Ordinal);
            Assert.Contains("WHERE clause", rule, StringComparison.Ordinal);
            Assert.Contains("against a constant", rule, StringComparison.Ordinal);
            Assert.Contains("inner join", rule, StringComparison.Ordinal);
            Assert.Contains("FULL OUTER JOIN", rule, StringComparison.Ordinal);
            Assert.Contains("ORA-01417", rule, StringComparison.Ordinal);
            Assert.Contains("ORA-01719", rule, StringComparison.Ordinal);
            Assert.Contains("ORA-01799", rule, StringComparison.Ordinal);
            Assert.Contains("rationale", rule, StringComparison.Ordinal);
            Assert.Contains("ORA-25156", rule, StringComparison.Ordinal); // 한 쿼리 블록에 ANSI 와 (+) 를 섞지 않는다
            Assert.Contains("bind variable token", rule, StringComparison.Ordinal);
            Assert.Contains("rule about keeping comments", rule, StringComparison.Ordinal);
            Assert.DoesNotContain("placeholders", rule, StringComparison.Ordinal); // 「placeholders」 는 가린 대화의 자리표시 안내가 붙었는지 가르는 낱말이다
            Assert.DoesNotContain("smallest change", rule, StringComparison.Ordinal); // 최소 변경 지시와 다투지 않는다(우선 문구 없음)
        }
    }

    [Fact]
    public void The_two_rules_differ_tuning_converts_ansi_joins_and_fix_error_leaves_unrelated_ones()
    {
        // 튜닝 = 원래가 ANSI 여도 Oracle 문법으로 바꾼다.
        Assert.Contains("even when the original statement uses ANSI joins", AiTuningFlow.OracleJoinRule, StringComparison.Ordinal);
        Assert.Contains("same result set", AiTuningFlow.OracleJoinRule, StringComparison.Ordinal);
        Assert.DoesNotContain("Leave every ANSI join", AiTuningFlow.OracleJoinRule, StringComparison.Ordinal);

        // 오류 고치기 = 자기가 쓰거나 고치는 조인만, 무관한 ANSI 는 그대로. 고친 조인이 있는 블록에 ANSI 가 남으면 그 블록은 ANSI.
        Assert.Contains("When you add a new join, or must rewrite the structure of a join to remove the error, write that join in Oracle join syntax", FixErrorCommand.FixErrorJoinRule, StringComparison.Ordinal);
        Assert.Contains("Fix a small error inside an existing join", FixErrorCommand.FixErrorJoinRule, StringComparison.Ordinal);
        Assert.Contains("keep that join's current syntax, ANSI or Oracle, and change only that spot", FixErrorCommand.FixErrorJoinRule, StringComparison.Ordinal);
        Assert.DoesNotContain("When you write or fix a join", FixErrorCommand.FixErrorJoinRule, StringComparison.Ordinal); // 옛 첫 문장은 없다
        foreach (var onlyInFixError in new[] { "Fix a small error inside an existing join", "keep that join's current syntax", "must rewrite the structure of a join" })
        {
            Assert.DoesNotContain(onlyInFixError, AiTuningFlow.OracleJoinRule, StringComparison.Ordinal); // 튜닝은 작은 오류 예외 없이 늘 Oracle 문법
        }
        Assert.Contains("Leave every ANSI join that has nothing to do with the error exactly as it is", FixErrorCommand.FixErrorJoinRule, StringComparison.Ordinal);
        Assert.Contains("write the join you added or rewrote in ANSI in that block too", FixErrorCommand.FixErrorJoinRule, StringComparison.Ordinal);
        Assert.Contains("subqueries, are decided separately", FixErrorCommand.FixErrorJoinRule, StringComparison.Ordinal);
        Assert.DoesNotContain("even when the original statement uses ANSI joins", FixErrorCommand.FixErrorJoinRule, StringComparison.Ordinal);
        Assert.DoesNotContain("every rewrite you propose", FixErrorCommand.FixErrorJoinRule, StringComparison.Ordinal);

        Assert.NotEqual(AiTuningFlow.OracleJoinRule, FixErrorCommand.FixErrorJoinRule);
    }

    [Fact]
    public async Task Tuning_first_request_and_follow_up_carry_the_tuning_rule_only_after_the_comment_rule()
    {
        var (first, followUp) = await RunAsync(rig => rig.Flow.StartAsync(new TuningStartChoice(true, true, "default"), Full));

        foreach (var prompt in new[] { first, followUp })
        {
            Assert.Equal(1, Count(prompt, AiTuningFlow.OracleJoinRule));
            Assert.DoesNotContain(FixErrorCommand.FixErrorJoinRule, prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("Leave every ANSI join", prompt, StringComparison.Ordinal);
            Assert.True(
                prompt.IndexOf(AiTuningFlow.KeepCommentsRule, StringComparison.Ordinal) < prompt.IndexOf(AiTuningFlow.OracleJoinRule, StringComparison.Ordinal),
                "규칙 글은 주석 유지 규칙 뒤에 붙는다");
        }
    }

    [Fact]
    public void Tuning_prompt_builder_carries_the_tuning_rule_once()
    {
        var prompt = AiTuningFlow.BuildPrompt(new TuningStartChoice(false, false, "default"), Sql);

        Assert.Equal(1, Count(prompt, AiTuningFlow.OracleJoinRule));
        Assert.Equal(1, Count(prompt, AiTuningFlow.OracleJoinSyntaxRule)); // 공통 글이 두 번 실리지 않는다
        Assert.Contains(AiTuningFlow.KeepCommentsRule, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_masked_conversation_keeps_the_placeholder_note_after_the_rule_and_carries_each_rule_once()
    {
        var vault = new LiteralVault();
        var masked = vault.Add("'Y'");
        var statement = $"SELECT * FROM tb_order o, tb_cust c WHERE c.use_yn(+) = {masked}";

        var tuning = AiTuningFlow.BuildPrompt(new TuningStartChoice(false, false, "default"), statement, vault);
        var target = new AiCommandTarget(new SqlStatement(statement, new TextRange(0, statement.Length), StatementKind.Query), State: new FixErrorState(904, null, null, null, null));
        var fix = AiTuningFlow.BuildCommandPrompt(new FixErrorCommand(new NoTabs()), new AiCommandPromptInput(target, statement, vault, null, Full));

        foreach (var (prompt, rule) in new[] { (tuning, AiTuningFlow.OracleJoinRule), (fix, FixErrorCommand.FixErrorJoinRule) })
        {
            Assert.Equal(1, Count(prompt, rule));
            var placeholderNote = prompt.IndexOf("placeholders such as", StringComparison.Ordinal);
            Assert.True(placeholderNote > prompt.IndexOf(rule, StringComparison.Ordinal), "자리표시 안내는 규칙 글 뒤에 그대로 붙는다");
            Assert.Contains(masked, prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Fix_error_first_request_and_follow_up_carry_the_fix_error_rule_only()
    {
        var command = new FixErrorCommand(new NoTabs());
        var target = new AiCommandTarget(Statement(), TabId: Guid.NewGuid(), State: new FixErrorState(904, 83, 1, 84, "ORA-00904: \"ZZ\": invalid identifier"));

        var (first, followUp) = await RunAsync(rig => rig.Flow.StartCommandAsync(command, target, Full));

        foreach (var prompt in new[] { first, followUp })
        {
            Assert.Equal(1, Count(prompt, FixErrorCommand.FixErrorJoinRule));
            Assert.Equal(1, Count(prompt, AiTuningFlow.OracleJoinSyntaxRule));
            Assert.DoesNotContain(AiTuningFlow.OracleJoinRule, prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("every rewrite you propose", prompt, StringComparison.Ordinal);
            Assert.Contains(AiTuningFlow.KeepCommentsRule, prompt, StringComparison.Ordinal);
        }

        Assert.Contains("smallest change", first, StringComparison.Ordinal); // 기존 지시는 그대로이고 규칙과 충돌하지 않는다
    }

    [Fact]
    public async Task Explain_sql_and_explain_plan_prompts_carry_neither_rule()
    {
        foreach (AiCommandSpec command in new AiCommandSpec[] { ExplainSqlCommand.Instance, ExplainPlanCommand.Instance })
        {
            var (first, followUp) = await RunAsync(rig => rig.Flow.StartCommandAsync(command, new AiCommandTarget(Statement()), Full));

            foreach (var prompt in new[] { first, followUp })
            {
                Assert.DoesNotContain(AiTuningFlow.OracleJoinRule, prompt, StringComparison.Ordinal);
                Assert.DoesNotContain(FixErrorCommand.FixErrorJoinRule, prompt, StringComparison.Ordinal);
                Assert.DoesNotContain(AiTuningFlow.OracleJoinSyntaxRule, prompt, StringComparison.Ordinal);
                Assert.DoesNotContain("Oracle join syntax", prompt, StringComparison.Ordinal);
                Assert.Contains(command.ResponseRules, prompt, StringComparison.Ordinal); // 이 명령의 규칙은 그대로 붙어 있다(빈 글로 통과하지 않는다)
            }
        }
    }

    private sealed class NoTabs : IEditorTabs
    {
        public Guid? ActiveTabId => null;

        public string? GetText(Guid tabId) => null;

        public SqlStatement? GetStatementAt(Guid tabId, int offset) => null;

        public bool Activate(Guid tabId) => false;

        public string? GetTitle(Guid tabId) => null;

        public Guid OpenText(string title, string text, Guid? connectionFrom = null) => Guid.NewGuid();

        public bool ReplaceText(Guid tabId, string text) => false;

        public void CopyBinds(Guid fromTab, Guid toTab)
        {
        }
    }
}
