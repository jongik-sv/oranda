using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Tuning;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Ai.Contracts;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// AI 대화 창 가독성(chat-style 시안): 도구 카드 묶음(st-3), 제안 카드 변화율(st-4), Markdown 규격(st-2 — 결론 상자·숫자 칸·주의 상자·작은 제목).
/// <c>ORANDA_RENDER_DIR</c> 가 있으면 시안 본보기 대화를 라이트·다크 PNG(ai-chat-style-*.png)로 남긴다.
/// </summary>
public sealed class AiChatStyleTests
{
    /// <summary>응답 규칙대로 쓴 본보기 답(시안 Main 의 순서와 같다).</summary>
    private const string Answer = """
        **keep_last 재작성 · 37% 빨라짐**
        결과 817행 같음 · 벤치마크 3회 평균

        | 지표 | 원문 | 제안 |
        |---|---|---|
        | 경과 | 141.5 ms | 89.1 ms |
        | 논리 읽기 | 2,237 | 1,975 |
        | MO_INST 접근 | 2회 | 1회 |
        | 결과 행 | 817 | 817 |

        ## 근거

        - MO_INST 를 두 번 읽음 — `FULL SCAN` + `PK FFS`
        - MAX(PROC_SEQ) 는 FAC_CD·MTL_MO_NO 에만 좌우
        - COIL 조인 중복 행은 MAX·MIN 값을 안 바꿈

        ## 뺀 후보

        - COIL 세미조인 필터 — 113.3 ms · 제안보다 느림

        ## 다음

        - MO_INST FULL SCAN 1,378블록 → 커버링 인덱스 후보 (미측정)

        > 적용 전 확인: PROC_SEQ 가 같은 행이 여럿이면 PROC_CD·시각이 원문과 같은지
        """;

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    [Fact]
    public void Tool_cards_of_a_turn_are_grouped_under_one_summary_line_that_is_open_while_running_and_folds_at_the_end()
    {
        var vm = new AiChatViewModel();
        vm.StartConversation("튜닝");
        vm.BeginTurn("이 문장 튜닝");
        vm.Apply(new AiToolCall("c1", "get_cursor_plan", "{}"));
        var group = Assert.Single(vm.Entries.OfType<ChatToolGroupEntry>()).Group;
        Assert.True(group.IsExpanded);
        Assert.Equal("…", group.Mark);
        Assert.StartsWith("도구 1개 실행 중", group.Summary, StringComparison.Ordinal);

        vm.Apply(new AiToolResult("c1", "get_cursor_plan", 1400, AiToolJudgement.Allowed));
        vm.Apply(new AiToolCall("c2", "get_index_info", "{}"));
        vm.Apply(new AiToolResult("c2", "get_index_info", 900, AiToolJudgement.Allowed));
        Assert.Equal("도구 2개 실행 · 모두 허용", group.Summary);
        Assert.Equal("✓", group.Mark);
        Assert.All(vm.Entries.OfType<ChatToolEntry>(), c => Assert.Same(group, c.Group));

        vm.Apply(new AiFinal("끝", null, null));
        Assert.False(group.IsExpanded); // 끝나면 접힌다
        Assert.Equal("펼치기", group.ToggleText);
    }

    [Fact]
    public void Mixed_judgements_are_counted_and_text_between_tools_starts_a_new_group()
    {
        var vm = new AiChatViewModel();
        vm.StartConversation("튜닝");
        vm.BeginTurn("이 문장 튜닝");
        vm.Apply(new AiToolCall("c1", "get_cursor_plan", "{}"));
        vm.Apply(new AiToolResult("c1", "get_cursor_plan", 1400, AiToolJudgement.Allowed));
        vm.Apply(new AiToolCall("c2", "run_query", "{}"));
        vm.Apply(new AiToolResult("c2", "run_query", 0, AiToolJudgement.Denied));
        var first = Assert.Single(vm.Entries.OfType<ChatToolGroupEntry>()).Group;
        Assert.Equal("도구 2개 실행 · 허용 1 · 거부 1", first.Summary);
        Assert.True(first.HasProblem);
        Assert.Equal("!", first.Mark);

        vm.Apply(new AiTextDelta("계획을 봤습니다."));
        vm.Apply(new AiToolCall("c3", "get_table_stats", "{}"));
        Assert.Equal(2, vm.Entries.OfType<ChatToolGroupEntry>().Count());
        Assert.Equal(["ChatUserEntry", "ChatToolGroupEntry", "ChatToolEntry", "ChatToolEntry", "ChatAssistantEntry", "ChatToolGroupEntry", "ChatToolEntry"], vm.Entries.Select(e => e.GetType().Name));

        vm.EndTurn();
        vm.BeginTurn("이어 말하기");
        vm.AddToolCall("get_cursor_plan", "1 자", "허용");
        Assert.Equal(3, vm.Entries.OfType<ChatToolGroupEntry>().Count()); // 새 차례는 새 묶음
    }

