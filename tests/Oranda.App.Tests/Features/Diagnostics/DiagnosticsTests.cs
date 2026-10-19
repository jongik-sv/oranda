using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.Diagnostics;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Composition;
using Oranda.Core.Language;
using Oranda.Core.Text;
using Oranda.Language;

namespace Oranda.App.Tests.Features.Diagnostics;

/// <summary>TSK-04-03 편집기 쪽: 문법 오류 밑줄(디바운스·백그라운드 분석·취소), 서버 컴파일 오류 경로, 편집기에 붙는 방식.</summary>
public sealed class DiagnosticsTests
{
    private static readonly ILanguageService Language = new LanguageService();

    private static (Window Window, SqlEditorView View, SqlEditorViewModel Vm) Show(string text)
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), text);
        var view = new SqlEditorView { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, vm);
    }

    private static async Task WaitUntil(Func<bool> done, int timeoutMs = 20000)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < timeoutMs)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public Task Syntax_error_is_underlined_after_typing_stops() => Ui.Run(async () =>
    {
        var (window, view, vm) = Show("SELECT 1 FROM dual");
        var diagnostics = EditorDiagnostics.Attach(view, () => Language);
        await WaitUntil(() => diagnostics.AppliedAnalyses >= 1);
        Assert.Empty(diagnostics.Current);

        vm.Document.Text = "SELECT * FROM WHERE x = 1";
        Assert.True(diagnostics.IsPending);
        await WaitUntil(() => diagnostics.AppliedAnalyses >= 2);

        var error = Assert.Single(diagnostics.Current);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Equal(new TextRange(vm.Text.IndexOf("WHERE", StringComparison.Ordinal), 5), error.Range);
        Assert.False(diagnostics.IsPending);
        Assert.Contains(diagnostics, view.TextEditorControl.TextArea.TextView.BackgroundRenderers);
        window.Close();
    });

    [Fact]
    public Task Underline_follows_edits_and_is_cleared_when_fixed() => Ui.Run(async () =>
    {
        var (window, view, vm) = Show("SELECT * FROM WHERE x = 1");
        var diagnostics = EditorDiagnostics.Attach(view, () => Language);
        await diagnostics.AnalyzeNowAsync();
        int at = Assert.Single(diagnostics.Current).Range.Start;

        vm.Document.Insert(0, "  ");
        Assert.Equal(at + 2, Assert.Single(diagnostics.Current).Range.Start);

        vm.Document.Text = "SELECT * FROM t WHERE x = 1";
        await diagnostics.AnalyzeNowAsync();
        Assert.Empty(diagnostics.Current);
        window.Close();
    });

    [Fact]
    public Task Stale_result_is_discarded_when_text_changed_during_analysis() => Ui.Run(async () =>
    {
        var (window, view, vm) = Show("SELECT * FROM WHERE x = 1");
        var gate = new TaskCompletionSource();
        var slow = new GatedLanguage(Language, gate.Task);
        var diagnostics = EditorDiagnostics.Attach(view, () => slow);

        var analysis = diagnostics.AnalyzeNowAsync();
        await WaitUntil(() => slow.Calls >= 1);
        vm.Document.Insert(0, " ");
        gate.SetResult();
        await analysis;

        // 옛 글에 대한 결과는 버린다(그 사이 디바운스가 새 글을 다시 분석했다면 새 위치만 남는다).
        Assert.True(slow.Cancelled);
        int where = vm.Text.IndexOf("WHERE", StringComparison.Ordinal);
        Assert.All(diagnostics.Current, d => Assert.Equal(where, d.Range.Start));
        window.Close();
    });

    [Fact]
    public Task Server_compile_errors_share_the_underline_and_survive_reanalysis() => Ui.Run(async () =>
    {
        var (window, view, vm) = Show("BEGIN\n  x := 1;\nEND;");
        var diagnostics = EditorDiagnostics.Attach(view, () => Language);
        var server = new Diagnostic(new TextRange(vm.Text.IndexOf('x'), 1), DiagnosticSeverity.Error, "PLS-00201") { Code = "Server" };

        diagnostics.SetServerDiagnostics([server]);
        await diagnostics.AnalyzeNowAsync();

        Assert.Equal("PLS-00201", Assert.Single(diagnostics.Current).Message);
        diagnostics.SetServerDiagnostics([]);
        Assert.Empty(diagnostics.Current);
        window.Close();
    });

    [Fact]
    public Task Unsupported_23ai_statement_is_kept_as_info_not_error() => Ui.Run(async () =>
    {
        var (window, view, _) = Show("CREATE TABLE IF NOT EXISTS emb (id NUMBER, v VECTOR(3, FLOAT32))");
        var diagnostics = EditorDiagnostics.Attach(view, () => Language);

        await diagnostics.AnalyzeNowAsync();

        Assert.DoesNotContain(diagnostics.Current, d => d.Severity == DiagnosticSeverity.Error);
        window.Close();
    });

    [Fact]
    public Task Startup_task_attaches_to_editors_opened_later() => Ui.Run(() =>
    {
        var task = new EditorDiagnosticsStartup();
        Assert.Equal(StartupPhase.AfterFirstFrame, task.Phase);
        Assert.NotNull(typeof(EditorDiagnosticsStartup).GetCustomAttributes(typeof(OrandaServiceAttribute), false).SingleOrDefault());

        EditorDiagnosticsStartup.Install();
        var (window, view, _) = Show("SELECT 1 FROM dual");

        Assert.NotNull(EditorDiagnostics.Of(view));
        Assert.Same(EditorDiagnostics.Of(view), EditorDiagnostics.Attach(view));
        window.Close();
    });

    [Fact]
    public Task Squiggle_spans_the_whole_rectangle() => Ui.Run(() =>
    {
        var g = EditorDiagnostics.Squiggle(new Avalonia.Rect(10, 0, 30, 16));

        Assert.True(g.Bounds.Left <= 10.01 && g.Bounds.Right >= 39);
    });

    private sealed class GatedLanguage(ILanguageService inner, Task gate) : ILanguageService
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool Cancelled { get; private set; }

        public async Task<IReadOnlyList<Diagnostic>> AnalyzeAsync(string text, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            await gate.ConfigureAwait(false);
            Cancelled |= ct.IsCancellationRequested;
            return await inner.AnalyzeAsync(text, CancellationToken.None).ConfigureAwait(false);
        }

        public IReadOnlyList<Oranda.Core.Execution.SqlStatement> Split(string text, SplitOptions o) => inner.Split(text, o);

        public Oranda.Core.Execution.SqlStatement? GetStatementAt(string text, int offset, SplitOptions o) => inner.GetStatementAt(text, offset, o);

        public CompletionContext GetCompletionContext(string text, int offset) => inner.GetCompletionContext(text, offset);
    }
}
