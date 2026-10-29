using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Schemas;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// 대화 창의 AI 글이 Markdown 으로 그려진다(f4, Headless). 사용자 글은 그대로이고 AI 글은 선택·복사가 되는 글 칸이다.
/// 도구 카드는 사람 말 한 줄·사유 한 줄이고 펼치면 인자(가린 글 그대로)가 보인다(c1).
/// <c>ORANDA_RENDER_DIR</c> 가 있으면 라이트·다크 PNG(ai-chat-markdown-*.png)를 남긴다.
/// </summary>
public sealed class AiChatMarkdownHeadlessTests
{
    private const string Answer = """
        ## 진단

        `TB_ORDER` 를 **FULL** 로 읽고 있습니다. 조건 *STAT_CD = :v1* 에 맞는 인덱스가 없습니다.

        - 인덱스 `IX_ORDER_STAT` 추가
        - 힌트는 **쓰지 않음**
          - 통계가 낡았을 때만 다시 수집

        ### 재작성 후보

        ```sql
        SELECT o.*
          FROM tb_order o
         WHERE o.stat_cd = :v1
           AND o.cust_nm = :v2 /* 아주 긴 줄은 가로로 스크롤되어야 합니다 아주 긴 줄은 가로로 스크롤되어야 합니다 */
        ```

        | 방법 | 시간 | 비고 |
        |:--|--:|:-:|
        | 현재 | 1.82 s | FULL |
        | 인덱스 | 0.21 s | `IX_ORDER_STAT` |

        1. 후보를 비교 창에서 확인
        2. 받아들이면 **Ctrl+Z** 로 되돌림

        > 운영 DB 에서는 **통계부터** 확인하세요. 자세한 것은 [Oracle 문서](https://docs.oracle.com/x) 참고.

        ---

        ```
        plain text block
        ```
        """;

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Window Show(AiChatViewModel vm)
    {
        var window = new Window { Width = 460, Height = 760, Content = new AiChatView { DataContext = vm } };
        window.Show();
        Pump();
        window.UpdateLayout();
        return window;
    }

