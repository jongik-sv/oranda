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

    private static IReadOnlyList<AiTuningReportTurn> Turns(TuningResult? result = null) => [new AiTuningReportTurn(0, result ?? Result(), [])];

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
        var source = new AiTuningReportSource(Created, ConnectionEnvironment.Production, null, false, "SELECT 1", Turns(result));
        var vm = new AiChatViewModel();
        vm.StartConversation("튜닝");
        vm.BeginTurn("이 문장 튜닝");
        vm.AddToolCall("get_cursor_plan", "1.4 KB", "허용");
        vm.AddAssistantText(result.Summary);
        vm.EndTurn();
        vm.BeginTurn("인덱스는요?");
        vm.AddAssistantText("새 인덱스가 필요합니다."); // 구조 결과가 없는 차례: AI 글이 답이다
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

        vm.SetReportSource(new AiTuningReportSource(Created, null, null, false, "SELECT 1", Turns()));
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

    [Fact]
    public void Assembler_keeps_each_turns_result_so_a_follow_up_does_not_replace_the_first_conclusion()
    {
        var first = new TuningResult("처음 결론", [Rewrite("r1", "SELECT 1")], [Index("TB_A", "CREATE INDEX IX_A ON TB_A (A)")], []);
        var second = new TuningResult("두 번째 답", [Rewrite("r1", "SELECT 1"), Rewrite("r2", "SELECT 2")], [Index("TB_A", "CREATE INDEX IX_A ON TB_A (A)"), Index("TB_B", "CREATE INDEX IX_B ON TB_B (B)")], []);
        var source = new AiTuningReportSource(Created, null, null, false, "SELECT 0", [new(0, first, []), new(1, second, [])]);
        var vm = new AiChatViewModel();
        vm.StartConversation("튜닝");
        vm.BeginTurn("이 문장 튜닝");
        vm.AddAssistantText(first.Summary);
        vm.EndTurn();
        vm.BeginTurn("r2 도 보여 주세요");
        vm.AddAssistantText(second.Summary);
        vm.EndTurn();
        vm.BeginTurn("구조 결과 없는 질문");
        vm.AddAssistantText("그냥 말로 한 답");
        vm.EndTurn();

        var input = AiTuningReportAssembler.Build(source, vm.Title, vm.Entries);

        Assert.Equal("처음 결론", input.Result.Summary);
        Assert.Equal(["두 번째 답", "그냥 말로 한 답"], input.FollowUps.Select(f => f.Answer));
        Assert.Equal(["r1", "r2"], input.Result.Rewrites.Select(r => r.Label)); // 같은 라벨·SQL 은 한 번만
        Assert.Equal([0, 1], input.RewriteTurns);
        Assert.Equal([0, 1], input.IndexTurns);
        var md = AiTuningReportBuilder.ToMarkdown(input);
        Assert.Contains("### 2. r2 · 질문 1", md, StringComparison.Ordinal);
        Assert.Contains("### 1. r1\n", md, StringComparison.Ordinal);
        Assert.Contains("TB_B (C) · 질문 1", md, StringComparison.Ordinal);
    }

    private static TuningRewrite Rewrite(string label, string sql) => new(label, sql, "근거", null);

    private static TuningIndexSuggestion Index(string table, string ddl) => new(table, ["C"], ddl, "근거", null);

    [Fact]
    public void Markdown_does_not_treat_a_one_line_inline_code_triple_backtick_as_an_open_fence()
    {
        var result = Result("앞 문단\n\n```code``` 한 줄 코드\n\n뒤 문단");

        var md = AiTuningReportBuilder.ToMarkdown(Input(result));

        var conclusion = md[..md.IndexOf("## 대상 SQL", StringComparison.Ordinal)];
        Assert.DoesNotContain(conclusion.Split('\n'), l => l.TrimEnd() == "```");
        Assert.Contains("뒤 문단", conclusion, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_lists_dropped_index_candidates_in_one_line()
    {
        var input = Input() with { DroppedIndexes = ["뺀 인덱스 후보: TB_X (A) — 기존 IX_X (A) 가 이미 덮음"] };

        var md = AiTuningReportBuilder.ToMarkdown(input);

        Assert.Contains("뺀 후보: TB_X (A) — 기존 IX_X (A) 가 이미 덮음", md, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Typing_and_sending_are_blocked_while_saving_and_a_run_started_meanwhile_stops_the_write()
    {
        var vm = NewChat(out var dialogs);
        vm.Draft = "이어서";
        Assert.True(vm.CanType);
        Assert.True(vm.SendCommand.CanExecute(null));
        var gate = new TaskCompletionSource();
        dialogs.Gate = gate.Task;
        dialogs.Path = "/tmp/report.html";
        var wrote = false;
        vm.FileWriter = (_, _, _) =>
        {
            wrote = true;
            return Task.CompletedTask;
        };

        var saving = vm.SaveReportCommand.ExecuteAsync(null);
        Assert.False(vm.CanType);
        Assert.False(vm.SendCommand.CanExecute(null));

        vm.BeginTurn("저장 창이 떠 있는 사이 시작된 차례");
        gate.SetResult();
        await saving;

        Assert.False(wrote);
        Assert.StartsWith(AiTuningReportAssembler.FailedPrefix, ((ChatAssistantEntry)vm.Entries[^1]).Text, StringComparison.Ordinal);
        vm.EndTurn();
        Assert.True(vm.CanType);
    }

    [Theory]
    [InlineData("/tmp/report", false)]
    [InlineData("/tmp/report.txt", false)]
    [InlineData("/tmp/report.MD", true)]
    [InlineData("/tmp/report.htm", true)]
    public void Only_md_and_html_paths_are_accepted_and_the_path_is_never_rewritten(string path, bool accepted)
    {
        Assert.Equal(accepted, AiTuningReportFiles.TryResolve(path, out _));
    }

    [Fact]
    public async Task Save_refuses_a_path_without_a_report_extension_instead_of_appending_one()
    {
        var vm = NewChat(out var dialogs);
        dialogs.Path = "/tmp/report.txt";
        var wrote = false;
        vm.FileWriter = (_, _, _) =>
        {
            wrote = true;
            return Task.CompletedTask;
        };

        await vm.SaveReportCommand.ExecuteAsync(null);

        Assert.False(wrote);
        Assert.StartsWith(AiTuningReportAssembler.FailedPrefix, ((ChatAssistantEntry)vm.Entries[^1]).Text, StringComparison.Ordinal);
    }

    private static AiChatViewModel NewChat(out FakeDialogs dialogs)
    {
        var fake = dialogs = new FakeDialogs();
        var vm = new AiChatViewModel { DialogsProvider = () => fake };
        vm.StartConversation("튜닝 — SELECT 1");
        vm.BeginTurn("이 문장 튜닝");
        vm.EndTurn();
        vm.SetReportSource(new AiTuningReportSource(Created, ConnectionEnvironment.Development, "sonnet", false, "SELECT 1", Turns()));
        return vm;
    }

    private sealed class FakeDialogs : IEditorDialogs
    {
        public string? Path { get; set; }

        public string? Suggested { get; private set; }

        /// <summary>있으면 저장 창이 이 작업이 끝날 때까지 떠 있는 것으로 본다.</summary>
        public Task? Gate { get; set; }

        public async Task<string?> PickReportSaveFileAsync(string suggestedName, CancellationToken ct)
        {
            Suggested = suggestedName;
            if (Gate is not null)
            {
                await Gate;
            }

            return Path;
        }

        public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string initial, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct) => Task.FromResult(false);
    }
}
