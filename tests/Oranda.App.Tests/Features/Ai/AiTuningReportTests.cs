using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Report;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>AI 튜닝 레포트: 생성기(Markdown·HTML)와 대화 창의 「레포트 저장」.</summary>
public class AiTuningReportTests
{
    private static readonly DateTimeOffset Created = new(2026, 11, 2, 14, 3, 0, TimeSpan.FromHours(9));

    private static TuningResult Result(string summary = "**keep_last 재작성 · 37% 빨라짐**\n\n## 근거\n- 읽기 줄임") => new(
        summary,
        [new TuningRewrite("keep_last", "SELECT 1 FROM dual", "상관 서브쿼리 제거", "141.5 ms → 89.1 ms")],
        [new TuningIndexSuggestion("TB_ORDER", ["ORD_DT", "CUST_ID"], "CREATE INDEX IX_ORDER ON TB_ORDER (ORD_DT, CUST_ID)", "범위 읽기 감소", "쓰기 비용 증가")],
        []);

    private static AiTuningReportInput Input(TuningResult? result = null, bool masked = false, string target = "SELECT * FROM tb_order") => new(
        "튜닝 — SELECT * FROM tb_order", Created, "운영", "sonnet", masked, target, result ?? Result(),
        [new AiTuningReportTool("실행 계획 보기", "허용", "1,400 자", "0.4 초"), new AiTuningReportTool("run|query", "거부", "0 자", string.Empty)],
        [new AiTuningReportFollowUp("인덱스는 어떻게 되나요?", "새 인덱스가 필요합니다.")]);

