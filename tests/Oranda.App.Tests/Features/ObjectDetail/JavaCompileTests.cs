using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.ObjectDetail;

/// <summary>
/// TSK-14-23 Java 객체 컴파일·적용: 객체 상세의 「다시 컴파일」(확인 → ALTER JAVA … 를 실행 엔진으로 → ALL_ERRORS 오류 → 상세·브라우저 새로 읽기),
/// JVM 없음·읽기 전용 접속이면 막고 이유, Java 소스의 「편집기로 열기」, SQL 편집기에서 실행한 CREATE JAVA SOURCE 의 오류 읽기 판정. 가짜 메타데이터·엔진(Oracle 없음).
/// </summary>
public class JavaCompileTests
{
    private static readonly DbObjectRef Source = new("SCOTT", "com/example/Hello", "JAVA SOURCE");

    private static readonly DbObjectRef Class = new("SCOTT", "com/example/Hello", "JAVA CLASS");

    private static (ObjectDetailViewModel Vm, Meta Meta, Exec Exec, Probe Probe) Make(bool jvm = true)
    {
        var meta = new Meta { Jvm = jvm };
        var exec = new Exec();
        var probe = new Probe();
        var vm = new ObjectDetailViewModel(meta)
        {
            Exec = exec,
            Confirm = (message, _) =>
            {
                probe.Asked.Add(message);
                return Task.FromResult(probe.Answer);
            },
            ShowErrorsPanel = () => probe.Shown++,
            RefreshSchemaBrowser = () =>
            {
                probe.Refreshed++;
                return Task.CompletedTask;
            },
            EditorOpener = (text, profile) =>
            {
                probe.Opened.Add((text, profile));
                return true;
            },
        };
        return (vm, meta, exec, probe);
    }

