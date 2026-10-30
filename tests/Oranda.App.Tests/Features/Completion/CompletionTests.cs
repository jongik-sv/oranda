using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit.CodeCompletion;
using Oranda.App.Features.Completion;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Language.Completion;

namespace Oranda.App.Tests.Features.Completion;

/// <summary>TSK-04-05 헤드리스 UI 시험: 편집기 위 자동완성 목록·인자 도움말 창, <c>.</c> 입력 자동 열기, 명령 등록, 150 ms.</summary>
public sealed class CompletionTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");
    private static readonly DbObjectRef EmpPkg = new("SCOTT", "EMP_PKG", "PACKAGE");

    [Fact]
    public void Commands_are_registered_with_their_conditions()
    {
        var registry = ShellTestHelpers.CreateCommandRegistry();
        var trigger = registry.Find(CompletionCommands.TriggerCompletion);
        Assert.NotNull(trigger);
        Assert.Equal("editorFocus", trigger.When);
        var hints = registry.Find(CompletionCommands.ParameterHints);
        Assert.NotNull(hints);
        Assert.Equal("hasSession", hints.Enablement);
        Assert.Equal("Oranda.App.Features.Completion", typeof(CompletionCommands).Namespace);
    }

    private static (Window Window, SqlEditorView View, SqlEditorViewModel Vm) Show(string text, int caret)
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), text);
        var view = new SqlEditorView { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        view.TextEditorControl.CaretOffset = caret;
        view.TextEditorControl.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        return (window, view, vm);
    }

    private static CompletionController Controller(ISession? session = null)
    {
        var meta = new Meta();
        var provider = new CompletionProvider(meta);
        session ??= new Session();
        return new CompletionController
        {
            ProviderResolver = () => provider,
            MetadataResolver = () => meta,
            SessionResolver = _ => session,
        };
    }

    private static bool Optimized =>
        typeof(CompletionController).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled != true;

    [Trait("Category", "Perf")]
    [Fact]
    public Task Alias_dot_opens_emp_columns_within_150ms() => HeadlessHost.Run(async () =>
    {
        var (window, view, _) = Show("SELECT e. FROM emp e", 9);
        var c = Controller();
        var area = view.TextEditorControl.TextArea;

        Assert.True(await c.ShowCompletionAsync(area, null, automatic: true)); // 데우기
        c.CloseCompletion();

        var sw = Stopwatch.StartNew();
        Assert.True(await c.ShowCompletionAsync(area, null, automatic: true));
        sw.Stop();

        var w = Assert.IsType<CompletionWindow>(c.CurrentWindow);
        var labels = w.CompletionList.CompletionData.Cast<CompletionData>().Select(d => d.Text).ToList();
        Assert.Equal(["EMPNO", "ENAME", "SAL"], labels);
        Assert.Equal(9, w.StartOffset);
        long limit = Optimized ? 150 : 450; // PRD 150 ms(Release). Debug·병렬 부하는 3배.
        Assert.True(sw.ElapsedMilliseconds <= limit, $"{sw.ElapsedMilliseconds} ms (한도 {limit} ms)");

        // 고르면 넣을 글자가 들어간다. 커서 앞 별칭 e 가 소문자라 소문자로 넣는다.
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(w.CompletionList.ListBox); // 팝업에 AvaloniaEdit 템플릿이 붙었다
        w.CompletionList.SelectedItem = w.CompletionList.CompletionData[1];
        Assert.Same(w.CompletionList.CompletionData[1], w.CompletionList.SelectedItem);
        w.CompletionList.RequestInsertion(EventArgs.Empty);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("SELECT e.ename FROM emp e", view.TextEditorControl.Text);
        c.CloseCompletion();
        window.Close();
    });

    [Fact]
    public Task Typing_dot_in_the_editor_opens_the_list_by_itself() => HeadlessHost.Run(async () =>
    {
        var (window, view, _) = Show("select e FROM emp e", 8);
        var shared = CompletionController.Shared;
        var (p, m, s) = (shared.ProviderResolver, shared.MetadataResolver, shared.SessionResolver);
        var fake = Controller();
        shared.ProviderResolver = fake.ProviderResolver;
        shared.MetadataResolver = fake.MetadataResolver;
        shared.SessionResolver = fake.SessionResolver;
        try
        {
            view.TextEditorControl.TextArea.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "." });
            Assert.Equal("select e. FROM emp e", view.TextEditorControl.Text);
            await HeadlessHost.WaitUntil(() => shared.CurrentWindow is not null);
            var items = shared.CurrentWindow!.CompletionList.CompletionData.Cast<CompletionData>().ToList();
            Assert.Contains(items, d => d.Text == "ENAME" && d.Item.InsertText == "ename");
        }
        finally
        {
            shared.CloseCompletion();
            (shared.ProviderResolver, shared.MetadataResolver, shared.SessionResolver) = (p, m, s);
            window.Close();
        }
    });

    [Fact]
    public Task Dot_without_session_does_nothing_but_ctrl_space_still_offers_keywords() => HeadlessHost.Run(async () =>
    {
        var (window, view, _) = Show("sel", 3);
        var meta = new Meta();
        var provider = new CompletionProvider(meta);
        var c = new CompletionController { ProviderResolver = () => provider, SessionResolver = _ => null };
        var area = view.TextEditorControl.TextArea;

        Assert.False(await c.ShowCompletionAsync(area, null, automatic: true));
        Assert.Null(c.CurrentWindow);

        Assert.True(await c.ShowCompletionAsync(area, null, automatic: false));
        var only = Assert.Single(c.CurrentWindow!.CompletionList.CompletionData.Cast<CompletionData>());
        Assert.Equal("select", only.Item.InsertText);
        c.CloseCompletion();
        window.Close();
    });

    [Fact]
    public Task Commands_open_completion_and_parameter_hints_on_the_active_tab() => HeadlessHost.Run(async () =>
    {
        var host = new SqlEditorHost();
        var vm = host.NewDocument("BEGIN emp_pkg.hire('A', ");
        var view = new SqlEditorView { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        view.TextEditorControl.CaretOffset = vm.Document.TextLength;

        var c = Controller();
        var handler = new CompletionCommandHandler { Host = host, Controller = c };
        await handler.ExecuteAsync(new CommandContext(CompletionCommands.ParameterHints, FakeContextKeys.AllTrue), CancellationToken.None);
        var insight = Assert.IsType<OverloadInsightWindow>(c.CurrentInsight);
        var provider = Assert.IsType<SignatureOverloads>(insight.Provider);
        Assert.Equal("HIRE(P_NAME IN VARCHAR2, P_SAL IN NUMBER := …)", provider.CurrentHeader);
        Assert.StartsWith("P_SAL", (string)provider.CurrentContent, StringComparison.Ordinal);
        c.CloseInsight();

        vm.Document.Text = "BEGIN emp_pkg.";
        view.TextEditorControl.CaretOffset = vm.Document.TextLength;
        await handler.ExecuteAsync(new CommandContext(CompletionCommands.TriggerCompletion, FakeContextKeys.AllTrue), CancellationToken.None);
        var list = c.CurrentWindow!.CompletionList.CompletionData.Cast<CompletionData>().Select(d => d.Text).ToList();
        Assert.Equal(["HIRE", "FIRE"], list);
        c.CloseCompletion();
        window.Close();
    });

    [Fact]
    public Task Shortcut_opens_the_window_on_the_focused_editor_not_the_active_sql_tab() => HeadlessHost.Run(async () =>
    {
        // 활성 SQL 탭(숨은 쪽)과, 호스트 밖에서 포커스를 가진 편집기(PL/SQL·MyBatis 탭 역할)를 따로 둔다.
        var host = new SqlEditorHost();
        var hidden = host.NewDocument("sel");
        var hiddenView = new SqlEditorView { DataContext = hidden };
        var hiddenWindow = new Window { Width = 600, Height = 400, Content = hiddenView };
        hiddenWindow.Show();
        Dispatcher.UIThread.RunJobs();
        hiddenView.TextEditorControl.CaretOffset = 3;

        var (focusedWindow, focusedView, _) = Show("sel", 3);
        Assert.True(focusedView.TextEditorControl.TextArea.IsKeyboardFocusWithin);

        var c = Controller();
        var handler = new CompletionCommandHandler { Host = host, Controller = c };
        await handler.ExecuteAsync(new CommandContext(CompletionCommands.TriggerCompletion, FakeContextKeys.AllTrue), CancellationToken.None);

        Assert.Same(focusedView.TextEditorControl.TextArea, c.CurrentWindow!.TextArea);
        c.CloseCompletion();
        focusedWindow.Close();
        hiddenWindow.Close();
    });

    [Fact]
    public Task Shortcut_without_any_sql_tab_still_opens_on_the_focused_editor() => HeadlessHost.Run(async () =>
    {
        var (window, view, _) = Show("sel", 3);
        var c = Controller();
        var handler = new CompletionCommandHandler { Host = new SqlEditorHost(), Controller = c };
        await handler.ExecuteAsync(new CommandContext(CompletionCommands.TriggerCompletion, FakeContextKeys.AllTrue), CancellationToken.None);

        Assert.Same(view.TextEditorControl.TextArea, c.CurrentWindow!.TextArea);
        c.CloseCompletion();
        window.Close();
    });

    [Fact]
    public Task Result_is_dropped_when_the_caret_moved_away_meanwhile() => HeadlessHost.Run(async () =>
    {
        var (window, view, _) = Show("SELECT e. FROM emp e", 9);
        var gate = new TaskCompletionSource();
        var c = new CompletionController
        {
            ProviderResolver = () => new GatedProvider(gate.Task, new CompletionProvider(new Meta())),
            SessionResolver = _ => new Session(),
        };
        var pending = c.ShowCompletionAsync(view.TextEditorControl.TextArea, null, automatic: false);
        view.TextEditorControl.CaretOffset = 0;
        gate.SetResult();
        Assert.False(await pending);
        Assert.Null(c.CurrentWindow);
        window.Close();
    });

    // ---- 가짜 ----------------------------------------------------------------------------------

    private sealed class GatedProvider(Task gate, ICompletionProvider inner) : ICompletionProvider
    {
        public async Task<CompletionResult> GetCompletionsAsync(ISession? session, string text, int offset, CancellationToken ct)
        {
            await gate.ConfigureAwait(false);
            return await inner.GetCompletionsAsync(session, text, offset, ct).ConfigureAwait(false);
        }
    }

    private sealed class Session : ISession
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = new("test", "scott", "localhost/FREEPDB1");

        public ServerInfo Server { get; } = new("23.26.0.0.0", "FREE", "localhost", "FREEPDB1", "FREE", "FREEPDB1", true, 1,
            new ServerCapabilities(false, true, true, false, false), SessionUser: "SCOTT");

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => "FREE/FREEPDB1/SCOTT";

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Meta : IMetadataProvider
    {
        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["SCOTT"]);

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DbObjectRef>>(q.Owner is null or "SCOTT" ? [Emp, EmpPkg] : []);

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct)
            => table.Name == "EMP"
                ? Task.FromResult(new TableInfo(Emp, [new ColumnInfo("EMPNO", 1, "NUMBER"), new ColumnInfo("ENAME", 2, "VARCHAR2"), new ColumnInfo("SAL", 3, "NUMBER")]))
                : Task.FromException<TableInfo>(new InvalidOperationException("없음"));

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ProcedureInfo>>(obj.Name == "EMP_PKG" && obj.Owner == "SCOTT"
                ?
                [
                    new ProcedureInfo("HIRE", false, [new ArgumentInfo("P_NAME", 1, "VARCHAR2", ArgumentDirection.In), new ArgumentInfo("P_SAL", 2, "NUMBER", ArgumentDirection.In) { HasDefault = true }]),
                    new ProcedureInfo("FIRE", false, [new ArgumentInfo("P_ID", 1, "NUMBER", ArgumentDirection.In)]),
                ]
                : []);

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => Task.FromResult(string.Empty);

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => Task.FromResult(new Oranda.Core.Metadata.ObjectDetail(obj, []));

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => Task.FromResult(string.Empty);

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => Task.FromResult<IReadOnlyList<CompileError>>([]);
    }
}