    [Fact]
    public void Markdown_has_sections_in_the_agreed_order_and_marks_indexes_as_not_executed()
    {
        var md = AiTuningReportBuilder.ToMarkdown(Input());

        var order = new[]
        {
            "# 튜닝 — SELECT * FROM tb_order", "- 접속 환경: 운영", "## 결론", "## 대상 SQL", "## 재작성 후보", "## 인덱스 후보", "## 이어 말하기", "## 사용한 도구",
        };
        var positions = order.Select(h => md.IndexOf(h, StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.OrderBy(p => p), positions);
        Assert.Contains("실행 안 함", md, StringComparison.Ordinal);
        Assert.Contains("- 벤치마크: 141.5 ms → 89.1 ms", md, StringComparison.Ordinal);
        Assert.Contains("| run\\|query | 거부 | 0 자 | - |", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_keeps_placeholders_of_a_masked_conversation_and_closes_a_broken_fence()
    {
        var result = Result("결론\n\n```sql\nSELECT :v1 FROM dual"); // 닫히지 않은 울타리
        var md = AiTuningReportBuilder.ToMarkdown(Input(result, masked: true, target: "SELECT * FROM t WHERE a = :v1 AND b = :v2"));

        Assert.StartsWith("# ", md, StringComparison.Ordinal);
        Assert.Equal(AiTuningReportBuilder.MaskedNote, md.Split('\n')[2].TrimStart('>', ' '));
        Assert.Contains("a = :v1 AND b = :v2", md, StringComparison.Ordinal);
        var summaryEnd = md.IndexOf("## 대상 SQL", StringComparison.Ordinal);
        Assert.Equal(0, md[..summaryEnd].Split('\n').Count(l => l.StartsWith("```", StringComparison.Ordinal)) % 2); // 결론 절의 울타리는 짝이 맞는다
    }

    [Fact]
    public void Html_escapes_every_ai_text_so_a_script_in_the_response_stays_inert()
    {
        const string evil = "<script>alert('x')</script>";
        var result = new TuningResult(
            "**결론** " + evil + "\n\n```\n" + evil + "\n```\n\n| a | b |\n| - | - |\n| " + evil + " | <img src=x onerror=1> |",
            [new TuningRewrite("<b>" + evil, "SELECT '" + evil + "' FROM dual", evil, evil)],
            [new TuningIndexSuggestion(evil, [evil], evil, evil, evil)],
            []);
        var input = Input(result, target: evil) with { Title = evil, FollowUps = [new AiTuningReportFollowUp(evil, evil)], Tools = [new AiTuningReportTool(evil, evil, evil, evil)] };

        var html = AiTuningReportBuilder.Build(input, AiTuningReportFormat.Html);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror=1>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_is_one_self_contained_file_without_external_references_or_scripts()
    {
        var html = AiTuningReportBuilder.Build(Input(), AiTuningReportFormat.Html);

        foreach (var forbidden in new[] { "http://", "https://", "//cdn", "<script", "<link", "@import", "url(", " src=", " href=", "<iframe", "@font-face" })
        {
            Assert.DoesNotContain(forbidden, html, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("prefers-color-scheme:dark", html, StringComparison.Ordinal);
        Assert.Contains("@media print", html, StringComparison.Ordinal);
        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Assembler_turns_chat_lines_into_tools_and_follow_ups_without_the_conclusion_or_save_notices()
    {
        var result = Result();
        var source = new AiTuningReportSource(Created, ConnectionEnvironment.Production, null, false, "SELECT 1", result);
        var vm = new AiChatViewModel();
        vm.StartConversation("튜닝");
        vm.BeginTurn("이 문장 튜닝");
        vm.AddToolCall("get_cursor_plan", "1.4 KB", "허용");
        vm.AddAssistantText(result.Summary);
        vm.EndTurn();
        vm.BeginTurn("인덱스는요?");
        vm.AddAssistantText("새 인덱스가 필요합니다.");
        vm.AddAssistantText(result.Summary);
        vm.AddAssistantText(AiTuningReportAssembler.SavedPrefix + "/tmp/x.html");
        vm.EndTurn();

        var input = AiTuningReportAssembler.Build(source, vm.Title, vm.Entries);

        Assert.Equal("운영", input.EnvironmentText);
        Assert.Single(input.Tools);
        var followUp = Assert.Single(input.FollowUps);
        Assert.Equal("인덱스는요?", followUp.Question);
        Assert.Equal("새 인덱스가 필요합니다.", followUp.Answer);
    }

    [Fact]
    public void Save_is_disabled_without_a_tuning_result_and_while_running()
    {
        var vm = new AiChatViewModel();
        vm.StartConversation("튜닝");
        Assert.False(vm.CanSaveReport);
        Assert.False(vm.SaveReportCommand.CanExecute(null));

        vm.SetReportSource(new AiTuningReportSource(Created, null, null, false, "SELECT 1", Result()));
        Assert.True(vm.SaveReportCommand.CanExecute(null));

        vm.BeginTurn("이어서");
        Assert.False(vm.SaveReportCommand.CanExecute(null));
        vm.EndTurn();
        Assert.True(vm.SaveReportCommand.CanExecute(null));

        vm.StartConversation("새 대화");
        Assert.False(vm.CanSaveReport);
    }

    [Fact]
    public async Task Save_writes_the_format_of_the_chosen_extension_and_reports_the_path_in_the_chat()
    {
        var vm = NewChat(out var dialogs);
        dialogs.Path = "/tmp/report.md";
        string? written = null;
        vm.FileWriter = (path, text, _) =>
        {
            written = path + "\n" + text;
            return Task.CompletedTask;
        };

        await vm.SaveReportCommand.ExecuteAsync(null);

        Assert.StartsWith("/tmp/report.md\n# ", written, StringComparison.Ordinal);
        Assert.EndsWith(AiTuningReportAssembler.SavedPrefix + "/tmp/report.md", ((ChatAssistantEntry)vm.Entries[^1]).Text, StringComparison.Ordinal);
        Assert.StartsWith("AI튜닝레포트_20261102-1403.html", dialogs.Suggested, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_failure_leaves_one_notice_line_instead_of_throwing()
    {
        var vm = NewChat(out var dialogs);
        dialogs.Path = "/readonly/report.html";
        vm.FileWriter = (_, _, _) => throw new UnauthorizedAccessException("권한이 없습니다.");

        await vm.SaveReportCommand.ExecuteAsync(null);

        var last = Assert.IsType<ChatAssistantEntry>(vm.Entries[^1]);
        Assert.Equal(AiTuningReportAssembler.FailedPrefix + "권한이 없습니다.", last.Text);
        Assert.True(vm.CanSaveReport);
    }

    private static AiChatViewModel NewChat(out FakeDialogs dialogs)
    {
        var fake = dialogs = new FakeDialogs();
        var vm = new AiChatViewModel { DialogsProvider = () => fake };
        vm.StartConversation("튜닝 — SELECT 1");
        vm.BeginTurn("이 문장 튜닝");
        vm.EndTurn();
        vm.SetReportSource(new AiTuningReportSource(Created, ConnectionEnvironment.Development, "sonnet", false, "SELECT 1", Result()));
        return vm;
    }

    private sealed class FakeDialogs : IEditorDialogs
    {
        public string? Path { get; set; }

        public string? Suggested { get; private set; }

        public Task<string?> PickReportSaveFileAsync(string suggestedName, CancellationToken ct)
        {
            Suggested = suggestedName;
            return Task.FromResult(Path);
        }

        public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string initial, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct) => Task.FromResult(false);
    }
}
