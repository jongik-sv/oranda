using Oranda.App.Features.Ai.Kit;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;
using Oranda.Core.Settings;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.Ai.Kit;

/// <summary>입구 단추 규칙·답 틀·한 번짜리 요청 경로(받는 곳 확인·가림·실행 범위·감사).</summary>
public class AiKitRulesTests
{
    // ---- 입구 단추 ----------------------------------------------------------------

    [Fact]
    public void Entry_is_on_only_when_ai_is_configured_access_is_on_and_the_screen_agrees()
    {
        Assert.Equal(new AiEntryState(true, "풀이"), AiEntryRule.Evaluate(true, true, enabledTip: "풀이"));
        Assert.Equal(AiEntryRule.NotConfiguredText, AiEntryRule.Evaluate(false, false, "사슬 없음").DisabledReason); // 첫 까닭 하나
        Assert.Equal(AiEntryRule.AccessOffText, AiEntryRule.Evaluate(true, false, "사슬 없음").DisabledReason);
        Assert.Equal("사슬 줄을 고르세요", AiEntryRule.Evaluate(true, true, "사슬 줄을 고르세요").ToolTip);
        Assert.False(AiEntryRule.Evaluate(true, true, "사슬 줄을 고르세요").IsEnabled);
    }

    [Fact]
    public void Entry_with_a_minimum_level_names_that_level()
    {
        var low = AiEntryRule.Evaluate(true, AiAccessLevel.Metadata, AiAccessLevel.Plan);
        Assert.False(low.IsEnabled);
        Assert.Contains("「계획」 이상", low.ToolTip, StringComparison.Ordinal);
        Assert.Equal(AiEntryRule.AccessOffText, AiEntryRule.Evaluate(true, AiAccessLevel.Off, AiAccessLevel.Plan).DisabledReason);
        Assert.True(AiEntryRule.Evaluate(true, AiAccessLevel.Query, AiAccessLevel.Plan).IsEnabled);
        Assert.Equal("화면", AiEntryRule.Evaluate(true, AiAccessLevel.Plan, AiAccessLevel.Plan, "화면").DisabledReason);
    }

    [Fact]
    public void Entry_reads_the_same_context_keys_as_the_menu()
    {
        Assert.Equal("aiConfigured && aiAccessOn", AiEntryRule.Enablement);
        Assert.True(AiEntryRule.Evaluate(new Keys("aiConfigured", "aiAccessOn")).IsEnabled);
        Assert.Equal(AiEntryRule.AccessOffText, AiEntryRule.Evaluate(new Keys("aiConfigured")).DisabledReason);
    }

    [Fact]
    public async Task Entry_button_follows_the_rule_and_cannot_be_pressed_twice()
    {
        var configured = false;
        var gate = new TaskCompletionSource();
        var runs = 0;
        var button = new AiEntryButton("AI 풀이", () => AiEntryRule.Evaluate(configured, true), async () =>
        {
            runs++;
            await gate.Task;
        });
        Assert.False(button.RunCommand.CanExecute(null));
        Assert.Equal(AiEntryRule.NotConfiguredText, button.ToolTip);

        configured = true;
        button.Refresh();
        Assert.True(button.IsEnabled);
        var pressing = button.RunCommand.ExecuteAsync(null);
        Assert.False(button.IsEnabled);
        gate.SetResult();
        await pressing;
        Assert.Equal(1, runs);
        Assert.True(button.IsEnabled);
    }

    private sealed class Keys(params string[] on) : IContextKeys
    {
        public bool Evaluate(string? expression) => expression is null || on.Contains(expression);
    }

    // ---- 답 틀 --------------------------------------------------------------------

    private static readonly AiAnswerTemplate Template = new([
        new AiAnswerSection("상황", "one line"),
        new AiAnswerSection("원인 추정", "a few lines"),
        new AiAnswerSection("점검", "risk items", ["항목", "위험"]),
    ]);

    [Fact]
    public void Rules_fix_the_headings_and_table_header()
    {
        Assert.Contains("## 상황", Template.Rules, StringComparison.Ordinal);
        Assert.Contains("## 점검", Template.Rules, StringComparison.Ordinal);
        Assert.Contains("| 항목 | 위험 |", Template.Rules, StringComparison.Ordinal);
    }

    [Fact]
    public void An_answer_in_the_template_is_read_into_parts_and_rows()
    {
        var answer = Template.Parse("## 상황\n세션 812 가 막고 있습니다.\n\n## 원인 추정\n커밋 전에 멈춤\n\n## 점검\n| 항목 | 위험 |\n| --- | :---: |\n| 잠금 | 높음 |\n| 무효화 | 낮음 |\n");

        Assert.NotNull(answer);
        Assert.Equal(["상황", "원인 추정", "점검"], answer!.Parts.Select(p => p.Title));
        Assert.Equal("세션 812 가 막고 있습니다.", answer["상황"]!.Body);
        Assert.Equal([["잠금", "높음"], ["무효화", "낮음"]], answer["점검"]!.Rows!.Select(r => r.ToArray()));
    }

    [Theory]
    [InlineData("머리말입니다\n## 상황\na\n## 원인 추정\nb\n## 점검\n| 항목 | 위험 |\n| --- | --- |")] // 틀 밖 머리말
    [InlineData("## 상황\na\n## 점검\n| 항목 | 위험 |\n| --- | --- |")] // 칸이 빠짐
    [InlineData("## 원인 추정\nb\n## 상황\na\n## 점검\n| 항목 | 위험 |\n| --- | --- |")] // 차례가 다름
    [InlineData("## 상황\na\n## 원인 추정\nb\n## 점검\n| 항목 | 정도 |\n| --- | --- |")] // 표 머리가 다름
    [InlineData("## 상황\na\n## 원인 추정\nb\n## 점검\n| 항목 | 위험 |\n| --- | --- |\n| 하나 |")] // 칸 수가 다른 행
    [InlineData("")]
    public void A_broken_answer_is_not_read_so_the_screen_shows_the_text(string text) => Assert.Null(Template.Parse(text));

