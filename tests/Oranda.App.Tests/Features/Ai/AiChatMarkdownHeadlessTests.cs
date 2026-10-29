using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Tests.Features.SqlEditor;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// 대화 창의 AI 글이 Markdown 으로 그려진다(f4, Headless). 사용자 글·도구 카드는 그대로이고 AI 글은 선택·복사가 되는 글 칸이다.
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
            Assert.All(view.GetVisualDescendants().OfType<TextBlock>().Where(t => !string.IsNullOrEmpty(t.Text) || t.Inlines?.Count > 0 || t is SelectableTextBlock), t => Assert.True(t is SelectableTextBlock || t.Text is "•" or "1." or "2." || t.Text is null));
            var selectable = view.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();
            Assert.Contains(selectable, t => t.Text?.Contains("SELECT o.*", StringComparison.Ordinal) == true && t.TextWrapping == Avalonia.Media.TextWrapping.NoWrap);
            Assert.Contains(selectable, t => t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Any(r => r.Text == "FULL" && r.FontWeight == Avalonia.Media.FontWeight.Bold) == true);
            Assert.Contains(selectable, t => t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Any(r => r.Text == ":v1" || r.Text?.Contains(":v1", StringComparison.Ordinal) == true) == true); // 자리표시는 그대로
            Assert.Contains(view.GetVisualDescendants().OfType<Button>(), b => ToolTip.GetTip(b) is "코드를 복사합니다"); // 코드 복사 단추

            // 사용자 글·도구 카드는 지금처럼 일반 글 칸.
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "이 문장 튜닝");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "get_cursor_plan");

            Shots(window, "answer");
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
