using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Binds;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.Execution;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.History;
using Oranda.Core.Language;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.Execution;

// 소유: TSK-06-02. 실행 화면(컨트롤러·상태·대화상자 모델·상태 표시줄 구간·결과 영역) 시험.
// 통합 시험(편집기 → 실행 엔진 → 그리드)은 ExecutionIntegrationTests, 환경 변수 ORANDA_IT_* 로 26ai 컨테이너에 접속한다.

internal sealed class FakeSession(ConnectionProfile profile) : ISession
{
    public Guid Id { get; } = Guid.NewGuid();

    public ConnectionProfile Profile { get; } = profile;

    public ServerInfo Server { get; } = new("23.26.0.0.0", "FREE", "localhost", "FREEPDB1", "FREE", "FREEPDB1", true, 1,
        new ServerCapabilities(false, true, true, false, false), DbUniqueName: "FREE", SessionUser: "SCOTT");

    public SessionPurpose Purpose => SessionPurpose.Work;

    public string DbKey => "FREE/FREEPDB1/SCOTT";

    public SessionState State { get; set; } = SessionState.Idle;

    public bool HasPendingTransaction { get; set; }

    public int Commits { get; private set; }

    public int Rollbacks { get; private set; }

    public Task CommitAsync(CancellationToken ct)
    {
        Commits++;
        HasPendingTransaction = false;
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken ct)
    {
        Rollbacks++;
        HasPendingTransaction = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeExecution : IExecutionService
{
    public Func<SqlStatement, CancellationToken, Task<ExecutionResult>> Handler { get; set; } =
        (s, _) => Task.FromResult(new ExecutionResult(s, ExecutionStatus.Succeeded) { RowsAffected = 1 });

    public List<SqlStatement> Statements { get; } = [];

    public IReadOnlyDictionary<string, BindValue>? LastBinds { get; private set; }

    public ScriptErrorMode? LastMode { get; private set; }

    public ExecutionOptions? LastOptions { get; private set; }

    public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds, ExecutionOptions? options, CancellationToken ct)
    {
        Statements.Add(stmt);
        LastBinds = binds;
        return Handler(stmt, ct);
    }

    public async IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode,
        ExecutionOptions? options, [EnumeratorCancellation] CancellationToken ct)
    {
        LastMode = mode;
        LastOptions = options;
        foreach (var stmt in stmts)
        {
            var r = await ExecuteAsync(s, stmt, new Dictionary<string, BindValue>(), options, ct);
            yield return r;
            if (r.Status != ExecutionStatus.Succeeded && mode != ScriptErrorMode.Continue)
            {
                yield break;
            }
        }
    }
}

internal sealed class FakeDialogs : IExecutionDialogs
{
    public bool ConfirmAnswer { get; set; } = true;

    public List<ConfirmRequest> Confirms { get; } = [];

    public IReadOnlyDictionary<string, string>? SubstitutionAnswer { get; set; } = new Dictionary<string, string> { ["x"] = "1" };

    public List<IReadOnlyDictionary<string, string>> SubstitutionLasts { get; } = [];

    public ScriptErrorAction ScriptErrorAnswer { get; set; } = ScriptErrorAction.Stop;

    public UncommittedChoice UncommittedAnswer { get; set; } = UncommittedChoice.Cancel;

    public int UncommittedAsked { get; private set; }

    public bool PreviewAnswer { get; set; } = true;

    public string? PreviewText { get; private set; }

    public Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
    {
        Confirms.Add(request);
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, IReadOnlyDictionary<string, string> last, CancellationToken ct)
    {
        SubstitutionLasts.Add(new Dictionary<string, string>(last));
        return Task.FromResult(SubstitutionAnswer);
    }

    public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) => Task.FromResult(ScriptErrorAnswer);

    public Task<UncommittedChoice> AskUncommittedAsync(CancellationToken ct)
    {
        UncommittedAsked++;
        return Task.FromResult(UncommittedAnswer);
    }

    /// <summary>접속마다 미커밋 확인에서 물은 접속 이름(차례대로).</summary>
    public List<string> ConnectionsAsked { get; } = [];

    public Task<UncommittedChoice> AskConnectionUncommittedAsync(string connection, CancellationToken ct)
    {
        ConnectionsAsked.Add(connection);
        UncommittedAsked++;
        return Task.FromResult(UncommittedAnswer);
    }

    public MissingConnectionChoice MissingAnswer { get; set; } = MissingConnectionChoice.Cancel;

    /// <summary>접속 안 됨 탭 묻기: (탭 접속 이름, 지금 접속 이름).</summary>
    public List<(string Profile, string? Current)> MissingAsked { get; } = [];

    public Task<MissingConnectionChoice> AskMissingConnectionAsync(string profile, string? current, CancellationToken ct)
    {
        MissingAsked.Add((profile, current));
        return Task.FromResult(MissingAnswer);
    }

    public Task<bool> PreviewDmlAsync(string text, CancellationToken ct)
    {
        PreviewText = text;
        return Task.FromResult(PreviewAnswer);
    }
}

/// <summary>한 열짜리 메모리 결과.</summary>
internal sealed class OneColumnResult(params string[] values) : IResultSet
{
    public IReadOnlyList<ResultColumn> Columns { get; } = [new ResultColumn(0, "N", "VARCHAR2", ColumnDataKind.Text)];

    public bool IsComplete => true;

    public long? KnownRowCount => values.Length;

    public bool Disposed { get; private set; }

    public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
    {
        IReadOnlyList<IReadOnlyList<object?>> cols = [values.Cast<object?>().ToList()];
        return Task.FromResult(new ResultPage(pageIndex, 0, pageIndex == 0 ? values.Length : 0, pageIndex == 0 ? cols : [[]]) { IsLast = true });
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>컨트롤러 시험 장비: 가짜 세션·엔진·대화상자 + 실제 문장 분리(언어 서비스).</summary>
internal sealed class Rig : IDisposable
{
    private static readonly Lazy<ServiceContainer> Services = new(() =>
        AppComposition.Create(new AppCompositionOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "oranda-exec-" + Guid.NewGuid().ToString("N")) }));

    public Rig(ConnectionProfile? profile = null)
    {
        Profile = profile ?? new ConnectionProfile("dev", "scott", "localhost:1521/FREEPDB1");
        Session = new FakeSession(Profile);
        State = new ExecutionState(Bus);
        Log = new ExecutionLog(Bus, State);
        Host = new SqlEditorHost();
        var lang = LanguageForTests();
        Controller = new ExecutionController(new ExecutionDeps
        {
            State = State,
            Log = Log,
            Dialogs = Dialogs,
            Session = () => Session,
            Execution = () => Exec,
            Language = () => lang,
            Host = () => Host,
            Binds = BindVariableStore.For(Bus),
            ActivatePanel = id => Panels.Add("show " + id),
            TogglePanel = id => Panels.Add("toggle " + id),
        });
        Bus.Publish(new SessionOpened(Session.Id, Profile, Session.Server, SessionPurpose.Work));
    }

    public EventBus Bus { get; } = new();

    /// <summary>컨트롤러가 요청한 도구 창 동작(<c>show id</c>·<c>toggle id</c>).</summary>
    public List<string> Panels { get; } = [];

    /// <summary>이 리그의 활성 SQL 탭의 바인드 변수 목록(탭마다 하나). 열린 탭이 없으면 도구 창용 기본 목록.</summary>
    public BindVariableStore Binds => BindVariableStore.ForTab(Host.Active, Bus);

    public ConnectionProfile Profile { get; }

    public FakeSession Session { get; }

    public FakeExecution Exec { get; } = new();

    public FakeDialogs Dialogs { get; } = new();

    public ExecutionState State { get; }

    public ExecutionLog Log { get; }

    public SqlEditorHost Host { get; }

    public ExecutionController Controller { get; }

    public static ILanguageService LanguageForTests() => Services.Value.GetRequiredService<ILanguageService>();

    public static ExecutionResult Query(SqlStatement s, params string[] values) =>
        new(s, ExecutionStatus.Succeeded) { ResultSet = new OneColumnResult(values.Length == 0 ? ["1"] : values), Elapsed = TimeSpan.FromMilliseconds(12) };

    public void Dispose() => Controller.Dispose();
}

[Collection("GridEditStatics")]
public sealed class ExecutionTests
{
    private static readonly string[] StatusIds = ["oranda.status.session", "oranda.status.transaction", "oranda.status.lastRun", "oranda.status.protection"];

