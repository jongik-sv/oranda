using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.Ai.Kit;
using Oranda.App.Features.ChangeBundle;
using Oranda.App.Features.ChangeBundle.Ai;
using Oranda.App.Tests.Features.Ai.Kit;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.ChangeBundle;
using Oranda.Core.Connection;
using Oranda.Core.Settings;
using Oranda.Language;
using Oranda.Language.Lexing;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.ChangeBundle;

// 소유: TSK-15-09. 실행 전 AI 검토(설계 §4.1): 주 모델·도구 범위·답 틀·위험도·확인 한 번·입구 조건, 묶음 실행 창의 재료와 화면(Headless).
[Collection(ChangeBundleDialogsGroup.Name)]
public sealed class DdlReviewTests
{
    private const string Answer = """
        ## 위험도
        보통 · 표가 커서 시간이 걸립니다

        ## 점검 항목
        | 항목 | 판단 | 근거 |
        | --- | --- | --- |
        | 잠금 | 짧음 | ONLINE |
        | 공간 | 주의 | 18 GB 더 씀 |
        | 시간 | 40~70분 | 1.2억 행 |
        | 무효화 | 뷰 2 | V_ORDER_SUM |
        | 되돌리기 | 어려움 | 다시 만들어야 함 |

        ## 권하는 순서
        업무 외 시간에 실행하고 끝나면 무효 객체를 컴파일합니다.
        """;

    private static readonly AiSettings Settings = new() { Model = "opus", Driver = AiDriverKind.ClaudeCode };

    private static DdlReviewInput Input(ConnectionProfile? profile = null, string script = "ALTER TABLE SAMPLE.ORDERS MOVE ONLINE", params string[] findings) =>
        new(profile ?? KitFakes.Profile(access: AiAccessLevel.Plan), script, findings, [new DdlReviewTarget("SAMPLE", "ORDERS")], "Oracle 19");

    private static (DdlReviewViewModel Review, FakeDriver Driver, List<AgentSession> Tracked) Create(
        Func<DdlReviewInput?>? input = null, AiSettings? settings = null, Func<AiStripRequest, CancellationToken, Task<bool>>? confirm = null, FakeDriver? driver = null)
    {
        driver ??= new FakeDriver { Answer = _ => KitFakes.Answer(Answer) };
        var tracked = new List<AgentSession>();
        var services = new DdlReviewServices(() => driver, () => settings ?? Settings)
        {
            Masker = new KitMasker(),
            TrackSession = s =>
            {
                tracked.Add(s);
                return s;
            },
        };
        var fixedInput = Input();
        return (new DdlReviewViewModel(input ?? (() => fixedInput), services, confirm), driver, tracked);
    }

