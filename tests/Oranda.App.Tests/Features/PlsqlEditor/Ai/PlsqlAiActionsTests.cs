using Oranda.App.Features.Ai;
using Oranda.App.Features.Ai.Agent;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Tuning.Commands;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Features.Ai.Kit;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.PlsqlEditor.Ai;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.Ai.Kit;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Settings;
using Oranda.Language.Ai.Literals;

namespace Oranda.App.Tests.Features.PlsqlEditor.Ai;

/// <summary>
/// PL/SQL 편집기 AI(설계 §3.3, TSK-15-07): 오류 고치기 = 편집기 아래 제안 막대(서버 줄 → 편집기 줄, 다른 쪽 오류 뺌, 받아들이기 = 차이 미리보기·대조),
/// 코드 설명 = 채팅창 새 대화(첫 요청을 채팅창에서 보냄 · 가벼운 일 · 가림 · 이어 묻기 같은 경로). 컴파일·실행은 하지 않는다.
/// </summary>
public class PlsqlAiActionsTests
{
    private const string Source = "-- 머리 주석\nCREATE OR REPLACE PROCEDURE P_DEMO IS\n  v_name VARCHAR2(10) := 'abc';\nBEGIN\n  v_nam := 'x';\nEND;";

    private static readonly DbObjectRef Body = new("SAMPLE", "PKG_DEMO", "PACKAGE BODY");

    private static readonly SqlLiteralMasker Masker = new();

    private static AiOneShotRun Run(FakeDriver driver, ConnectionEnvironment env = ConnectionEnvironment.Development, AiSettings? settings = null, int tokens = 0) =>
        new(new AiOneShotSetup(driver, AiKitRoute.For(KitFakes.Profile(env, AiAccessLevel.Plan), settings ?? new AiSettings { Light = new AiLightSettings { Model = "haiku" } }, PlsqlFixPrompt.Tier, tokens), PlsqlAiActions.FixFeature));

    [Fact]
    public void Fix_input_moves_server_lines_to_editor_lines_and_keeps_only_this_side()
    {
        var (offset, _) = Oranda.App.Features.PlsqlEditor.PlsqlSourceText.SourceStart(Source);
        var errors = new[]
        {
            new CompileError(4, 3, "PLS-00201: identifier 'V_NAM' must be declared", false) { MessageNumber = 201, ObjectType = "PACKAGE BODY" },
            new CompileError(2, 1, "PLS-00103: spec error", false) { ObjectType = "PACKAGE" }, // 다른 쪽(명세)
            new CompileError(1, 1, "PLW-06002: unreachable", true) { ObjectType = "PACKAGE BODY" },
        };

        var input = PlsqlAiActions.FixInput(Source, Body, errors, "PACKAGE PKG_DEMO IS END;");

        Assert.True(offset >= 1); // 머리 주석만큼 서버 줄이 밀린다
        Assert.Equal("SAMPLE.PKG_DEMO", input.ObjectName);
        Assert.Equal("PACKAGE BODY", input.ObjectType);
        Assert.Equal([4 + offset, 1 + offset], input.Errors.Select(e => e.Line)); // 명세 오류는 뺐다(경고는 프롬프트가 뺀다)
        Assert.Equal("PACKAGE PKG_DEMO IS END;", input.SpecSource);
    }

    private static string Answer(string fixedSource, int end) =>
        "## 까닭\n**V_NAM 오타 · 변수 이름을 고침**\n## 고친 소스\n<fixed_part start=\"1\" end=\"" + end + "\">\n" + fixedSource + "\n</fixed_part>";

    [Fact]
    public async Task Accept_puts_the_fixed_source_through_the_diff_preview_with_the_reason()
    {
        var editor = new SqlEditorViewModel(Guid.NewGuid(), Source);
        var input = PlsqlAiActions.FixInput(Source, null, [new CompileError(4, 3, "PLS-00201: x", false) { MessageNumber = 201 }], null);
        var driver = new FakeDriver { Answer = _ => KitFakes.Answer(Answer(Source.Replace("v_nam :=", "v_name :=", StringComparison.Ordinal), 6)) };
        var run = Run(driver);
        var previews = new List<(string Title, string Before, string After)>();
        var version = editor.Version;
        var bar = AiSuggestionBar.Create(PlsqlFixPrompt.Title, (_, _) => Task.FromResult(PlsqlFixPrompt.Prepare(input, run, Masker)), (accept, ct) =>
            PlsqlAiActions.AcceptAsync(accept, input, editor, version, (title, before, after, _) =>
            {
                previews.Add((title, before, after));
                return Task.FromResult<string?>(after);
            }, Masker, ct));

        await bar.RunAsync(CancellationToken.None);
        Assert.Equal(AiTier.Light, Assert.Single(driver.Requests).Tier);
        Assert.Null(driver.Requests[0].OutputSchema);
        Assert.True(bar.ShowsAccept);
        await bar.AcceptCommand.ExecuteAsync(null);

        var (title, before, after) = Assert.Single(previews);
        Assert.Equal("AI 오류 고치기 — V_NAM 오타 · 변수 이름을 고침", title);
        Assert.Equal(Source, before);
        Assert.Contains("v_name := 'x';", after, StringComparison.Ordinal);
        Assert.Equal(after, editor.Text);
        Assert.Equal(AiInlineStripViewModel.AcceptedText, bar.StatusText);
    }

