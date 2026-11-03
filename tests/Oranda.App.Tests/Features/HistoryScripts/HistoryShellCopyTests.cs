using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.HistoryScripts;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.HistoryScripts;

/// <summary>실제 셸(주 창 + 도킹된 실행 이력 창)에 키·마우스 입력을 넣어 복사가 이력 창으로 가는지 본다.</summary>
public sealed class HistoryShellCopyTests
{
    private const string Sql1 = "SELECT 1\nFROM dual";
    private const string Sql2 = "DELETE FROM t";
    private const string Error2 = "ORA-00942: table or view does not exist";

    private static readonly RawInputModifiers Ctrl = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private sealed class Rig : IDisposable
    {
        private Rig(ShellHarness shell, HistoryView view, HistoryViewModel vm, AvaloniaEdit.TextEditor editor)
        {
            Shell = shell;
            View = view;
            Vm = vm;
            Editor = editor;
        }

        public ShellHarness Shell { get; }

        public Window Window => Shell.Window;

        public HistoryView View { get; }

        public HistoryViewModel Vm { get; }

        public AvaloniaEdit.TextEditor Editor { get; }

        public ListBox List => View.FindControl<ListBox>("List")!;

        public IClipboard Clipboard => Window.Clipboard!;

        public static async Task<Rig> StartAsync()
        {
            var shell = new ShellHarness();
            try
            {
                var wiring = shell.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
                await wiring.RunAsync(default);
                await shell.Get<ICommandRegistry>().ExecuteAsync("oranda.file.newSqlEditor", shell.Get<IContextKeys>());
                shell.Get<DockWorkspace>().ShowTool("oranda.panel.history");
                Dispatcher.UIThread.RunJobs();
                shell.Window.UpdateLayout();

                var editor = shell.Window.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().First(e => e.IsEffectivelyVisible);
                var view = shell.Window.GetVisualDescendants().OfType<HistoryView>().Single(v => v.IsEffectivelyVisible);
                var vm = (HistoryViewModel)view.DataContext!;
                await HeadlessHost.WaitUntil(() => true);
                vm.Items.Clear();
                vm.Items.Add(new HistoryItem(Entry(1, Sql1)));
                vm.Items.Add(new HistoryItem(Entry(2, Sql2, HistoryStatus.Failed, "ORA-00942", Error2)));
                Dispatcher.UIThread.RunJobs();
                shell.Window.UpdateLayout();
                return new Rig(shell, view, vm, editor);
            }
            catch
            {
                shell.Dispose();
                throw;
            }
        }

        /// <summary>편집기에 글을 넣어 고르고 포커스를 둔다. 포커스 자리가 편집기로 남는 흐름이다.</summary>
        public void FocusEditor(string text)
        {
            Editor.Text = text;
            Editor.TextArea.Focus();
            Editor.SelectAll();
            Dispatcher.UIThread.RunJobs();
        }

        public void ClickRow(int index, MouseButton button = MouseButton.Left, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            var container = (Control)List.ContainerFromIndex(index)!;
            var at = container.TranslatePoint(new Point(40, container.Bounds.Height / 2), Window)!.Value;
            Window.MouseDown(at, button, modifiers);
            Window.MouseUp(at, button, modifiers);
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        public async Task PressCopyAsync()
        {
            Window.KeyPress(Key.C, Ctrl, PhysicalKey.C, "c");
            Window.KeyRelease(Key.C, Ctrl, PhysicalKey.C, "c");
            await HeadlessHost.WaitUntil(() => true);
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>클립보드가 <paramref name="old"/> 에서 바뀔 때까지(최대 2초) 기다려 그 글을 돌려준다.</summary>
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

    private static HistoryEntry Entry(long id, string sql, HistoryStatus status = HistoryStatus.Succeeded, string? code = null, string? message = null) => new()
    {
        Id = id,
        ProfileId = Guid.Empty,
        ProfileName = "개발",
        ExecutedAt = new DateTimeOffset(2026, 10, 1, 12, 0, (int)id, TimeSpan.Zero),
        SqlText = sql,
        StatementKind = StatementKind.Query,
        Source = HistorySource.Editor,
        Status = status,
        ErrorCode = code,
        ErrorMessage = message,
        ElapsedMs = 42,
        RowsAffected = 3,
    };

    [Fact]
    public Task Ctrl_C_on_a_row_copies_its_full_sql_even_when_the_editor_had_focus_before() => HeadlessHost.Run(async () =>
    {
        using var rig = await Rig.StartAsync();
        rig.FocusEditor("select editor from dual");

        rig.ClickRow(0);
        await rig.Clipboard.SetTextAsync("before");
        await rig.PressCopyAsync();

        Assert.Equal(Sql1, await rig.ClipboardAfterAsync("before"));
    });

    [Fact]
    public Task Ctrl_C_copies_every_selected_row_in_list_order() => HeadlessHost.Run(async () =>
    {
        using var rig = await Rig.StartAsync();
        rig.ClickRow(1);
        rig.ClickRow(0, modifiers: Ctrl); // 아래 줄을 먼저 골라도 복사는 목록 순서를 따른다

        await rig.Clipboard.SetTextAsync("before");
        await rig.PressCopyAsync();

        Assert.Equal(Sql1 + Environment.NewLine + Environment.NewLine + Sql2, await rig.ClipboardAfterAsync("before"));
    });

    [Fact]
    public Task Ctrl_C_in_the_detail_box_copies_the_selected_text_not_the_editor() => HeadlessHost.Run(async () =>
    {
        using var rig = await Rig.StartAsync();
        rig.FocusEditor("select editor from dual");
        rig.ClickRow(0);

        var detail = rig.View.FindControl<SqlCodeView>("DetailBox")!.TextEditorControl;
        detail.TextArea.Focus();
        detail.Select(0, 6);
        Dispatcher.UIThread.RunJobs();

        await rig.Clipboard.SetTextAsync("before");
        await rig.PressCopyAsync();

        Assert.Equal("SELECT", await rig.ClipboardAfterAsync("before"));
    });

    [Fact]
    public Task Right_click_menu_copies_sql_error_and_row() => HeadlessHost.Run(async () =>
    {
        using var rig = await Rig.StartAsync();
        rig.ClickRow(1, MouseButton.Right);
        var menu = rig.List.ContextMenu!;
        Assert.True(menu.IsOpen);
        MenuItem Item(string header) => menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == header);
        Assert.True(Item("오류 복사").IsEnabled);

        await rig.Clipboard.SetTextAsync("before");
        ClickMenuItem(Item("SQL 복사"));
        Assert.Equal(Sql2, await rig.ClipboardAfterAsync("before"));

        rig.ClickRow(1, MouseButton.Right);
        ClickMenuItem(Item("오류 복사"));
        Assert.Equal(Error2, await rig.ClipboardAfterAsync(Sql2));

        rig.ClickRow(1, MouseButton.Right);
        ClickMenuItem(Item("줄 전체 복사"));
        var row = await rig.ClipboardAfterAsync(Error2);
        Assert.Equal(new HistoryItem(Entry(2, Sql2, HistoryStatus.Failed, "ORA-00942", Error2)).TimeText + "\t실패\t개발\t42 ms\t3\t" + Sql2 + "\t" + Error2, row);
    });

    [Fact]
    public void Row_copy_keeps_one_line_per_entry()
    {
        var text = HistoryViewModel.CopyRows([new HistoryItem(Entry(1, Sql1)), new HistoryItem(Entry(2, Sql2))]);
        var lines = text.Split(Environment.NewLine);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("\tSELECT 1 FROM dual\t", lines[0], StringComparison.Ordinal);
    }

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
}
