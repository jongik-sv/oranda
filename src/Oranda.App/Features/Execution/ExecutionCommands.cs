using System.Runtime.CompilerServices;
using Oranda.App.Features.Binds;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.DedicatedSession;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Features;
using Oranda.Core.Grid;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Execution;

/// <summary>
/// TSK-06-02 SQL 실행 화면·운영 안전장치 표시 — 명령 처리기와 컨트롤러 조립. 처리기 클래스에 <c>[OrandaCommand]</c> 를 달면 셸이 자동 수집한다
/// (docs/design/ui-contract.md). 명령 ID 는 commands.md 7장 「소유 Task = TSK-06-02」 행과 같다.
/// 셸 조립 파일(<c>AppComposition</c>)을 고치지 않고 이 안에서 끝내려고, 서비스는 처음 쓸 때 컨테이너에 등록한다
/// (<see cref="ConnectionManagerCommands.Resolve"/> 와 같은 방식).
/// </summary>
public static class ExecutionCommands
{
    private static readonly ConditionalWeakTable<ServiceContainer, ExecutionController> Controllers = [];
    private static readonly ConditionalWeakTable<ServiceContainer, object> Installed = [];
    private static readonly Lock Gate = new();

    /// <summary>이 Task 가 등록하는 명령 ID 전체(시험이 등록 누락을 잡는다).</summary>
    public static readonly string[] Ids =
    [
        "oranda.exec.runStatement",
        "oranda.exec.runSubquery",
        "oranda.exec.runSelection",
        "oranda.exec.runScript",
        "oranda.exec.runFromCursor",
        "oranda.exec.runStep",
        "oranda.exec.runToNewResultTab",
        "oranda.exec.runAllTabs",
        "oranda.exec.cancel",
        "oranda.exec.bindVariables",
        "oranda.exec.pasteBindValues",
        "oranda.exec.toggleDbmsOutput",
        "oranda.exec.errorModeAsk",
        "oranda.exec.errorModeStop",
        "oranda.exec.errorModeContinue",
        "oranda.exec.goToError",
        "oranda.exec.toggleBlankLineDelimiter",
        "oranda.txn.commit",
        "oranda.txn.rollback",
        "oranda.txn.toggleAutoCommit",
        "oranda.view.output",
        "oranda.view.messages",
        "oranda.window.toggleResultPane",
    ];

    /// <summary>
    /// 컨테이너의 실행 컨트롤러를 꺼낸다. 처음이면 만든다. 서비스(<c>IExecutionService</c>·<c>ILanguageService</c>·접속)는
    /// 실제로 쓸 때 해석하므로 여기서는 Oracle·Language 어셈블리를 깨우지 않는다.
    /// </summary>
    public static ExecutionController Resolve(ServiceContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        lock (Gate)
        {
            if (Controllers.TryGetValue(container, out var existing))
            {
                return existing;
            }

            var bus = container.GetService<IEventBus>();
            var state = ExecutionState.For(bus);
            state.UseKeys(container.GetService<ContextKeyService>());
            state.UseFeatures(container.GetService<IFeatureGate>()); // readOnlySession = 프로필 읽기 전용 또는 조회 전용 Role(TSK-07-05)
            var shell = container.GetService<ShellController>();
            ExecutionDialogs.UseOwner(() => shell?.MainWindow);

            var controller = new ExecutionController(new ExecutionDeps
            {
                State = state,
                Log = ExecutionLog.For(bus),
                Dialogs = ExecutionDialogs.Current,
                Session = () => SessionRouter.Resolve(container).Current,
                TabSession = tab => SessionRouter.Resolve(container).SessionFor(tab),
                MissingConnection = tab => SessionRouter.Resolve(container).MissingProfileOf(tab),
                WorkSessions = () => SessionRouter.Resolve(container).AllSessions, // 같은 DB 에 더 연 세션도 미커밋을 묻는다(TSK-14-01)
                PrepareSession = (tab, ct) => SessionRouter.Resolve(container).PrepareAsync(tab, ct),
                UseConnection = (tab, session) => SessionRouter.Resolve(container).UseConnection(tab, session),
                SharedBusy = (tab, rerun) => SessionRouter.Resolve(container).OfferNewSession(tab, rerun),
                ConnectionLabel = s => SessionRouter.Resolve(container).LabelOf(s),
                ProfileName = id => SessionRouter.Resolve(container).ProfileNameAsync(id),
                Reconnect = (id, _) => SessionRouter.Resolve(container).ReconnectAsync(id, () => ConnectionManagerCommands.ConnectProfileAsync(container, id)),
                Execution = () =>
                {
                    EnsureEngineServices(container, state);
                    return container.GetRequiredService<IExecutionService>();
                },
                Language = () => container.GetRequiredService<ILanguageService>(),
                RowEditor = () => container.GetService<IRowEditor>(),
                Metadata = () => container.GetService<IMetadataProvider>(),
                Binds = BindVariableStore.For(bus),
                ActivatePanel = id => container.GetService<IShellUi>()?.Docking.ActivateTool(id),
                TogglePanel = id => container.GetService<IShellUi>()?.Docking.ToggleTool(id),
            });
            Controllers.Add(container, controller);
            return controller;
        }
    }

