using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Navigation;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.Navigation;

/// <summary>테이블 레이아웃 창(헤드리스): 범위·개별·전체 선택, 두 번 누르기(선택 안·밖), ⌘C = 넣기 글, 거르기, 입력 칸 포커스.</summary>
public class TableLayoutWindowTests
{
    private static readonly TableLayoutRow[] Rows =
    [
        new(1, "CODE_GRP", "VARCHAR2(20)", true, null, "코드 그룹", true),
        new(2, "CODE", "VARCHAR2(30)", true, null, "코드", true),
        new(3, "CODE_NM", "VARCHAR2(200)", true, null, null, false),
        new(4, "SORT_SEQ", "NUMBER(5)", false, "0", "정렬 순서", false),
        new(5, "USE_YN", "CHAR(1)", true, "'Y'", "사용 여부", false),
    ];

    // 개별 선택·⌘A·⌘C 의 수식키는 플랫폼 설정을 따른다(mac 실앱 = ⌘, Windows·헤드리스 = Ctrl).
    private static RawInputModifiers Primary
        => Application.Current!.PlatformSettings!.HotkeyConfiguration.CommandModifiers.HasFlag(KeyModifiers.Meta) ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static (SqlEditorViewModel Tab, TableLayoutWindow Window) Open(TableLayoutOptions? options = null)
    {
        var tab = new SqlEditorViewModel(Guid.NewGuid(), "select ");
        tab.UpdateCaret(7, 7, 0);
        var session = new TableLayoutSession(tab, new TableLayoutModel("SCOTT.TB_CODE", Rows), options ?? new TableLayoutOptions("a", false, false, true, false));
        var window = new TableLayoutWindow(session);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (tab, window);
    }

    // 목록 항목은 배치·그리기 패스 뒤에야 만들어진다. 부하로 그 패스가 늦으면 곧바로 찾지 못하므로 패스를 돌려 가며 기다린다.
    private static ListBoxItem Item(TableLayoutWindow w, string name)
    {
        for (var attempt = 0; ; attempt++)
        {
            var item = w.List.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is TableLayoutRow r && r.Name == name);
            if (item is not null || attempt >= 50)
            {
                return item ?? throw new InvalidOperationException($"목록 항목 '{name}' 이 만들어지지 않았다.");
            }

            w.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
    }

    private static Point At(TableLayoutWindow w, Control c, double x) => c.TranslatePoint(new Point(x, c.Bounds.Height / 2), w)!.Value;

    // x 를 바꾸면 바로 앞 클릭과 같은 자리 연속 클릭(두 번 누르기)으로 묶이지 않는다.
    private static void Click(TableLayoutWindow w, string name, RawInputModifiers mods = RawInputModifiers.None, int times = 1, double x = 20)
    {
        var p = At(w, Item(w, name), x);
        for (var i = 0; i < times; i++)
        {
            w.MouseDown(p, MouseButton.Left, mods);
            w.MouseUp(p, MouseButton.Left, mods);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static string[] Selected(TableLayoutWindow w) => w.SelectedRows.Select(r => r.Name).ToArray();

    [Fact]
    public Task Shift_click_selects_a_range_and_primary_click_adds_single_rows_and_enter_inserts_in_table_order() => HeadlessHost.Run(() =>
    {
        var (tab, w) = Open();
        try
        {
            Click(w, "USE_YN");
            Click(w, "CODE", Primary);
            Assert.Equal(["USE_YN", "CODE"], Selected(w));

            Click(w, "CODE_NM");
            Click(w, "USE_YN", RawInputModifiers.Shift);
            Assert.Equal(3, w.SelectedRows.Count);

            Click(w, "CODE_GRP", Primary);
            w.List.Focus();
            w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("select a.CODE_GRP, a.CODE_NM, a.SORT_SEQ, a.USE_YN", tab.Text);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Current_row_marker_follows_the_clicked_row_not_the_selection_order() => HeadlessHost.Run(() =>
    {
        var (_, w) = Open();
        try
        {
            Click(w, "USE_YN");
            Click(w, "CODE", RawInputModifiers.Shift, x: 120);
            Assert.Equal("CODE", w.CurrentRow?.Name);
            Assert.Equal(4, w.SelectedRows.Count);

            w.FilterBox.Text = "c";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("CODE", w.CurrentRow?.Name);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Select_all_then_copy_puts_the_same_text_as_insert_on_the_clipboard() => HeadlessHost.Run(async () =>
    {
        var (tab, w) = Open(new TableLayoutOptions("", true, true, false, false));
        try
        {
            w.List.Focus();
            w.KeyPress(Key.A, Primary, PhysicalKey.A, "a");
            Assert.Equal(Rows.Length, w.SelectedRows.Count);

            w.KeyPress(Key.C, Primary, PhysicalKey.C, "c");
            await HeadlessHost.WaitUntil(() => true);
            var copied = await w.Clipboard!.TryGetTextAsync();
            Assert.Equal("code_grp, code, code_nm, sort_seq, use_yn, ", copied);
            Assert.True(w.PasteSelection());
            Assert.Equal("select " + copied, tab.Text);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Double_click_inside_the_selection_inserts_the_selection_and_outside_only_that_row() => HeadlessHost.Run(() =>
    {
        var (tab, w) = Open();
        try
        {
            Click(w, "CODE");
            Click(w, "USE_YN", Primary, x: 120);
            Click(w, "USE_YN", times: 2);
            Assert.Equal("select a.CODE, a.USE_YN", tab.Text);
            Assert.Equal(["CODE", "USE_YN"], Selected(w).Order().ToArray());

            tab.Select(tab.Text.Length, 0);
            Click(w, "CODE_GRP", times: 2);
            Assert.Equal("select a.CODE, a.USE_YNa.CODE_GRP", tab.Text);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Filter_hides_rows_keeps_the_visible_selection_and_its_box_keeps_its_own_keys() => HeadlessHost.Run(() =>
    {
        var (tab, w) = Open();
        try
        {
            Click(w, "CODE");
            Click(w, "SORT_SEQ", Primary);
            w.FilterBox.Text = "code";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["CODE_GRP", "CODE", "CODE_NM"], w.VisibleRows.Select(r => r.Name).ToArray());
            Assert.Equal(["CODE"], Selected(w));

            // 거르기 칸의 Enter·⌘A 는 칸 안의 글 편집이다(편집기에 넣지 않는다).
            w.FilterBox.Focus();
            w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            w.KeyPress(Key.A, Primary, PhysicalKey.A, "a");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("select ", tab.Text);
            Assert.Equal(["CODE"], Selected(w));
        }
        finally
        {
            w.Close();
        }
    });
}
