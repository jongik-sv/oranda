using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Binds;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.Execution;

/// <summary>
/// TSK-03-14: 실제 셸(주 창 + 도킹된 바인드 변수 창)에 키·마우스 입력을 넣어 복사·붙여 넣기가 이 창으로 가는지 본다.
/// 처리기·뷰 모델을 직접 부르지 않는다(TSK-03-09 시험은 그래서 셸 키맵이 Ctrl+C 를 편집기로 보내는 것을 못 잡았다).
/// </summary>
public sealed class BindPaneShellKeysTests
{
    private static readonly RawInputModifiers Ctrl = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    // 입력 칸(TextBox) 자체의 복사 키는 플랫폼 HotkeyConfiguration 을 따른다. 실제 macOS 는 ⌘ 이지만 macOS 의 헤드리스 플랫폼은 Ctrl 이다.
    private static RawInputModifiers TextBoxCtrl =>
        Application.Current!.PlatformSettings!.HotkeyConfiguration.CommandModifiers.HasFlag(KeyModifiers.Meta) ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private sealed class Rig : IDisposable
    {
        private Rig(ShellHarness shell, BindVariablesView view, BindVariablesViewModel vm, AvaloniaEdit.TextEditor editor)
        {
            Shell = shell;
            View = view;
            Vm = vm;
            Editor = editor;
        }

        public ShellHarness Shell { get; }

        public Window Window => Shell.Window;

        public BindVariablesView View { get; }

        public BindVariablesViewModel Vm { get; }

        public AvaloniaEdit.TextEditor Editor { get; }

        public ListBox List => View.FindControl<ListBox>("RowList")!;

        public IClipboard Clipboard => Window.Clipboard!;

        public static async Task<Rig> StartAsync()
        {
            var shell = new ShellHarness();
            try
            {
                var wiring = shell.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
                await wiring.RunAsync(default);
                await shell.Get<ICommandRegistry>().ExecuteAsync("oranda.file.newSqlEditor", shell.Get<IContextKeys>());
                shell.Get<DockWorkspace>().ShowTool(ExecutionController.BindsPanelId);
                Dispatcher.UIThread.RunJobs();
                shell.Window.UpdateLayout();

                var view = shell.Window.GetVisualDescendants().OfType<BindVariablesView>().Single(v => v.IsEffectivelyVisible);
                var vm = (BindVariablesViewModel)view.DataContext!;
                vm.Store.Ensure(["EMP_ID", "ENAME"]);
                vm.Rows[0].Type = BindDataType.Number;
                vm.Rows[0].Text = "7369";
                vm.Rows[1].Text = "SMITH";
                Dispatcher.UIThread.RunJobs();
                shell.Window.UpdateLayout();
                var editor = shell.Window.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().First(e => e.IsEffectivelyVisible);
                return new Rig(shell, view, vm, editor);
            }
            catch
            {
                shell.Dispose();
                throw;
            }
        }

        /// <summary>편집기에 포커스를 둔다: 포커스 자리가 sqlText 가 된다(실앱에서 편집기를 쓰다 바인드 창으로 오는 흐름).</summary>
        public void FocusEditor(string text)
        {
            Editor.Text = text;
            Editor.TextArea.Focus();
            Editor.SelectAll();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>줄 번호 칸(<c>row-head</c>)을 마우스로 누른다.</summary>
        public void ClickRowHead(int index, MouseButton button = MouseButton.Left, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            List.ScrollIntoView(index);
            Window.UpdateLayout();
            var container = (Control)List.ContainerFromIndex(index)!;
            var head = container.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("row-head"));
            var at = head.TranslatePoint(new Point(head.Bounds.Width / 2, head.Bounds.Height / 2), Window)!.Value;
            Window.MouseDown(at, button, modifiers);
            Window.MouseUp(at, button, modifiers);
            Dispatcher.UIThread.RunJobs();
        }

        public async Task PressAsync(Key key, PhysicalKey physical, string symbol, RawInputModifiers? modifiers = null)
        {
            Window.KeyPress(key, modifiers ?? Ctrl, physical, symbol);
            Window.KeyRelease(key, modifiers ?? Ctrl, physical, symbol);
            await HeadlessHost.WaitUntil(() => true);
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>클립보드가 <paramref name="old"/> 에서 바뀔 때까지(최대 2초) 기다려 그 글을 돌려준다(명령은 비동기로 돈다).</summary>
        public async Task<string?> ClipboardAfterAsync(string old)
        {
            string? text = old;
            for (var i = 0; i < 200 && text == old; i++)
            {
                Dispatcher.UIThread.RunJobs();
                text = await Clipboard.TryGetTextAsync();
                if (text == old)
                {
                    await Task.Delay(10);
                }
            }

            return text;
        }

        public void Dispose() => Shell.Dispose();
    }

    private static string Expected(BindVariablesViewModel vm, bool header = false) => BindTsv.Format(vm.Rows, header);

    [Fact]
    public Task Ctrl_C_after_clicking_rows_copies_the_bind_rows_even_when_the_editor_had_focus_before() => HeadlessHost.Run(async () =>
    {
        using var rig = await Rig.StartAsync();
        rig.FocusEditor("select 1 from dual");
        Assert.Equal("sqlText", rig.Shell.Get<ContextKeyService>().FocusSlot);

        rig.ClickRowHead(0);
        rig.ClickRowHead(1, modifiers: RawInputModifiers.Shift);
        Assert.Equal(2, rig.Vm.SelectedRows.Count);
        Assert.IsNotType<TextBox>(rig.Window.FocusManager!.GetFocusedElement());
        Assert.True(rig.Window.FocusManager.GetFocusedElement() is Visual v && v.GetSelfAndVisualAncestors().Contains(rig.List));

        await rig.Clipboard.SetTextAsync("before");
        await rig.PressAsync(Key.C, PhysicalKey.C, "c");

        Assert.Equal(Expected(rig.Vm), await rig.ClipboardAfterAsync("before"));
        Assert.Equal("select 1 from dual", rig.Editor.Text); // 편집기는 그대로
    });

    [Fact]
    public Task Ctrl_V_on_a_selected_row_fills_values_from_tsv_on_the_clipboard() => HeadlessHost.Run(async () =>
    {
        using var rig = await Rig.StartAsync();
        rig.FocusEditor("select :emp_id, :ename from dual");
        rig.ClickRowHead(0);

        var other = BindVariablesViewModel.ForHost(new EventBus(), new SqlEditorHost());
        other.Store.Ensure(["EMP_ID", "ENAME"]);
        other.Rows[0].Type = BindDataType.Number;
        other.Rows[0].Text = "7499";
        other.Rows[1].Text = "ALLEN";
        await rig.Clipboard.SetTextAsync(BindTsv.Format(other.Rows));
        await rig.PressAsync(Key.V, PhysicalKey.V, "v");

        Assert.Equal("7499", rig.Vm.Rows[0].Text);
        Assert.Equal("ALLEN", rig.Vm.Rows[1].Text);
        Assert.Equal("select :emp_id, :ename from dual", rig.Editor.Text); // 편집기에 붙지 않았다
    });

    [Fact]
    public Task Ctrl_C_in_a_value_cell_without_text_selection_copies_rows_and_with_selection_copies_the_text() => HeadlessHost.Run(async () =>
    {
        using var rig = await Rig.StartAsync();
        rig.FocusEditor("select 1 from dual");
        var box = rig.List.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "ValueBox" && t.DataContext == rig.Vm.Rows[1]);
        box.Focus();
        box.SelectionStart = box.SelectionEnd = 0;
        Dispatcher.UIThread.RunJobs();

        await rig.PressAsync(Key.C, PhysicalKey.C, "c");
        Assert.Equal(Expected(rig.Vm), await rig.Clipboard.TryGetTextAsync());

        box.SelectionStart = 0;
        box.SelectionEnd = 3;
        await rig.PressAsync(Key.C, PhysicalKey.C, "c", TextBoxCtrl); // 글을 골랐으면 창은 비켜 주고 입력 칸이 제 복사 키로 복사한다
        Assert.Equal("SMI", await rig.ClipboardAfterAsync(Expected(rig.Vm)));
    });

