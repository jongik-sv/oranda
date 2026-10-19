using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ExtensionTest;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.ExtensionTest;

// 소유: TSK-10-03. AI 확장 지점 시험 패널 — 확장 지점(IEditorContext·IExecutionService·IMetadataProvider·IOutboundConsent)만으로
// 선택 SQL 을 바꿔 다시 넣고(미리보기·승인 뒤에만), 넣은 SQL 이 실제 실행 엔진의 정책 관문을 그대로 지나는지 본다.
// 편집기는 실제 EditorContext·SqlEditorHost, 실행은 실제 OracleExecutionService + ExecutionPolicy + AppExecutionInteraction.
// 시험 세션은 Oracle 연결이 없으므로 드라이버까지 가지 않는 경로(거부·확인 거절)만 단정한다.
public class ExtensionTestTests
{
    // ---- 등록 ----------------------------------------------------------------------------

    [Fact]
    public void Panel_and_view_command_are_registered_for_dev_builds_only()
    {
        var panel = ShellTestHelpers.RequirePanel(ExtensionTestCommands.PanelId);
        Assert.True(panel.DevBuildOnly);
        Assert.Equal(typeof(ExtensionTestViewModel), panel.ViewModelType);
        Assert.Equal(DockLocation.Right, panel.DefaultLocation);

        var cmd = ShellTestHelpers.CreateCommandRegistry().Find(ExtensionTestCommands.ViewCommandId);
        Assert.NotNull(cmd);
        Assert.Equal("devBuild", cmd.Enablement);
        Assert.Equal(CommandCategory.View, cmd.Category);
    }

    [Fact]
    public async Task View_command_without_services_does_nothing()
    {
        await new ExtensionTestCommandHandler().ExecuteAsync(
            new CommandContext(ExtensionTestCommands.ViewCommandId, FakeContextKeys.AllTrue), CancellationToken.None);
    }

    [Fact]
    public void Panel_uses_only_core_extension_points()
    {
        // 셸 생성자 고르기가 지연 어셈블리를 깨우지 않게 생성자는 인자 없음·IServiceProvider 뿐(D-006).
        var ctorParams = typeof(ExtensionTestViewModel).GetConstructors()
            .Select(c => string.Join(",", c.GetParameters().Select(p => p.ParameterType.Name))).Order().ToList();
        Assert.Equal(["", "IServiceProvider"], ctorParams);

        // 주입 지점은 모두 Oranda.Core 의 확장 지점 인터페이스다(Core 를 고치지 않고 붙는다).
        Type[] points = [typeof(IEditorContext), typeof(IExecutionService), typeof(IMetadataProvider), typeof(IOutboundConsent)];
        foreach (var name in new[] { "Editor", "Execution", "Metadata", "Consent" })
        {
            var type = typeof(ExtensionTestViewModel).GetProperty(name)!.PropertyType;
            Assert.Contains(type, points);
            Assert.Equal("Oranda.Core", type.Assembly.GetName().Name);
        }
    }

    // ---- 읽기·제안 ------------------------------------------------------------------------

    [Theory]
    [InlineData(ExtensionTransform.UpperCase, "select a from t", "SELECT A FROM T")]
    [InlineData(ExtensionTransform.LowerCase, "SELECT A FROM T", "select a from t")]
    [InlineData(ExtensionTransform.CommentHeader, "select 1 from dual", "-- extension test\nselect 1 from dual")]
    public void Transform_rules(ExtensionTransform t, string input, string expected) =>
        Assert.Equal(expected, ExtensionTestViewModel.Apply(t, input));

    [Fact]
    public Task Read_uses_selection_and_proposal_applies_only_after_user_accepts_preview() => Ui.Run(async () =>
    {
        var dialogs = new PreviewDialogs();
        var host = new SqlEditorHost(dialogs: dialogs);
        var tab = host.NewDocument("select a from t;\nselect b from u;");
        tab.Select(0, 15);
        var vm = new ExtensionTestViewModel { Editor = new EditorContext { Host = host } };

        Assert.True(vm.ReadEditor());
        Assert.Equal("select a from t", vm.SourceText);
        Assert.Equal("SELECT A FROM T", vm.ProposedText);

        // 거절: 문서는 그대로.
        Assert.False(await vm.ProposeAsync(default));
        Assert.Equal("select a from t;\nselect b from u;", tab.Text);
        Assert.Equal(("select a from t", "SELECT A FROM T"), (dialogs.Before, dialogs.After));
        Assert.StartsWith("확장 시험", dialogs.Title, StringComparison.Ordinal);

        // 승인: 바뀐다. 사용자가 제안 글을 고친 것도 그대로 들어간다.
        dialogs.Accept = true;
        vm.ProposedText = "SELECT A FROM T WHERE 1 = 1";
        Assert.True(await vm.ProposeAsync(default));
        Assert.Equal("SELECT A FROM T WHERE 1 = 1;\nselect b from u;", tab.Text);
        Assert.Equal(2, dialogs.Calls);
    });