    /// <summary>
    /// 실행 엔진이 생성자에서 요구하는 정책 관문·대화 창구를 컨테이너에 등록한다(없을 때만). 정책 관문에는 등록된 기능 게이트를 넘긴다(조회 전용 Role, TSK-07-05).
    /// 앱 조립이 먼저 등록했으면(<c>AppExecutionInteraction</c> 의 <c>[OrandaService]</c>) 그대로 쓰고 접속 표시만 이어 준다.
    /// </summary>
    public static void EnsureEngineServices(ServiceContainer container, ExecutionState state)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(state);
        lock (Gate)
        {
            if (container.GetService<IExecutionPolicy>() is null)
            {
                container.AddInstance<IExecutionPolicy>(new ExecutionPolicy(container.GetService<IFeatureGate>()));
            }

            var interaction = container.GetService<IExecutionInteraction>();
            if (interaction is null)
            {
                container.AddInstance<IExecutionInteraction>(new AppExecutionInteraction { ProfileProvider = () => ExecutionController.RunningProfile ?? state.ShownProfile });
            }
            else if (interaction is AppExecutionInteraction app)
            {
                app.ProfileProvider ??= () => ExecutionController.RunningProfile ?? state.ShownProfile;
            }
        }
    }

    /// <summary>
    /// 활성 SQL 탭이 쓰는 세션: 전용 세션 탭(D-017)이면 그 세션, 아니면 탭에 묶인 접속(<see cref="SessionRouter.SessionFor"/>, 열려 있지 않으면 null).
    /// 결과 그리드·실행 계획처럼 활성 탭을 따르는 기능이 쓴다.
    /// </summary>
    public static Oranda.Core.Sessions.ISession? ActiveTabSession(ServiceContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return SessionRouter.Resolve(container).SessionFor(SqlEditorHost.Current.Active);
    }

    /// <summary>
    /// 결과 그리드 「전체 행 수 세기」의 실행 쪽. 세션·실행 엔진은 부를 때 해석한다(시작 때 Oracle 어셈블리를 깨우지 않게).
    /// 상태는 컨트롤러와 같은 것(버스마다 하나), 바인드 목록은 활성 SQL 탭의 것(TSK-04-11)을 쓴다.
    /// </summary>
    private static GridRowCounter RowCounter(ServiceContainer container)
    {
        var bus = container.GetService<IEventBus>();
        var state = ExecutionState.For(bus);
        return new GridRowCounter(
            state,
            BindVariableStore.ForTab(SqlEditorHost.Current.Active, bus),
            () => ActiveTabSession(container),
            () =>
            {
                EnsureEngineServices(container, state);
                return container.GetRequiredService<IExecutionService>();
            });
    }

    /// <summary>
    /// 결과 거르기 줄(GRID-10)의 실행 쪽. 행 수 세기와 같이 세션·실행 엔진은 부를 때 해석하고, 상태는 버스마다 하나, 바인드 목록은 활성 SQL 탭의 것을 쓴다.
    /// </summary>
    private static GridFilterRunner FilterRunner(ServiceContainer container)
    {
        var bus = container.GetService<IEventBus>();
        var state = ExecutionState.For(bus);
        return new GridFilterRunner(
            state,
            BindVariableStore.ForTab(SqlEditorHost.Current.Active, bus),
            () => ActiveTabSession(container),
            () =>
            {
                EnsureEngineServices(container, state);
                return container.GetRequiredService<IExecutionService>();
            });
    }

    /// <summary>
    /// 객체 상세 「데이터」 탭(SCH-03)의 실행 쪽. 세션은 활성 세션이 아니라 그 상세 문서의 세션이고, 실행 엔진은 부를 때 해석한다.
    /// 상태는 버스마다 하나, 바인드 목록은 탭이 없으니 기본 목록을 쓴다(데이터를 읽는 동안 편집기 실행과 겹치지 않게).
    /// </summary>
    private static ObjectDetail.ObjectDataRunner DataRunner(ServiceContainer container, Oranda.Core.Sessions.ISession session)
    {
        var bus = container.GetService<IEventBus>();
        var state = ExecutionState.For(bus);
        return new ObjectDetail.ObjectDataRunner(
            state,
            BindVariableStore.For(bus),
            session,
            () =>
            {
                EnsureEngineServices(container, state);
                return container.GetRequiredService<IExecutionService>();
            });
    }

    /// <summary>
    /// 시작 때 불러도 되는 가벼운 설치: 그리드 편집의 <see cref="GridEditCommands.Rerun"/>·<see cref="GridEditCommands.ConfirmPreview"/> 를
    /// 컨트롤러로 잇는 얇은 위임자와 결과 그리드 「전체 행 수 세기」(<see cref="ResultGrid.ResultGridCommands.CountRows"/>)·
    /// 「결과 거르기」(<see cref="ResultGrid.ResultGridCommands.Filter"/>)·「모두 가져오기」(<see cref="ResultGrid.ResultGridCommands.RunFetch"/>)를 걸고, 미커밋이 생기면 주 창 닫기 확인을 건다. 컨트롤러·서비스는 만들지 않는다.
    /// 상태 표시줄 구간(시작 때 만들어진다)이 부른다.
    /// </summary>
    public static void Install(ServiceContainer? container)
    {
        if (container is null)
        {
            return;
        }

        lock (Gate)
        {
            if (Installed.TryGetValue(container, out _))
            {
                return;
            }

            Installed.Add(container, new object());
        }

        ExecutionState.For(container.GetService<IEventBus>()).UseFeatures(container.GetService<IFeatureGate>()); // 시작 때부터 readOnlySession 이 Role 을 따른다
        GridEditCommands.Rerun ??= (stmt, ct) => Resolve(container).RerunForEditAsync(stmt, ct);
        GridEditCommands.ConfirmPreview ??= (text, commit, ct) => Resolve(container).ConfirmPreviewAsync(text, commit, ct);
        GridEditCommands.Report ??= notice => Resolve(container).ReportGridApply(notice);
        ResultGrid.ResultGridCommands.CountRows ??= (stmt, ct) => RowCounter(container).CountAsync(stmt, ct);
        ResultGrid.ResultGridCommands.Filter ??= (request, ct) => FilterRunner(container).RunAsync(request, ct);
        ResultGrid.ResultGridCommands.CountFiltered ??= (request, ct) => FilterRunner(container).CountAsync(request, ct);
        ResultGrid.ResultGridCommands.OpenRelatedRows ??= (request, ct) => RelatedRowsRunner.Create(container).OpenAsync(request, ct); // 외래키 따라가기(GRID-11)
        ResultGrid.ResultGridCommands.RunFetch ??= (fetch, ct) => Resolve(container).FetchAsync(fetch, ct); // 「모두 가져오기」도 취소 명령이 끊는다
        ObjectDetail.ObjectDetailCommands.DataRunner ??= session => DataRunner(container, session);

        // 이력 「다시 실행」·내보내기/가져오기의 완료 알림은 메시지 창으로 보낸다(받는 곳이 없으면 조용히 사라졌다).
        var log = ExecutionLog.For(container.GetService<IEventBus>());
        void Notify(string text) => Avalonia.Threading.Dispatcher.UIThread.Post(() => log.AddMessage(MessageKind.Info, text));
        HistoryScripts.HistoryScriptsCommands.Notify ??= Notify;
        DataTransfer.DataTransferCommands.Notify ??= Notify;
        DedicatedSessionCommands.Resolve(container); // 활성 탭이 바뀌면 상태 표시줄·문맥 키가 그 탭의 세션을 보이게 한다(D-017)
    }
}