    [Fact]
    public async Task Review_goes_to_the_main_model_with_tools_scoped_to_the_changed_objects()
    {
        var (review, driver, tracked) = Create(() => Input(findings: "⚠ 잠금 시간이 길 수 있다"));

        await review.Entry.RunCommand.ExecuteAsync(null);

        var request = Assert.Single(driver.Requests);
        Assert.Equal(AiTier.Main, request.Tier);
        Assert.NotNull(request.Session);
        Assert.Same(Assert.Single(tracked), request.Session);
        Assert.Equal(["SAMPLE.ORDERS"], request.Session!.Send.Patterns);
        Assert.True(request.Session.Send.Matches("SAMPLE", "ORDERS"));
        Assert.False(request.Session.Send.Matches("SAMPLE", "CUSTOMERS"));
        Assert.True(request.Session.IsRevoked); // 끝나면 실행 범위를 남기지 않는다
        Assert.Contains("get_dependents", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("get_table_stats", request.Prompt, StringComparison.Ordinal); // 계획 수준 = 크기도 본다
        Assert.Contains("ALTER TABLE SAMPLE.ORDERS MOVE ONLINE", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("⚠ 잠금 시간이 길 수 있다", request.Prompt, StringComparison.Ordinal); // 앱 검사 줄은 덧붙일 바탕
        Assert.Contains("| 항목 | 판단 | 근거 |", request.Prompt, StringComparison.Ordinal); // 답 틀
    }

    [Fact]
    public async Task Templated_answer_gives_risk_and_the_five_check_rows()
    {
        var (review, _, _) = Create();

        await review.Strip.RunAsync(CancellationToken.None);

        Assert.Equal(AiStripState.Done, review.Strip.State);
        Assert.Equal(DdlRisk.Medium, review.Risk);
        Assert.Equal("위험도 보통", review.RiskText);
        Assert.False(review.IsHighRisk);
        var rows = review.Strip.Answer![DdlReviewPrompt.ItemsSection]!.Rows!;
        Assert.Equal(DdlReviewPrompt.CheckRows, rows.Select(r => r[0]));
        Assert.True(review.Strip.CanContinueVisible is false); // 채팅창 동작이 없으면 「채팅에서 이어 보기」 를 숨긴다
    }

    [Theory]
    [InlineData("높음 · 운영 큰 표", DdlRisk.High)]
    [InlineData("**낮음** 짧은 DDL", DdlRisk.Low)]
    [InlineData("모르겠음", null)]
    public void Risk_reads_the_first_word_of_the_risk_section(string body, DdlRisk? expected)
    {
        var answer = new AiTemplatedAnswer([new AiAnswerPart(DdlReviewPrompt.RiskSection, body)]);

        Assert.Equal(expected, DdlReviewPrompt.RiskOf(answer));
        Assert.Null(DdlReviewPrompt.RiskOf(null));
    }

    [Fact]
    public async Task Broken_answer_shows_the_text_without_a_risk()
    {
        var (review, _, _) = Create(driver: new FakeDriver { Answer = _ => KitFakes.Answer("그냥 글로 답합니다") });

        await review.Strip.RunAsync(CancellationToken.None);

        Assert.Equal(AiStripState.Done, review.Strip.State);
        Assert.Null(review.Risk);
        Assert.True(review.Strip.ShowsRawText);
    }

    [Fact]
    public async Task Confirm_is_asked_once_and_cancel_sends_nothing()
    {
        var answers = new Queue<bool>([false, true]);
        var asked = new List<string>();
        var (review, driver, _) = Create(confirm: (request, _) =>
        {
            asked.Add(DdlReviewViewModel.ConfirmMessage(request));
            return Task.FromResult(answers.Dequeue());
        });

        await review.Strip.RunAsync(CancellationToken.None);
        Assert.Equal(AiStripState.Refused, review.Strip.State);
        Assert.Empty(driver.Requests);

        await review.Strip.RunAsync(CancellationToken.None);
        await review.Strip.RunAsync(CancellationToken.None);

        Assert.Equal(2, asked.Count); // 취소 뒤 한 번 더 묻고, 받은 뒤에는 묻지 않는다
        Assert.Equal(2, driver.Requests.Count);
        Assert.Contains("받는 곳:", asked[0], StringComparison.Ordinal);
        Assert.Contains("실행할 문장", asked[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Production_connection_masks_literals_in_the_script()
    {
        var prod = KitFakes.Profile(ConnectionEnvironment.Production, AiAccessLevel.Plan);
        var (review, driver, _) = Create(() => Input(prod, "ALTER TABLE SAMPLE.ORDERS ADD (STAT_CD VARCHAR2(2) DEFAULT 'SECRET')"));

        await review.Strip.RunAsync(CancellationToken.None);

        var prompt = Assert.Single(driver.Requests).Prompt;
        Assert.DoesNotContain("SECRET", prompt, StringComparison.Ordinal);
        Assert.True(driver.Requests[0].MaskingRequired);
    }

    [Fact]
    public async Task Entry_is_off_without_a_model_without_access_or_without_statements()
    {
        var off = Create(settings: Settings with { Driver = AiDriverKind.None }).Review;
        Assert.False(off.Entry.IsEnabled);
        Assert.Equal(AiEntryRule.NotConfiguredText, off.Entry.ToolTip);

        var noAccess = Create(() => Input(KitFakes.Profile(access: AiAccessLevel.Off))).Review;
        Assert.False(noAccess.Entry.IsEnabled);
        Assert.Equal(AiEntryRule.AccessOffText, noAccess.Entry.ToolTip);

        var empty = Create(() => null).Review;
        Assert.False(empty.Entry.IsEnabled);
        Assert.Equal(DdlReviewViewModel.NoScriptText, empty.Entry.ToolTip);

        var (metadataOnly, driver, _) = Create(() => Input(KitFakes.Profile(access: AiAccessLevel.Metadata)));
        Assert.True(metadataOnly.Entry.IsEnabled);
        await metadataOnly.Strip.RunAsync(CancellationToken.None);
        Assert.DoesNotContain("get_table_stats", Assert.Single(driver.Requests).Prompt, StringComparison.Ordinal); // 메타데이터 수준은 크기를 묻지 않는다
    }

    [Fact]
    public void Run_window_input_uses_owner_mapping_findings_and_does_not_block_run()
    {
        var bundle = new ChangeBundleInfo(1, "릴리스 1", DateTimeOffset.Now, DateTimeOffset.Now, Dbms.Oracle, ["SAMPLE"], true);
        var item = new ChangeBundleItem(1, 1, 1, "ALTER TABLE SAMPLE.ORDERS MOVE ONLINE", ChangeBundleItemSource.SqlEditor, "SAMPLE", "ORDERS", true,
            DateTimeOffset.Now, "h", ChangeBundleCautions.None, null);
        var test = new FakeSession(TestData.Profile(ConnectionEnvironment.Test) with { Name = "test-example" }, TestData.Server());
        var run = new ChangeBundleRunViewModel(bundle, [item], [test], null, new ChangeBundleOwnerRewriter(), new LanguageService(), new FakeExecutionService(), null);
        run.OwnerMap.Single().To = "SAMPLE_T";
        run.FindingLines.Add("⚠ 큰 표");
        DdlReviewViewModel? created = null;

        run.AttachReview(r => created = Create(r.ReviewInput).Review);

        var input = run.ReviewInput()!;
        Assert.Same(created, run.Review);
        Assert.True(run.HasReview);
        Assert.Equal([new DdlReviewTarget("SAMPLE_T", "ORDERS")], input.Targets);
        Assert.Contains("SAMPLE_T.ORDERS", input.Script, StringComparison.Ordinal);
        Assert.Equal(["⚠ 큰 표"], input.AppFindings);
        Assert.True(run.CanRun); // 검토는 실행을 막지 않는다
        Assert.True(run.Review!.Entry.IsEnabled);
    }

    [Fact]
    public async Task Run_window_shows_the_review_button_and_strip_with_risk()
    {
        var bundle = new ChangeBundleInfo(1, "릴리스 1", DateTimeOffset.Now, DateTimeOffset.Now, Dbms.Oracle, ["SAMPLE"], true);
        var item = new ChangeBundleItem(1, 1, 1, "DROP TABLE SAMPLE.OLD_ORDERS", ChangeBundleItemSource.SqlEditor, "SAMPLE", "OLD_ORDERS", true,
            DateTimeOffset.Now, "h", ChangeBundleCautions.None, null);
        var dev = new FakeSession(TestData.Profile(ConnectionEnvironment.Development) with { AiAccess = AiAccessLevel.Plan }, TestData.Server());
        var run = new ChangeBundleRunViewModel(bundle, [item], [dev], null, new ChangeBundleOwnerRewriter(), new LanguageService(), new FakeExecutionService(), null);
        run.AttachReview(r => Create(r.ReviewInput).Review);

        await HeadlessHost.Run(async () =>
        {
            var window = new ChangeBundleRunWindow { DataContext = run };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var button = window.FindControl<Button>("ReviewButton")!;
                Assert.True(button.IsVisible);
                Assert.True(button.IsEnabled);
                Assert.Equal("AI 검토", button.Content);
                Assert.False(window.FindControl<DdlReviewPanel>("ReviewPanel")!.IsVisible); // 묻기 전에는 띠를 숨긴다

                await run.Review!.Entry.RunCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();

                var panel = window.FindControl<DdlReviewPanel>("ReviewPanel")!;
                Assert.True(panel.IsVisible);
                Assert.Equal("위험도 보통", panel.FindControl<TextBlock>("RiskLabel")!.Text);
                Assert.True(window.FindControl<Button>("RunButton")!.IsEnabled); // 실행 단추는 그대로
            }
            finally
            {
                window.Close();
            }
        });
    }
}