    [Fact]
    public async Task Accept_refuses_after_the_source_changed_or_when_the_preview_is_rejected()
    {
        var input = PlsqlAiActions.FixInput(Source, null, [new CompileError(4, 3, "PLS-00201: x", false)], null);
        var driver = new FakeDriver { Answer = _ => KitFakes.Answer(Answer(Source.Replace("v_nam", "v_name", StringComparison.Ordinal), 6)) };
        var run = Run(driver);
        var request = PlsqlFixPrompt.Prepare(input, run, Masker).Request!;
        var result = await run.AskAsync(request.Prompt, null, request.Bundle, null, CancellationToken.None);
        var accept = new AiStripAccept(request, result, request.Template!.Parse(result.Text));

        var rejected = new SqlEditorViewModel(Guid.NewGuid(), Source);
        Assert.Equal(PlsqlAiActions.RejectedText, await PlsqlAiActions.AcceptAsync(accept, input, rejected, rejected.Version, (_, _, _, _) => Task.FromResult<string?>(null), Masker, CancellationToken.None));
        Assert.Equal(Source, rejected.Text);

        var changed = new SqlEditorViewModel(Guid.NewGuid(), Source);
        var version = changed.Version;
        changed.Apply(new TextOpResult(0, 0, "-- 새 줄\n", 0, 0));
        Assert.Equal(PlsqlAiActions.ChangedText, await PlsqlAiActions.AcceptAsync(accept, input, changed, version, (_, _, after, _) => Task.FromResult<string?>(after), Masker, CancellationToken.None));
    }