/// <summary>실행·취소·커밋·롤백 등 TSK-06-02 소유 명령의 처리기. 한 클래스가 명령 ID 로 나눠 처리한다.</summary>
[OrandaCommand("oranda.exec.runStatement", CommandCategory.Sql, When = "sqlDocFocus", Enablement = "(hasSession || dedicatedSession || tabConnectionMissing) && (!sessionBusy || sessionBusyElsewhere) && !mybatisDocActive",
    Icon = "run", Placements = ["menu:sql/runStatement@10", "toolbar:main/run@210", "context:editor/runStatement@1000"])]
[OrandaCommand("oranda.exec.runSubquery", CommandCategory.Sql, When = "sqlDocFocus", Enablement = "(hasSession || dedicatedSession || tabConnectionMissing) && (!sessionBusy || sessionBusyElsewhere) && !mybatisDocActive",
    Placements = ["menu:sql/runSubquery@11", "context:editor/runSubquery@1005"])]
[OrandaCommand("oranda.exec.runSelection", CommandCategory.Sql, When = "sqlDocFocus", Enablement = "(hasSession || dedicatedSession || tabConnectionMissing) && (!sessionBusy || sessionBusyElsewhere) && hasSelection && !mybatisDocActive",
    Placements = ["menu:sql/runSelection@12"])]