    private static void Shots(Window window, string name)
    {
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        try
        {
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
                var size = new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height));
                using var bmp = new RenderTargetBitmap(size);
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"ai-chat-markdown-{name}-{suffix}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        }
    }

    [Fact]
    public Task Assistant_text_is_drawn_as_markdown_blocks_with_selectable_text() => Ui.Run(() =>
    {
        var vm = new AiChatViewModel();
        vm.StartConversation("튜닝 — SELECT * FROM tb_order");
        vm.BeginTurn("이 문장 튜닝");
        vm.AddToolCall("get_cursor_plan", "1,400 자", "허용");
        vm.AddAssistantText(Answer);
        vm.AddAssistantText("[NOT EXISTS 로 재작성] 반조인을 쓰게 합니다. (1.82 s → 0.21 s)");
        vm.EndTurn();

        var window = Show(vm);
        try
        {
            var view = window.GetVisualDescendants().OfType<MarkdownView>().First();
            Assert.Equal(2, window.GetVisualDescendants().OfType<MarkdownView>().Count());

            // 제목·문단·목록·코드 블록·표·번호 목록이 모두 글 칸으로 그려지고, 글 칸은 선택·복사가 되는 형식이다.
            Assert.All(view.GetVisualDescendants().OfType<TextBlock>().Where(t => !string.IsNullOrEmpty(t.Text) || t.Inlines?.Count > 0 || t is SelectableTextBlock), t => Assert.True(t is SelectableTextBlock || t.Text is "•" or "1." or "2." or "sql" || t.Text is null)); // 표식·코드 언어 이름만 일반 글 칸
            var selectable = view.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();
            // 닫힌 sql 블록은 편집기와 같은 색으로 칠한 Run 들(키워드 SELECT 는 기본 글색이 아니다). 언어 없는 블록은 일반 글.
            var sqlBlock = Assert.Single(selectable, t => t.TextWrapping == Avalonia.Media.TextWrapping.NoWrap && t.Inlines?.Count > 0);
            var sqlRuns = sqlBlock.Inlines!.OfType<Avalonia.Controls.Documents.Run>().ToList();
            Assert.StartsWith("SELECT o.*", string.Concat(sqlRuns.Select(r => r.Text)), StringComparison.Ordinal);
            Assert.NotNull(sqlRuns.First(r => r.Text == "SELECT").Foreground);
            Assert.Contains(selectable, t => t.Text == "plain text block" && t.TextWrapping == Avalonia.Media.TextWrapping.NoWrap);
            Assert.Contains(selectable, t => t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Any(r => r.Text == "Oracle 문서" && r.TextDecorations is { Count: > 0 }) == true); // 링크 모양
            Assert.Contains(view.GetVisualDescendants().OfType<Border>(), b => b.BorderThickness.Left == 3 && b.BorderThickness.Right == 0); // 인용 막대
            Assert.Contains(view.GetVisualDescendants().OfType<Border>(), b => b.Height == 1); // 수평선
            Assert.Contains(selectable, t => t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Any(r => r.Text == "FULL" && r.FontWeight == Avalonia.Media.FontWeight.Bold) == true);
            Assert.Contains(selectable, t => t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Any(r => r.Text == ":v1" || r.Text?.Contains(":v1", StringComparison.Ordinal) == true) == true); // 자리표시는 그대로
            Assert.Contains(view.GetVisualDescendants().OfType<Button>(), b => ToolTip.GetTip(b) is "코드를 복사합니다"); // 코드 복사 단추

            // 사용자 글·도구 카드는 지금처럼 일반 글 칸.
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "이 문장 튜닝");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "실제 실행 계획(커서)");

            Shots(window, "answer");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Tool_cards_show_a_plain_language_line_the_gate_reason_and_masked_arguments_when_expanded() => Ui.Run(() =>
    {
        var vm = new AiChatViewModel();
        vm.StartConversation("튜닝 — WITH CSM_BASE AS (…)");
        vm.BeginTurn("이 문장 튜닝");
        vm.Apply(new AiToolCall("c1", AgentToolNames.GetTuningContext, "{\"literals\":\"masked\",\"budget_tokens\":4000}"));
        vm.Apply(new AiToolResult("c1", AgentToolNames.GetTuningContext, 159, AiToolJudgement.Denied, "sql_unsafe"));
        vm.Apply(new AiToolCall("c2", AgentToolNames.GetIndexInfo, "{\"table\":\"MESAPUSER.TB_M30_SMTL_CSM_CMN\"}"));
        vm.Apply(new AiToolResult("c2", AgentToolNames.GetIndexInfo, 1809, AiToolJudgement.Allowed));
        vm.Apply(new AiToolCall("c3", AgentToolNames.ExplainPlan, "{\"sql\":\"SELECT * FROM tb_order WHERE stat_cd = :v1 AND cust_nm = :v2\"}"));
        vm.Apply(new AiToolResult("c3", AgentToolNames.ExplainPlan, 2300, AiToolJudgement.Failed, "tool_failed: ORA-00942"));
        vm.Apply(new AiToolCall("c4", AgentToolNames.BenchmarkQueries, "{\"variants\":[{\"label\":\"a\",\"sql\":\"SELECT 1 FROM dual\"}],\"repeat\":3}"));
        vm.EndTurn();
        var reopened = 0;
        vm.AddProposal("NOT EXISTS 로 재작성", "반조인을 쓰게 해 TB_ORDER 를 한 번만 읽습니다.", applied: false, () => { reopened++; return Task.FromResult<bool?>(true); });
        vm.AddProposal("인덱스 힌트", "IX_ORDER_STAT 를 쓰게 합니다.", applied: true, () => Task.FromResult<bool?>(true));

        var window = Show(vm);
        try
        {
            // 제안 카드(c6): 적용 안 한 제안은 「다시 보기」, 적용한 제안은 「적용함」. 다시 보기로 받아들이면 그 카드도 「적용함」 이 된다.
            var review = Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => b.Name == "ReviewButton" && b.IsEffectivelyVisible);
            Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "AppliedMark" && t.IsEffectivelyVisible);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "재작성 제안 · NOT EXISTS 로 재작성");
            Shots(window, "proposals");
            review.Command!.Execute(review.CommandParameter);
            Pump();
            window.UpdateLayout();
            Assert.Equal(1, reopened);
            Assert.Equal(2, window.GetVisualDescendants().OfType<TextBlock>().Count(t => t.Name == "AppliedMark" && t.IsEffectivelyVisible));

            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("튜닝 정보 묶음 · 요청의 대상 문장", texts);
            Assert.Contains("인덱스 정보 조회 · MESAPUSER.TB_M30_SMTL_CSM_CMN", texts);
            Assert.Contains("실행 계획(EXPLAIN) · SQL 60자 · 자리표시 2", texts);
            Assert.Contains("변형 성능 비교 · 변형 1개 · 반복 3회", texts);
            Assert.Contains("안전하다고 판정할 수 없는 SQL 이라 보내지 않음", texts);
            Assert.Contains("도구 실행 중 오류 (ORA-00942)", texts);
            Assert.Contains("1,809 자", texts);
            Assert.DoesNotContain(texts, t => t?.Contains("{\"table\"", StringComparison.Ordinal) == true); // 접힌 줄에는 인자 원문이 없다

            var cards = window.GetVisualDescendants().OfType<Expander>().ToList();
            Assert.Equal(4, cards.Count);
            Assert.All(cards, c => Assert.False(c.IsExpanded));
            Shots(window, "tools");

            cards[0].IsExpanded = true;
            cards[2].IsExpanded = true;
            Pump();
            window.UpdateLayout();
            var details = window.GetVisualDescendants().OfType<SelectableTextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();
            Assert.Contains(details, t => t.Contains("tool: get_tuning_context", StringComparison.Ordinal) && t.Contains("reason: sql_unsafe", StringComparison.Ordinal));
            Assert.Contains(details, t => t.Contains("sql: SELECT * FROM tb_order WHERE stat_cd = :v1 AND cust_nm = :v2", StringComparison.Ordinal)); // 가린 글 그대로
            Shots(window, "tools-expanded");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task A_new_text_replaces_the_drawn_blocks_and_empty_text_draws_nothing() => Ui.Run(() =>
    {
        var view = new MarkdownView { Markdown = "# 하나\n\n둘" };
        Assert.Equal(2, view.Children.Count);

        view.Markdown = "```sql\nSELECT 1 FROM dual\n```";
        Assert.Single(view.Children);

        view.Markdown = null;
        Assert.Empty(view.Children);
    });
}