    [Fact]
    public void Explain_request_masks_literals_on_a_masked_connection_and_keeps_the_spec_as_reference()
    {
        var input = PlsqlAiActions.FixInput(Source, Body, [], "PACKAGE PKG_DEMO IS c CONSTANT VARCHAR2(3) := 'SPC'; END;");

        var masked = PlsqlAiActions.ExplainRequest(input, Run(new FakeDriver(), ConnectionEnvironment.Production).Route, Masker)!.Value;
        Assert.DoesNotContain("'abc'", masked.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("'SPC'", masked.Prompt, StringComparison.Ordinal);
        Assert.True(masked.Bundle.Masked);
        Assert.Contains("package_spec is for reference only", masked.Prompt, StringComparison.Ordinal);

        var plain = PlsqlAiActions.ExplainRequest(input, Run(new FakeDriver()).Route, Masker)!.Value;
        Assert.Contains("'abc'", plain.Prompt, StringComparison.Ordinal);
        Assert.Contains("## 하는 일", plain.Prompt, StringComparison.Ordinal);

        var broken = PlsqlAiActions.FixInput("BEGIN x := 'open; END;", null, [], null);
        Assert.Null(PlsqlAiActions.ExplainRequest(broken, Run(new FakeDriver(), ConnectionEnvironment.Production).Route, Masker)); // 가릴 수 없는 소스는 보내지 않는다
    }

    [Fact]
    public async Task Explain_opens_a_new_chat_conversation_that_sends_its_first_request_there_and_follows_up_the_same_way()
    {
        var chat = new AiChatViewModel();
        chat.StartConversation("튜닝 — SELECT 1");
        var driver = new FakeDriver { Answer = _ => KitFakes.Answer("**주문 상태를 바꾸는 프로시저**") };
        var run = Run(driver);
        var input = PlsqlAiActions.FixInput(Source, null, [], null);
        var request = PlsqlAiActions.ExplainRequest(input, run.Route, Masker)!.Value;

        var conversation = await AiChatOpener.OpenAsync(KitFakes.Host(chat), c => AiCarriedConversation.ForRequest(c, "코드 설명 — SAMPLE.P_DEMO", run, request.Prompt, request.Bundle));
        await conversation!.StartAsync("코드 설명 · SAMPLE.P_DEMO");

        Assert.Same(conversation, chat.Owner);
        Assert.Equal("코드 설명 — SAMPLE.P_DEMO", chat.Title);
        Assert.Equal("코드 설명 · SAMPLE.P_DEMO", Assert.IsType<ChatUserEntry>(chat.Entries[0]).Text);
        Assert.Equal("**주문 상태를 바꾸는 프로시저**", chat.Entries.OfType<ChatAssistantEntry>().Last().Text);
        var first = Assert.Single(driver.Requests);
        Assert.Equal(request.Prompt, first.Prompt);
        Assert.Equal(AiTier.Light, first.Tier);

        await conversation.FollowUpAsync(new ChatFollowUp("예외는?", []), CancellationToken.None);
        var follow = driver.Requests[1];
        Assert.Equal("resume-1", follow.ResumeId);
        Assert.Equal(first.Expected, follow.Expected);
        Assert.Equal(AiTier.Light, follow.Tier);
    }

    [Fact]
    public async Task A_large_source_goes_to_the_main_model_when_the_route_is_chosen()
    {
        var id = Guid.NewGuid();
        var local = new AiLlmProfile { Id = id, Name = "로컬", Preset = AiProfilePreset.Custom, Format = AiApiFormat.OpenAi, BaseUrl = "http://192.0.2.10:11434/v1", Model = "m", MaxInputTokens = 8_192, LocalAiConfirmedUrl = "http://192.0.2.10:11434/v1" };
        var settings = new AiSettings { Model = "opus", Profiles = [local], Light = new AiLightSettings { ProfileId = id } };
        var big = string.Concat(Enumerable.Repeat("  v := v + 1; -- 한 줄\n", 900));

        var small = Run(new FakeDriver(), settings: settings, tokens: AiKitRoute.EstimateFor(Source)).Route;
        var large = Run(new FakeDriver(), settings: settings, tokens: AiKitRoute.EstimateFor(big)).Route;

        Assert.Equal(AiTier.Light, small.Tier);
        Assert.Equal(AiTier.Main, large.Tier);
        Assert.True(large.Escalated);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Fix_error_without_a_session_shows_the_bar_with_the_reason()
    {
        var vm = new PlsqlEditorViewModel();
        vm.StartNew(Source, session: null);

        await PlsqlAiActions.FixErrorAsync(new EmptyServices(), vm, CancellationToken.None);

        var bar = Assert.IsType<AiInlineStripViewModel>(vm.Editor.AiBar);
        Assert.Equal(PlsqlFixPrompt.Title, bar.Title);
        Assert.NotNull(bar.Confirm); // SQL 오류 고치기·다른 AI 띠와 같은 보내기 경고 창
        Assert.Equal(AiStripState.Refused, bar.State);
        Assert.Equal(PlsqlAiActions.NoSessionText, bar.StatusText);
    }

    [Fact]
    public void Explain_and_fix_error_are_offered_in_a_plsql_editor_without_an_execution_error()
    {
        var commands = ShellContributions.Scan(typeof(ShellContributions).Assembly).Commands.Select(c => c.Definition).ToList();
        var explain = commands.Single(c => c.Id == AiCommands.ExplainSql);
        var fix = commands.Single(c => c.Id == AiCommands.FixError);
        var keys = new ContextKeyService();
        keys.Set(AiContextKeys.ConfiguredKey, true);
        keys.Set(AiContextKeys.AccessOnKey, true);
        keys.Set("hasSession", true);
        keys.Set("plsqlEditorFocus", true);
        keys.Set(AiCommands.PlsqlDocKey, true);

        Assert.True(keys.Evaluate(explain.When));
        Assert.True(keys.Evaluate(fix.When));
        Assert.True(keys.Evaluate(explain.Enablement));
        Assert.True(keys.Evaluate(fix.Enablement)); // 실행 오류가 없어도 PL/SQL 문서면(컴파일 오류가 없으면 막대가 까닭을 보인다)

        keys.Set(AiCommands.PlsqlDocKey, false);
        keys.Set("plsqlEditorFocus", false);
        keys.Set("sqlDocFocus", true);
        Assert.False(keys.Evaluate(fix.Enablement)); // SQL 탭은 지금처럼 실행 오류가 있어야 한다
        keys.Set("hasExecError", true);
        Assert.True(keys.Evaluate(fix.Enablement));
        Assert.Equal(FixErrorCommand.Enablement, fix.Enablement);
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