    [Fact]
    public Task Read_without_selection_takes_the_statement_at_caret_and_reports_missing_editor() => Ui.Run(() =>
    {
        var host = new SqlEditorHost();
        var vm = new ExtensionTestViewModel { Editor = new EditorContext { Host = host }, Transform = ExtensionTransform.CommentHeader };
        Assert.False(vm.ReadEditor()); // 탭 없음
        Assert.Null(vm.SourceRange);

        var tab = host.NewDocument("select 1 from dual;\nupdate t set a = 2 where id = 1;");
        tab.Select(25, 0);
        Assert.True(vm.ReadEditor());
        Assert.StartsWith("update t set a = 2", vm.SourceText, StringComparison.Ordinal);
        Assert.StartsWith("-- extension test\nupdate", vm.ProposedText, StringComparison.Ordinal);

        vm.Transform = ExtensionTransform.UpperCase; // 규칙을 바꾸면 제안이 다시 만들어진다
        Assert.StartsWith("UPDATE T SET A = 2", vm.ProposedText, StringComparison.Ordinal);
    });

    [Fact]
    public async Task Propose_before_read_does_nothing()
    {
        var vm = new ExtensionTestViewModel { Editor = new EditorContext { Host = new SqlEditorHost() } };
        Assert.False(await vm.ProposeAsync(default));
        Assert.Contains("읽기", vm.StatusText, StringComparison.Ordinal);
    }

    // ---- 정책 관문(실제 실행 엔진) ---------------------------------------------------------

    [Fact]
    public Task Panel_update_on_production_goes_through_the_production_confirm_dialog() => Ui.Run(async () =>
    {
        var prod = new ConnectionProfile("PROD", "scott", "db:1521/S", Environment: ConnectionEnvironment.Production);
        var rig = new EngineRig(prod);
        var vm = rig.Panel("update emp set sal = 0 where id = 1");

        var result = await vm.RunCurrentStatementAsync(default);

        Assert.NotNull(result);
        Assert.Equal(ExecutionStatus.Cancelled, result.Status); // 대화상자에서 거절
        var confirm = Assert.Single(rig.Dialogs.Confirms);
        Assert.Equal(PolicyRule.ProductionChange, confirm.Decision.Rule);
        Assert.Equal(ConnectionEnvironment.Production, confirm.Profile!.Environment);
        Assert.Equal("UPDATE", confirm.Statement.Verb);
        Assert.Equal(HistorySource.Extension, Assert.Single(rig.Policy.Sources));
        Assert.Equal(PolicyDecisionKind.Confirm, Assert.Single(rig.Decisions).Decision.Kind);
        Assert.Contains("확인 대화상자", vm.StatusText, StringComparison.Ordinal);
    });

    [Fact]
    public Task Panel_dml_on_read_only_profile_is_denied_without_a_dialog() => Ui.Run(async () =>
    {
        var ro = new ConnectionProfile("RO", "scott", "db:1521/S", ReadOnly: true);
        var rig = new EngineRig(ro);
        var vm = rig.Panel("delete from emp where id = 1");

        var result = await vm.RunCurrentStatementAsync(default);

        Assert.Equal(ExecutionStatus.Denied, result!.Status);
        Assert.Equal(PolicyRule.ReadOnlyProfile, result.Policy!.Rule);
        Assert.Empty(rig.Dialogs.Confirms);
        Assert.Contains("거부", vm.StatusText, StringComparison.Ordinal);
    });

    [Fact]
    public Task Panel_drop_asks_for_retyping_the_target_name() => Ui.Run(async () =>
    {
        var dev = new ConnectionProfile("DEV", "scott", "db:1521/S");
        var rig = new EngineRig(dev);
        var vm = rig.Panel("drop table emp_backup");

        var result = await vm.RunCurrentStatementAsync(default);

        Assert.Equal(ExecutionStatus.Cancelled, result!.Status);
        var confirm = Assert.Single(rig.Dialogs.Confirms);
        Assert.Equal(PolicyRule.DangerousCommand, confirm.Decision.Rule);
        Assert.False(string.IsNullOrEmpty(confirm.Decision.RetypeText));
    });

