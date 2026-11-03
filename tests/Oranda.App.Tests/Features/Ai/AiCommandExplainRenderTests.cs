using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Oranda.App.Features.Ai;
using Oranda.App.Features.Ai.Agent;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Tuning.Commands;
using Oranda.App.Shell;
using Oranda.App.Shell.Menus;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// w30-render: 해설 답 대화 창과 메뉴 활성·비활성 이유(<c>aiAccessPlan</c>)의 헤드리스 확인. <c>ORANDA_RENDER_DIR</c> 가 있으면 라이트·다크 PNG
/// (<c>explain-chat-*</c>·<c>explain-plan-menu-*</c>)를 그 폴더에 쓴다. 없으면 단언만 한다.
/// </summary>
public class AiCommandExplainRenderTests
{
    private const string SqlAnswer = """
        **주문 테이블에서 미처리 건을 최신순으로 조회**

        ## 읽는 곳
        - TB_ORDER: 주문 헤더. 상태 코드(STAT_CD)로 처리 여부를 구분

        ## 처리 순서
        1. STAT_CD 가 :v1 인 행만 걸러냄
        2. 주문일 기준 내림차순 정렬

        ## 주의
        - 값이 가려져 있어 :v1 이 어떤 상태인지는 알 수 없음
        """;

    private const string PlanAnswer = """
        **TB_ORDER 전체 스캔이 비용의 80%**

        ## 계획 요약
        - TABLE ACCESS FULL TB_ORDER 에서 시작해 정렬로 마무리

        ## 눈여겨볼 곳
        - 비용 1,240 중 단계 2 가 대부분(추정)
        - E-Rows 50 에 A-Rows 38,000: 통계가 낡았을 수 있음

        > 화면의 계획 형식과 다를 수 있으며, 통계는 최근 수집 시점에 따라 달라집니다.
        """;

    private static readonly string[] Languages = ["ko", "en"];

    [Fact]
    public Task Render_the_explanation_chats() => HeadlessHost.Run(async () =>
    {
        foreach (var (name, command, answer) in new (string, AiCommandSpec, string)[]
        {
            ("sql", ExplainSqlCommand.Instance, SqlAnswer),
            ("plan", new ExplainPlanCommand(_ => new ExplainPlanCommand.ScreenPlan("abc123xyz", "SQL_ID  abc123xyz, child number 0\n----\nSELECT * FROM tb_order WHERE stat_cd = 'SECRET' /* 메모본문 */\n\nPlan hash value: 1\n")), PlanAnswer),
        })
        {
            var rig = new AiCommandExplainRig { Mask = true };
            rig.Driver.Answer = answer;
            Assert.Null(await rig.RunAsync(command, tabId: Guid.NewGuid()));

            var texts = rig.AssistantTexts.ToList();
            Assert.Contains("가려서 보냈습니다", texts[0], StringComparison.Ordinal); // 첫 줄: 무엇을 보냈고 가렸는지
            Assert.Contains(texts, t => t.StartsWith(answer.Split('\n')[0].Trim(), StringComparison.Ordinal));

            var window = new Window { Width = 460, Height = 560, Content = new AiChatView { DataContext = rig.Chat } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Render(window, $"explain-chat-{name}");
            window.Close();
        }
    });

    [Fact]
    public Task Render_the_plan_menu_enabled_and_disabled_with_its_reason() => HeadlessHost.Run(() =>
    {
        var keys = new ContextKeyService();
        foreach (var k in new[] { AiContextKeys.ConfiguredKey, AiContextKeys.AccessOnKey, "hasSession" })
        {
            keys.Set(k, true);
        }

        keys.Set("mybatisDocActive", false);
        keys.Set("dedicatedSession", false);
        var rows = new List<(string Title, string State, string? Reason)>();
        foreach (var lang in Languages)
        {
            var text = new ShellText(language: lang);
            foreach (var (label, level) in new[] { ("Metadata", false), ("Plan", true) })
            {
                keys.Set(AiContextKeys.AccessPlanKey, level);
                var enabled = keys.Evaluate(ExplainPlanCommand.Enablement);
                Assert.Equal(level, enabled);
                var reason = enabled ? null : ContextKeyReasons.Explain(ExplainPlanCommand.Enablement, keys.Evaluate, k => text.Lookup(k) is var v && v != k ? v : null);
                if (!enabled)
                {
                    Assert.False(string.IsNullOrWhiteSpace(reason));
                }

                rows.Add(($"[{lang}] {text.Lookup("실행 계획 해설")} · level {label}", enabled ? "enabled" : "disabled", reason));
            }

            Assert.True(keys.Evaluate(ExplainSqlCommand.Enablement)); // SQL 설명은 계획 수준과 무관하게 켜진다
        }

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        foreach (var (title, state, reason) in rows)
        {
            panel.Children.Add(new TextBlock { Text = $"{title} → {state}", FontWeight = FontWeight.SemiBold });
            if (reason is not null)
            {
                panel.Children.Add(new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
            }
        }

        var window = new Window { Width = 620, Height = 360, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Render(window, "explain-plan-menu");
        window.Close();
    });

    private static void Render(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR") is not { Length: > 0 } dir)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        foreach (var (variant, tone) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
        {
            window.RequestedThemeVariant = variant;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            using var bmp = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
            bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
            bmp.Save(Path.Combine(dir, $"{name}-{tone}.png"));
#pragma warning restore CS0618
        }
    }
}
