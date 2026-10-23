using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Journal;
using Oranda.Core.Text;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.PlsqlEditor;

/// <summary>TSK-08-01 PL/SQL 소스 편집·컴파일·충돌 확인 시험(글자 도우미·ViewModel·명령 처리기·등록).</summary>
public class PlsqlEditorTests
{
    private static readonly DbObjectRef Body = new("SCOTT", "PK", "PACKAGE BODY");

    private const string BodySource = "PACKAGE BODY pk AS\n  PROCEDURE a IS BEGIN\n    NULL;\n  END;\nEND pk;\n";

    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new PlsqlEditorViewModel());
        Assert.NotNull(new CompileErrorsViewModel());
    }

    [Fact]
    public void Panels_and_commands_are_registered()
    {
        var editor = ShellTestHelpers.RequirePanel("oranda.panel.plsqlEditor");
        Assert.Equal(ToolPanelKind.Document, editor.Kind);
        Assert.True(editor.AllowMultiple);
        Assert.Equal(ToolPanelKind.Tool, ShellTestHelpers.RequirePanel("oranda.panel.compileErrors").Kind);

        var reg = ShellTestHelpers.CreateCommandRegistry();
        foreach (var id in new[]
                 {
                     "oranda.file.newPlsqlEditor", "oranda.view.compileErrors", "oranda.plsql.openSource", "oranda.plsql.compile",
                     "oranda.plsql.toggleSpecBody", "oranda.plsql.showServerDiff",
                 })
        {
            Assert.NotNull(reg.Find(id));
        }
    }

    // ---- 글자 도우미 ----

    [Theory]
    [InlineData("CREATE OR REPLACE PACKAGE BODY scott.pk AS", "PACKAGE BODY", "SCOTT", "PK")]
    [InlineData("create or replace editionable procedure hello is", "PROCEDURE", null, "HELLO")]
    [InlineData("-- 설명\n/* x */\nCREATE FUNCTION \"Mixed\".\"f\" RETURN NUMBER IS", "FUNCTION", "Mixed", "f")]
    [InlineData("TYPE BODY t AS", "TYPE BODY", null, "T")]
    public void Header_gives_type_and_name(string text, string type, string? owner, string name)
        => Assert.Equal((type, owner, name), PlsqlSourceText.ParseHeader(text));

    [Fact]
    public void Header_of_non_source_is_null()
        => Assert.Null(PlsqlSourceText.ParseHeader("SELECT 1 FROM dual"));

    [Fact]
    public void Editor_text_requalifies_owner_that_server_blanks_out_of_stored_source()
    {
        // 서버는 소유자 이름을 공백으로 바꿔 저장한다(열 위치를 지키려고). 그대로 컴파일하면 접속 사용자 스키마에 만들어진다.
        var text = PlsqlSourceText.ToEditorText("PACKAGE BODY            pk AS\nEND pk;\n", "SCOTT", out var shift);

        Assert.StartsWith("CREATE OR REPLACE PACKAGE BODY \"SCOTT\".pk AS\n", text);
        Assert.Equal(("PACKAGE BODY", "SCOTT", "PK"), PlsqlSourceText.ParseHeader(text));
        Assert.Equal(" \"SCOTT\".".Length - 12, shift);
    }

    [Fact]
    public void Editor_text_leaves_already_qualified_name_alone()
    {
        var text = PlsqlSourceText.ToEditorText("PROCEDURE hr.p IS BEGIN NULL; END;", "SCOTT", out var shift);

        Assert.Equal("CREATE OR REPLACE PROCEDURE hr.p IS BEGIN NULL; END;", text);
        Assert.Equal(0, shift);
    }

    [Fact]
    public void Compile_text_drops_trailing_slash_and_normalizes_line_ends()
    {
        var text = PlsqlSourceText.ToCompileText("CREATE OR REPLACE PROCEDURE p IS\r\nBEGIN\r\n  NULL;\r\nEND;\r\n/\r\n\r\n");

        Assert.Equal("CREATE OR REPLACE PROCEDURE p IS\nBEGIN\n  NULL;\nEND;", text);
    }

    [Fact]
    public void Source_start_counts_from_type_keyword_after_comments()
    {
        Assert.Equal((0, 18), PlsqlSourceText.SourceStart("CREATE OR REPLACE PACKAGE p AS"));
        Assert.Equal((2, 18), PlsqlSourceText.SourceStart("-- a\n-- b\nCREATE OR REPLACE PACKAGE p AS"));
    }

    [Fact]
    public void Diff_marks_lines_only_in_one_side()
    {
        Assert.Equal(string.Empty, PlsqlSourceText.BuildDiff("CREATE OR REPLACE PACKAGE p AS\nEND;", "PACKAGE p AS\nEND;\n"));

        var diff = PlsqlSourceText.BuildDiff("PACKAGE p AS\n  x;\nEND;", "PACKAGE p AS\n  y;\nEND;");

        Assert.Contains("- " + "  x;", diff);
        Assert.Contains("+ " + "  y;", diff);
        Assert.Contains("  PACKAGE p AS", diff);
    }

    [Theory]
    [InlineData("PACKAGE", "PACKAGE BODY")]
    [InlineData("PACKAGE BODY", "PACKAGE")]
    [InlineData("TYPE", "TYPE BODY")]
    [InlineData("PROCEDURE", null)]
    public void Sibling_type_pairs_spec_and_body(string type, string? sibling)
        => Assert.Equal(sibling, PlsqlSourceText.SiblingType(type));

    /// <summary>서버에서 연 프로시저 SCOTT.P 탭(다른 시험 파일이 쓴다).</summary>
    internal static async Task<PlsqlEditorViewModel> OpenedProcedure()
    {
        var vm = new PlsqlEditorViewModel(new FakeMeta { Source = "PROCEDURE p IS BEGIN\n  NULL;\nEND;\n" }, new FakeExec());
        Assert.True(await vm.OpenAsync(new FakeSession(), new DbObjectRef("SCOTT", "P", "PROCEDURE")));
        return vm;
    }

    // ---- ViewModel ----

    [Fact]
    public async Task Open_fills_editor_with_create_or_replace_and_reads_errors()
    {
        var meta = new FakeMeta { Source = BodySource, Errors = [new CompileError(3, 5, "PLS-00103: x", false)] };
        var vm = new PlsqlEditorViewModel(meta, new FakeExec());

        Assert.True(await vm.OpenAsync(new FakeSession(), Body));

        Assert.StartsWith("CREATE OR REPLACE PACKAGE BODY \"SCOTT\".pk AS", vm.Editor.Text);
        Assert.Equal("SCOTT.PK (본문)", vm.Title);
        Assert.False(vm.Editor.IsDirty);
        Assert.True(vm.IsPackageDoc);
        Assert.Single(vm.Errors);
        Assert.False(vm.ServerSourceChanged);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Opened_package_source_is_highlighted_in_the_editor_view(bool openFirst) => Oranda.App.Tests.Shell.Headless.HeadlessHost.Run(async () =>
    {
        var vm = new PlsqlEditorViewModel(new FakeMeta { Source = BodySource }, new FakeExec());
        if (openFirst)
        {
            Assert.True(await vm.OpenAsync(new FakeSession(), Body));
        }

        var view = new PlsqlEditorView { DataContext = vm };
        var window = new Avalonia.Controls.Window { Width = 900, Height = 600, Content = view };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        if (!openFirst)
        {
            Assert.True(await vm.OpenAsync(new FakeSession(), Body));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        var editor = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<Oranda.App.Features.SqlEditor.SqlEditorView>().First();
        Assert.NotNull(editor.Colorizer);
        Assert.Contains(editor.Colorizer!.Lexer.TokensOf(0), t => t.Kind == Oranda.Language.Lexing.TokenKind.Keyword);
        Assert.Contains(editor.TextEditorControl.TextArea.TextView.LineTransformers, t => ReferenceEquals(t, editor.Colorizer));
        window.Close();
    });

    [Fact]
    public async Task Open_failure_reports_in_status_without_throwing()
    {
        var vm = new PlsqlEditorViewModel(new FakeMeta { SourceFails = true }, new FakeExec());

        Assert.False(await vm.OpenAsync(new FakeSession(), Body));

        Assert.Contains("소스를 읽지 못했다", vm.StatusText);
    }

    [Fact]
    public async Task Compile_goes_through_execution_service_and_loads_errors()
    {
        var meta = new FakeMeta { Source = BodySource };
        var exec = new FakeExec();
        var vm = new PlsqlEditorViewModel(meta, exec);
        await vm.OpenAsync(new FakeSession(), Body);
        vm.Editor.LoadText(vm.Editor.Text.Replace("NULL;", "NULL; NULL;", StringComparison.Ordinal), dirty: true);
        meta.Errors = [new CompileError(3, 10, "PLS-00103: bad", false), new CompileError(2, 1, "PLW-05005: warn", true)];

        var outcome = await vm.CompileAsync();

        Assert.Equal(PlsqlCompileStatus.CompiledWithErrors, outcome.Status);
        var sent = Assert.Single(exec.Statements, s => s.Verb == "CREATE");
        Assert.StartsWith("CREATE OR REPLACE PACKAGE BODY \"SCOTT\".pk AS\n", sent.Text);
        Assert.EndsWith("END pk;", sent.Text);
        Assert.Equal(2, vm.Errors.Count);
        Assert.False(vm.Editor.IsDirty);
        Assert.Contains("오류 1건", outcome.Message);
    }

    // ---- 변경 일지(TSK-13-07 J3) ----

    [Fact]
    public async Task Compile_archives_current_server_source_before_overwriting_and_twice_gives_two_versions()
    {
        var bus = new FakeEventBus();
        var meta = new FakeMeta { Source = BodySource };
        var exec = new FakeExec();
        var vm = new PlsqlEditorViewModel(meta, exec, bus);
        await vm.OpenAsync(new FakeSession(), Body);
        var captured = new List<JournalEntry>();
        bus.Subscribe<ChangeSnapshotCaptured>(this, e => captured.Add(e.Entry));

        await vm.CompileAsync();
        meta.Source = BodySource.Replace("NULL;", "NULL; NULL;", StringComparison.Ordinal); // 첫 컴파일 뒤 서버 소스
        await vm.CompileAsync(force: true);

        Assert.Equal(2, captured.Count);
        Assert.All(captured, e =>
        {
            Assert.Equal(JournalCause.Compile, e.Cause);
            Assert.Equal(("SCOTT", "PK", "PACKAGE BODY"), (e.Owner, e.Name, e.ObjectType));
            Assert.Equal("FREE/FREEPDB1/SCOTT", e.DbKey);
            Assert.False(e.IsFailure);
        });
        Assert.StartsWith("CREATE OR REPLACE PACKAGE BODY \"SCOTT\".pk AS\n", captured[0].PreviousDdl);
        Assert.NotEqual(captured[0].PreviousDdl, captured[1].PreviousDdl);
        Assert.NotEmpty(PlsqlSourceText.BuildDiff(captured[0].PreviousDdl!, captured[1].PreviousDdl!));
        Assert.All(exec.Statements.Where(s => s.Verb == "CREATE"), s => Assert.Null(s.ChangeTarget)); // 실행 엔진 보관과 겹치지 않는다.
    }

    [Fact]
    public async Task Compile_of_new_object_archives_nothing_and_read_failure_is_logged_but_compile_proceeds()
    {
        var bus = new FakeEventBus();
        var captured = new List<JournalEntry>();
        bus.Subscribe<ChangeSnapshotCaptured>(this, e => captured.Add(e.Entry));
        var meta = new FakeMeta { Source = BodySource };
        var exec = new FakeExec();
        var vm = new PlsqlEditorViewModel(meta, exec, bus);
        await vm.OpenAsync(new FakeSession(), Body);

        meta.SourceFails = true; // 서버가 소스를 못 찾음: 새 객체와 같다.
        Assert.Equal(PlsqlCompileStatus.Compiled, (await vm.CompileAsync()).Status);
        Assert.Empty(captured);

        meta.SourceFails = false;
        meta.SourceError = new TimeoutException("slow");
        Assert.Equal(PlsqlCompileStatus.Compiled, (await vm.CompileAsync(force: true)).Status);
        var failure = Assert.Single(captured);
        Assert.True(failure.IsFailure);
        Assert.Contains("slow", failure.FailureReason, StringComparison.Ordinal);
        Assert.Equal(2, exec.Statements.Count(s => s.Verb == "CREATE")); // 보관 실패와 무관하게 컴파일은 갔다.
    }

    [Fact]
    public async Task Compile_without_errors_reports_success()
    {
        var vm = new PlsqlEditorViewModel(new FakeMeta { Source = BodySource }, new FakeExec());
        await vm.OpenAsync(new FakeSession(), Body);

        var outcome = await vm.CompileAsync();

        Assert.Equal(PlsqlCompileStatus.Compiled, outcome.Status);
        Assert.Empty(vm.Errors);
    }

    [Fact]
    public async Task Compile_stops_when_server_changed_since_open_and_shows_diff()
    {
        var meta = new FakeMeta { Source = BodySource };
        var exec = new FakeExec();
        var vm = new PlsqlEditorViewModel(meta, exec);
        await vm.OpenAsync(new FakeSession(), Body);
        vm.Editor.LoadText(vm.Editor.Text.Replace("NULL;", "mine;", StringComparison.Ordinal), dirty: true);
        meta.Source = BodySource.Replace("NULL;", "theirs;", StringComparison.Ordinal);
        meta.LastDdl = meta.LastDdl.AddMinutes(5);

        var outcome = await vm.CompileAsync();

        Assert.Equal(PlsqlCompileStatus.Conflict, outcome.Status);
        Assert.True(vm.ServerSourceChanged);
        Assert.Contains("- ", vm.DiffText);
        Assert.Contains("mine;", vm.DiffText);
        Assert.Contains("theirs;", vm.DiffText);
        Assert.DoesNotContain(exec.Statements, s => s.Verb == "CREATE");
        Assert.True(vm.Editor.IsDirty);
    }

    [Fact]
    public async Task Force_compile_overwrites_after_conflict_and_resets_baseline()
    {
        var meta = new FakeMeta { Source = BodySource };
        var exec = new FakeExec();
        var vm = new PlsqlEditorViewModel(meta, exec);
        await vm.OpenAsync(new FakeSession(), Body);
        meta.LastDdl = meta.LastDdl.AddMinutes(5);
        await vm.CompileAsync();

        var outcome = await vm.CompileAsync(force: true);

        Assert.Equal(PlsqlCompileStatus.Compiled, outcome.Status);
        Assert.False(vm.ServerSourceChanged);
        Assert.Equal(meta.LastDdl, vm.BaselineDdlTime);
        Assert.Contains(exec.Statements, s => s.Verb == "CREATE");
    }

    [Fact]
    public async Task Compile_warns_and_holds_when_other_sessions_use_the_object()
    {
        var exec = new FakeExec { OtherSessions = 2 };
        var vm = new PlsqlEditorViewModel(new FakeMeta { Source = BodySource }, exec);
        await vm.OpenAsync(new FakeSession(), Body);

        var warned = await vm.CompileAsync();
        var forced = await vm.CompileAsync(force: true);

        Assert.Equal(PlsqlCompileStatus.InUse, warned.Status);
        Assert.Contains("다른 세션 2개", vm.WarningText + warned.Message);
        Assert.Equal(PlsqlCompileStatus.Compiled, forced.Status);
        Assert.Single(exec.Statements, s => s.Verb == "CREATE");
        Assert.Equal(string.Empty, vm.WarningText);
    }

    [Fact]
    public async Task Policy_denial_and_server_failure_are_reported()
    {
        var exec = new FakeExec { CreateStatus = ExecutionStatus.Denied };
        var vm = new PlsqlEditorViewModel(new FakeMeta { Source = BodySource }, exec);
        await vm.OpenAsync(new FakeSession(), Body);

        Assert.Equal(PlsqlCompileStatus.Blocked, (await vm.CompileAsync()).Status);

        exec.CreateStatus = ExecutionStatus.Failed;
        var failed = await vm.CompileAsync();
        Assert.Equal(PlsqlCompileStatus.Failed, failed.Status);
        Assert.Contains("ORA-00900", failed.Message);
    }

    [Fact]
    public async Task New_document_compiles_and_becomes_a_server_object()
    {
        var exec = new FakeExec();
        var meta = new FakeMeta();
        var vm = new PlsqlEditorViewModel(meta, exec);
        vm.StartNew(PlsqlEditorCommandHandler.NewTemplate, new FakeSession());

        var outcome = await vm.CompileAsync();

        Assert.Equal(PlsqlCompileStatus.Compiled, outcome.Status);
        Assert.Equal(new DbObjectRef("SCOTT", "NEW_PROCEDURE", "PROCEDURE"), vm.Target);
        Assert.Equal(meta.LastDdl, vm.BaselineDdlTime);
    }

    [Fact]
    public async Task Compile_without_source_header_does_nothing()
    {
        var exec = new FakeExec();
        var vm = new PlsqlEditorViewModel(new FakeMeta(), exec);
        vm.StartNew("SELECT 1 FROM dual", new FakeSession());

        Assert.Equal(PlsqlCompileStatus.NothingToCompile, (await vm.CompileAsync()).Status);
        Assert.Empty(exec.Statements);
    }

    [Fact]
    public async Task Choosing_an_error_moves_the_caret_to_that_line_and_column()
    {
        var vm = new PlsqlEditorViewModel(new FakeMeta { Source = BodySource }, new FakeExec());
        await vm.OpenAsync(new FakeSession(), Body);

        Assert.True(vm.NavigateTo(new CompileError(3, 5, "x", false)));
        Assert.Equal(3, vm.Editor.CaretPosition.Line);
        Assert.Equal(5, vm.Editor.CaretPosition.Column);

        // 1줄 오류는 앞에 붙인 CREATE OR REPLACE 와 다시 적은 소유자("SCOTT".) 만큼 열을 민다.
        Assert.True(vm.NavigateTo(new CompileError(1, 9, "x", false)));
        Assert.Equal(1, vm.Editor.CaretPosition.Line);
        Assert.Equal(9 + PlsqlSourceText.CreatePrefix.Length + " \"SCOTT\".".Length - 1, vm.Editor.CaretPosition.Column);

        Assert.False(vm.NavigateTo(new CompileError(99, 1, "x", false)));
    }

    [Fact]
    public async Task Error_list_shows_errors_of_last_compiled_tab_and_navigates_on_select()
    {
        var errorsVm = new CompileErrorsViewModel();
        var meta = new FakeMeta { Source = BodySource, Errors = [new CompileError(3, 5, "PLS-00103: x", false)] };
        var vm = new PlsqlEditorViewModel(meta, new FakeExec());
        await vm.OpenAsync(new FakeSession(), Body);

        Assert.Single(errorsVm.Items);
        Assert.Contains("오류 1건", errorsVm.Header);

        errorsVm.SelectedItem = errorsVm.Items[0];

        Assert.Equal(3, vm.Editor.CaretPosition.Line);
    }

    [Fact]
    public async Task Error_list_counts_errors_and_warnings_and_picks_the_header_icon()
    {
        var errorsVm = new CompileErrorsViewModel();
        var meta = new FakeMeta { Source = BodySource, Errors = [new CompileError(3, 5, "PLS-00103: x", false), new CompileError(4, 1, "PLW-06009: y", true)] };
        var vm = new PlsqlEditorViewModel(meta, new FakeExec());
        await vm.OpenAsync(new FakeSession(), Body);

        Assert.Equal(1, errorsVm.ErrorCount);
        Assert.Equal(1, errorsVm.WarningCount);
        Assert.Equal("error", errorsVm.HeaderIconKey);

        vm.Errors.RemoveAt(0);
        Assert.Equal(0, errorsVm.ErrorCount);
        Assert.Equal("warn", errorsVm.HeaderIconKey);

        vm.Errors.Clear();
        Assert.Equal(0, errorsVm.WarningCount);
        Assert.Equal("ok", errorsVm.HeaderIconKey);
    }

    [Fact]
    public async Task Check_server_changed_detects_other_sessions_ddl()
    {
        var meta = new FakeMeta { Source = BodySource };
        var vm = new PlsqlEditorViewModel(meta, new FakeExec());
        await vm.OpenAsync(new FakeSession(), Body);

        Assert.False(await vm.CheckServerChangedAsync());

        meta.LastDdl = meta.LastDdl.AddSeconds(1);

        Assert.True(await vm.CheckServerChangedAsync());
        Assert.True(vm.ServerSourceChanged);
    }

    [Fact]
    public async Task Toggle_opens_the_other_half_in_a_new_tab_and_keeps_this_one()
    {
        var meta = new FakeMeta { Source = BodySource };
        var vm = new PlsqlEditorViewModel(meta, new FakeExec());
        var opened = new List<PlsqlEditorViewModel>();
        vm.OpenDocument = (v, _) =>
        {
            opened.Add(v);
            return true;
        };
        await vm.OpenAsync(new FakeSession(), Body);
        vm.Editor.LoadText("edited", dirty: true);

        var sibling = await vm.OpenSiblingAsync();

        Assert.NotNull(sibling);
        Assert.Equal("PACKAGE", sibling.Target?.ObjectType);
        Assert.Same(sibling, Assert.Single(opened));
        Assert.Equal("edited", vm.Editor.Text);
    }

    [Fact]
    public async Task Toggle_does_nothing_for_standalone_procedure_and_missing_half_leaves_status()
    {
        var meta = new FakeMeta { Source = BodySource };
        var vm = new PlsqlEditorViewModel(meta, new FakeExec());
        await vm.OpenAsync(new FakeSession(), new DbObjectRef("SCOTT", "P", "PROCEDURE"));
        Assert.Null(await vm.OpenSiblingAsync());

        var pkg = new PlsqlEditorViewModel(meta, new FakeExec()) { OpenDocument = (_, _) => true };
        await pkg.OpenAsync(new FakeSession(), Body);
        meta.SourceFails = true;
        Assert.Null(await pkg.OpenSiblingAsync());
        Assert.Contains("소스가 없다", pkg.StatusText);
    }

    // ---- 명령 처리기 ----

    [Fact]
    public async Task Open_source_command_opens_a_tab_for_the_node_and_loads_source()
    {
        var meta = new FakeMeta { Source = BodySource };
        var opened = new List<PlsqlEditorViewModel>();
        var handler = new PlsqlEditorCommandHandler(new NoServices())
        {
            ViewModelFactory = () => new PlsqlEditorViewModel(meta, new FakeExec()),
            SessionProvider = () => new FakeSession(),
            OpenDocument = (v, _) =>
            {
                opened.Add(v);
                return true;
            },
        };

        await handler.ExecuteAsync(new CommandContext("oranda.plsql.openSource", new NoKeys(), Body), default);
        await handler.ExecuteAsync(new CommandContext("oranda.plsql.openSource", new NoKeys(), new DbObjectRef("SCOTT", "EMP", "TABLE")), default);

        var vm = Assert.Single(opened);
        Assert.StartsWith("CREATE OR REPLACE PACKAGE BODY", vm.Editor.Text);
    }

    [Fact]
    public async Task Open_source_command_with_a_line_moves_the_caret_to_that_server_line()
    {
        var meta = new FakeMeta { Source = BodySource };
        PlsqlEditorViewModel? vm = null;
        var handler = new PlsqlEditorCommandHandler(new NoServices())
        {
            ViewModelFactory = () => new PlsqlEditorViewModel(meta, new FakeExec()),
            SessionProvider = () => new FakeSession(),
            OpenDocument = (v, _) =>
            {
                vm = v;
                return true;
            },
        };

        await handler.ExecuteAsync(new CommandContext("oranda.plsql.openSource", new NoKeys(), new PlsqlSourceLocation(Body, 3)), default);

        Assert.NotNull(vm);
        Assert.Equal(3, vm.Editor.CaretPosition.Line);
        Assert.Equal(1, vm.Editor.CaretPosition.Column);
        Assert.Equal(3, vm.Editor.ScrollLine); // 편집기가 화면에 붙을 때 그 줄을 보인다
        Assert.False(vm.GoToServerLine(99));
        Assert.False(vm.GoToServerLine(0));
    }

    [Fact]
    public async Task Compile_command_compiles_active_tab_and_shows_error_panel()
    {
        var meta = new FakeMeta { Source = BodySource, Errors = [new CompileError(3, 5, "x", false)] };
        var vm = new PlsqlEditorViewModel(meta, new FakeExec());
        await vm.OpenAsync(new FakeSession(), Body);
        var shown = 0;
        var handler = new PlsqlEditorCommandHandler(new NoServices())
        {
            ActiveProvider = () => vm,
            ShowErrorsPanel = () => shown++,
        };

        await handler.ExecuteAsync(new CommandContext("oranda.plsql.compile", new NoKeys()), default);

        Assert.Equal(1, shown);
        Assert.False(vm.Editor.IsDirty);
    }

    [Fact]
    public async Task New_editor_command_opens_template_tab()
    {
        var opened = new List<PlsqlEditorViewModel>();
        var handler = new PlsqlEditorCommandHandler(new NoServices())
        {
            ViewModelFactory = () => new PlsqlEditorViewModel(new FakeMeta(), new FakeExec()),
            SessionProvider = () => new FakeSession(),
            OpenDocument = (v, _) =>
            {
                opened.Add(v);
                return true;
            },
        };

        await handler.ExecuteAsync(new CommandContext("oranda.file.newPlsqlEditor", new NoKeys()), default);

        Assert.StartsWith("CREATE OR REPLACE PROCEDURE", Assert.Single(opened).Editor.Text);
    }

    [Fact]
    public async Task Handler_without_services_does_nothing()
    {
        await new PlsqlEditorCommandHandler().ExecuteAsync(new CommandContext("oranda.plsql.compile", new NoKeys()), default);
    }

    // ---- 가짜 ----

    private sealed class NoKeys : IContextKeys
    {
        public bool Evaluate(string? expression) => true;
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class FakeEventBus : IEventBus
    {
        private readonly List<(Type Type, Action<OrandaEvent> Handler)> _handlers = [];

        public IDisposable Subscribe<TEvent>(object owner, Action<TEvent> handler)
            where TEvent : OrandaEvent
        {
            _handlers.Add((typeof(TEvent), e => handler((TEvent)e)));
            return new Noop();
        }

        public void Publish<TEvent>(TEvent e)
            where TEvent : OrandaEvent
        {
            foreach (var (type, handler) in _handlers.ToList())
            {
                if (type == typeof(TEvent))
                {
                    handler(e);
                }
            }
        }

        private sealed class Noop : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class FakeSession : ISession
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = new("t", "scott", "localhost/FREEPDB1", Id: Guid.NewGuid());

        public ServerInfo Server => throw new NotSupportedException();

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => "FREE/FREEPDB1/SCOTT";

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ---- 탭 닫기 저장 확인(TSK-13-27) ----

    [Fact]
    public void Close_confirm_asks_for_new_tab_with_content_only()
    {
        var vm = new PlsqlEditorViewModel();
        vm.StartNew(PlsqlEditorCommandHandler.NewTemplate, null);
        Assert.False(vm.NeedsSaveConfirm); // 템플릿 그대로면 묻지 않는다

        vm.Editor.Document.Insert(0, "-- mine\n");
        Assert.True(vm.NeedsSaveConfirm);

        vm.Editor.Document.Remove(0, "-- mine\n".Length);
        Assert.False(vm.NeedsSaveConfirm); // 템플릿으로 되돌리면 다시 묻지 않는다

        vm.Editor.LoadText("  \n");
        Assert.False(vm.NeedsSaveConfirm);
    }

    [Fact]
    public async Task Close_confirm_for_server_object_follows_edits()
    {
        var vm = new PlsqlEditorViewModel(new FakeMeta { Source = BodySource }, new FakeExec());
        Assert.True(await vm.OpenAsync(new FakeSession(), Body));
        Assert.False(vm.NeedsSaveConfirm);

        vm.Editor.Document.Insert(0, "-- x\n");
        Assert.True(vm.NeedsSaveConfirm);
    }

    [Fact]
    public void Close_confirm_for_file_tab_follows_dirty()
    {
        var vm = new PlsqlEditorViewModel();
        vm.StartNew("CREATE PROCEDURE p IS BEGIN NULL; END;", null);
        vm.Editor.FilePath = "/x/p.sql";
        vm.Editor.MarkSaved();
        Assert.False(vm.NeedsSaveConfirm);

        vm.Editor.Document.Insert(0, "-- x\n");
        Assert.True(vm.NeedsSaveConfirm);
    }

    [Fact]
    public async Task Close_confirm_dont_save_and_cancel_follow_answer()
    {
        var vm = new PlsqlEditorViewModel();
        vm.StartNew(PlsqlEditorCommandHandler.NewTemplate, null);
        vm.Editor.Document.Insert(0, "-- mine\n");

        Assert.True(await vm.ConfirmCloseAsync(new SqlEditorHost(dialogs: new AnswerDialogs(SaveChoice.DontSave)), default));
        Assert.False(await vm.ConfirmCloseAsync(new SqlEditorHost(dialogs: new AnswerDialogs(SaveChoice.Cancel)), default));
    }

    [Fact]
    public async Task Close_confirm_save_writes_local_file_and_not_the_server()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oranda-plsql-{Guid.NewGuid():N}.sql");
        try
        {
            var exec = new FakeExec();
            var vm = new PlsqlEditorViewModel(new FakeMeta(), exec);
            vm.StartNew(PlsqlEditorCommandHandler.NewTemplate, null);
            vm.Editor.Document.Insert(0, "-- mine\n");
            var dialogs = new AnswerDialogs(SaveChoice.Save) { SavePath = path };

            Assert.True(await vm.ConfirmCloseAsync(new SqlEditorHost(dialogs: dialogs), default));

            Assert.Contains("new_procedure", await File.ReadAllTextAsync(path));
            Assert.Empty(exec.Statements);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Close_confirm_save_cancelled_picker_keeps_tab()
    {
        var vm = new PlsqlEditorViewModel();
        vm.StartNew(PlsqlEditorCommandHandler.NewTemplate, null);
        vm.Editor.Document.Insert(0, "-- mine\n");

        Assert.False(await vm.ConfirmCloseAsync(new SqlEditorHost(dialogs: new AnswerDialogs(SaveChoice.Save)), default));
    }

    // ---- SQL 편집기에서 만든 PL/SQL 객체의 컴파일 오류(TSK-13-28) ----

    [Theory]
    [InlineData("CREATE OR REPLACE PROCEDURE p IS BEGIN NULL; END;", "PROCEDURE", null, "P")]
    [InlineData("create function scott.f return number is begin return 1; end;", "FUNCTION", "SCOTT", "F")]
    [InlineData("-- 설명\nCREATE OR REPLACE EDITIONABLE PACKAGE BODY pk AS END;", "PACKAGE BODY", null, "PK")]
    [InlineData("CREATE OR REPLACE TRIGGER trg BEFORE INSERT ON t BEGIN NULL; END;", "TRIGGER", null, "TRG")]
    [InlineData("CREATE TYPE BODY ty AS END;", "TYPE BODY", null, "TY")]
    public void Create_header_is_recognised(string text, string type, string? owner, string name)
        => Assert.Equal((type, owner, name), PlsqlSourceText.ParseCreateHeader(text));

    [Theory]
    [InlineData("TYPE BODY t AS END;")]
    [InlineData("SELECT 1 FROM dual")]
    [InlineData("CREATE TABLE t (a NUMBER)")]
    [InlineData("DROP PROCEDURE p")]
    public void Create_header_rejects_other_statements(string text)
        => Assert.Null(PlsqlSourceText.ParseCreateHeader(text));

    private static (SqlEditorViewModel Tab, SqlStatement Stmt) SqlTab(string text, string statement)
    {
        var tab = new SqlEditorViewModel();
        tab.LoadText(text);
        var start = text.IndexOf(statement, StringComparison.Ordinal);
        return (tab, new SqlStatement(statement, new TextRange(start, statement.Length), StatementKind.Ddl) { Verb = "CREATE" });
    }

    [Fact]
    public Task Sql_editor_create_with_errors_fills_errors_and_maps_line_into_the_sql_tab() => Ui.Run(async () =>
    {
        var statement = "CREATE OR REPLACE PROCEDURE bad_proc\nIS\nBEGIN\n  x := ;\nEND;";
        var (tab, stmt) = SqlTab("SELECT 1 FROM dual;\n\n" + statement, statement);
        var meta = new FakeMeta { Errors = [new CompileError(4, 7, "PLS-00103: x", false)] };
        var result = new ExecutionResult(stmt, ExecutionStatus.Succeeded);
        var errorsVm = new CompileErrorsViewModel();

        Assert.True(await SqlCompileErrors.CaptureAsync(meta, new FakeSession(), tab, stmt, result, default));

        Assert.Single(errorsVm.Items);
        Assert.Contains("SCOTT.BAD_PROC", errorsVm.Header);
        errorsVm.SelectedItem = errorsVm.Items[0];
        Assert.Equal(3 + 4 - 1, tab.CaretPosition.Line); // 문장은 3줄째에서 시작: 3 + 오류 줄 4 − 1
        Assert.Equal(7, tab.CaretPosition.Column);
    });

    [Fact]
    public Task Sql_editor_create_without_errors_clears_the_previous_entries() => Ui.Run(async () =>
    {
        var statement = "CREATE OR REPLACE PROCEDURE clear_proc IS BEGIN NULL; END;";
        var (tab, stmt) = SqlTab(statement, statement);
        var session = new FakeSession();
        var meta = new FakeMeta { Errors = [new CompileError(1, 1, "PLS-00103: x", false)] };
        var errorsVm = new CompileErrorsViewModel();
        var result = new ExecutionResult(stmt, ExecutionStatus.Succeeded);
        Assert.True(await SqlCompileErrors.CaptureAsync(meta, session, tab, stmt, result, default));
        Assert.Single(errorsVm.Items);

        meta.Errors = [];
        Assert.False(await SqlCompileErrors.CaptureAsync(meta, session, tab, stmt, result, default));

        Assert.Empty(errorsVm.Items);
    });

    [Fact]
    public void Capture_applies_only_to_create_statements_that_succeeded_or_compiled_with_errors()
    {
        var (_, create) = SqlTab("CREATE PROCEDURE p IS BEGIN NULL; END;", "CREATE PROCEDURE p IS BEGIN NULL; END;");
        var (_, select) = SqlTab("SELECT 1 FROM dual", "SELECT 1 FROM dual");

        Assert.True(SqlCompileErrors.ShouldCapture(create, new ExecutionResult(create, ExecutionStatus.Succeeded)));
        Assert.True(SqlCompileErrors.ShouldCapture(create, new ExecutionResult(create, ExecutionStatus.Failed) { Error = new DbError("ORA-24344", "success with compilation error") }));
        Assert.False(SqlCompileErrors.ShouldCapture(create, new ExecutionResult(create, ExecutionStatus.Failed) { Error = new DbError("ORA-00900", "invalid SQL statement") }));
        Assert.False(SqlCompileErrors.ShouldCapture(select, new ExecutionResult(select, ExecutionStatus.Succeeded)));
    }

    private sealed class AnswerDialogs(SaveChoice answer) : IEditorDialogs
    {
        public string? SavePath { get; init; }

        public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct) => Task.FromResult(SavePath);

        public Task<string?> PromptAsync(string title, string initial, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct) => Task.FromResult(false);

        public Task<SaveChoice> AskSaveChangesAsync(string tabName, CancellationToken ct) => Task.FromResult(answer);
    }

    private sealed class FakeMeta : IMetadataProvider
    {
        public string Source { get; set; } = BodySource;

        public bool SourceFails { get; set; }

        public Exception? SourceError { get; set; }

        public DateTime LastDdl { get; set; } = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

        public IReadOnlyList<CompileError> Errors { get; set; } = [];

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct)
            => SourceError is not null ? Task.FromException<string>(SourceError)
                : SourceFails ? Task.FromException<string>(new InvalidOperationException("none")) : Task.FromResult(Source);

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => Task.FromResult(Errors);

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct)
            => Task.FromResult(new Oranda.Core.Metadata.ObjectDetail(obj, [new DetailSection("일반", ["항목", "값"], [["유형", obj.ObjectType], ["마지막 DDL", LastDdl]])]));

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeExec : IExecutionService
    {
        public List<SqlStatement> Statements { get; } = [];

        public int OtherSessions { get; set; }

        public ExecutionStatus CreateStatus { get; set; } = ExecutionStatus.Succeeded;

        public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds,
            ExecutionOptions? options, CancellationToken ct)
        {
            if (stmt.Kind == StatementKind.Query)
            {
                // 다른 세션 수 조회: 열 1개·행 1개짜리 가짜 결과.
                return Task.FromResult(new ExecutionResult(stmt, ExecutionStatus.Succeeded) { ResultSet = new CountResultSet(OtherSessions) });
            }

            Statements.Add(stmt);
            var result = new ExecutionResult(stmt, CreateStatus);
            if (CreateStatus == ExecutionStatus.Failed)
            {
                result = result with { Error = new DbError("ORA-00900", "invalid SQL statement") };
            }

            return Task.FromResult(result);
        }

        public IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode,
            ExecutionOptions? options, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class CountResultSet(int count) : Oranda.Core.Grid.IResultSet
    {
        public IReadOnlyList<Oranda.Core.Grid.ResultColumn> Columns => [];

        public bool IsComplete => true;

        public long? KnownRowCount => 1;

        public Task<Oranda.Core.Grid.ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
            => Task.FromResult(new Oranda.Core.Grid.ResultPage(0, 0, 1, [[(object?)count]]) { IsLast = true });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