    [Fact]
    public Task Proposed_update_inserted_by_the_panel_still_meets_the_production_gate() => Ui.Run(async () =>
    {
        // 패널이 넣은 SQL(제안 승인 뒤)을 실행해도 같은 관문을 지난다.
        var prod = new ConnectionProfile("PROD", "scott", "db:1521/S", Environment: ConnectionEnvironment.Production);
        var rig = new EngineRig(prod);
        var vm = rig.Panel("select * from emp where id = 1");
        rig.Preview.Accept = true;
        Assert.True(vm.ReadEditor());
        vm.ProposedText = "update emp set sal = sal * 2 where id = 1";
        Assert.True(await vm.ProposeAsync(default));

        var result = await vm.RunCurrentStatementAsync(default);

        Assert.Equal(ExecutionStatus.Cancelled, result!.Status);
        Assert.Equal(PolicyRule.ProductionChange, Assert.Single(rig.Dialogs.Confirms).Decision.Rule);
    });

    [Fact]
    public async Task Run_without_session_or_statement_does_not_execute()
    {
        var exec = new CountingExecution();
        var vm = new ExtensionTestViewModel { Editor = new EditorContext { Host = new SqlEditorHost() }, Execution = exec, SessionResolver = _ => null };
        Assert.Null(await vm.RunCurrentStatementAsync(default));
        Assert.Equal(0, exec.Calls);
    }

    // ---- 메타데이터 ------------------------------------------------------------------------

    [Fact]
    public async Task Load_tables_reads_through_the_metadata_extension_point()
    {
        var session = new TestSession(new ConnectionProfile("DEV", "scott", "db:1521/S"));
        var meta = new FakeMetadata();
        var vm = new ExtensionTestViewModel { Metadata = meta, Editor = new EditorContext { Host = new SqlEditorHost() }, SessionResolver = _ => session };

        Assert.Equal(2, await vm.LoadTablesAsync(default));
        Assert.Equal(["SCOTT.EMP", "SCOTT.DEPT"], vm.Objects);
        Assert.Same(session, meta.Session);
        Assert.Equal(["TABLE"], meta.Query!.ObjectTypes);

        var none = new ExtensionTestViewModel { Metadata = meta, Editor = new EditorContext { Host = new SqlEditorHost() }, SessionResolver = _ => null };
        Assert.Equal(-1, await none.LoadTablesAsync(default));
    }

    // ---- 외부 전송 동의 --------------------------------------------------------------------

    [Fact]
    public async Task Outbound_is_blocked_without_consent_service_or_when_denied()
    {
        var sent = new List<OutboundRequest>();
        Task Transport(OutboundRequest r, CancellationToken ct)
        {
            sent.Add(r);
            return Task.CompletedTask;
        }

        var noConsent = new ExtensionTestViewModel(new EmptyServices()) { Transport = Transport, ProposedText = "select 1 from dual" };
        Assert.False(await noConsent.SendOutboundAsync(default));

        var denied = new FakeConsent(ConsentDecision.Denied);
        var vm = new ExtensionTestViewModel { Consent = denied, Transport = Transport, ProposedText = "select 1 from dual" };
        Assert.False(await vm.SendOutboundAsync(default));
        Assert.Empty(sent);

        // 동의 요청은 보낼 내용을 그대로 보여 준다.
        var req = Assert.Single(denied.Requests);
        Assert.Equal(OutboundPurpose.Extension, req.Purpose);
        Assert.Equal("select 1 from dual", req.PayloadPreview);
        Assert.Equal(ExtensionTestViewModel.TestDestination, req.Destination);
    }

    [Fact]
    public async Task Outbound_is_sent_only_after_consent()
    {
        var sent = new List<OutboundRequest>();
        var vm = new ExtensionTestViewModel
        {
            Consent = new FakeConsent(ConsentDecision.AllowedOnce),
            ProposedText = "select 2 from dual",
            Transport = (r, _) =>
            {
                sent.Add(r);
                return Task.CompletedTask;
            },
        };

        Assert.True(await vm.SendOutboundAsync(default));
        Assert.Equal("select 2 from dual", Assert.Single(sent).PayloadPreview);

        // 전송 함수가 없으면(1.0 기본) 동의를 받아도 아무것도 보내지 않는다.
        var noTransport = new ExtensionTestViewModel { Consent = new FakeConsent(ConsentDecision.AllowedAlways), ProposedText = "x" };
        Assert.True(await noTransport.SendOutboundAsync(default));
        Assert.Contains("보내지 않음", noTransport.StatusText, StringComparison.Ordinal);
    }

    // ---- 화면(Headless) ---------------------------------------------------------------------