[OrandaCommand("oranda.exec.runScript", CommandCategory.Sql, When = "sqlDocFocus", Enablement = "(hasSession || dedicatedSession || tabConnectionMissing) && (!sessionBusy || sessionBusyElsewhere) && !mybatisDocActive",
    Icon = "runScript", Placements = ["menu:sql/runScript@14", "toolbar:main/runScript@220", "context:editor/runScript@1010"])]
[OrandaCommand("oranda.exec.runFromCursor", CommandCategory.Sql, When = "sqlDocFocus", Enablement = "(hasSession || dedicatedSession || tabConnectionMissing) && (!sessionBusy || sessionBusyElsewhere) && !mybatisDocActive",
    Placements = ["menu:sql/runFromCursor@16"])]
[OrandaCommand("oranda.exec.runStep", CommandCategory.Sql, When = "sqlDocFocus", Enablement = "(hasSession || dedicatedSession || tabConnectionMissing) && (!sessionBusy || sessionBusyElsewhere) && !mybatisDocActive",
    Placements = ["menu:sql/runStep@18"])]
[OrandaCommand("oranda.exec.runToNewResultTab", CommandCategory.Sql, When = "sqlDocFocus", Enablement = "(hasSession || dedicatedSession || tabConnectionMissing) && (!sessionBusy || sessionBusyElsewhere) && !mybatisDocActive",
    Placements = ["menu:sql/runToNewResultTab@20"])]
[OrandaCommand("oranda.exec.runAllTabs", CommandCategory.Sql, Enablement = "(hasSession || dedicatedSession || tabConnectionMissing) && !sessionBusy && !mybatisDocActive",
    Placements = ["menu:sql/runAllTabs@22"])]
[OrandaCommand("oranda.exec.cancel", CommandCategory.Sql, When = "sqlDocFocus", Enablement = "sessionBusy && !sessionBusyElsewhere",
    Icon = "cancel", Placements = ["menu:sql/cancel@50", "toolbar:main/cancel@240"])]
[OrandaCommand("oranda.exec.bindVariables", CommandCategory.Sql, When = "sqlDocFocus", Enablement = "sqlDocActive",
    Icon = "binds", Placements = ["menu:sql/bindVariables@60"])]
[OrandaCommand("oranda.exec.pasteBindValues", CommandCategory.Sql, When = "sqlDocFocus", Enablement = "sqlDocActive && !mybatisDocActive",
    Placements = ["menu:sql/pasteBindValues@62"])]
[OrandaCommand("oranda.exec.toggleDbmsOutput", CommandCategory.Sql, Enablement = "sqlDocActive",
    Placements = ["menu:sql/toggleDbmsOutput@70"])]
[OrandaCommand("oranda.exec.errorModeAsk", CommandCategory.Sql, Enablement = "sqlDocActive",
    Placements = ["menu:sql/errorMode/ask@80"])]
[OrandaCommand("oranda.exec.errorModeStop", CommandCategory.Sql, Enablement = "sqlDocActive",
    Placements = ["menu:sql/errorMode/stop@82"])]
