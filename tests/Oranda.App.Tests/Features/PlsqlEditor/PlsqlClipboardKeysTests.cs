using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.PlsqlEditor;

/// <summary>
/// PL/SQL 편집기에서 고른 글의 복사·잘라내기·실행 취소: 셸 키맵이 Ctrl+C 등을 <c>oranda.edit.*</c> 명령으로 가져가므로,
/// 그 명령의 활성 조건(<c>hasSelection</c>·<c>canUndo</c>)이 숨은 SQL 탭이 아니라 포커스를 가진 PL/SQL 편집기를 봐야 한다.
/// 실제 셸 창에 키를 넣는다(처리기를 직접 부르면 활성 조건에 막혀 키가 삼켜지는 것을 못 잡는다).
/// </summary>
[Collection(Oranda.App.Tests.Features.PlsqlWorkspace.PlsqlStatics.Name)]
public sealed class PlsqlClipboardKeysTests
{
    private static readonly RawInputModifiers Ctrl = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private const string Source = "create or replace procedure p is begin null; end;";

    private static async Task<(ShellHarness Shell, PlsqlEditorViewModel Plsql, AvaloniaEdit.TextEditor Editor)> OpenAsync()
    {
        var h = new ShellHarness();
        var wiring = h.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
        await wiring.RunAsync(default); // 편집기 상태 키(hasSelection 등)를 문맥 키로 옮기는 배선
        var registry = h.Get<ICommandRegistry>();
        var keys = h.Get<IContextKeys>();
        await registry.ExecuteAsync("oranda.file.newSqlEditor", keys); // 숨은 SQL 탭: 고른 글·되돌릴 것이 없다
        Dispatcher.UIThread.RunJobs();
        Assert.False(SqlEditorHost.Current.Active!.HasSelection);

        var plsql = new PlsqlEditorViewModel();
        plsql.Editor.Document.Text = Source;
        h.Get<DockWorkspace>().OpenDocument("oranda.panel.plsqlEditor", plsql, "P"); // 실제 앱처럼 도킹 문서로 연다
        Dispatcher.UIThread.RunJobs();
        h.Window.UpdateLayout();
        PlsqlEditorViewModel.Active = plsql;
        var view = h.Window.GetVisualDescendants().OfType<PlsqlEditorView>().Single(v => v.IsEffectivelyVisible);
        var editor = view.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().First(e => e.IsEffectivelyVisible);
        editor.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("plsql", h.Get<ContextKeyService>().FocusSlot);
        return (h, plsql, editor);
    }

    private static async Task PressAsync(ShellHarness h, Key key, PhysicalKey physical, string symbol)
    {
        h.Window.KeyPress(key, Ctrl, physical, symbol);
        h.Window.KeyRelease(key, Ctrl, physical, symbol);
        await HeadlessHost.WaitUntil(() => true);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<string?> ClipboardAfterAsync(ShellHarness h, string old)
    {
        string? text = old;
        for (var i = 0; i < 200 && text == old; i++)
        {
            Dispatcher.UIThread.RunJobs();
            text = await h.Window.Clipboard!.TryGetTextAsync();
            if (text == old)
            {
                await Task.Delay(10);
            }
        }

        return text;
    }

    [Fact]
    public Task Ctrl_C_copies_the_selection_of_the_focused_plsql_editor() => HeadlessHost.Run(async () =>
    {
        var (h, plsql, editor) = await OpenAsync();
        using (h)
        {
            try
            {
                editor.Select(0, 16); // "create or replac"
                Dispatcher.UIThread.RunJobs();
                Assert.True(plsql.Editor.HasSelection);
                Assert.True(h.Get<IContextKeys>().Evaluate(h.Get<ICommandRegistry>().Find("oranda.edit.copy")!.Enablement));

                await h.Window.Clipboard!.SetTextAsync("before");
                await PressAsync(h, Key.C, PhysicalKey.C, "c");

                Assert.Equal("create or replac", await ClipboardAfterAsync(h, "before"));
                Assert.Equal(Source, plsql.Editor.Text);
            }
            finally
            {
                PlsqlEditorViewModel.Active = null;
            }
        }
    });

    [Fact]
    public Task Ctrl_X_and_Ctrl_Z_work_in_the_focused_plsql_editor() => HeadlessHost.Run(async () =>
    {
        var (h, plsql, editor) = await OpenAsync();
        using (h)
        {
            try
            {
                editor.Select(0, 7); // "create "
                Dispatcher.UIThread.RunJobs();
                await h.Window.Clipboard!.SetTextAsync("before");
                await PressAsync(h, Key.X, PhysicalKey.X, "x");

                Assert.Equal("create ", await ClipboardAfterAsync(h, "before"));
                Assert.Equal(Source[7..], plsql.Editor.Text);

                await PressAsync(h, Key.Z, PhysicalKey.Z, "z");
                Assert.Equal(Source, plsql.Editor.Text);
            }
            finally
            {
                PlsqlEditorViewModel.Active = null;
            }
        }
    });

    [Fact]
    public Task Copy_is_disabled_without_a_selection_in_the_focused_plsql_editor() => HeadlessHost.Run(async () =>
    {
        var (h, plsql, editor) = await OpenAsync();
        using (h)
        {
            try
            {
                var keys = h.Get<IContextKeys>();
                var copy = h.Get<ICommandRegistry>().Find("oranda.edit.copy")!.Enablement;
                editor.Select(0, 0);
                Dispatcher.UIThread.RunJobs();
                Assert.False(plsql.Editor.HasSelection);
                Assert.False(keys.Evaluate(copy));

                // 숨은 SQL 탭에 고른 글이 있어도 포커스를 가진 PL/SQL 편집기 기준이다.
                var sql = SqlEditorHost.Current.Active!;
                sql.Document.Text = "select 1 from dual";
                sql.Select(0, 6);
                Dispatcher.UIThread.RunJobs();
                Assert.False(keys.Evaluate(copy));

                PlsqlEditorViewModel.Active = null; // PL/SQL 이 아닌 곳: SQL 탭 기준으로 돌아간다
                h.Get<ContextKeyService>().SetFocusSlot("sqlText");
                Assert.True(keys.Evaluate(copy));
            }
            finally
            {
                PlsqlEditorViewModel.Active = null;
            }
        }
    });
}