    [Fact]
    public Task View_renders_and_read_button_reads_the_editor() => Ui.Run(() =>
    {
        var host = new SqlEditorHost();
        var tab = host.NewDocument("select a from t");
        tab.Select(0, 8);
        var vm = new ExtensionTestViewModel { Editor = new EditorContext { Host = host } };
        var view = new ExtensionTestView { DataContext = vm };
        var window = new Window { Width = 420, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var buttons = view.GetVisualDescendants().OfType<Button>().ToDictionary(b => b.Name ?? string.Empty);
        Assert.Contains("ProposeButton", buttons.Keys);
        Assert.Contains("RunButton", buttons.Keys);
        Assert.Contains("SendButton", buttons.Keys);

        buttons["ReadButton"].Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("select a", vm.SourceText);
        var proposed = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ProposedBox");
        Assert.Equal("SELECT A", proposed.Text);
        window.Close();
    });

    // ---- 도우미 ----------------------------------------------------------------------------

    /// <summary>실제 실행 엔진(OracleExecutionService, internal) + 실제 정책 + 앱 확인 상호작용 + 가짜 대화상자.</summary>
    private sealed class EngineRig
    {
        public EngineRig(ConnectionProfile profile)
        {
            Session = new TestSession(profile);
            var bus = new EventBus();
            bus.Subscribe<PolicyDecisionMade>(this, e => Decisions.Add(e));
            var interaction = new AppExecutionInteraction { Dialogs = Dialogs, ProfileProvider = () => profile };
            var type = Type.GetType("Oranda.Oracle.Execution.OracleExecutionService, Oranda.Oracle", throwOnError: true)!;
            var ctor = type.GetConstructors().Single();
            Engine = (IExecutionService)ctor.Invoke([Policy, interaction, bus, null]);
        }

        public TestSession Session { get; }

        public RecordingPolicy Policy { get; } = new();

        public ConfirmDialogs Dialogs { get; } = new();

        public PreviewDialogs Preview { get; } = new();

        public List<PolicyDecisionMade> Decisions { get; } = [];

        public IExecutionService Engine { get; }

        public ExtensionTestViewModel Panel(string sql)
        {
            var host = new SqlEditorHost(dialogs: Preview);
            var tab = host.NewDocument(sql);
            tab.Select(0, 0);
            return new ExtensionTestViewModel
            {
                Editor = new EditorContext { Host = host },
                Execution = Engine,
                SessionResolver = _ => Session,
            };
        }
    }

    private sealed class RecordingPolicy : IExecutionPolicy
    {
        private readonly ExecutionPolicy _inner = new();

        public List<HistorySource> Sources { get; } = [];

        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct)
        {
            Sources.Add(ctx.Source);
            return _inner.EvaluateAsync(ctx, stmt, ct);
        }
    }

    private sealed class ConfirmDialogs : IExecutionDialogs
    {
        public List<ConfirmRequest> Confirms { get; } = [];

        public Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
        {
            Confirms.Add(request);
            return Task.FromResult(false);
        }

        public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(
            IReadOnlyList<string> names, IReadOnlyDictionary<string, string> last, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

        public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) =>
            Task.FromResult(ScriptErrorAction.Stop);

        public Task<UncommittedChoice> AskUncommittedAsync(CancellationToken ct) => Task.FromResult(default(UncommittedChoice));

        public Task<bool> PreviewDmlAsync(string text, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class PreviewDialogs : IEditorDialogs
    {
        public bool Accept { get; set; }

        public int Calls { get; private set; }

        public string? Title { get; private set; }

        public string? Before { get; private set; }

        public string? After { get; private set; }

        public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string initial, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct)
        {
            Calls++;
            (Title, Before, After) = (title, before, after);
            return Task.FromResult(Accept);
        }
    }

    private sealed class TestSession(ConnectionProfile profile) : ISession
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = profile;

        public ServerInfo Server { get; } = new("23.26.0.0.0", "FREE", "localhost", "FREEPDB1", "FREE", "FREEPDB1", true, 1,
            new ServerCapabilities(false, true, true, false, false), DbUniqueName: "FREE", SessionUser: "SCOTT");

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => "FREE/FREEPDB1/SCOTT";

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingExecution : IExecutionService
    {
        public int Calls { get; private set; }

        public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds,
                                                  ExecutionOptions? options, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ExecutionResult(stmt, ExecutionStatus.Succeeded));
        }

        public IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode,
                                                                    ExecutionOptions? options, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeMetadata : IMetadataProvider
    {
        public ISession? Session { get; private set; }

        public ObjectQuery? Query { get; private set; }

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct)
        {
            (Session, Query) = (s, q);
            IReadOnlyList<DbObjectRef> rows = [new("SCOTT", "EMP", "TABLE"), new("SCOTT", "DEPT", "TABLE")];
            return Task.FromResult(rows);
        }

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => throw new NotSupportedException();

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeConsent(ConsentDecision decision) : IOutboundConsent
    {
        public List<OutboundRequest> Requests { get; } = [];

        public Task<ConsentDecision> RequestAsync(OutboundRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(decision);
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