    [Fact]
    public Task Right_click_menu_copy_with_header_and_paste_work_in_the_shell() => HeadlessHost.Run(async () =>
    {
        using var rig = await Rig.StartAsync();
        rig.ClickRowHead(0);
        rig.ClickRowHead(0, MouseButton.Right);
        var menu = rig.List.ContextMenu!;
        Assert.True(menu.IsOpen);
        var items = menu.Items.OfType<MenuItem>().ToList();

        await rig.Clipboard.SetTextAsync("before");
        ClickMenuItem(items[1]); // 머리글 포함 복사
        await HeadlessHost.WaitUntil(() => true);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(BindTsv.Format([rig.Vm.Rows[0]], true), await rig.Clipboard.TryGetTextAsync());

        await rig.Clipboard.SetTextAsync("ENAME\tWARD\tVARCHAR2\tIN");
        rig.ClickRowHead(0, MouseButton.Right);
        ClickMenuItem(menu.Items.OfType<MenuItem>().ToList()[2]); // 붙여 넣기
        await HeadlessHost.WaitUntil(() => rig.Vm.Rows[1].Text == "WARD");
    });

    /// <summary>열린 메뉴 항목을 그 팝업 창에서 마우스로 누른다.</summary>
    private static void ClickMenuItem(MenuItem item)
    {
        Dispatcher.UIThread.RunJobs();
        var top = TopLevel.GetTopLevel(item)!;
        top.UpdateLayout();
        var at = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), top)!.Value;
        top.MouseDown(at, MouseButton.Left);
        top.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public Task Ctrl_C_in_the_editor_still_copies_the_editor_selection() => HeadlessHost.Run(async () =>
    {
        using var rig = await Rig.StartAsync();
        rig.FocusEditor("select 2 from dual");
        await rig.Clipboard.SetTextAsync("before");
        await rig.PressAsync(Key.C, PhysicalKey.C, "c");
        Assert.Equal("select 2 from dual", await rig.ClipboardAfterAsync("before"));
    });

    [Fact]
    public Task Edit_key_scope_is_found_from_descendants_and_not_outside() =>
        HeadlessHost.Run(() =>
        {
            var inner = new TextBlock();
            var scope = new Border { Child = inner };
            EditKeyScope.SetOwns(scope, true);
            var outside = new Border();
            var root = new StackPanel { Children = { scope, outside } };
            var window = new Window { Content = root };
            window.Show();
            try
            {
                Assert.True(EditKeyScope.Contains(inner));
                Assert.True(EditKeyScope.Contains(scope));
                Assert.False(EditKeyScope.Contains(outside));
                Assert.False(EditKeyScope.Contains(null));
                Assert.False(EditKeyScope.TryRoute(outside, new KeyGesture(Key.C, KeyModifiers.Control)));
                Assert.False(EditKeyScope.TryRoute(null, new KeyGesture(Key.C, KeyModifiers.Control)));
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task System_menu_copy_routes_to_the_bind_pane_when_it_has_focus() => HeadlessHost.Run(async () =>
    {
        using var rig = await Rig.StartAsync();
        rig.ClickRowHead(0);
        await rig.Clipboard.SetTextAsync("before");
        var focused = rig.Window.FocusManager!.GetFocusedElement();
        var primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        Assert.True(EditKeyScope.TryRoute(focused, new KeyGesture(Key.C, primary)));
        await HeadlessHost.WaitUntil(() => true);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(BindTsv.Format([rig.Vm.Rows[0]]), await rig.Clipboard.TryGetTextAsync());
    });
}