    [Theory]
    [InlineData("141.5 ms → 89.1 ms", "−37%")]
    [InlineData("원문 1.82 s → 제안 0.21 s (3회 평균)", "−88%")]
    [InlineData("2,000 ms -> 2,500 ms", "+25%")]
    [InlineData("100 ms → 100 ms", "0%")]
    [InlineData("빨라짐", null)]
    [InlineData(null, null)]
    public void Change_rate_comes_from_the_first_elapsed_pair_of_the_benchmark_text(string? benchmark, string? expected) =>
        Assert.Equal(expected, AiTuningFlow.ChangeText(benchmark));

    [Fact]
    public void Numeric_cells_and_the_conclusion_block_are_recognized()
    {
        Assert.True(MarkdownView.IsNumeric("141.5 ms"));
        Assert.True(MarkdownView.IsNumeric("2,237"));
        Assert.True(MarkdownView.IsNumeric("2회"));
        Assert.True(MarkdownView.IsNumeric("−37%"));
        Assert.False(MarkdownView.IsNumeric("경과"));
        Assert.False(MarkdownView.IsNumeric("TB_ORDER"));

        Assert.Equal(0, MarkdownView.ConclusionAt(MarkdownParser.Parse(Answer)));
        Assert.Equal(-1, MarkdownView.ConclusionAt(MarkdownParser.Parse("보통 문단\n\n| a | b |\n|---|---|\n| 1 | 2 |"))); // 굵지 않은 줄
        Assert.Equal(-1, MarkdownView.ConclusionAt(MarkdownParser.Parse("**굵게**\n\n- 표 아님"))); // 뒤가 표가 아님
    }

    [Fact]
    public Task Design_sample_draws_a_conclusion_box_numeric_columns_small_headings_and_a_caution_box() => Ui.Run(() =>
    {
        var vm = new AiChatViewModel();
        vm.StartConversation("조업 · SQL 1");
        vm.BeginTurn("이 문장 튜닝 · 벤치마크 켬");
        foreach (var (id, tool) in new[] { ("c1", "get_tuning_context"), ("c2", "get_cursor_plan"), ("c3", "get_sql_stats"), ("c4", "benchmark_queries"), ("c5", "get_cursor_plan") })
        {
            vm.Apply(new AiToolCall(id, tool, "{}"));
            vm.Apply(new AiToolResult(id, tool, 1200, AiToolJudgement.Allowed));
        }

        vm.Apply(new AiFinal(null, null, null));
        vm.AddAssistantText(Answer);
        vm.AddProposal("keep_last", "MO_INST 한 번 읽기 · GROUP BY 한 단계 제거", applied: false, () => Task.FromResult<bool?>(false), change: AiTuningFlow.ChangeText("141.5 ms → 89.1 ms"));

        var window = new Window { Width = 400, Height = 1160, Content = new AiChatView { DataContext = vm } };
        window.Show();
        Pump();
        window.UpdateLayout();
        try
        {
            var view = window.GetVisualDescendants().OfType<MarkdownView>().Single();
            var box = Assert.Single(view.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("conclusion"));
            var boxTexts = box.GetVisualDescendants().OfType<TextBlock>().ToList();
            Assert.Contains(boxTexts, t => t.Text == "결론");
            Assert.Contains(boxTexts, t => t is SelectableTextBlock { FontWeight: var w } s && w == FontWeight.Bold && s.Inlines?.Any(r => r is Avalonia.Controls.Documents.Run { Text: "keep_last 재작성 · 37% 빨라짐" }) == true);

            // 숫자 칸은 오른쪽 정렬·고정폭, 글 칸(지표 이름)은 왼쪽.
            var cell = boxTexts.OfType<SelectableTextBlock>().First(t => t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Any(r => r.Text == "141.5 ms") == true);
            Assert.Equal(TextAlignment.Right, cell.TextAlignment);
            var name = boxTexts.OfType<SelectableTextBlock>().First(t => t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Any(r => r.Text == "논리 읽기") == true);
            Assert.Equal(TextAlignment.Left, name.TextAlignment);
            Assert.NotEqual(name.FontFamily, cell.FontFamily); // 숫자 칸만 고정폭

            // 제목은 작은 굵은 회색 글(밑줄 테두리 없음).
            var heading = view.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Any(r => r.Text == "근거") == true);
            Assert.True(heading.Opacity < 1);
            Assert.Equal(FontWeight.Bold, heading.FontWeight);
            Assert.IsNotType<Border>(heading.Parent);

            Assert.Single(view.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("caution"));
            Assert.False(Assert.Single(window.GetVisualDescendants().OfType<ToggleButton>(), t => t.Name == "ToolGroupToggle").IsChecked);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "ProposalChange" && t.Text == "−37%");

            var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
            foreach (var (variant, suffix) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Pump();
                window.UpdateLayout();
                if (string.IsNullOrEmpty(dir))
                {
                    continue;
                }

                Directory.CreateDirectory(dir);
                using var bmp = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height)));
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"ai-chat-style-{suffix}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            window.Close();
        }
    });
}