    [Fact]
    public void Template_rejects_empty_or_duplicate_sections()
    {
        Assert.Throws<ArgumentException>(() => new AiAnswerTemplate([]));
        Assert.Throws<ArgumentException>(() => new AiAnswerTemplate([new AiAnswerSection("a", "x"), new AiAnswerSection("a", "y")]));
    }

    // ---- 한 번짜리 요청 -------------------------------------------------------------

    [Fact]
    public async Task One_shot_sends_with_a_fresh_session_the_expected_target_and_an_audit_line()
    {
        var driver = new FakeDriver { Answer = _ => KitFakes.Answer("풀이 글") };
        var audit = new FakeAudit();
        var tracked = new List<Oranda.Core.Ai.Gate.AgentSession>();
        var run = KitFakes.Run(driver, audit: audit, tracked: tracked);
        var bundle = run.Route.Bundle("상황 풀이", new KitMasker()).Add("chain", "사슬", "SID 1", AiAccessLevel.Metadata).Build();

        var result = await run.AskAsync("PROMPT", null, bundle, null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("풀이 글", result.Text);
        Assert.Equal("resume-1", result.ResumeId);
        var request = Assert.Single(driver.Requests);
        Assert.Equal([false], driver.RevokedAtStart);
        Assert.Same(Assert.Single(tracked), request.Session);
        Assert.True(request.Session!.IsRevoked); // 끝나면 폐기
        Assert.Null(request.OutputSchema);
        Assert.NotNull(request.Expected);
        Assert.False(request.MaskingRequired);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("ai_kit:lock_explain", entry.Tool);
        Assert.Contains("items=1", entry.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("PROMPT", entry.Arguments, StringComparison.Ordinal);
        Assert.Equal("haiku", run.Route.ModelLabel);
    }

    [Fact]
    public async Task One_shot_failure_and_masked_route_are_reported()
    {
        var driver = new FakeDriver { Answer = _ => [new AiError("서버가 거절", "profile_changed")] };
        var run = KitFakes.Run(driver, KitFakes.Profile(ConnectionEnvironment.Production, AiAccessLevel.Plan));

        var result = await run.AskAsync("P", null, null, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("profile_changed", result.ErrorCode);
        Assert.True(run.Route.MaskRequired);
        Assert.True(Assert.Single(driver.Requests).MaskingRequired);
        Assert.True(driver.Requests[0].Session!.Run.MaskLiteralsRequired);
    }

    [Fact]
    public void Light_route_uses_the_light_target_for_recipient_and_masking()
    {
        var id = Guid.NewGuid();
        var local = new AiLlmProfile { Id = id, Name = "Ollama qwen3:8b", Preset = AiProfilePreset.Custom, Format = AiApiFormat.OpenAi, BaseUrl = "http://192.0.2.10:11434/v1", Model = "qwen3:8b", LocalAiConfirmedUrl = "http://192.0.2.10:11434/v1" };
        var settings = new AiSettings { Model = "opus", Profiles = [local], Light = new AiLightSettings { ProfileId = id } };
        var prod = KitFakes.Profile(ConnectionEnvironment.Production, AiAccessLevel.Plan);

        var light = AiKitRoute.For(prod, settings, AiTier.Light);
        var main = AiKitRoute.For(prod, settings, AiTier.Main);

        Assert.Equal(id, light.Target.ProfileId);
        Assert.False(light.MaskRequired); // 가벼운 일 모델이 로컬 AI 라 가리지 않는다
        Assert.True(light.Context.LocalAi);
        Assert.Equal("Ollama qwen3:8b · 로컬", light.ModelLabel);
        Assert.Null(main.Target.ProfileId); // 주 모델 = Claude Code(유료)라 가린다
        Assert.True(main.MaskRequired);
        Assert.Equal("opus", main.ModelLabel);
        Assert.Equal(AiTier.Light, light.Tier);
        Assert.Equal(main.Target, AiKitRoute.For(prod, new AiSettings { Model = "opus" }, AiTier.Light).Target); // 비어 있으면 주 모델
        Assert.Contains("tier=light", AiOneShotRun.BuildAuditEntry(light, "f", "p", null, DateTimeOffset.UnixEpoch).Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_label_follows_the_target()
    {
        var id = Guid.NewGuid();
        var local = new AiLlmProfile { Id = id, Name = "Ollama qwen3:8b", Preset = AiProfilePreset.Custom, Format = AiApiFormat.OpenAi, BaseUrl = "http://192.0.2.10:11434/v1", Model = "qwen3:8b", LocalAiConfirmedUrl = "http://192.0.2.10:11434/v1" };
        var route = AiKitRoute.For(KitFakes.Profile(ConnectionEnvironment.Production), new AiSettings { Profiles = [local], ActiveProfileId = id });

        Assert.Equal("Ollama qwen3:8b · 로컬", route.ModelLabel);
        Assert.False(route.MaskRequired); // 로컬 AI 는 가리지 않는다(D-045)
        Assert.Equal("Claude Code", AiKitRoute.For(KitFakes.Profile(), new AiSettings()).ModelLabel);
    }
}