    // ---------- 등록 ----------

    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new OutputViewModel());
        Assert.NotNull(new MessagesViewModel());
    }

    [Fact]
    public void All_commands_are_registered_with_commands_md_enablement()
    {
        var registry = ShellTestHelpers.CreateCommandRegistry();
        foreach (var id in ExecutionCommands.Ids)
        {
            Assert.NotNull(registry.Find(id));
        }

        Assert.Equal(23, ExecutionCommands.Ids.Length);
        Assert.Equal("(hasSession || dedicatedSession || tabConnectionMissing) && !sessionBusy", registry.Find("oranda.exec.runStatement")!.Enablement);
        Assert.Equal("(hasSession || dedicatedSession || tabConnectionMissing) && !sessionBusy", registry.Find("oranda.exec.runSubquery")!.Enablement);
        Assert.Equal("sqlDocFocus", registry.Find("oranda.exec.runSubquery")!.When);
        Assert.Contains("context:editor/runSubquery@1005", registry.Find("oranda.exec.runSubquery")!.Placements);
        Assert.Equal("(hasSession || dedicatedSession || tabConnectionMissing) && !sessionBusy && hasSelection", registry.Find("oranda.exec.runSelection")!.Enablement);
        Assert.Equal("sessionBusy", registry.Find("oranda.exec.cancel")!.Enablement);
        Assert.Equal("hasExecError", registry.Find("oranda.exec.goToError")!.Enablement);
        Assert.Equal("(hasSession || dedicatedSession) && txnPending && !sessionBusy", registry.Find("oranda.txn.commit")!.Enablement);
        Assert.Equal("(hasSession || dedicatedSession) && txnPending && !sessionBusy", registry.Find("oranda.txn.rollback")!.Enablement);
        Assert.Equal("(hasSession || dedicatedSession) && !readOnlySession", registry.Find("oranda.txn.toggleAutoCommit")!.Enablement);
        Assert.Equal("sqlDocFocus", registry.Find("oranda.exec.runScript")!.When);
        Assert.Null(registry.Find("oranda.view.output")!.Enablement);
    }

    [Fact]
    public void Status_items_and_panels_are_registered_in_ui_shell_order()
    {
        var items = ShellTestHelpers.AppContributions.StatusBarItems.ToDictionary(i => i.Id);
        Assert.Equal(2, items["oranda.status.session"].Order);
        Assert.Equal(3, items["oranda.status.transaction"].Order);
        Assert.Equal(4, items["oranda.status.lastRun"].Order);
        Assert.Equal(6, items["oranda.status.protection"].Order);
        Assert.Equal(0, items["oranda.status.session"].CollapseRank);
        Assert.Equal(0, items["oranda.status.transaction"].CollapseRank);
        Assert.Equal(0, items["oranda.status.protection"].CollapseRank);
        Assert.NotNull(ShellTestHelpers.RequirePanel("oranda.panel.output"));
        Assert.NotNull(ShellTestHelpers.RequirePanel("oranda.panel.messages"));
    }

    [Fact]
    public void Startup_pieces_do_not_load_oracle_or_language_assemblies()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-exec-" + Guid.NewGuid().ToString("N"));
        using var container = AppComposition.Create(new AppCompositionOptions { DataDirectory = dir });
        foreach (var def in ShellTestHelpers.AppContributions.StatusBarItems.Where(i => StatusIds.Contains(i.Id)))
        {
            Assert.NotNull(container.Create(def.ItemType));
        }

        container.Create(typeof(OutputViewModel));
        container.Create(typeof(MessagesViewModel));
        container.Create(typeof(ExecutionCommandHandler));
        ExecutionCommands.Resolve(container);

        Assert.False(container.DeferredScanned, "시작 경로에서 지연 어셈블리를 깨웠다: " + container.DeferredScanTrigger);
    }

    [Fact]
    public void Status_item_construction_wires_grid_edit_rerun_without_building_the_controller()
    {
        var oldRerun = GridEditCommands.Rerun;
        var oldPreview = GridEditCommands.ConfirmPreview;
        try
        {
            GridEditCommands.Rerun = null;
            GridEditCommands.ConfirmPreview = null;
            var dir = Path.Combine(Path.GetTempPath(), "oranda-exec-" + Guid.NewGuid().ToString("N"));
            using var container = AppComposition.Create(new AppCompositionOptions { DataDirectory = dir });
            container.Create(typeof(SessionStatusViewModel));

            Assert.NotNull(GridEditCommands.Rerun);
            Assert.NotNull(GridEditCommands.ConfirmPreview);
            Assert.False(container.DeferredScanned);
        }
        finally
        {
            GridEditCommands.Rerun = oldRerun;
            GridEditCommands.ConfirmPreview = oldPreview;
        }
    }

    [Fact]
    public void Engine_services_are_registered_lazily_and_the_interaction_is_the_app_implementation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-exec-" + Guid.NewGuid().ToString("N"));
        using var container = AppComposition.Create(new AppCompositionOptions { DataDirectory = dir });
        var state = ExecutionState.For(container.GetService<IEventBus>());

        ExecutionCommands.EnsureEngineServices(container, state);
        ExecutionCommands.EnsureEngineServices(container, state);

        Assert.IsType<ExecutionPolicy>(container.GetRequiredService<IExecutionPolicy>());
        Assert.IsType<AppExecutionInteraction>(container.GetRequiredService<IExecutionInteraction>());
        Assert.NotNull(container.GetRequiredService<IExecutionService>());
    }

    // ---------- 상태·상태 표시줄 ----------

    [Fact]
    public Task State_follows_session_transaction_and_statement_events() => Ui.Run(() =>
    {
        var bus = new EventBus();
        var keys = new ContextKeyService();
        var session = new SessionStatusViewModel(bus, keys);
        var txn = new TransactionStatusViewModel(bus, keys);
        var last = new LastRunStatusViewModel(bus, keys);
        var protect = new ProtectionStatusViewModel(bus, keys);
        Assert.False(session.IsVisible);

        var profile = new ConnectionProfile("ro", "scott", "h:1521/s", ReadOnly: true, Environment: ConnectionEnvironment.Production);
        var fake = new FakeSession(profile);
        bus.Publish(new SessionOpened(fake.Id, profile, fake.Server, SessionPurpose.Work));

        Assert.True(session.IsVisible);
        Assert.Equal("연결됨", session.Text);
        Assert.Equal("읽기 전용", protect.Text);
        Assert.True(keys.Get("readOnlySession"));

        bus.Publish(new TransactionStateChanged(fake.Id, true));
        Assert.Equal("미커밋", txn.Text);
        Assert.Equal("OrandaUncommittedBrush", txn.DotBrushKey);
        Assert.True(keys.Get("txnPending"));

        bus.Publish(new StatementExecuted(fake.Id, new SqlStatement("update t set a=1", default, StatementKind.Dml),
            new ExecutionResult(new SqlStatement("update t set a=1", default, StatementKind.Dml), ExecutionStatus.Succeeded) { RowsAffected = 1234, Elapsed = TimeSpan.FromMilliseconds(42) },
            new ExecutionOptions()));
        Assert.Equal("1,234행 · 0.042초", last.Text);

        bus.Publish(new StatementExecuted(fake.Id, new SqlStatement("select * from nope", default, StatementKind.Query),
            new ExecutionResult(new SqlStatement("select * from nope", default, StatementKind.Query), ExecutionStatus.Failed) { Error = new DbError("ORA-00942", "x") { Number = 942 }, Elapsed = TimeSpan.FromSeconds(1) },
            new ExecutionOptions()));
        Assert.Equal("ORA-00942 · 1초", last.Text);

        // 스키마 브라우저 같은 내부 질의는 「마지막 실행」을 바꾸지 않는다.
        bus.Publish(new StatementExecuted(fake.Id, new SqlStatement("select 1 from dual", default, StatementKind.Query),
            new ExecutionResult(new SqlStatement("select 1 from dual", default, StatementKind.Query), ExecutionStatus.Succeeded) { RowsAffected = 1 },
            new ExecutionOptions { Source = HistorySource.SchemaBrowser }));
        Assert.Equal("ORA-00942 · 1초", last.Text);

        bus.Publish(new SessionBroken(fake.Id, true, null));
        Assert.Equal("끊김", session.Text);
        Assert.Equal("OrandaEnvProductionBrush", session.DotBrushKey);

        bus.Publish(new SessionClosed(fake.Id));
        Assert.False(session.IsVisible);
        Assert.False(protect.IsVisible);
        Assert.False(txn.IsVisible);
        Assert.False(keys.Get("txnPending"));
    });

    [Fact]
    public Task Transaction_text_shows_auto_commit_when_enabled_and_nothing_pending() => Ui.Run(() =>
    {
        var bus = new EventBus();
        var state = ExecutionState.For(bus);
        var fake = new FakeSession(new ConnectionProfile("p", "u", "h"));
        bus.Publish(new SessionOpened(fake.Id, fake.Profile, fake.Server, SessionPurpose.Work));

        Assert.Equal(string.Empty, state.TransactionText);
        state.AutoCommit = true;
        Assert.Equal("자동 커밋", state.TransactionText);
        bus.Publish(new TransactionStateChanged(fake.Id, true));
        Assert.Equal("미커밋", state.TransactionText);
    });

    [Fact]
    public Task Running_text_counts_elapsed_and_cancelling_replaces_it() => Ui.Run(() =>
    {
        var bus = new EventBus();
        var state = ExecutionState.For(bus);
        var fake = new FakeSession(new ConnectionProfile("p", "u", "h"));
        bus.Publish(new SessionOpened(fake.Id, fake.Profile, fake.Server, SessionPurpose.Work));

        state.BeginRun();
        Assert.StartsWith("실행 중 00:0", state.SessionText, StringComparison.Ordinal);
        Assert.True(state.Busy);
        state.MarkCancelling();
        Assert.Equal("취소 중", state.SessionText);
        Assert.True(state.Busy);
        state.EndRun();
        Assert.Equal("연결됨", state.SessionText);
        Assert.False(state.Busy);
        Assert.Equal("01:05", ExecutionState.FormatElapsed(TimeSpan.FromSeconds(65)));
        Assert.Equal("1:01:05", ExecutionState.FormatElapsed(TimeSpan.FromSeconds(3665)));
    });

    // ---------- DBMS_OUTPUT · 메시지 ----------

    [Fact]
    public Task Dbms_output_events_fill_the_output_panel_until_collection_is_turned_off() => Ui.Run(() =>
    {
        var bus = new EventBus();
        var vm = new OutputViewModel(bus);
        var state = ExecutionState.For(bus);

        bus.Publish(new DbmsOutputReceived(Guid.NewGuid(), ["hello", "world"]));
        Assert.Equal("hello\nworld\n", vm.Text);

        state.DbmsOutputEnabled = false;
        bus.Publish(new DbmsOutputReceived(Guid.NewGuid(), ["ignored"]));
        Assert.Equal("hello\nworld\n", vm.Text);
        Assert.Contains("꺼짐", vm.Header, StringComparison.Ordinal);

        state.DbmsOutputEnabled = true;
        vm.ClearCommand.Execute(null);
        Assert.Equal(string.Empty, vm.Text);
    });

    [Fact]
    public void Output_keeps_only_the_newest_text_beyond_the_limit()
    {
        var log = new ExecutionLog();
        var line = new string('x', 999);
        log.AppendOutput(Enumerable.Repeat(line, (ExecutionLog.MaxOutputChars / 1000) + 500));

        Assert.True(log.OutputLength <= ExecutionLog.MaxOutputChars);
    }

    // ---------- 대화상자 모델 ----------

    [Fact]
    public void Confirm_model_requires_the_target_name_for_dangerous_commands_case_insensitively()
    {
        var prod = new ConnectionProfile("PROD-HR", "hr", "db:1521/ORCL", Environment: ConnectionEnvironment.Production);
        var stmt = new SqlStatement("DROP TABLE emp", default, StatementKind.Ddl) { Verb = "DROP", DangerousTarget = "emp" };
        var decision = new PolicyDecision(PolicyDecisionKind.Confirm, PolicyRule.DangerousCommand, "위험") { RetypeText = "emp" };
        var model = new ConfirmDialogModel(new ConfirmRequest(stmt, decision, prod));

        Assert.True(model.IsProduction);
        Assert.Equal("emp", model.RetypeText);
        Assert.Equal("PROD-HR", model.HeaderTitle);
        Assert.Equal("hr@db:1521/ORCL", model.HeaderDetail);
        Assert.Equal("운영", model.EnvironmentText);
        Assert.False(model.CanAccept(string.Empty));
        Assert.False(model.CanAccept("emp2"));
        Assert.True(model.CanAccept(" EMP "));
    }

    [Fact]
    public void Confirm_model_without_retype_accepts_immediately()
    {
        var stmt = new SqlStatement("UPDATE t SET a = 1", default, StatementKind.Dml) { Verb = "UPDATE" };
        var model = new ConfirmDialogModel(new ConfirmRequest(stmt, new PolicyDecision(PolicyDecisionKind.Confirm, PolicyRule.ProductionChange, "운영"), null));

        Assert.Null(model.RetypeText);
        Assert.True(model.CanAccept(string.Empty));
        Assert.False(model.IsProduction);
    }

    [Theory]
    [InlineData(BindDataType.Number, "12.5", typeof(decimal))]
    [InlineData(BindDataType.Varchar2, "abc", typeof(string))]
    [InlineData(BindDataType.Date, "2026-10-19", typeof(DateTime))]
    [InlineData(BindDataType.Timestamp, "2026-10-19 12:30:15.250", typeof(DateTime))]
    [InlineData(BindDataType.TimestampTz, "2026-10-19T12:30:15+09:00", typeof(DateTimeOffset))]
    [InlineData(BindDataType.Raw, "0A FF", typeof(byte[]))]
    [InlineData(BindDataType.Boolean, "true", typeof(bool))]
    [InlineData(BindDataType.BinaryDouble, "1.5", typeof(double))]
    public void Bind_input_converts_text_by_type(BindDataType type, string text, Type expected)
    {
        var bind = new BindInput("a") { Type = type, Text = text }.ToBindValue(out var error);

        Assert.Null(error);
        Assert.NotNull(bind);
        Assert.IsType(expected, bind.Value);
        Assert.Equal(type, bind.Type);
    }

    [Fact]
    public void Bind_input_reports_bad_values_and_supports_null_and_out()
    {
        Assert.Null(new BindInput("n") { Type = BindDataType.Number, Text = "abc" }.ToBindValue(out var error));
        Assert.Contains(":n", error, StringComparison.Ordinal);

        var isNull = new BindInput("n") { Type = BindDataType.Number, Text = "5", IsNull = true }.ToBindValue(out _);
        Assert.Null(isNull!.Value);

        var outBind = new BindInput("o") { Type = BindDataType.Varchar2, Direction = BindDirection.Out }.ToBindValue(out _);
        Assert.Equal(BindDirection.Out, outBind!.Direction);
        Assert.Equal(4000, outBind.Size);

        var empty = new BindInput("v") { Type = BindDataType.Varchar2, Text = string.Empty }.ToBindValue(out _);
        Assert.Equal(string.Empty, empty!.Value);
    }

    // ---------- IExecutionInteraction ----------

    [Fact]
    public async Task Interaction_asks_the_dialog_with_the_current_connection_and_the_policy_decision()
    {
        var dialogs = new FakeDialogs { ConfirmAnswer = false };
        var prod = new ConnectionProfile("PROD", "scott", "db:1521/S", Environment: ConnectionEnvironment.Production);
        var interaction = new AppExecutionInteraction { Dialogs = dialogs, ProfileProvider = () => prod };
        var stmt = new SqlStatement("UPDATE emp SET sal = 0 WHERE id = 1", default, StatementKind.Dml) { Verb = "UPDATE" };
        var server = new FakeSession(prod).Server;
        var decision = await new ExecutionPolicy().EvaluateAsync(new PolicyContext(prod, server, HistorySource.Editor), stmt, default);

        Assert.Equal(PolicyDecisionKind.Confirm, decision.Kind);
        Assert.Equal(PolicyRule.ProductionChange, decision.Rule);
        Assert.False(await interaction.ConfirmAsync(stmt, decision, default));

        var request = Assert.Single(dialogs.Confirms);
        Assert.Equal(ConnectionEnvironment.Production, request.Profile!.Environment);
        Assert.Same(stmt, request.Statement);
        Assert.Equal(PolicyRule.ProductionChange, request.Decision.Rule);
    }

    [Fact]
    public async Task Interaction_remembers_substitution_values_in_memory_only()
    {
        var dialogs = new FakeDialogs { SubstitutionAnswer = new Dictionary<string, string> { ["x"] = "SMITH" } };
        var interaction = new AppExecutionInteraction { Dialogs = dialogs };

        var first = await interaction.AskSubstitutionsAsync(["x"], default);
        await interaction.AskSubstitutionsAsync(["x"], default);

        Assert.Equal("SMITH", first!["x"]);
        Assert.Empty(dialogs.SubstitutionLasts[0]);
        Assert.Equal("SMITH", dialogs.SubstitutionLasts[1]["x"]);

        dialogs.SubstitutionAnswer = null;
        Assert.Null(await interaction.AskSubstitutionsAsync(["x"], default));
    }

    [Fact]
    public async Task Interaction_forwards_script_error_choice()
    {
        var dialogs = new FakeDialogs { ScriptErrorAnswer = ScriptErrorAction.ContinueAll };
        var interaction = new AppExecutionInteraction { Dialogs = dialogs };

        var answer = await interaction.AskScriptErrorAsync(new SqlStatement("x", default, StatementKind.Other), new DbError("ORA-1", "m"), default);

        Assert.Equal(ScriptErrorAction.ContinueAll, answer);
    }

    // ---------- 컨트롤러: 실행 범위 ----------

    [Fact]
    public Task Statement_run_uses_the_caret_statement_and_shows_a_grid_tab() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select 1 from dual;\nselect 2 from dual;");
        tab.Select(25, 0);
        rig.Exec.Handler = (s, _) => Task.FromResult(Rig.Query(s));

        Assert.True(await rig.Controller.RunAsync(RunMode.Statement));

        Assert.Equal("select 2 from dual", Assert.Single(rig.Exec.Statements).Text);
        var results = Assert.IsType<ResultTabsViewModel>(tab.ResultPane);
        var item = Assert.Single(results.Tabs);
        Assert.NotNull(item.Pane?.Model);
        Assert.Same(item, results.Selected);
        Assert.Equal(SessionRunState.Idle, rig.State.RunState);
        Assert.False(rig.State.Busy);
        Assert.Contains(rig.Log.Messages, m => m.Text.Contains("select 2 from dual", StringComparison.Ordinal));
    });

    [Fact]
    public Task Statement_run_with_a_selection_runs_only_the_selection() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select 1 from dual;\nselect 2 from dual;");
        tab.Select(20, 18);

        await rig.Controller.RunAsync(RunMode.Statement);

        var ran = Assert.Single(rig.Exec.Statements);
        Assert.Equal("select 2 from dual", ran.Text);
        Assert.Equal(20, ran.Range.Start); // 문서 안 위치로 되돌려 둔다(오류 문장으로 이동에 쓴다)
    });

    [Fact]
    public Task Selection_with_several_statements_runs_them_as_a_script() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select 1 from dual;\nselect 2 from dual;\nselect 3 from dual;");
        tab.Select(0, 40);

        await rig.Controller.RunAsync(RunMode.Selection);

        Assert.Equal(2, rig.Exec.Statements.Count);
        Assert.NotNull(rig.Exec.LastMode);
    });

    [Fact]
    public Task Script_run_executes_every_statement_and_adds_a_summary_tab() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select 1 from dual;\ncreate table t (a number);\nselect 2 from dual;");
        rig.Exec.Handler = (s, _) => Task.FromResult(s.Text.StartsWith("select", StringComparison.Ordinal) ? Rig.Query(s) : new ExecutionResult(s, ExecutionStatus.Succeeded));

        Assert.True(await rig.Controller.RunAsync(RunMode.Script));

        Assert.Equal(3, rig.Exec.Statements.Count);
        var results = Assert.IsType<ResultTabsViewModel>(tab.ResultPane);
        Assert.Equal(["결과 1", "결과 2", "스크립트"], results.Tabs.Select(t => t.Title).ToArray());
        Assert.Contains("3개 중 3개 실행, 성공 3, 실패 0", results.Tabs[^1].Message, StringComparison.Ordinal);
        Assert.Same(results.Tabs[0], results.Selected);
        Assert.Equal(ScriptErrorMode.Ask, rig.Exec.LastMode);
    });

    [Fact]
    public Task Script_run_passes_the_selected_error_mode_to_the_engine() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        rig.Host.NewDocument("select 1 from dual;\nselect 2 from dual;");
        rig.State.ErrorMode = ScriptErrorMode.Continue;

        await rig.Controller.RunAsync(RunMode.Script);

        Assert.Equal(ScriptErrorMode.Continue, rig.Exec.LastMode);
    });

    [Fact]
    public Task Script_stops_at_a_failure_in_stop_mode_and_reports_it() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select 1 from dual;\nselect * from nope;\nselect 3 from dual;");
        rig.State.ErrorMode = ScriptErrorMode.Stop;
        rig.Exec.Handler = (s, _) => Task.FromResult(s.Text.Contains("nope", StringComparison.Ordinal)
            ? new ExecutionResult(s, ExecutionStatus.Failed) { Error = new DbError("ORA-00942", "table or view does not exist") { Number = 942, Offset = 14 } }
            : Rig.Query(s));

        await rig.Controller.RunAsync(RunMode.Script);

        Assert.Equal(2, rig.Exec.Statements.Count);
        Assert.True(rig.State.HasExecError);
        var results = (ResultTabsViewModel)tab.ResultPane!;
        Assert.Contains(results.Tabs, t => t.IsError && t.Message.Contains("ORA-00942", StringComparison.Ordinal));
        Assert.Contains("실패 1", results.Tabs[^1].Message, StringComparison.Ordinal);
    });

    [Fact]
    public Task Run_from_cursor_runs_the_cursor_statement_and_everything_after_it() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select 1 from dual;\nselect 2 from dual;\nselect 3 from dual;");
        tab.Select(25, 0);

        await rig.Controller.RunAsync(RunMode.FromCursor);

        Assert.Equal(["select 2 from dual", "select 3 from dual"], rig.Exec.Statements.Select(s => s.Text).ToArray());
    });

    [Fact]
    public Task Step_run_executes_one_statement_and_moves_the_caret_to_the_next() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select 1 from dual;\nselect 2 from dual;");
        tab.Select(3, 0);

        await rig.Controller.RunAsync(RunMode.Step);

        Assert.Equal("select 1 from dual", Assert.Single(rig.Exec.Statements).Text);
        Assert.Equal(20, tab.CaretOffset);
    });

    // ---------- 컨트롤러: 실행 범위 강조 ----------

    [Fact]
    public Task Statement_run_flashes_the_statement_range_in_the_editor() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select 1 from dual;\nselect 2 from dual;");
        var flashes = new List<Oranda.Core.Text.TextRange>();
        tab.FlashRequested += (_, r) => flashes.Add(r);
        tab.Select(25, 0);

        await rig.Controller.RunAsync(RunMode.Statement);

        Assert.Equal(new Oranda.Core.Text.TextRange(20, 18), Assert.Single(flashes));
    });

    [Fact]
    public Task Script_run_flashes_from_the_first_to_the_last_statement() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        const string Text = "-- head\nselect 1 from dual;\nselect 2 from dual;\n";
        var tab = rig.Host.NewDocument(Text);
        var flashes = new List<Oranda.Core.Text.TextRange>();
        tab.FlashRequested += (_, r) => flashes.Add(r);

        await rig.Controller.RunAsync(RunMode.Script);

        int start = Text.IndexOf("select 1", StringComparison.Ordinal);
        int end = Text.IndexOf("select 2", StringComparison.Ordinal) + "select 2 from dual".Length;
        Assert.Equal(new Oranda.Core.Text.TextRange(start, end - start), Assert.Single(flashes));
    });

    [Fact]
    public Task Run_that_does_not_execute_does_not_flash() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select :a from dual");
        var flashes = new List<Oranda.Core.Text.TextRange>();
        tab.FlashRequested += (_, r) => flashes.Add(r);

        Assert.False(await rig.Controller.RunAsync(RunMode.Statement)); // 처음 보는 바인드: 실행 보류

        Assert.Empty(flashes);
    });

    // ---------- 컨트롤러: 커서의 하위 쿼리 ----------

    [Fact]
    public Task Subquery_run_executes_only_the_innermost_query_with_its_document_range() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        const string Text = "select 1 from dual;\nselect * from (select count(*) c from emp where deptno = 10) t;";
        var tab = rig.Host.NewDocument(Text);
        var flashes = new List<Oranda.Core.Text.TextRange>();
        tab.FlashRequested += (_, r) => flashes.Add(r);
        tab.Select(Text.IndexOf("*) c", StringComparison.Ordinal), 0); // count(*) 괄호 안이어도 바깥 하위 쿼리
        rig.Exec.Handler = (s, _) => Task.FromResult(Rig.Query(s));

        Assert.True(await rig.Controller.RunAsync(RunMode.Subquery));

        var ran = Assert.Single(rig.Exec.Statements);
        Assert.Equal("select count(*) c from emp where deptno = 10", ran.Text);
        Assert.Equal(StatementKind.Query, ran.Kind);
        Assert.Equal(new Oranda.Core.Text.TextRange(Text.IndexOf("select count", StringComparison.Ordinal), ran.Text.Length), ran.Range);
        Assert.Equal(ran.Range, Assert.Single(flashes));
        Assert.NotNull(Assert.Single(Assert.IsType<ResultTabsViewModel>(tab.ResultPane).Tabs).Pane?.Model);
    });

    [Fact]
    public Task Subquery_run_outside_a_subquery_runs_the_whole_statement() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        const string Text = "select 1 from dual;\nselect * from (select a from t) x where b in (1, 2);";
        var tab = rig.Host.NewDocument(Text);

        tab.Select(Text.IndexOf("* from", StringComparison.Ordinal), 0);
        await rig.Controller.RunAsync(RunMode.Subquery);
        tab.Select(Text.IndexOf("2)", StringComparison.Ordinal), 0); // IN 목록은 하위 쿼리가 아니다
        await rig.Controller.RunAsync(RunMode.Subquery);

        Assert.All(rig.Exec.Statements, s => Assert.Equal("select * from (select a from t) x where b in (1, 2)", s.Text));
        Assert.Equal(2, rig.Exec.Statements.Count);
    });

    [Fact]
    public Task Subquery_run_resolves_binds_through_the_bind_list() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        const string Text = "select * from (select :x n from dual where :y = 1) t where t.n = :z";
        var tab = rig.Host.NewDocument(Text);
        tab.Select(Text.IndexOf("n from", StringComparison.Ordinal), 0);

        // 하위 쿼리의 바인드만 목록에 더하고 실행을 미룬다(바깥 문장의 :z 는 묻지 않는다).
        Assert.False(await rig.Controller.RunAsync(RunMode.Subquery));
        Assert.Empty(rig.Exec.Statements);
        Assert.Equal(["X", "Y"], rig.Binds.Rows.Select(r => r.Name));

        rig.Binds.Find("x")!.Text = "a";
        rig.Binds.Find("y")!.Type = BindDataType.Number;
        rig.Binds.Find("y")!.Text = "1";
        Assert.True(await rig.Controller.RunAsync(RunMode.Subquery));

        Assert.Equal("select :x n from dual where :y = 1", Assert.Single(rig.Exec.Statements).Text);
        Assert.Equal("a", rig.Exec.LastBinds!["x"].Value);
        Assert.Equal(1m, rig.Exec.LastBinds["y"].Value);
        Assert.False(rig.Exec.LastBinds.ContainsKey("z"));
    });

    [Fact]
    public Task Two_tabs_with_the_same_bind_name_each_run_with_their_own_value() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        const string Text = "select * from emp where id = :ID";
        var a = rig.Host.NewDocument(Text);
        var b = rig.Host.NewDocument(Text);
        a.Binds.Add("ID").Text = "1";
        b.Binds.Add("ID").Text = "2";

        Assert.True(await rig.Controller.RunAsync(RunMode.Statement, tab: a));
        Assert.Equal("1", rig.Exec.LastBinds!["ID"].Value);
        Assert.True(await rig.Controller.RunAsync(RunMode.Statement, tab: b));
        Assert.Equal("2", rig.Exec.LastBinds["ID"].Value);
        Assert.Equal("1", a.Binds.Find("ID")!.Text);
    });

    [Fact]
    public Task New_result_tab_run_keeps_earlier_results_and_a_normal_run_replaces_them() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select 1 from dual");
        rig.Exec.Handler = (s, _) => Task.FromResult(Rig.Query(s));

        await rig.Controller.RunAsync(RunMode.Statement);
        var first = ((ResultTabsViewModel)tab.ResultPane!).Tabs[0];
        await rig.Controller.RunAsync(RunMode.Statement, newResultTab: true);
        var results = (ResultTabsViewModel)tab.ResultPane!;
        Assert.Equal(2, results.Tabs.Count);
        Assert.Same(first, results.Tabs[0]);

        await rig.Controller.RunAsync(RunMode.Statement);
        Assert.Single(results.Tabs);
    });

    [Fact]
    public Task Run_all_tabs_runs_each_tab_as_a_script_in_its_own_result_area() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = rig.Host.NewDocument("select 1 from dual;\nselect 2 from dual;");
        var b = rig.Host.NewDocument("select 3 from dual");

        await rig.Controller.RunAllTabsAsync();

        Assert.Equal(3, rig.Exec.Statements.Count);
        Assert.NotSame(a.ResultPane, b.ResultPane);
        Assert.IsType<ResultTabsViewModel>(a.ResultPane);
        Assert.IsType<ResultTabsViewModel>(b.ResultPane);
    });

    [Fact]
    public Task Run_without_a_session_explains_instead_of_executing() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        rig.Bus.Publish(new SessionClosed(rig.Session.Id));
        var tab = rig.Host.NewDocument("select 1 from dual");
        var controller = new ExecutionController(new ExecutionDeps
        {
            State = rig.State,
            Log = rig.Log,
            Dialogs = rig.Dialogs,
            Session = () => null,
            Execution = () => rig.Exec,
            Language = () => Rig.LanguageForTests(),
            Host = () => rig.Host,
        });

        Assert.False(await controller.RunAsync(RunMode.Statement));

        Assert.Empty(rig.Exec.Statements);
        var results = (ResultTabsViewModel)tab.ResultPane!;
        Assert.True(Assert.Single(results.Tabs).IsError);
        Assert.Contains(rig.Log.Messages, m => m.Kind == MessageKind.Warning);
    });

    // ---------- 컨트롤러: 오류·거부·취소 ----------

    [Fact]
    public Task Failed_statement_shows_the_error_and_go_to_error_selects_the_server_offset() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select 1 from dual;\nselect * from nope");
        tab.Select(25, 0);
        rig.Exec.Handler = (s, _) => Task.FromResult(new ExecutionResult(s, ExecutionStatus.Failed)
        {
            Error = new DbError("ORA-00942", "table or view does not exist") { Number = 942, Offset = 14 },
        });

        Assert.True(await rig.Controller.RunAsync(RunMode.Statement));

        var results = (ResultTabsViewModel)tab.ResultPane!;
        var item = Assert.Single(results.Tabs);
        Assert.True(item.IsError);
        Assert.StartsWith("ORA-00942: table or view does not exist", item.Message, StringComparison.Ordinal);
        Assert.True(rig.State.HasExecError);
        Assert.Contains(rig.Log.Messages, m => m.Kind == MessageKind.Error && m.Text.Contains("ORA-00942", StringComparison.Ordinal));

        tab.Select(0, 0);
        Assert.True(rig.Controller.GoToError());
        Assert.Equal(20 + 14, tab.CaretOffset);

        await rig.Controller.RunAsync(RunMode.Statement); // 다시 실행하면 오류 표시는 새 결과로 갱신된다
        Assert.True(rig.State.HasExecError);
        rig.Exec.Handler = (s, _) => Task.FromResult(Rig.Query(s));
        await rig.Controller.RunAsync(RunMode.Statement);
        Assert.False(rig.State.HasExecError);
    });

    [Fact]
    public Task Denied_statement_shows_the_policy_message() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("delete from t");
        rig.Exec.Handler = (s, _) => Task.FromResult(new ExecutionResult(s, ExecutionStatus.Denied)
        {
            Policy = new PolicyDecision(PolicyDecisionKind.Deny, PolicyRule.ReadOnlyProfile, "읽기 전용 접속입니다."),
        });

        await rig.Controller.RunAsync(RunMode.Statement);

        var item = Assert.Single(((ResultTabsViewModel)tab.ResultPane!).Tabs);
        Assert.Equal("거부됨", item.Title);
        Assert.Equal("읽기 전용 접속입니다.", item.Message);
        Assert.Contains(rig.Log.Messages, m => m.Kind == MessageKind.Warning);
    });

    [Fact]
    public Task Cancel_releases_the_running_state_within_one_second() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        rig.Host.NewDocument("begin dbms_session.sleep(600); end;");
        rig.Exec.Handler = async (s, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }

            return new ExecutionResult(s, ExecutionStatus.Cancelled);
        };

        var run = rig.Controller.RunAsync(RunMode.Statement);
        await HeadlessHost.WaitUntil(() => rig.Exec.Statements.Count == 1 && rig.State.Busy);
        Assert.Equal(SessionRunState.Running, rig.State.RunState);

        var clock = Stopwatch.StartNew();
        rig.Controller.Cancel();
        Assert.Equal(SessionRunState.Cancelling, rig.State.RunState);
        Assert.Equal("취소 중", rig.State.SessionText);
        await run;
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds < 1000, $"취소 후 {clock.ElapsedMilliseconds} ms");
        Assert.False(rig.State.Busy);
        Assert.Equal(SessionRunState.Idle, rig.State.RunState);
    });

    [Fact]
    public Task Cancel_stops_fetch_all_within_one_second() => Ui.Run(async () =>
    {
        // TSK-06-11: 「모두 가져오기」 도 실행 상태로 돌아 취소 명령(sessionBusy)이 켜지고 그것이 끊는다.
        using var rig = new Rig();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetch = rig.Controller.FetchAsync(async ct =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct); // 끝없는 가져오기: 취소 토큰만 끝낸다
        });
        await started.Task;
        Assert.True(rig.State.Busy);

        var clock = Stopwatch.StartNew();
        rig.Controller.Cancel();
        Assert.Equal(SessionRunState.Cancelling, rig.State.RunState);
        await fetch;
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds < 1000, $"취소 후 {clock.ElapsedMilliseconds} ms");
        Assert.False(rig.State.Busy);
        Assert.Contains(rig.Log.Messages, m => m.Text == "취소되었습니다.");
    });

    [Fact]
    public async Task Fetch_all_command_runs_through_the_execution_hook()
    {
        var previous = (ResultGridCommands.RunFetch, ResultGridCommands.Active);
        await using var model = new ResultGridModel(new OneColumnResult("a", "b"));
        var fetches = new List<Func<CancellationToken, Task>>();
        try
        {
            ResultGridCommands.Active = model;
            ResultGridCommands.RunFetch = async (fetch, ct) =>
            {
                fetches.Add(fetch);
                await fetch(ct);
            };

            await new ResultGridCommandHandler().ExecuteAsync(new CommandContext("oranda.grid.fetchAll", FakeContextKeys.AllTrue), CancellationToken.None);

            // 활성 그리드의 「모두 가져오기」 를 실행 상태(취소 명령이 끊는 곳)로 넘긴다.
            Assert.Same(model, Assert.Single(fetches).Target);
        }
        finally
        {
            (ResultGridCommands.RunFetch, ResultGridCommands.Active) = previous;
        }
    }

    [Fact]
    public Task A_second_run_is_ignored_while_busy() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        rig.Host.NewDocument("select 1 from dual");
        var gate = new TaskCompletionSource<ExecutionResult>();
        rig.Exec.Handler = (s, _) => gate.Task;

        var first = rig.Controller.RunAsync(RunMode.Statement);
        await HeadlessHost.WaitUntil(() => rig.State.Busy && rig.Exec.Statements.Count == 1);
        Assert.False(await rig.Controller.RunAsync(RunMode.Statement));
        gate.SetResult(new ExecutionResult(rig.Exec.Statements[0], ExecutionStatus.Succeeded));
        await first;

        Assert.Single(rig.Exec.Statements);
    });

    [Fact]
    public Task Engine_exception_does_not_leave_the_session_busy() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        rig.Host.NewDocument("select 1 from dual");
        rig.Exec.Handler = (_, _) => throw new InvalidOperationException("연결 끊김");

        await rig.Controller.RunAsync(RunMode.Statement);

        Assert.False(rig.State.Busy);
        Assert.Contains(rig.Log.Messages, m => m.Kind == MessageKind.Error && m.Text.Contains("연결 끊김", StringComparison.Ordinal));
    });

    // ---------- 컨트롤러: 바인드 ----------

    [Fact]
    public Task New_bind_names_fill_the_list_and_stop_then_list_values_reach_the_engine() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select :a, :b from dual");

        // 처음 보는 이름: 목록에 줄을 더하고 실행하지 않는다(값 없이 NULL 로 돌지 않게). 바인드 변수 창을 보인다.
        Assert.False(await rig.Controller.RunAsync(RunMode.Statement));
        Assert.Empty(rig.Exec.Statements);
        Assert.Equal(["A", "B"], rig.Binds.Rows.Select(r => r.Name));
        Assert.Contains("show " + ExecutionController.BindsPanelId, rig.Panels);
        Assert.Equal("a", rig.Binds.PendingFocus);
        Assert.Contains(rig.Log.Messages, m => m.Kind == MessageKind.Warning && m.Text.Contains(":a, :b", StringComparison.Ordinal));
        var message = Assert.IsType<ResultTabsViewModel>(tab.ResultPane).Tabs.Single();
        Assert.Equal("바인드 변수", message.Title);

        rig.Binds.Find("a")!.Type = BindDataType.Number;
        rig.Binds.Find("a")!.Text = "42";
        rig.Binds.Find("b")!.Text = "hi";
        Assert.True(await rig.Controller.RunAsync(RunMode.Statement));
        Assert.Equal(42m, rig.Exec.LastBinds!["a"].Value);
        Assert.Equal("hi", rig.Exec.LastBinds["b"].Value);

        // 다음 실행도 묻지 않고 같은 값. 빈 값은 NULL.
        rig.Binds.Find("b")!.Text = string.Empty;
        Assert.True(await rig.Controller.RunAsync(RunMode.Statement));
        Assert.Equal(BindDataType.Number, rig.Exec.LastBinds["a"].Type);
        Assert.Null(rig.Exec.LastBinds["b"].Value);
    });

    [Fact]
    public Task Invalid_bind_value_stops_with_a_message_and_shows_the_list() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        rig.Host.NewDocument("select :n from dual");
        var row = rig.Binds.Add("n");
        row.Type = BindDataType.Number;
        row.Text = "abc";

        Assert.False(await rig.Controller.RunAsync(RunMode.Statement));

        Assert.Empty(rig.Exec.Statements);
        Assert.False(rig.State.Busy);
        Assert.Contains(rig.Log.Messages, m => m.Kind == MessageKind.Error && m.Text.Contains(":N", StringComparison.Ordinal));
        Assert.Equal("show " + ExecutionController.BindsPanelId, rig.Panels[^1]);
    });

    [Fact]
    public Task Bind_variables_command_fills_the_list_without_running() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        rig.Host.NewDocument("select :a from dual where x = :A");

        Assert.True(await rig.Controller.EditBindsAsync());

        Assert.Empty(rig.Exec.Statements);
        Assert.Equal("A", Assert.Single(rig.Binds.Rows).Name);
        Assert.Contains("show " + ExecutionController.BindsPanelId, rig.Panels);
    });

    [Fact]
    public Task Script_run_takes_bind_values_from_the_list() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        rig.Host.NewDocument("select :a from dual;\nselect 1 from dual;");
        rig.Binds.Add("a").Text = "x";

        Assert.True(await rig.Controller.RunAsync(RunMode.Script));

        Assert.Equal("x", rig.Exec.LastOptions!.Binds!["a"].Value);
    });

    [Fact]
    public Task Paste_bind_values_replaces_bind_tokens_only_and_undoes_in_one_step() => Ui.Run(() =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select ':a' s, :a, :b, :c, :d from t -- :a\nwhere x = :a");
        rig.Binds.Add("a").Text = "O'Hara";
        var b = rig.Binds.Add("b");
        b.Type = BindDataType.Number;
        b.Text = "12.5";
        var c = rig.Binds.Add("c");
        c.Type = BindDataType.Date;
        c.Text = "2026-10-20";
        rig.Binds.Add("d"); // 빈 값 = NULL

        Assert.Equal(5, rig.Controller.PasteBindValues(tab));

        Assert.Equal(
            "select ':a' s, 'O''Hara', 12.5, TO_DATE('2026-10-20 00:00:00', 'YYYY-MM-DD HH24:MI:SS'), NULL from t -- :a\nwhere x = 'O''Hara'",
            tab.Text);
        tab.Document.UndoStack.Undo();
        Assert.Equal("select ':a' s, :a, :b, :c, :d from t -- :a\nwhere x = :a", tab.Text);
        return Task.CompletedTask;
    });

    [Fact]
    public void Bind_store_adds_only_new_names_and_builds_values_case_insensitively()
    {
        var store = BindVariableStore.For(new EventBus());
        Assert.Equal(["ID", "name"], store.Ensure(["ID", "name", "id"]));
        Assert.Empty(store.Ensure(["Id", "NAME"]));
        Assert.Equal([1, 2], store.Rows.Select(r => r.Number));

        store.Find("id")!.Type = BindDataType.Number;
        store.Find("id")!.Text = "7";
        Assert.True(store.TryBuild(["ID", "name"], out var values, out _));
        Assert.Equal(7m, values["id"].Value);
        Assert.Null(values["NAME"].Value); // 빈 값 = NULL

        Assert.False(store.TryBuild(["missing"], out _, out var error));
        Assert.Contains(":missing", error, StringComparison.Ordinal);
        store.Find("id")!.Text = "x";
        Assert.False(store.TryBuild(["id"], out _, out error));
        Assert.Contains(":ID", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BindDataType.NVarchar2, "가'나", "N'가''나'")]
    [InlineData(BindDataType.FixedChar, "Y", "'Y'")]
    [InlineData(BindDataType.BinaryDouble, "1.5", "1.5")]
    [InlineData(BindDataType.Timestamp, "2026-10-20 10:11:12", "TO_TIMESTAMP('2026-10-20 10:11:12.0000000', 'YYYY-MM-DD HH24:MI:SS.FF')")]
    [InlineData(BindDataType.Raw, "0a ff", "HEXTORAW('0AFF')")]
    [InlineData(BindDataType.Boolean, "y", "TRUE")]
    [InlineData(BindDataType.Number, "", "NULL")]
    [InlineData(BindDataType.Number, "abc", null)]
    public void Bind_literals_follow_the_type(BindDataType type, string text, string? expected)
    {
        var row = new BindVariableRow("v") { Type = type, Text = text };
        Assert.Equal(expected, BindVariableStore.ToLiteral(row));
    }

    [Fact]
    public Task Bind_panel_focuses_the_value_of_a_requested_variable_and_the_name_of_a_new_row() => Ui.Run(() =>
    {
        var bus = new EventBus();
        var vm = new BindVariablesViewModel(bus);
        vm.Store.Ensure(["A", "ID"]);
        var view = new BindVariablesView { DataContext = vm };
        var window = new Avalonia.Controls.Window { Width = 500, Height = 300, Content = view };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        vm.Store.RequestFocus("id");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var focused = Assert.IsType<Avalonia.Controls.TextBox>(window.FocusManager!.GetFocusedElement());
        Assert.Equal("ValueBox", focused.Name);
        Assert.Same(vm.Store.Find("ID"), vm.Selected);

        vm.AddCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, vm.Rows.Count);
        Assert.Equal("NameBox", Assert.IsType<Avalonia.Controls.TextBox>(window.FocusManager!.GetFocusedElement()).Name);

        vm.DeleteCommand.Execute(null);
        Assert.Equal(2, vm.Rows.Count);
        window.Close();
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(390)]
    [InlineData(800)]
    [InlineData(200)] // 최소 폭보다 좁으면 가로 스크롤(열은 그래도 맞다)
    public Task Bind_panel_columns_line_up_across_rows_whatever_the_name_length(double width) => Ui.Run(() =>
    {
        var vm = new BindVariablesViewModel(new EventBus());
        vm.Store.Ensure(["PROC_CD", "PDN_PST_DD_FR", "TRST_PROC_CMP_CD", "OP_GRD"]);
        var view = new BindVariablesView { DataContext = vm };
        var window = new Avalonia.Controls.Window { Width = width, Height = 400, Content = view };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var list = view.FindControl<Avalonia.Controls.ListBox>("RowList")!;
        var valueLefts = vm.Rows.Select(r =>
        {
            var box = ((Avalonia.Controls.Control)list.ContainerFromItem(r)!).GetVisualDescendants()
                .OfType<Avalonia.Controls.TextBox>().Single(t => t.Name == "ValueBox");
            return Math.Round(Avalonia.VisualExtensions.TranslatePoint(box, default, view)!.Value.X, 1);
        }).Distinct().ToList();
        Assert.Single(valueLefts);
        // 머리글 「값」 도 같은 자리에서 시작한다.
        var head = view.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Single(t => t.Text == "값");
        Assert.Equal(valueLefts[0], Math.Round(Avalonia.VisualExtensions.TranslatePoint(head, default, view)!.Value.X - head.Margin.Left, 1));
        window.Close();
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(240)]
    [InlineData(280)] // 오른쪽 열 기본 폭(창의 0.23) 근처
    [InlineData(390)]
    public Task Bind_panel_has_no_horizontal_scroll_at_default_right_pane_widths(double width) => Ui.Run(() =>
    {
        var vm = new BindVariablesViewModel(new EventBus());
        vm.Store.Ensure(["PROC_CD", "OP_GRD"]);
        var view = new BindVariablesView { DataContext = vm };
        var window = new Avalonia.Controls.Window { Width = width, Height = 400, Content = view };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var scroll = view.FindControl<Avalonia.Controls.ScrollViewer>("TableScroll")!;
        Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 0.5, $"extent {scroll.Extent.Width} > viewport {scroll.Viewport.Width}");
        window.Close();
        return Task.CompletedTask;
    });

    // ---------- 트랜잭션 ----------

    [Fact]
    public Task Auto_commit_commits_after_a_successful_change_only_when_enabled() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        rig.Host.NewDocument("update t set a = 1 where id = 1");
        rig.Exec.Handler = (s, _) => Task.FromResult(new ExecutionResult(s, ExecutionStatus.Succeeded) { RowsAffected = 1, TransactionPending = true });

        await rig.Controller.RunAsync(RunMode.Statement);
        Assert.Equal(0, rig.Session.Commits);

        rig.Controller.ToggleAutoCommit();
        Assert.True(rig.State.AutoCommit);
        await rig.Controller.RunAsync(RunMode.Statement);
        Assert.Equal(1, rig.Session.Commits);
    });

    [Fact]
    public Task Commit_and_rollback_clear_the_pending_state() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        rig.Session.HasPendingTransaction = true;
        rig.Bus.Publish(new TransactionStateChanged(rig.Session.Id, true));
        Assert.True(rig.State.TxnPending);

        await rig.Controller.CommitAsync();
        Assert.Equal(1, rig.Session.Commits);
        Assert.False(rig.State.TxnPending);

        rig.Session.HasPendingTransaction = true;
        rig.Bus.Publish(new TransactionStateChanged(rig.Session.Id, true));
        await rig.Controller.RollbackAsync();
        Assert.Equal(1, rig.Session.Rollbacks);
        Assert.False(rig.State.TxnPending);
    });

    [Fact]
    public Task Closing_with_an_uncommitted_transaction_asks_commit_rollback_or_cancel() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        Assert.True(await rig.Controller.ConfirmCloseAsync());
        Assert.Equal(0, rig.Dialogs.UncommittedAsked);

        rig.Session.HasPendingTransaction = true;
        rig.Bus.Publish(new TransactionStateChanged(rig.Session.Id, true));

        rig.Dialogs.UncommittedAnswer = UncommittedChoice.Cancel;
        Assert.False(await rig.Controller.ConfirmCloseAsync());
        Assert.True(rig.State.TxnPending);

        rig.Dialogs.UncommittedAnswer = UncommittedChoice.Rollback;
        Assert.True(await rig.Controller.ConfirmCloseAsync());
        Assert.Equal(1, rig.Session.Rollbacks);

        rig.Session.HasPendingTransaction = true;
        rig.Bus.Publish(new TransactionStateChanged(rig.Session.Id, true));
        rig.Dialogs.UncommittedAnswer = UncommittedChoice.Commit;
        Assert.True(await rig.Controller.ConfirmCloseAsync());
        Assert.Equal(1, rig.Session.Commits);
        Assert.Equal(3, rig.Dialogs.UncommittedAsked);
    });

    // ---------- 그리드 편집 연결 ----------

    [Fact]
    public Task Grid_edit_rerun_runs_the_given_or_current_statement_and_preview_uses_the_dialog() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select a from t");
        tab.Select(2, 0);

        Assert.NotNull(GridEditCommands.Rerun);
        await GridEditCommands.Rerun!(null, default);
        Assert.Equal("select a from t", Assert.Single(rig.Exec.Statements).Text);

        var withRowId = new SqlStatement("select rowid as oranda_rid, a from t", default, StatementKind.Query) { Verb = "SELECT" };
        await GridEditCommands.Rerun!(withRowId, default);
        Assert.Equal("select rowid as oranda_rid, a from t", rig.Exec.Statements[1].Text);

        Assert.True(await GridEditCommands.ConfirmPreview!("UPDATE t SET a = 1 WHERE ROWID = 'X'", false, default));
        Assert.Equal("UPDATE t SET a = 1 WHERE ROWID = 'X'", rig.Dialogs.PreviewText);
    });

    // ---------- 결과 영역 숨기기 ----------

    [Fact]
    public Task Run_shows_the_results_panel_and_toggle_keeps_the_tab_results() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select 1 from dual");
        rig.Exec.Handler = (s, _) => Task.FromResult(Rig.Query(s));
        await rig.Controller.RunAsync(RunMode.Statement);
        var results = tab.ResultPane;
        Assert.NotNull(results);
        Assert.Contains("show " + ExecutionController.ResultsPanelId, rig.Panels);

        // 결과 창을 숨겨도 탭의 결과는 그대로다. 다시 실행하면 같은 결과 영역을 쓴다.
        rig.Controller.ToggleResultPane();
        Assert.Equal("toggle " + ExecutionController.ResultsPanelId, rig.Panels[^1]);
        Assert.Same(results, tab.ResultPane);
        await rig.Controller.RunAsync(RunMode.Statement);
        Assert.Same(results, tab.ResultPane);
        Assert.Equal("show " + ExecutionController.ResultsPanelId, rig.Panels[^1]);
    });

    [Fact]
    public Task Results_panel_follows_the_active_tab_and_its_result_pane() => Ui.Run(() =>
    {
        using var rig = new Rig();
        var a = rig.Host.NewDocument("select 1 from dual");
        using var panel = ResultsPanelViewModel.ForHost(rig.Host);
        Assert.False(panel.HasPane);

        var paneA = new object();
        a.ResultPane = paneA;
        Assert.Same(paneA, panel.Pane);

        var b = rig.Host.NewDocument("select 2 from dual");
        Assert.Null(panel.Pane);
        var paneB = new object();
        b.ResultPane = paneB;
        Assert.Same(paneB, panel.Pane);

        rig.Host.Active = a;
        Assert.Same(paneA, panel.Pane);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Results_panel_view_reuses_each_tab_view_when_switching() => Ui.Run(() =>
    {
        using var rig = new Rig();
        var a = rig.Host.NewDocument("select 1 from dual");
        a.ResultPane = new ResultTabsViewModel(null);
        using var vm = ResultsPanelViewModel.ForHost(rig.Host);
        var view = new ResultsPanelView { DataContext = vm };
        var window = new Avalonia.Controls.Window { Width = 800, Height = 300, Content = view };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var host = view.FindControl<Avalonia.Controls.ContentControl>("PaneHost")!;
        var first = Assert.IsType<ResultTabsView>(host.Content);
        Assert.Same(a.ResultPane, first.DataContext);

        var b = rig.Host.NewDocument("select 2 from dual");
        b.ResultPane = new ResultTabsViewModel(null);
        Assert.NotSame(first, host.Content);

        rig.Host.Active = a;
        Assert.Same(first, host.Content); // 다시 만들지 않는다(스크롤·열 폭을 지킨다)
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_second_results_panel_view_takes_over_the_shared_result_view() => Ui.Run(() =>
    {
        // 도킹이 결과 창을 다시 만들어 옛 View 가 아직 붙어 있는 동안 새 View 가 같은 결과를 보이려 한다.
        // 예전에는 「already has a visual parent」 예외가 측정 중에 나서 앱이 죽었다.
        using var rig = new Rig();
        var a = rig.Host.NewDocument("select 1 from dual");
        a.ResultPane = new ResultTabsViewModel(null);
        using var vm = ResultsPanelViewModel.ForHost(rig.Host);
        var oldView = new ResultsPanelView { DataContext = vm };
        var newView = new ResultsPanelView();
        var window = new Avalonia.Controls.Window
        {
            Width = 800,
            Height = 600,
            Content = new Avalonia.Controls.StackPanel { Children = { oldView, newView } },
        };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var oldHost = oldView.FindControl<Avalonia.Controls.ContentControl>("PaneHost")!;
        var shared = Assert.IsType<ResultTabsView>(oldHost.Content);

        newView.DataContext = vm;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var newHost = newView.FindControl<Avalonia.Controls.ContentControl>("PaneHost")!;
        Assert.Same(shared, newHost.Content);
        Assert.Null(oldHost.Content);
        window.Close();
        return Task.CompletedTask;
    });

    // ---------- 명령 처리기 ----------

    [Fact]
    public async Task Toggle_commands_change_the_run_options()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-exec-" + Guid.NewGuid().ToString("N"));
        using var container = AppComposition.Create(new AppCompositionOptions { DataDirectory = dir });
        var handler = (ExecutionCommandHandler)container.Create(typeof(ExecutionCommandHandler));
        var state = ExecutionState.For(container.GetService<IEventBus>());
        var keys = FakeContextKeys.AllTrue;

        await handler.ExecuteAsync(new CommandContext("oranda.exec.errorModeStop", keys), default);
        Assert.Equal(ScriptErrorMode.Stop, state.ErrorMode);
        await handler.ExecuteAsync(new CommandContext("oranda.exec.errorModeContinue", keys), default);
        Assert.Equal(ScriptErrorMode.Continue, state.ErrorMode);
        await handler.ExecuteAsync(new CommandContext("oranda.exec.errorModeAsk", keys), default);
        Assert.Equal(ScriptErrorMode.Ask, state.ErrorMode);

        await handler.ExecuteAsync(new CommandContext("oranda.exec.toggleDbmsOutput", keys), default);
        Assert.False(state.DbmsOutputEnabled);
        await handler.ExecuteAsync(new CommandContext("oranda.exec.toggleDbmsOutput", keys), default);
        Assert.True(state.DbmsOutputEnabled);

        await handler.ExecuteAsync(new CommandContext("oranda.exec.toggleBlankLineDelimiter", keys), default);
        Assert.True(state.BlankLineDelimiter);
        await handler.ExecuteAsync(new CommandContext("oranda.txn.toggleAutoCommit", keys), default);
        Assert.True(state.AutoCommit);

        Assert.False(container.DeferredScanned);
    }

    // ---------- 화면(헤드리스) ----------

    [Fact]
    public Task Result_tabs_view_shows_the_connection_banner_grid_tabs_and_message_tabs() => Ui.Run(() =>
    {
        var prod = new ConnectionProfile("PROD", "hr", "db:1521/S", Environment: ConnectionEnvironment.Production, ReadOnly: true);
        var vm = new ResultTabsViewModel(prod);
        vm.AddQuery(new OneColumnResult("a", "b"), "결과 1", null);
        vm.AddMessage("메시지", "3행 처리됨", isError: false);
        var view = new ResultTabsView { DataContext = vm };
        var window = new Window { Width = 600, Height = 300, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("운영 · PROD · 읽기 전용", vm.Banner);
        Assert.True(vm.HasBanner);
        Assert.True(vm.IsProduction);
        var tabs = view.FindControl<TabControl>("Tabs")!;
        Assert.Equal(2, tabs.ItemCount);
        var banner = view.FindControl<Border>("BannerBorder")!;
        Assert.Contains("env-production", banner.Classes);
        Assert.True(banner.IsVisible);

        vm.SetProfile(new ConnectionProfile("DEV", "u", "h"));
        Assert.False(vm.HasBanner);
        vm.Clear();
        Assert.Empty(vm.Tabs);
        window.Close();
    });

    [Fact]
    public Task Output_messages_and_status_views_render_their_view_models() => Ui.Run(() =>
    {
        var bus = new EventBus();
        var output = new OutputView { DataContext = new OutputViewModel(bus) };
        var messages = new MessagesView { DataContext = new MessagesViewModel(bus) };
        var session = new SessionStatusView { DataContext = new SessionStatusViewModel(bus) };
        var panel = new StackPanel { Children = { output, messages, session } };
        var window = new Window { Width = 400, Height = 400, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        bus.Publish(new DbmsOutputReceived(Guid.NewGuid(), ["line 1"]));
        ExecutionLog.For(bus).AddMessage(MessageKind.Info, "hello");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("line 1\n", output.FindControl<TextBox>("OutputBox")!.Text);
        Assert.Equal(1, messages.FindControl<ListBox>("MessageList")!.ItemCount);
        window.Close();
    });

    [Fact]
    public Task Status_bar_in_the_real_shell_shows_session_transaction_and_protection() => Ui.Run(async () =>
    {
        using var shell = new ShellHarness();
        var bus = shell.Get<IEventBus>();
        var session = new FakeSession(new ConnectionProfile("ro", "scott", "h:1521/s", ReadOnly: true));
        bus.Publish(new SessionOpened(session.Id, session.Profile, session.Server, SessionPurpose.Work));
        bus.Publish(new TransactionStateChanged(session.Id, true));
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();

        var keys = shell.Get<ContextKeyService>();
        Assert.True(keys.Get("txnPending"));
        Assert.True(keys.Get("readOnlySession"));
        Assert.False(keys.Get("sessionBusy"));
        var state = ExecutionState.For(bus);
        Assert.Equal("미커밋", state.TransactionText);
        Assert.True(state.ReadOnly);

        bus.Publish(new SessionClosed(session.Id));
    });
}

/// <summary>
/// 통합 시험(Category=Integration): 편집기 → 실행 화면 → 실행 엔진(실제 Oracle) → 결과 그리드. 환경 변수가 없으면 아무것도 하지 않고 통과한다.
/// ORANDA_IT_HOST, ORANDA_IT_PORT(기본 1521), ORANDA_IT_SERVICE, ORANDA_IT_USER, ORANDA_IT_PASSWORD — 테이블을 만들 수 있는 일반 사용자.
/// </summary>
[Trait("Category", "Integration")]
[Collection("GridEditStatics")]
public sealed class ExecutionIntegrationTests
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    private static bool Configured => Env("HOST") is not null && Env("SERVICE") is not null && Env("USER") is not null && Env("PASSWORD") is not null;

    private static ConnectionProfile Profile(ConnectionEnvironment env = ConnectionEnvironment.Development, bool readOnly = false) =>
        new("it-" + env, Env("USER")!, $"{Env("HOST")}:{Env("PORT") ?? "1521"}/{Env("SERVICE")}", Environment: env, ReadOnly: readOnly, Id: Guid.NewGuid());

    private sealed class ItRig : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-exec-it-" + Guid.NewGuid().ToString("N"));
        private readonly SqlEditorHost _previousHost = SqlEditorHost.Current;
        private readonly IExecutionDialogs _previousDialogs = ExecutionDialogs.Current;

        public ItRig()
        {
            Directory.CreateDirectory(_dir);
            Container = AppComposition.Create(new AppCompositionOptions { DataDirectory = _dir });
            Host = new SqlEditorHost();
            SqlEditorHost.Current = Host;
            ExecutionDialogs.Current = Dialogs;
            Service = ConnectionManagerCommands.Resolve(Container);
            Controller = ExecutionCommands.Resolve(Container);
            Handler = (ExecutionCommandHandler)Container.Create(typeof(ExecutionCommandHandler));
        }

        public ServiceContainer Container { get; }

        public SqlEditorHost Host { get; }

        public FakeDialogs Dialogs { get; } = new();

        public IConnectionService Service { get; }

        public ExecutionController Controller { get; }

        public ExecutionCommandHandler Handler { get; }

        public ExecutionState State => Controller.State;

        public async Task ConnectAsync(ConnectionProfile profile)
        {
            if (Service.ActiveSession is not null)
            {
                await Service.DisconnectAsync(default);
            }

            var outcome = await Service.ConnectAsync(profile, Env("PASSWORD"), default);
            Assert.True(outcome.IsConnected, outcome.Message);
            await HeadlessHost.WaitUntil(() => State.HasSession);
        }

        public Task Run(string commandId) => Handler.ExecuteAsync(new CommandContext(commandId, FakeContextKeys.AllTrue), default);

        public async Task<ResultTabsViewModel> RunSqlAsync(string sql, string command = "oranda.exec.runStatement")
        {
            var tab = Host.NewDocument(sql);
            tab.Select(0, 0);
            await Run(command);
            return (ResultTabsViewModel)tab.ResultPane!;
        }

        public void Dispose()
        {
            try
            {
                Service.DisconnectAsync(default).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // 정리 중 오류는 무시한다.
            }

            SqlEditorHost.Current = _previousHost;
            ExecutionDialogs.Current = _previousDialogs;
            Container.Dispose();
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<string> CellAsync(ResultTabItem item, long row = 0, int col = 0)
    {
        var model = item.Pane!.Model!;
        await model.LoadFirstPageAsync();
        return model.GetCellText(row, col);
    }

    [Fact]
    public Task Editor_runs_a_query_and_the_grid_shows_the_rows() => Ui.Run(async () =>
    {
        if (!Configured)
        {
            return;
        }

        using var rig = new ItRig();
        await rig.ConnectAsync(Profile());

        var results = await rig.RunSqlAsync("select level as n from dual connect by level <= 3");

        var item = Assert.Single(results.Tabs);
        Assert.NotNull(item.Pane?.Model);
        Assert.Equal("1", await CellAsync(item));
        Assert.Equal(3, item.Pane!.Model!.LoadedRowCount);
        await HeadlessHost.WaitUntil(() => rig.State.LastRunText.Length > 0);
        Assert.Equal(SessionRunState.Idle, rig.State.RunState);
    });

    [Fact]
    public Task Substitution_variable_and_bind_values_reach_the_server() => Ui.Run(async () =>
    {
        if (!Configured)
        {
            return;
        }

        using var rig = new ItRig();
        await rig.ConnectAsync(Profile());

        rig.Dialogs.SubstitutionAnswer = new Dictionary<string, string> { ["x"] = "'abc'" };
        var sub = await rig.RunSqlAsync("select &x as v from dual");
        Assert.Equal("abc", await CellAsync(Assert.Single(sub.Tabs)));

        var a = BindVariableStore.For(rig.Container.GetService<IEventBus>()).Add("a");
        a.Type = BindDataType.Number;
        a.Text = "42";
        var bind = await rig.RunSqlAsync("select :a as v from dual");
        Assert.Equal("42", await CellAsync(Assert.Single(bind.Tabs)));
    });

    [Fact]
    public Task Production_label_asks_before_update_and_unfiltered_dml_asks_everywhere() => Ui.Run(async () =>
    {
        if (!Configured)
        {
            return;
        }

        using var rig = new ItRig();
        var table = "oranda_it_" + Guid.NewGuid().ToString("N")[..8];
        await rig.ConnectAsync(Profile());
        try
        {
            await rig.RunSqlAsync($"create table {table} (id number primary key, v number);\ninsert into {table} values (1, 1);\ncommit;", "oranda.exec.runScript");
            Assert.Empty(rig.Dialogs.Confirms); // 개발 접속의 DDL·WHERE 있는 DML 은 묻지 않는다.

            // WHERE 없는 DELETE 는 개발 접속에서도 확인한다.
            rig.Dialogs.ConfirmAnswer = false;
            await rig.RunSqlAsync($"delete from {table}");
            var unfiltered = Assert.Single(rig.Dialogs.Confirms);
            Assert.Equal(PolicyRule.UnfilteredDml, unfiltered.Decision.Rule);
            rig.Dialogs.Confirms.Clear();

            // 운영 라벨 접속: WHERE 있는 UPDATE 도 확인한다. 거절하면 바뀌지 않는다.
            await rig.ConnectAsync(Profile(ConnectionEnvironment.Production));
            var declined = await rig.RunSqlAsync($"update {table} set v = 2 where id = 1");
            var confirm = Assert.Single(rig.Dialogs.Confirms);
            Assert.Equal(PolicyRule.ProductionChange, confirm.Decision.Rule);
            Assert.Equal(ConnectionEnvironment.Production, confirm.Profile!.Environment);
            Assert.Equal("취소되었습니다.", Assert.Single(declined.Tabs).Message);
            Assert.False(rig.State.TxnPending);
            var check = await rig.RunSqlAsync($"select v from {table} where id = 1");
            Assert.Equal("1", await CellAsync(Assert.Single(check.Tabs)));

            // 승인하면 실행되고 미커밋 상태가 된다. 커밋 명령으로 확정한다.
            rig.Dialogs.ConfirmAnswer = true;
            await rig.RunSqlAsync($"update {table} set v = 2 where id = 1");
            await HeadlessHost.WaitUntil(() => rig.State.TxnPending);
            Assert.Equal("미커밋", rig.State.TransactionText);
            await rig.Run("oranda.txn.commit");
            Assert.False(rig.State.TxnPending);
            var after = await rig.RunSqlAsync($"select v from {table} where id = 1");
            Assert.Equal("2", await CellAsync(Assert.Single(after.Tabs)));

            // 위험 명령은 대상 이름 재입력 요구가 결정에 실린다.
            rig.Dialogs.Confirms.Clear();
            rig.Dialogs.ConfirmAnswer = false;
            await rig.RunSqlAsync($"drop table {table}");
            Assert.Equal(table, Assert.Single(rig.Dialogs.Confirms).Decision.RetypeText, ignoreCase: true);
        }
        finally
        {
            rig.Dialogs.ConfirmAnswer = true;
            await rig.ConnectAsync(Profile());
            await rig.RunSqlAsync($"drop table {table} purge");
        }
    });

    [Fact]
    public Task Read_only_profile_denies_changes_and_shows_the_protection_state() => Ui.Run(async () =>
    {
        if (!Configured)
        {
            return;
        }

        using var rig = new ItRig();
        await rig.ConnectAsync(Profile(readOnly: true));
        Assert.True(rig.State.ReadOnly);

        var denied = await rig.RunSqlAsync("update dual set dummy = 'Y'");

        var item = Assert.Single(denied.Tabs);
        Assert.Equal("거부됨", item.Title);
        Assert.Contains("읽기 전용", item.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Dialogs.Confirms);
    });

    [Fact]
    public Task Cancelling_a_long_running_statement_frees_the_session_within_one_second() => Ui.Run(async () =>
    {
        if (!Configured)
        {
            return;
        }

        using var rig = new ItRig();
        await rig.ConnectAsync(Profile());
        var tab = rig.Host.NewDocument("SELECT COUNT(*) FROM all_objects a, all_objects b, (SELECT 1 FROM dual CONNECT BY level <= 100) c");
        tab.Select(0, 0);

        // CPU 를 오래 쓰는 질의(DBMS_SESSION.SLEEP 은 컨테이너에서 중단 요청을 잠이 끝난 뒤에야 처리한다).
        var run = rig.Run("oranda.exec.runStatement");
        await HeadlessHost.WaitUntil(() => rig.State.Busy, 10000);
        await Task.Delay(1500); // 서버에서 실제로 돌기 시작할 시간을 준다
        var clock = Stopwatch.StartNew();
        await rig.Run("oranda.exec.cancel");
        await run;
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds < 1000, $"취소 후 {clock.ElapsedMilliseconds} ms");
        Assert.False(rig.State.Busy);
        Assert.Equal(SessionRunState.Idle, rig.State.RunState);

        var next = await rig.RunSqlAsync("select 1 as n from dual");
        Assert.Equal("1", await CellAsync(Assert.Single(next.Tabs)));
    });

    [Fact]
    public Task Dbms_output_goes_to_the_output_panel_and_script_errors_follow_the_chosen_mode() => Ui.Run(async () =>
    {
        if (!Configured)
        {
            return;
        }

        using var rig = new ItRig();
        await rig.ConnectAsync(Profile());

        await rig.RunSqlAsync("begin dbms_output.put_line('hello from oranda'); end;");
        var log = ExecutionLog.For(rig.Container.GetService<IEventBus>());
        await HeadlessHost.WaitUntil(() => log.OutputText.Contains("hello from oranda", StringComparison.Ordinal));

        const string script = "select 1 from dual;\nselect * from oranda_no_such_table;\nselect 3 from dual;";
        await rig.Run("oranda.exec.errorModeContinue");
        var all = await rig.RunSqlAsync(script, "oranda.exec.runScript");
        Assert.Contains("3개 중 3개 실행", all.Tabs[^1].Message, StringComparison.Ordinal);
        Assert.Contains("ORA-00942", all.Tabs.Single(t => t.IsError && t.Title.StartsWith("오류", StringComparison.Ordinal)).Message, StringComparison.Ordinal);
        Assert.True(rig.State.HasExecError);

        await rig.Run("oranda.exec.errorModeStop");
        var stopped = await rig.RunSqlAsync(script, "oranda.exec.runScript");
        Assert.Contains("3개 중 2개 실행", stopped.Tabs[^1].Message, StringComparison.Ordinal);

        rig.Dialogs.ScriptErrorAnswer = ScriptErrorAction.Continue;
        await rig.Run("oranda.exec.errorModeAsk");
        var asked = await rig.RunSqlAsync(script, "oranda.exec.runScript");
        Assert.Contains("3개 중 3개 실행", asked.Tabs[^1].Message, StringComparison.Ordinal);
    });

    [Fact]
    public Task F9_runs_the_statement_and_opens_the_result_in_edit_mode() => Ui.Run(async () =>
    {
        if (!Configured)
        {
            return;
        }

        using var rig = new ItRig();
        var table = "oranda_it_" + Guid.NewGuid().ToString("N")[..8];
        await rig.ConnectAsync(Profile());
        try
        {
            await rig.RunSqlAsync($"create table {table} (id number primary key, v varchar2(10));\ninsert into {table} values (1, 'a');\ncommit;", "oranda.exec.runScript");
            var tab = rig.Host.NewDocument($"select id, v from {table}");
            tab.Select(0, 0);

            await GridEditCommands.Rerun!(null, default);

            var results = (ResultTabsViewModel)tab.ResultPane!;
            var item = Assert.Single(results.Tabs);
            Assert.NotNull(item.EditSession);
            Assert.Same(item.EditSession, GridEditCommands.Active);
            Assert.EndsWith("(편집)", item.Title, StringComparison.Ordinal);
            Assert.True(item.EditSession!.CanEdit, item.EditSession.Capability.Message);
        }
        finally
        {
            GridEditCommands.Active = null;
            await rig.RunSqlAsync($"drop table {table} purge");
        }
    });
}