[OrandaCommand("oranda.exec.errorModeContinue", CommandCategory.Sql, Enablement = "sqlDocActive",
    Placements = ["menu:sql/errorMode/continue@84"])]
[OrandaCommand("oranda.exec.goToError", CommandCategory.Sql, When = "sqlDocFocus", Enablement = "hasExecError",
    Placements = ["menu:sql/goToError@90"])]
[OrandaCommand("oranda.exec.toggleBlankLineDelimiter", CommandCategory.Sql, Enablement = "sqlDocActive",
    Placements = ["menu:sql/toggleBlankLineDelimiter@95"])]
[OrandaCommand("oranda.txn.commit", CommandCategory.Sql, When = "!readOnlySession || txnPending", Enablement = "(hasSession || dedicatedSession) && txnPending && !sessionBusy",
    Icon = "commit", Placements = ["menu:sql/commit@100", "toolbar:main/commit@310"])]
[OrandaCommand("oranda.txn.rollback", CommandCategory.Sql, When = "!readOnlySession || txnPending", Enablement = "(hasSession || dedicatedSession) && txnPending && !sessionBusy",
    Icon = "rollback", Placements = ["menu:sql/rollback@110", "toolbar:main/rollback@320"])]
[OrandaCommand("oranda.txn.toggleAutoCommit", CommandCategory.Sql, Enablement = "(hasSession || dedicatedSession) && !readOnlySession",
    Placements = ["menu:sql/toggleAutoCommit@120"])]
[OrandaCommand("oranda.view.output", CommandCategory.View)]
[OrandaCommand("oranda.view.messages", CommandCategory.View)]
[OrandaCommand("oranda.window.toggleResultPane", CommandCategory.Window, When = "sqlDocFocus", Enablement = "sqlDocActive",
    Placements = ["menu:window/toggleResultPane@40"])]
public sealed class ExecutionCommandHandler(ServiceContainer container) : ICommandHandler
{
    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var c = ExecutionCommands.Resolve(container);
        var state = c.State;

        switch (context.CommandId)
        {
            case "oranda.exec.runStatement":
                await c.RunAsync(RunMode.Statement, ct: cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.exec.runSubquery":
                await c.RunAsync(RunMode.Subquery, ct: cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.exec.runSelection":
                await c.RunAsync(RunMode.Selection, ct: cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.exec.runScript":
                await c.RunAsync(RunMode.Script, ct: cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.exec.runFromCursor":
                await c.RunAsync(RunMode.FromCursor, ct: cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.exec.runStep":
                await c.RunAsync(RunMode.Step, ct: cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.exec.runToNewResultTab":
                await c.RunAsync(RunMode.Statement, newResultTab: true, ct: cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.exec.runAllTabs":
                await c.RunAllTabsAsync(cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.exec.cancel":
                c.Cancel();
                break;
            case "oranda.exec.bindVariables":
                await c.EditBindsAsync(ct: cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.exec.pasteBindValues":
                c.PasteBindValues();
                break;
            case "oranda.exec.toggleDbmsOutput":
                state.DbmsOutputEnabled = !state.DbmsOutputEnabled;
                break;
            case "oranda.exec.errorModeAsk":
                state.ErrorMode = ScriptErrorMode.Ask;
                break;
            case "oranda.exec.errorModeStop":
                state.ErrorMode = ScriptErrorMode.Stop;
                break;
            case "oranda.exec.errorModeContinue":
                state.ErrorMode = ScriptErrorMode.Continue;
                break;
            case "oranda.exec.goToError":
                c.GoToError();
                break;
            case "oranda.exec.toggleBlankLineDelimiter":
                state.BlankLineDelimiter = !state.BlankLineDelimiter;
                break;
            case "oranda.txn.commit":
                await c.CommitAsync(cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.txn.rollback":
                await c.RollbackAsync(cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.txn.toggleAutoCommit":
                c.ToggleAutoCommit();
                break;
            case "oranda.view.output":
                container.GetService<IShellUi>()?.Docking.ShowTool("oranda.panel.output");
                break;
            case "oranda.view.messages":
                container.GetService<IShellUi>()?.Docking.ShowTool("oranda.panel.messages");
                break;
            case "oranda.window.toggleResultPane":
                c.ToggleResultPane();
                break;
        }
    }
}
