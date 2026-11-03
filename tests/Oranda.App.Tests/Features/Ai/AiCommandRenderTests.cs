using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Ai;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Tuning.Commands;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Menus;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// 오류 고치기의 화면 점검(w144-render): 메뉴 활성·비활성 이유, 대화 창 제안 카드, 비교 창. 헤드리스로 만들고 <c>ORANDA_RENDER_DIR</c> 가 있을 때만 PNG 로 남긴다
/// (저장소에 넣지 않는다). 메뉴는 실제 명령 정의·활성 식·이유 문구를 쓰되 줄만 그리는 대역 창이다(실제 도구 모음 흐름을 띄우지 않는다).
/// </summary>
public class AiCommandRenderTests
{
    private static readonly IReadOnlyList<CommandDefinition> AppCommands =
        [.. ShellContributions.Scan(typeof(ShellContributions).Assembly).Commands.Select(c => c.Definition)];

    private static void Render(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR") is not { Length: > 0 } dir)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        using var bmp = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)window.Bounds.Width), Math.Max(1, (int)window.Bounds.Height)));
        bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
        bmp.Save(Path.Combine(dir, name + ".png"));
#pragma warning restore CS0618
    }

    private static ContextKeyService Keys(bool hasExecError, bool planLevel)
    {
        var keys = new ContextKeyService();
        keys.Set("hasSession", true);
        keys.Set("aiConfigured", true);
        keys.Set("aiAccessOn", true);
        keys.Set("aiAccessPlan", planLevel);
        keys.Set("hasExecError", hasExecError);
        return keys;
    }

    private static IReadOnlyList<MenuNode> AiChildren() =>
        MenuModelBuilder.BuildToolbar(AppCommands, new ShellText(language: "ko"), _ => null).Single(n => n.CommandId == AiCommands.Tune).Children;

    private static string? Reason(ContextKeyService keys, ShellText text, string expression) =>
        ContextKeyReasons.Explain(expression, keys.Evaluate, k => text.Lookup(k) is var v && v != k ? v : null);

    /// <summary>▾ 메뉴의 자식 줄을 그린 대역 창: 활성 줄은 진하게, 비활성 줄은 흐리게 하고 이유를 옆에 적는다.</summary>
    private static Window MenuWindow(ContextKeyService keys, ShellText text)
    {
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(16) };
        foreach (var node in AiChildren())
        {
            var enabled = node.Enablement is null || keys.Evaluate(node.Enablement);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Opacity = enabled ? 1 : 0.45 };
            row.Children.Add(new TextBlock { Text = node.Title, Width = 130, FontWeight = enabled ? FontWeight.SemiBold : FontWeight.Normal });
            row.Children.Add(new TextBlock { Text = enabled ? "사용 가능" : Reason(keys, text, node.Enablement!) ?? "(이유 없음)", Name = "Reason" });
            panel.Children.Add(row);
        }

        return new Window { Width = 640, Height = 4 * 28 + 48, Content = panel };
    }

    [Fact]
    public Task The_fix_error_menu_row_is_enabled_with_an_error_and_greyed_with_its_reason_without_one() => HeadlessHost.Run(() =>
    {
        var text = new ShellText(language: "ko");
        var fix = AiChildren().Single(n => n.CommandId == AiCommands.FixError);
        Assert.Equal(FixErrorCommand.Enablement, fix.Enablement);

        var withError = Keys(hasExecError: true, planLevel: true);
        Assert.True(withError.Evaluate(fix.Enablement!));
        var enabledWindow = MenuWindow(withError, text);
        enabledWindow.Show();
        Assert.DoesNotContain(enabledWindow.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "실행 오류가 있을 때 쓸 수 있습니다");
        Render(enabledWindow, "ai-fixerror-menu-enabled");
        enabledWindow.Close();

        var noError = Keys(hasExecError: false, planLevel: true);
        Assert.False(noError.Evaluate(fix.Enablement!));
        Assert.Equal("실행 오류가 있을 때 쓸 수 있습니다", Reason(noError, text, fix.Enablement!));
        var disabledWindow = MenuWindow(noError, text);
        disabledWindow.Show();
        Assert.Contains(disabledWindow.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "실행 오류가 있을 때 쓸 수 있습니다");
        Render(disabledWindow, "ai-fixerror-menu-disabled");
        disabledWindow.Close();

        // 같은 메뉴의 아직 꺼진 두 줄(SQL 설명·실행 계획 해설)은 이 항목에서 켜지 않는다.
        Assert.Equal(ExplainSqlCommand.Enablement, AiChildren().Single(n => n.CommandId == AiCommands.ExplainSql).Enablement);
    });

    [Fact]
    public Task The_chat_window_shows_the_first_line_and_the_fix_proposal_card() => HeadlessHost.Run(() =>
    {
        var vm = new AiChatViewModel();
        vm.StartConversation("오류 고치기 — SELECT * FROM tb_order WHERE …");
        vm.AddAssistantText("보낸 문장: 「SELECT * FROM tb_order WHERE stat_cd = :v1 AND qty > zz」 · 리터럴 값과 주석은 가려서 보냈습니다. " + FixErrorCommand.Instance.SentNote(true)); // 안내 글은 명령이 정한 실제 글
        vm.BeginTurn("오류 고치기", []);
        vm.AddAssistantText("**ORA-00904 · 열 이름 오타**\n\n- `zz` 를 `qty_ord` 로 바꿈\n\n> 바꾼 열이 의도한 열인지 확인하세요.");
        vm.AddProposal("오류 수정", "zz 를 qty_ord 로 바꿈", applied: true, () => Task.FromResult<bool?>(true), view: () => Task.CompletedTask);
        var window = new Window { Width = 440, Height = 520, Content = new AiChatView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var label = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ProposalLabel"); // 제안 카드의 이름 칸
        Assert.Equal("오류 수정", label.Text);
        Assert.True(label.IsEffectivelyVisible);
        Assert.Contains(vm.Entries.OfType<ChatAssistantEntry>(), e => e.Text.StartsWith("보낸 문장:", StringComparison.Ordinal) && e.Text.Contains("가려서 보냈습니다", StringComparison.Ordinal));
        Assert.Single(vm.Entries.OfType<ChatProposalEntry>(), e => e.Applied);
        Assert.False(vm.CanSaveReport); // 오류 고치기는 레포트를 만들지 않는다
        Render(window, "ai-fixerror-chat-card");
        window.Close();
    });

    [Fact]
    public Task The_comparison_window_shows_the_original_and_the_fixed_statement_side_by_side() => HeadlessHost.Run(() =>
    {
        const string before = "SELECT * FROM tb_order\nWHERE stat_cd = 'A'\n  AND qty > zz";
        const string after = "SELECT * FROM tb_order\nWHERE stat_cd = 'A'\n  AND qty > qty_ord";
        var panel = AvaloniaEditorDialogs.BuildPreviewEditPanel(before, after);
        var window = new Window { Width = 900, Height = 420, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(after, panel.ProposedText); // 고치지 않고 받아들이면 제안 글 그대로
        Assert.True(panel.Bounds.Width > 0 && panel.Bounds.Height > 0);
        Render(window, "ai-fixerror-compare");
        window.Close();
    });
}
