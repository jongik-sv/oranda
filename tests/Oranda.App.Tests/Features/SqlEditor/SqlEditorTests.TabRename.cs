using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Tests.Features.SqlEditor;

/// <summary>SQL 탭 머리에서 바로 이름 바꾸기, 끌어서 순서 바꾸기, 탭 머리 메뉴의 닫기 명령.</summary>
public sealed partial class SqlEditorTests
{
    [Fact]
    public Task Inline_rename_commits_trimmed_name_rejects_blank_and_cancels() => Ui.Run(() =>
    {
        var host = new SqlEditorHost();
        var tab = host.NewDocument();
        tab.Title = "원래";

        tab.BeginRename();
        Assert.True(tab.IsRenaming);
        Assert.Equal("원래", tab.EditTitle);

        tab.EditTitle = "   ";
        Assert.False(tab.CommitRename());
        Assert.True(tab.IsRenaming);
        Assert.Equal("원래", tab.Title);

        tab.EditTitle = "  새 이름 ";
        Assert.True(tab.CommitRename());
        Assert.False(tab.IsRenaming);
        Assert.Equal("새 이름", tab.Title);

        tab.BeginRename();
        tab.EditTitle = "버릴 이름";
        tab.CancelRename();
        Assert.False(tab.IsRenaming);
        Assert.Equal("새 이름", tab.Title);
    });

    [Fact]
    public Task Moving_a_tab_reorders_workspace_all_tabs_and_saved_form_and_keeps_active() => Ui.Run(() =>
    {
        var host = new SqlEditorHost();
        var a = host.NewDocument();
        var b = host.NewDocument();
        var c = host.NewDocument();
        var ws = host.ActiveWorkspace!;
        ws.ActiveTab = a;
        Assert.True(ws.ChangedSinceAutosave(out var before));
        ws.MarkAutosaved(before);

        Assert.True(host.MoveTab(a, 2));

        Assert.Equal([b, c, a], ws.Tabs);
        Assert.Equal([b, c, a], host.Tabs);
        Assert.Same(a, ws.ActiveTab);
        Assert.Equal([b.TabId, c.TabId, a.TabId], ws.ToWorkspace().Tabs.Select(t => t.Id));
        Assert.True(ws.ChangedSinceAutosave(out _), "순서만 바뀌어도 자동 저장 표지가 달라져야 한다.");
        Assert.False(host.MoveTab(a, 2));
    });

    [Fact]
    public Task Reordering_workspaces_follows_document_order_and_ignores_mismatched_sets() => Ui.Run(() =>
    {
        var host = new SqlEditorHost();
        var first = host.NewDocument();
        var w1 = host.ActiveWorkspace!;
        host.NewWorkspace();
        var second = host.NewDocument();
        var w2 = host.ActiveWorkspace!;

        Assert.True(host.ReorderWorkspaces([w2, w1]));
        Assert.Equal([w2, w1], host.Workspaces);
        Assert.Equal([second, first], host.Tabs.Where(t => t == first || t == second));
        Assert.False(host.ReorderWorkspaces([w2, w1]));
        Assert.False(host.ReorderWorkspaces([w2]));
    });

    [Fact]
    public Task Close_other_and_right_tabs_keep_the_anchor() => Ui.Run(async () =>
    {
        var host = new SqlEditorHost();
        var a = host.NewDocument();
        var b = host.NewDocument();
        var c = host.NewDocument();
        var d = host.NewDocument();

        Assert.True(await host.CloseTabsAsync(b, rightOnly: true, default));
        Assert.Equal([a, b], host.ActiveWorkspace!.Tabs);

        Assert.True(await host.CloseTabsAsync(b, rightOnly: false, default));
        Assert.Equal([b], host.ActiveWorkspace.Tabs);
        Assert.DoesNotContain(c, host.Tabs);
        Assert.DoesNotContain(d, host.Tabs);
    });

    [Fact]
    public Task Double_tap_on_tab_header_starts_inline_edit_and_drag_reorders() => Ui.Run(() =>
    {
        var host = new SqlEditorHost();
        var a = host.NewDocument();
        var b = host.NewDocument();
        var ws = host.ActiveWorkspace!;
        var window = new Window { Width = 800, Height = 500, Content = new SqlWorkspaceView { DataContext = ws } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "SqlTabs");

        Point CenterOf(SqlEditorViewModel t, double xFraction = 0.5)
        {
            var item = (Control)list.ContainerFromItem(t)!;
            return item.TranslatePoint(new Point(item.Bounds.Width * xFraction, item.Bounds.Height / 2), window)!.Value;
        }

        // 끌기: a 를 b 의 오른쪽 절반으로.
        var from = CenterOf(a, 0.3);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point(from.X + 20, from.Y));
        window.MouseMove(CenterOf(b, 0.8));
        window.MouseUp(CenterOf(b, 0.8), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([b, a], ws.Tabs);

        // 두 번 누르기: 제자리 편집.
        var p = CenterOf(a, 0.3);
        for (var i = 0; i < 2; i++)
        {
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(a.IsRenaming);
        window.Close();
    });
}