    [Fact]
    public async Task Recompiling_a_source_confirms_runs_alter_through_the_engine_and_publishes_errors()
    {
        var (vm, meta, exec, probe) = Make();
        meta.Errors = [new CompileError(3, 9, "cannot find symbol", false)];
        exec.Failure = new DbError("ORA-24344", "success with compilation error");
        var session = new Session();
        await vm.OpenAsync(session, Source, showDdl: false);
        Assert.True(vm.IsJavaCompilable);
        Assert.True(vm.IsJavaSource);
        Assert.True(vm.CanCompileJava);
        Assert.Equal(string.Empty, vm.JavaCompileReason);
        var readsBefore = meta.DetailReads;

        Assert.True(await vm.CompileJavaAsync());

        const string sql = "ALTER JAVA SOURCE \"SCOTT\".\"com/example/Hello\" COMPILE";
        Assert.Contains(sql, Assert.Single(probe.Asked), StringComparison.Ordinal);
        var stmt = Assert.Single(exec.Statements);
        Assert.Equal(sql, stmt.Text);
        Assert.Equal(StatementKind.Ddl, stmt.Kind);
        Assert.Equal(Oranda.Core.History.HistorySource.SchemaBrowser, Assert.Single(exec.Options)!.Source); // 실행 기록에 남는다
        Assert.Equal(Source, meta.LastErrorsTarget); // ALL_ERRORS 를 이 객체(JAVA SOURCE, 짧은 이름)로 읽는다
        Assert.Equal("cannot find symbol", Assert.Single(vm.JavaErrors).Message);
        Assert.Equal(1, probe.Shown);
        Assert.Contains("com/example/Hello", CompileErrorsViewModel.LastSource?.Title, StringComparison.Ordinal);
        Assert.True(meta.DetailReads > readsBefore); // 상태(VALID/INVALID)를 새로 읽는다
        Assert.Equal(1, probe.Refreshed);
        Assert.Contains("컴파일 오류 1개", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolving_a_class_without_errors_says_compiled()
    {
        var (vm, _, exec, probe) = Make();
        await vm.OpenAsync(new Session(), Class, showDdl: false);
        Assert.False(vm.IsJavaSource);

        Assert.True(await vm.CompileJavaAsync());

        Assert.Equal("ALTER JAVA CLASS \"SCOTT\".\"com/example/Hello\" RESOLVE", Assert.Single(exec.Statements).Text);
        Assert.Equal(0, probe.Shown);
        Assert.Empty(vm.JavaErrors);
        Assert.Equal("컴파일됨", vm.StatusText);
    }

    [Fact]
    public async Task Declining_the_confirmation_sends_nothing()
    {
        var (vm, _, exec, probe) = Make();
        probe.Answer = false;
        await vm.OpenAsync(new Session(), Source, showDdl: false);

        Assert.False(await vm.CompileJavaAsync());

        Assert.Empty(exec.Statements);
        Assert.Equal(0, probe.Refreshed);
    }

    [Fact]
    public async Task Without_a_jvm_the_button_is_disabled_with_a_reason_and_nothing_is_sent()
    {
        var (vm, _, exec, probe) = Make(jvm: false);
        await vm.OpenAsync(new Session(), Source, showDdl: false);

        Assert.True(vm.IsJavaCompilable);
        Assert.False(vm.CanCompileJava);
        Assert.Contains("JVM", vm.JavaCompileReason, StringComparison.Ordinal);
        Assert.False(await vm.CompileJavaAsync());
        Assert.Empty(probe.Asked);
        Assert.Empty(exec.Statements);
        Assert.Contains("JVM", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_only_connection_cannot_compile()
    {
        var (vm, _, exec, _) = Make();
        await vm.OpenAsync(new Session(readOnly: true), Class, showDdl: false);

        Assert.False(vm.CanCompileJava);
        Assert.Contains("읽기 전용", vm.JavaCompileReason, StringComparison.Ordinal);
        Assert.False(await vm.CompileJavaAsync());
        Assert.Empty(exec.Statements);
    }

    [Fact]
    public async Task A_policy_denial_is_reported_and_nothing_is_reloaded()
    {
        var (vm, _, exec, probe) = Make();
        exec.DeniedMessage = "운영 접속입니다";
        await vm.OpenAsync(new Session(), Source, showDdl: false);

        Assert.False(await vm.CompileJavaAsync());

        Assert.Single(exec.Statements);
        Assert.Equal("운영 접속입니다", vm.StatusText);
        Assert.Equal(0, probe.Refreshed);
    }

    [Fact]
    public async Task Other_objects_have_no_java_toolbar()
    {
        var (vm, _, _, _) = Make();
        await vm.OpenAsync(new Session(), new DbObjectRef("SCOTT", "JAVA_RES", "JAVA RESOURCE"), showDdl: false);
        Assert.False(vm.IsJavaCompilable);
        Assert.False(vm.CanCompileJava);
        Assert.False(await vm.CompileJavaAsync());
    }

    [Fact]
    public async Task Open_in_editor_puts_the_source_ddl_with_a_slash_line_in_a_new_tab_of_this_connection()
    {
        var (vm, meta, exec, probe) = Make();
        var session = new Session();
        await vm.OpenAsync(session, Source, showDdl: false);

        Assert.True(vm.OpenInEditor());
        var (text, profile) = Assert.Single(probe.Opened);
        Assert.StartsWith("CREATE OR REPLACE AND COMPILE JAVA SOURCE", text, StringComparison.Ordinal);
        Assert.EndsWith("}\n/\n", text, StringComparison.Ordinal);
        Assert.Equal(session.Profile.Id, profile);
        Assert.Empty(exec.Statements); // 실행은 사용자가 한다

        meta.Terminator = false; // 종결자 옵션을 끈 DDL 이어도 / 줄을 붙인다
        vm.SqlTerminator = false;
        await vm.LoadDdlAsync();
        Assert.True(vm.OpenInEditor());
        Assert.EndsWith("}\n/\n", probe.Opened[1].Text, StringComparison.Ordinal);

        // 편집기에 넣은 글은 Java 코드 전체가 한 문장이다
        var statements = Oranda.Language.Lexing.StatementSplitter.Split(text, Oranda.Core.Language.SplitOptions.Default);
        Assert.Single(statements);
    }

    [Fact]
    public Task Detail_view_shows_recompile_and_open_in_editor_and_disables_without_jvm() => HeadlessHost.Run(async () =>
    {
        var (vm, meta, exec, probe) = Make();
        var session = new Session();
        await vm.OpenAsync(session, Source, showDdl: false);
        var view = new ObjectDetailView { DataContext = vm };
        var w = new Window { Content = view, Width = 900, Height = 600 };
        w.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var compile = view.FindControl<Button>("CompileJavaButton")!;
            var open = view.FindControl<Button>("OpenInEditorButton")!;
            Assert.True(compile.IsEffectivelyVisible);
            Assert.True(compile.IsEffectivelyEnabled);
            Assert.Equal("다시 컴파일", Avalonia.Automation.AutomationProperties.GetName(compile));
            Assert.True(open.IsEffectivelyVisible);

            compile.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await HeadlessHost.WaitUntil(() => exec.Statements.Count == 1 && vm.StatusText == "컴파일됨");
            Assert.Single(probe.Asked); // 확인 창을 거쳤다
            open.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Single(probe.Opened);

            await vm.OpenAsync(session, Class, showDdl: false); // 클래스: 편집기로 열기는 없다
            Dispatcher.UIThread.RunJobs();
            Assert.True(compile.IsEffectivelyVisible);
            Assert.False(open.IsEffectivelyVisible);

            meta.Jvm = false;
            var noJvm = Make(jvm: false).Vm;
            await noJvm.OpenAsync(session, Source, showDdl: false);
            view.DataContext = noJvm;
            Dispatcher.UIThread.RunJobs();
            Assert.True(compile.IsEffectivelyVisible);
            Assert.False(compile.IsEffectivelyEnabled);
            Assert.Contains("JVM", view.FindControl<TextBlock>("JavaCompileReasonText")!.Text, StringComparison.Ordinal);

            await noJvm.OpenAsync(session, new DbObjectRef("SCOTT", "EMP", "TABLE"), showDdl: false);
            Dispatcher.UIThread.RunJobs();
            Assert.False(compile.IsEffectivelyVisible); // Java 가 아니면 줄 자체가 없다
        }
        finally
        {
            w.Close();
        }
    });

    [Theory]
    [InlineData("CREATE OR REPLACE AND COMPILE JAVA SOURCE NAMED \"SCOTT\".\"com/example/Hello\" AS\nclass Hello {}", "SCOTT", "com/example/Hello")]
    [InlineData("-- head\ncreate and resolve noforce java source named \"Hello\" as class Hello {}", null, "Hello")]
    [InlineData("CREATE JAVA SOURCE NAMED Hello\n  AUTHID CURRENT_USER\nAS\nclass Hello {}", null, "HELLO")]
    public void Java_create_header_is_read(string text, string? owner, string name)
    {
        var header = PlsqlSourceText.ParseJavaCreateHeader(text);
        Assert.Equal(("JAVA SOURCE", owner, name), header);
    }

    [Theory]
    [InlineData("CREATE JAVA SOURCE NAMED Hello USING BFILE (d, 'Hello.java')")]
    [InlineData("CREATE JAVA CLASS USING BFILE (d, 'Hello.class')")]
    [InlineData("CREATE OR REPLACE PROCEDURE p AS BEGIN NULL; END;")]
    public void Non_java_source_text_has_no_java_header(string text) => Assert.Null(PlsqlSourceText.ParseJavaCreateHeader(text));

    [Fact]
    public void Server_error_lines_count_from_the_java_code_after_as()
    {
        Assert.Equal((1, 0), PlsqlSourceText.SourceStart("CREATE OR REPLACE AND COMPILE JAVA SOURCE NAMED \"Hello\" AS\npublic class Hello {}"));
        Assert.Equal((2, 0), PlsqlSourceText.SourceStart("-- c\nCREATE JAVA SOURCE NAMED \"Hello\" AS\r\npublic class Hello {}"));
        const string oneLine = "CREATE JAVA SOURCE NAMED \"Hello\" AS public class Hello {}";
        Assert.Equal((0, oneLine.IndexOf("public", StringComparison.Ordinal)), PlsqlSourceText.SourceStart(oneLine));
    }

    [Fact]
    public void Running_create_java_source_in_the_editor_reads_its_errors()
    {
        var stmt = new SqlStatement("CREATE OR REPLACE AND COMPILE JAVA SOURCE NAMED \"Hello\" AS\nclass Hello { int a; }", default, StatementKind.Ddl);
        Assert.True(SqlCompileErrors.ShouldCapture(stmt, new ExecutionResult(stmt, ExecutionStatus.Succeeded)));
        Assert.True(SqlCompileErrors.ShouldCapture(stmt, new ExecutionResult(stmt, ExecutionStatus.Failed) { Error = new DbError("ORA-24344", "x") }));
        Assert.True(SqlCompileErrors.ShouldCapture(stmt, new ExecutionResult(stmt, ExecutionStatus.Failed) { Error = new DbError("ORA-29535", "x") }));
        Assert.False(SqlCompileErrors.ShouldCapture(stmt, new ExecutionResult(stmt, ExecutionStatus.Failed) { Error = new DbError("ORA-29538", "Java not installed") }));

        var plsql = new SqlStatement("CREATE OR REPLACE PROCEDURE p AS BEGIN NULL; END;", default, StatementKind.Ddl);
        Assert.False(SqlCompileErrors.ShouldCapture(plsql, new ExecutionResult(plsql, ExecutionStatus.Failed) { Error = new DbError("ORA-29535", "x") }));
    }

    private sealed class Probe
    {
        public bool Answer { get; set; } = true;

        public List<string> Asked { get; } = [];

        public List<(string Text, Guid? Profile)> Opened { get; } = [];

        public int Shown { get; set; }

        public int Refreshed { get; set; }
    }

    private sealed class Session(bool readOnly = false) : ISession
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = new("dev", "SCOTT", "h:1521/s", ReadOnly: readOnly, Id: Guid.NewGuid());

        public ServerInfo Server { get; } = new("23.26.0.0.0", null, null, null, null, null, false, 1, new ServerCapabilities(false, false, false, false, false));

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => "FREE/FREEPDB1/SCOTT";

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Exec : IExecutionService
    {
        public List<SqlStatement> Statements { get; } = [];

        public List<ExecutionOptions?> Options { get; } = [];

        public DbError? Failure { get; set; }

        public string? DeniedMessage { get; set; }

        public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds,
                                                  ExecutionOptions? options, CancellationToken ct)
        {
            Statements.Add(stmt);
            Options.Add(options);
            if (DeniedMessage is not null)
            {
                return Task.FromResult(new ExecutionResult(stmt, ExecutionStatus.Denied)
                {
                    Policy = new PolicyDecision(PolicyDecisionKind.Deny, PolicyRule.None, DeniedMessage),
                });
            }

            return Task.FromResult(Failure is null
                ? new ExecutionResult(stmt, ExecutionStatus.Succeeded)
                : new ExecutionResult(stmt, ExecutionStatus.Failed) { Error = Failure });
        }

        public async IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode,
                                                                         ExecutionOptions? options, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>Java 객체 상세·DDL·오류·JVM 유무만 아는 가짜 메타데이터.</summary>
    private sealed class Meta : IMetadataProvider
    {
        public bool Jvm { get; set; } = true;

        public bool Terminator { get; set; } = true;

        public IReadOnlyList<CompileError> Errors { get; set; } = [];

        public DbObjectRef? LastErrorsTarget { get; private set; }

        public int DetailReads { get; private set; }

        public Task<bool> IsJavaAvailableAsync(ISession s, CancellationToken ct) => Task.FromResult(Jvm);

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct)
        {
            DetailReads++;
            return Task.FromResult(new Oranda.Core.Metadata.ObjectDetail(obj, [new DetailSection("일반", ["항목", "값"], [["상태", "VALID"]])]));
        }

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) =>
            Task.FromResult(obj.ObjectType == "JAVA SOURCE"
                ? $"CREATE OR REPLACE AND COMPILE JAVA SOURCE NAMED \"{obj.Owner}\".\"{obj.Name}\" AS\npublic class Hello {{\n    int a;\n}}" + (Terminator ? "\n/" : string.Empty)
                : "-- note");

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct)
        {
            LastErrorsTarget = obj;
            return Task.FromResult(Errors);
        }

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }
}
