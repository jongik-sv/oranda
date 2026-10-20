using System.Globalization;
using System.Text;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.History;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Text;

namespace Oranda.App.Features.Execution;

/// <summary>실행 범위(commands.md 「SQL」).</summary>
public enum RunMode
{
    /// <summary>선택이 있으면 선택, 없으면 커서 문장.</summary>
    Statement,

    /// <summary>선택 영역(문장이 여럿이면 스크립트로).</summary>
    Selection,

    /// <summary>문서 전체.</summary>
    Script,

    /// <summary>커서 문장부터 끝까지.</summary>
    FromCursor,

    /// <summary>커서 문장 하나를 실행하고 커서를 다음 문장으로 옮긴다.</summary>
    Step,

    /// <summary>커서(선택)를 품은 가장 안쪽 괄호 하위 쿼리. 하위 쿼리 밖이면 <see cref="Statement"/> 와 같다.</summary>
    Subquery,
}

/// <summary>
/// 컨트롤러가 쓰는 바깥 의존. 서비스는 전부 호출 때 해석한다(<see cref="Func{TResult}"/>) — 편집기 생성과 시작 경로가
/// Oracle·Language 어셈블리를 깨우지 않도록(D-006, 04-02 decisions).
/// </summary>
public sealed class ExecutionDeps
{
    public required ExecutionState State { get; init; }

    public required ExecutionLog Log { get; init; }

    public required IExecutionDialogs Dialogs { get; init; }

    /// <summary>공유 작업 세션(접속 관리자의 활성 세션). 전용 세션 탭은 <see cref="TabSession"/> 이 먼저다.</summary>
    public required Func<ISession?> Session { get; init; }

    /// <summary>탭의 세션(전용 세션 D-017·탭에 묶인 접속 CONN-06). null 이고 <see cref="MissingConnection"/> 도 null 이면 공유 작업 세션을 쓴다.</summary>
    public Func<SqlEditorViewModel, ISession?> TabSession { get; init; } = _ => null;

    /// <summary>탭에 묶인 접속이 열려 있지 않으면 그 프로필 ID(접속 안 됨: 공유 작업 세션으로 대신하지 않는다).</summary>
    public Func<SqlEditorViewModel, Guid?> MissingConnection { get; init; } = _ => null;

    /// <summary>열린 작업 세션(접속, 연 순서). 앱 종료 미커밋 확인이 접속마다 묻는다. null 이면 <see cref="Session"/> 하나.</summary>
    public Func<IReadOnlyList<ISession>>? WorkSessions { get; init; }

    /// <summary>대화상자에 보일 접속 이름(「번호 - 사용자@이름」).</summary>
    public Func<ISession, string> ConnectionLabel { get; init; } = s => $"{s.Profile.User}@{s.Profile.Name}";

    /// <summary>프로필 이름(접속 안 됨 탭 묻기, 저장된 프로필을 아직 안 읽었으면 읽는다). 모르면 null.</summary>
    public Func<Guid, Task<string?>> ProfileName { get; init; } = _ => Task.FromResult<string?>(null);

    /// <summary>프로필로 다시 접속한다(비밀번호를 물을 수 있다). 연 세션, 못 열었거나 사용자가 취소하면 null.</summary>
    public Func<Guid, CancellationToken, Task<ISession?>> Reconnect { get; init; } = (_, _) => Task.FromResult<ISession?>(null);

    public required Func<IExecutionService> Execution { get; init; }

    public required Func<ILanguageService> Language { get; init; }

    public Func<SqlEditorHost> Host { get; init; } = () => SqlEditorHost.Current;

    public Func<IRowEditor?> RowEditor { get; init; } = () => null;

    public Func<IMetadataProvider?> Metadata { get; init; } = () => null;

    /// <summary>도구 창 탭을 앞으로 가져온다(패널 ID). 실행하면 「조회 결과」 창을 보인다.</summary>
    public Action<string> ActivatePanel { get; init; } = _ => { };

    /// <summary>도구 창 보이기/숨기기(패널 ID).</summary>
    public Action<string> TogglePanel { get; init; } = _ => { };

    /// <summary>바인드 변수 목록(「바인드 변수」 창과 같은 것). 기본값은 버스 없는 공용 목록.</summary>
    public BindVariableStore Binds { get; init; } = BindVariableStore.For(null);
}

/// <summary>
/// SQL 실행 화면의 중심(TSK-06-02). 편집기에서 실행 범위를 정하고(문장 나누기는 <see cref="ILanguageService"/>),
/// 바인드·치환 변수를 받고, <see cref="IExecutionService"/> 로 실행하고, 결과를 결과 영역(<see cref="ResultTabsViewModel"/> →
/// <c>SqlResultPaneViewModel</c>)·메시지 창·상태에 반영한다. DB 호출은 항상 스레드 풀에서 한다(UI 스레드 금지).
/// </summary>
public sealed class ExecutionController : IDisposable
{
    private readonly ExecutionDeps _d;
    private readonly Dictionary<Guid, CancellationTokenSource> _runs = []; // 세션 ID → 실행 중 취소 토큰(전용 세션 탭은 따로 돈다)
    private readonly HashSet<Guid> _lastCancelled = [];
    /// <summary>결과·오류를 보이는 아래 도구 창(<see cref="ResultsPanelViewModel"/>).</summary>
    public const string ResultsPanelId = "oranda.panel.results";

    /// <summary>바인드 변수 창(<see cref="BindVariablesViewModel"/>).</summary>
    public const string BindsPanelId = "oranda.panel.binds";

    public ExecutionController(ExecutionDeps deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        _d = deps;

        // 그리드 편집(TSK-06-04)이 비워 둔 연결 지점을 채운다.
        GridEditCommands.Rerun = RerunForEditAsync;
        GridEditCommands.ConfirmPreview = (text, commit, ct) => _d.Dialogs.PreviewDmlAsync(text, commit, ct);
        GridEditCommands.Report = ReportGridApply;
    }

    public ExecutionState State => _d.State;

    public ExecutionLog Log => _d.Log;

    private SqlEditorHost Host => _d.Host();

    /// <summary>
    /// 탭이 쓰는 세션: 탭의 세션(전용 세션·묶인 접속)이 있으면 그것, 없으면 공유 작업 세션. 묶인 접속이 열려 있지 않은 탭은 null(접속 안 됨).
    /// 탭이 null 이면 공유 작업 세션.
    /// </summary>
    public ISession? SessionFor(SqlEditorViewModel? tab) =>
        tab is null ? _d.Session() : _d.TabSession(tab) ?? (_d.MissingConnection(tab) is null ? _d.Session() : null);

    /// <summary>
    /// 지금 실행 중인 문장의 세션 프로필(실행 흐름마다). 실행 확인 대화상자의 머리 띠가 활성 탭이 아니라 실행하는 세션의 접속을 보이게 한다.
    /// </summary>
    public static ConnectionProfile? RunningProfile => s_runningProfile.Value;

    private static readonly AsyncLocal<ConnectionProfile?> s_runningProfile = new();

    /// <summary>
    /// 상태 표시줄이 보여 주는 세션(<see cref="ExecutionState.FocusedDedicated"/>)과 같은 세션: SQL 작업 공간이 아닌 문서가 활성이면
    /// 활성 SQL 탭이 전용 세션 탭이어도 공유 작업 세션이다. 탭을 고르지 않는 커밋·롤백·취소 명령이 쓴다.
    /// </summary>
    private ISession? FocusedSession()
    {
        if (State.FocusedNothing)
        {
            return null; // 활성 SQL 탭의 접속이 닫혔다: 다른 접속을 커밋하지 않는다
        }

        // 보이는 세션이 활성 탭의 세션일 때만 그것(SQL 작업 공간이 아닌 문서가 활성이면 보이는 세션은 지금 접속이다).
        var tab = SessionFor(Host.Active);
        return tab is not null && State.FocusedSessionId == tab.Id ? tab : _d.Session();
    }

    // ---- 실행 ------------------------------------------------------------------------------

    /// <summary>편집기 <paramref name="tab"/>(없으면 활성 탭)에서 <paramref name="mode"/> 범위를 실행한다.</summary>
    public async Task<bool> RunAsync(RunMode mode, bool newResultTab = false, SqlEditorViewModel? tab = null, CancellationToken ct = default)
    {
        tab ??= Host.Active;
        if (tab is null || !await EnsureConnectionAsync(tab, ct).ConfigureAwait(true) || IsBusy(SessionFor(tab)))
        {
            return false;
        }

        var (stmts, script) = SelectStatements(tab, mode);
        if (stmts.Count == 0)
        {
            Log.AddMessage(MessageKind.Warning, "실행할 문장이 없습니다.");
            return false;
        }

        var ok = stmts.Count == 1 && !script
            ? await RunOneAsync(tab, stmts[0], newResultTab, ct).ConfigureAwait(true)
            : await RunScriptAsync(tab, stmts, newResultTab, ct).ConfigureAwait(true);

        if (mode == RunMode.Step && ok)
        {
            MoveToNextStatement(tab, stmts[0]);
        }

        return ok;
    }

    /// <summary>
    /// 모든 탭을 차례로 스크립트로 실행한다. 취소하면 거기서 멈춘다. 실행하는 탭을 활성으로 바꾼다: 탭마다 세션이 다를 수 있어
    /// 취소 명령·상태 표시줄이 지금 실행 중인 탭의 세션을 봐야 한다(D-017).
    /// </summary>
    public async Task RunAllTabsAsync(CancellationToken ct = default)
    {
        var start = Host.Active;
        try
        {
            foreach (var tab in Host.Tabs.ToList())
            {
                if (Host.WorkspaceOf(tab) is null)
                {
                    continue; // 앞 탭을 실행하는 동안 닫혔다
                }

                Host.Active = tab;
                if (!await EnsureConnectionAsync(tab, ct).ConfigureAwait(true))
                {
                    break; // 접속 안 됨 탭에서 취소했다: 남은 탭도 돌리지 않는다
                }

                var id = SessionFor(tab)?.Id ?? Guid.Empty;
                _lastCancelled.Remove(id);
                await RunAsync(RunMode.Script, newResultTab: false, tab, ct).ConfigureAwait(true);
                if (_lastCancelled.Contains(id))
                {
                    break;
                }
            }
        }
        finally
        {
            if (start is not null && Host.WorkspaceOf(start) is not null)
            {
                Host.Active = start; // 시작할 때의 탭으로 돌아간다
            }
        }
    }

    /// <summary>
    /// 실행 취소. 서버에서 중단하고(엔진이 <c>OracleCommand.Cancel</c>) 상태는 「취소 중」으로 바뀐다.
    /// 끊는 것은 활성 탭 세션의 실행이다(다른 전용 세션 탭의 실행은 그대로 둔다).
    /// </summary>
    public void Cancel()
    {
        if (FocusedSession() is { } session
            && _runs.TryGetValue(session.Id, out var cts) && !cts.IsCancellationRequested)
        {
            State.MarkCancelling(session.Id);
            Log.AddMessage(MessageKind.Info, "실행 취소를 요청했습니다.");
            cts.Cancel();
        }
    }

    private bool IsBusy(ISession? session) => session is null ? State.Busy : State.IsBusy(session.Id);

    private ISession? Usable(ISession? session) =>
        session is null || session.State is SessionState.Broken or SessionState.Closed || State.IsBroken(session.Id) ? null : session;

    /// <summary>
    /// 탭에 묶인 접속이 열려 있지 않으면 묻는다(CONN-06): 다시 접속 / 지금 접속으로 바꾸기 / 취소. 다른 접속으로 몰래 실행하지 않는다.
    /// true = 이제 탭에 접속이 있다(묶인 접속이 열려 있거나 묶이지 않았다).
    /// </summary>
    public async Task<bool> EnsureConnectionAsync(SqlEditorViewModel tab, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (_d.MissingConnection(tab) is not { } profileId)
        {
            return true;
        }

        var name = await _d.ProfileName(profileId).ConfigureAwait(true) ?? "?";
        var current = Usable(_d.Session()); // 끊긴 지금 접속으로는 옮기지 않는다
        var choice = await _d.Dialogs.AskMissingConnectionAsync(name, current is null ? null : _d.ConnectionLabel(current), ct).ConfigureAwait(true);
        switch (choice)
        {
            case MissingConnectionChoice.Reconnect:
                if (await _d.Reconnect(profileId, ct).ConfigureAwait(true) is null)
                {
                    Log.AddMessage(MessageKind.Warning, $"「{name}」 에 다시 접속하지 않았습니다.");
                    return false;
                }

                break;
            case MissingConnectionChoice.UseCurrent when current is not null:
                if (!ReferenceEquals(Usable(_d.Session()), current))
                {
                    Log.AddMessage(MessageKind.Warning, "묻는 사이에 지금 접속이 바뀌었거나 끊겼습니다. 다시 실행하세요.");
                    return false;
                }

                tab.ConnectionProfileId = current.Profile.Id;
                break;
            default:
                return false;
        }

        if (Host.WorkspaceOf(tab) is null || _d.MissingConnection(tab) is not null)
        {
            return false; // 접속하는 사이에 탭을 닫았거나 다른 접속으로 옮겼다
        }

        if (IsBusy(SessionFor(tab)))
        {
            Log.AddMessage(MessageKind.Warning, "그 접속은 지금 실행 중입니다. 실행이 끝난 뒤 다시 실행하세요.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 결과를 끝까지 가져오는 긴 작업(「모두 가져오기」)을 실행 상태로 돌린다. 그동안 취소 명령이 켜지고, 누르면 페이지 가져오기를
    /// 서버에서 끊는다(EXEC-02, TSK-06-11). 이미 실행 중이거나 접속이 없으면 상태 없이 그냥 돌린다.
    /// </summary>
    public async Task FetchAsync(Func<CancellationToken, Task> fetch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        if (SessionFor(Host.Active) is not { } session || State.IsBusy(session.Id))
        {
            await fetch(ct).ConfigureAwait(true);
            return;
        }

        await GuardedAsync(session, fetch, ct).ConfigureAwait(true);
    }

    /// <summary>F9 · 「ROWID 추가 후 다시 실행」: 한 문장을 실행하고 결과를 편집 모드로 연다. <paramref name="query"/> 가 null 이면 커서 문장.</summary>
    public async Task RerunForEditAsync(SqlStatement? query, CancellationToken ct)
    {
        var tab = Host.Active;
        if (tab is null || !await EnsureConnectionAsync(tab, ct).ConfigureAwait(true) || IsBusy(SessionFor(tab)))
        {
            return;
        }

        var stmt = query ?? tab.GetStatementAtCaret();
        if (stmt is null)
        {
            Log.AddMessage(MessageKind.Warning, "실행할 문장이 없습니다.");
            return;
        }

        if (!await RunOneAsync(tab, stmt, newResultTab: false, ct).ConfigureAwait(true))
        {
            return;
        }

        if (tab.ResultPane is not ResultTabsViewModel tabs
            || tabs.Selected is not { Pane.Model: { } model } item
            || SessionFor(tab) is not { } session
            || _d.RowEditor() is not { } editor
            || _d.Metadata() is not { } metadata)
        {
            return;
        }

        var es = await GridEditSession.OpenAsync(
            editor, metadata, session, stmt, model.Columns,
            GridEditBinding.OriginalValues(model), // 원래 값(WHERE 키·낙관적 잠금): 그리드에 보이는 바꾼 값이 아니다
            () => model.LoadedRowCount, null, ct).ConfigureAwait(true);
        item.EditSession = es;
        item.Title += " (편집)";
        GridEditCommands.Active = es;
        GridEditBinding.Attach(model, es); // 바꾼 값·새 행을 그리드에 그리고 셀 편집기가 세션을 찾는다
        item.Pane.SetEditSession(es); // 편집 툴바(고칠 수 있을 때)나 고칠 수 없는 이유(안내 막대)를 보인다
        if (!es.CanEdit)
        {
            Log.AddMessage(MessageKind.Warning, "결과를 편집할 수 없습니다: " + (es.Capability.Message ?? es.Capability.Reason.ToString()));
        }
    }

    // ---- 트랜잭션 ---------------------------------------------------------------------------

    /// <summary>활성 탭 세션을 커밋한다(<c>oranda.txn.commit</c>).</summary>
    public Task CommitAsync(CancellationToken ct = default) => CommitAsync(FocusedSession(), ct);

    /// <summary>활성 탭 세션을 롤백한다(<c>oranda.txn.rollback</c>).</summary>
    public Task RollbackAsync(CancellationToken ct = default) => RollbackAsync(FocusedSession(), ct);

    public async Task CommitAsync(ISession? s, CancellationToken ct = default)
    {
        if (s is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => s.CommitAsync(ct), CancellationToken.None).ConfigureAwait(true);
            State.ApplyTransaction(s.Id, s.HasPendingTransaction);
            Log.AddMessage(MessageKind.Info, "커밋했습니다.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.AddMessage(MessageKind.Error, "커밋 실패: " + e.Message);
        }
    }

    public async Task RollbackAsync(ISession? s, CancellationToken ct = default)
    {
        if (s is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => s.RollbackAsync(ct), CancellationToken.None).ConfigureAwait(true);
            State.ApplyTransaction(s.Id, s.HasPendingTransaction);
            Log.AddMessage(MessageKind.Info, "롤백했습니다.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.AddMessage(MessageKind.Error, "롤백 실패: " + e.Message);
        }
    }

    /// <summary>자동 커밋을 켜면 이후 성공한 변경 문장 뒤에 바로 커밋한다(화면 수준 동작).</summary>
    public void ToggleAutoCommit() => State.AutoCommit = !State.AutoCommit;

    /// <summary>
    /// 열린 접속(공유 작업 세션)마다 미커밋 트랜잭션이 있으면 커밋/롤백/취소를 묻는다(SAFE: 종료 시 확인, CONN-06). true = 계속 닫아도 된다.
    /// 하나라도 취소하면 거기서 멈춘다. 전용 세션 탭은 SQL 탭 닫기 가드가 묻는다(D-017). 앱 종료 때 주 창(<c>MainWindow.OnClosing</c>)이 부른다.
    /// </summary>
    public async Task<bool> ConfirmCloseAsync(CancellationToken ct = default)
    {
        var sessions = _d.WorkSessions?.Invoke() ?? (_d.Session() is { } only ? [only] : []);
        foreach (var session in sessions.ToList())
        {
            if (sessions.Count > 1 && !State.IsTracked(session.Id) && !session.HasPendingTransaction)
            {
                continue; // 자기 상태가 없는 세션에 지금 접속의 미커밋을 묻지 않는다
            }

            if (!await ConfirmConnectionUncommittedAsync(session, ct).ConfigureAwait(true))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 접속 하나(<paramref name="session"/>)에 미커밋 트랜잭션이 있으면 접속 이름을 밝혀 커밋/롤백/취소를 묻는다(접속 끊기·앱 종료).
    /// true = 이제 미커밋이 없다.
    /// </summary>
    public Task<bool> ConfirmConnectionUncommittedAsync(ISession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return ConfirmUncommittedCoreAsync(session, () => _d.Dialogs.AskConnectionUncommittedAsync(_d.ConnectionLabel(session), ct), ct);
    }

    /// <summary>
    /// <paramref name="session"/> 에 미커밋 트랜잭션이 있으면 커밋/롤백/취소를 묻고 고른 대로 한다. true = 이제 미커밋이 없다(닫아도 된다).
    /// 커밋·롤백이 실패하면(메시지 창에 남는다) 트랜잭션이 남으므로 false. <paramref name="subject"/> = 전용 세션 탭 이름(대화상자 글).
    /// </summary>
    public Task<bool> ConfirmUncommittedAsync(ISession session, string? subject, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return ConfirmUncommittedCoreAsync(
            session,
            () => subject is null ? _d.Dialogs.AskUncommittedAsync(ct) : _d.Dialogs.AskUncommittedAsync(subject, ct),
            ct);
    }

    private async Task<bool> ConfirmUncommittedCoreAsync(ISession session, Func<Task<UncommittedChoice>> ask, CancellationToken ct)
    {
        if (!State.IsTxnPending(session.Id) && !session.HasPendingTransaction)
        {
            return true;
        }

        if (session.State == SessionState.Broken || State.IsBroken(session.Id))
        {
            // 끊긴 세션은 커밋·롤백할 수 없고 서버가 이미 되돌렸다: 묻지 않고 알린 뒤 닫게 둔다(종료·끊기가 막히지 않게).
            State.ApplyTransaction(session.Id, false);
            Log.AddMessage(MessageKind.Warning, $"「{session.Profile.User}@{session.Profile.Name}」 접속이 끊겨 커밋하지 않은 변경은 서버에서 되돌려졌습니다.");
            return true;
        }

        var choice = await ask().ConfigureAwait(true);
        switch (choice)
        {
            case UncommittedChoice.Commit:
                await CommitAsync(session, ct).ConfigureAwait(true);
                return !State.IsTxnPending(session.Id) && !session.HasPendingTransaction;
            case UncommittedChoice.Rollback:
                await RollbackAsync(session, ct).ConfigureAwait(true);
                return !State.IsTxnPending(session.Id) && !session.HasPendingTransaction;
            default:
                return false;
        }
    }

    /// <summary>조회 결과 창 보이기/숨기기(<c>oranda.window.toggleResultPane</c>). 숨겨도 탭의 결과는 남는다.</summary>
    public void ToggleResultPane() => _d.TogglePanel(ResultsPanelId);

    /// <summary>그리드 편집 반영 전 DML 미리보기. 사용자가 확인하면 true.</summary>
    public Task<bool> ConfirmPreviewAsync(string text, bool commit, CancellationToken ct) => _d.Dialogs.PreviewDmlAsync(text, commit, ct);

    /// <summary>그리드 반영 결과(커밋 여부 포함)를 메시지 창에 남긴다. 실패 행이 있으면 경고로 보인다.</summary>
    public void ReportGridApply(string notice)
        => Log.AddMessage(notice.Contains("반영하지 않았습니다", StringComparison.Ordinal) ? MessageKind.Warning : MessageKind.Info, notice);

    // ---- 바인드 ----------------------------------------------------------------------------

    /// <summary>탭의 바인드 목록(탭마다 하나, TSK-04-11). 탭이 없으면 도구 창용 기본 목록.</summary>
    private BindVariableStore BindsOf(SqlEditorViewModel? tab) => tab?.Binds ?? _d.Binds;

    /// <summary>
    /// 바인드 변수 창(<c>oranda.exec.bindVariables</c>): 창을 보이고, 커서 문장의 바인드 이름을 목록에 채워 첫 값 칸으로 간다.
    /// 실행은 하지 않는다(오렌지 Bind Variable List).
    /// </summary>
    public Task<bool> EditBindsAsync(SqlEditorViewModel? tab = null, CancellationToken ct = default)
    {
        tab ??= Host.Active;
        _d.ActivatePanel(BindsPanelId);
        if (tab?.GetStatementAtCaret() is not { BindNames.Count: > 0 } stmt)
        {
            Log.AddMessage(MessageKind.Info, "현재 문장에 바인드 변수가 없습니다. 「바인드 변수」 창에서 직접 더할 수 있습니다.");
            return Task.FromResult(false);
        }

        var binds = BindsOf(tab);
        binds.Ensure(stmt.BindNames);
        binds.RequestFocus(stmt.BindNames[0]);
        return Task.FromResult(true);
    }

    /// <summary>
    /// 실행할 바인드 값을 「바인드 변수」 목록에서 만든다(묻는 창 없음). 처음 보는 이름은 목록에 더하고 실행하지 않는다:
    /// 값 없이 실행하면 INSERT·UPDATE 가 NULL 로 돌 수 있어서다. 형식에 맞지 않는 값도 실행하지 않는다.
    /// </summary>
    private Dictionary<string, BindValue>? ResolveBinds(SqlEditorViewModel tab, IEnumerable<string> names, ResultTabsViewModel? results)
    {
        var store = BindsOf(tab);
        var list = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
        {
            return new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase);
        }

        string? problem = null;
        var added = store.Ensure(list);
        if (added.Count > 0)
        {
            problem = $"바인드 변수 {string.Join(", ", added.Select(n => ":" + n))} 의 값을 「바인드 변수」 창에 넣은 뒤 다시 실행하세요. (빈 값은 NULL)";
            store.RequestFocus(added[0]);
        }
        else if (!store.TryBuild(list, out var values, out var error))
        {
            problem = error ?? "바인드 값을 읽을 수 없습니다.";
        }
        else
        {
            return values;
        }

        _d.ActivatePanel(BindsPanelId);
        Log.AddMessage(added.Count > 0 ? MessageKind.Warning : MessageKind.Error, problem);
        if (results is not null)
        {
            results.Clear();
            results.AddMessage("바인드 변수", problem, isError: added.Count == 0);
        }

        return null;
    }

    /// <summary>
    /// 편집기의 바인드 변수를 목록의 값으로 바꿔 넣는다(오렌지 「Paste Bind Variables Into Editor」, <c>oranda.exec.pasteBindValues</c>).
    /// 렉서의 바인드 토큰만 바꾼다(문자열·주석 안은 그대로). 목록에 없거나 값을 읽을 수 없는 변수는 남긴다. 한 번에 실행 취소된다.
    /// </summary>
    public int PasteBindValues(SqlEditorViewModel? tab = null)
    {
        tab ??= Host.Active;
        if (tab is null || tab.IsReadOnly)
        {
            return 0;
        }

        var text = tab.Text;
        var binds = BindsOf(tab);
        var edits = new List<(int Start, int Length, string Literal)>();
        foreach (var t in Oranda.Language.Lexing.SqlLexer.Tokenize(text))
        {
            if (t.Kind != Oranda.Language.Lexing.TokenKind.Bind || t.Length < 2)
            {
                continue;
            }

            var name = text.Substring(t.Start + 1, t.Length - 1);
            if (binds.Find(name) is { } row && BindVariableStore.ToLiteral(row) is { } literal)
            {
                edits.Add((t.Start, t.Length, literal));
            }
        }

        if (edits.Count == 0)
        {
            Log.AddMessage(MessageKind.Info, "바꿀 바인드 변수가 없습니다(목록에 값이 있는 변수만 바꿉니다).");
            return 0;
        }

        var doc = tab.Document;
        doc.BeginUpdate();
        try
        {
            for (int i = edits.Count - 1; i >= 0; i--)
            {
                doc.Replace(edits[i].Start, edits[i].Length, edits[i].Literal);
            }
        }
        finally
        {
            doc.EndUpdate();
        }

        Log.AddMessage(MessageKind.Info, $"바인드 변수 {edits.Count.ToString(CultureInfo.InvariantCulture)}곳을 값으로 바꿨습니다.");
        return edits.Count;
    }

    // ---- 문장 고르기 -------------------------------------------------------------------------

    private SplitOptions Options => new() { SplitOnBlankLine = State.BlankLineDelimiter };

    /// <summary>실행 범위의 문장 목록. 두 번째 값이 참이면 스크립트 실행(문장이 하나여도 스크립트로 취급하는 경우).</summary>
    public (IReadOnlyList<SqlStatement> Statements, bool Script) SelectStatements(SqlEditorViewModel tab, RunMode mode)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var lang = _d.Language();
        var text = tab.Text;
        if (mode == RunMode.Subquery)
        {
            if (FindSubquery(lang, tab) is { } sub)
            {
                return ([sub], false);
            }

            mode = RunMode.Statement; // 하위 쿼리 밖: 현재 문장 실행과 같다
        }

        switch (mode)
        {
            case RunMode.Script:
                return (NonEmpty(lang.Split(text, Options)), true);

            case RunMode.Selection:
                return (SplitSelection(lang, tab), false);

            case RunMode.Statement when tab.HasSelection:
                return (SplitSelection(lang, tab), false);

            case RunMode.FromCursor:
                {
                    var all = NonEmpty(lang.Split(text, Options));
                    int caret = tab.CaretOffset;
                    int index = -1;
                    for (int i = 0; i < all.Count; i++)
                    {
                        if (all[i].Range.End >= caret)
                        {
                            index = i;
                            break;
                        }
                    }

                    return (index < 0 ? [] : [.. all.Skip(index)], true);
                }

            default:
                {
                    var one = lang.GetStatementAt(text, tab.CaretOffset, Options);
                    return (one is null || string.IsNullOrWhiteSpace(one.Text) ? [] : [one], false);
                }
        }
    }

    private List<SqlStatement> SplitSelection(ILanguageService lang, SqlEditorViewModel tab)
    {
        int start = tab.SelectionStart;
        var sel = tab.Document.GetText(start, tab.SelectionLength);
        var parts = NonEmpty(lang.Split(sel, Options));
        return [.. parts.Select(p => p with { Range = new TextRange(p.Range.Start + start, p.Range.Length) })];
    }

    /// <summary>
    /// 커서(선택)를 품은 가장 안쪽 하위 쿼리를 문서 위치를 가진 문장으로 만든다. 본문을 언어 서비스로 다시 나눠
    /// 분류·바인드 이름이 채워진다(바인드는 보통 실행과 같은 목록 값으로 푼다). 없거나 한 문장이 아니면 null.
    /// </summary>
    private SqlStatement? FindSubquery(ILanguageService lang, SqlEditorViewModel tab)
    {
        var text = tab.Text;
        int from = tab.HasSelection ? tab.SelectionStart : tab.CaretOffset;
        int to = from + tab.SelectionLength;
        if (lang.GetStatementAt(text, from, Options) is not { Range: { Length: > 0 } outer } || outer.End > text.Length)
        {
            return null;
        }

        // 현재 문장 안에서만 찾는다(짝이 어긋난 괄호가 문장을 넘지 않게).
        if (SqlStructure.FindSubqueryAt(text.Substring(outer.Start, outer.Length), from - outer.Start, to - outer.Start) is not { } inner)
        {
            return null;
        }

        int start = outer.Start + inner.Start;
        var parts = NonEmpty(lang.Split(text.Substring(start, inner.Length), Options with { SplitOnBlankLine = false }));
        return parts.Count == 1 ? parts[0] with { Range = new TextRange(parts[0].Range.Start + start, parts[0].Range.Length) } : null;
    }

    private static List<SqlStatement> NonEmpty(IEnumerable<SqlStatement> all) => [.. all.Where(s => !string.IsNullOrWhiteSpace(s.Text))];

    private void MoveToNextStatement(SqlEditorViewModel tab, SqlStatement done)
    {
        // 다음 문장이 있으면 그 시작으로, 없으면 현재 자리.
        var next = NonEmpty(_d.Language().Split(tab.Text, Options)).FirstOrDefault(s => s.Range.Start >= done.Range.End);
        if (next is not null)
        {
            tab.Select(next.Range.Start, 0);
        }
    }

    // ---- 실행 몸통 ---------------------------------------------------------------------------

    private async Task<bool> RunOneAsync(SqlEditorViewModel tab, SqlStatement stmt, bool newResultTab, CancellationToken ct)
    {
        var results = Prepare(tab, out var session);
        if (session is null)
        {
            return false;
        }

        var binds = ResolveBinds(tab, stmt.BindNames, results);
        if (binds is null)
        {
            return false;
        }

        Flash(tab, stmt.Range.Start, stmt.Range.End);
        ExecutionResult? result = null;
        await GuardedAsync(session, async token =>
        {
            var exec = _d.Execution();
            result = await Task.Run(
                () => exec.ExecuteAsync(session, stmt, binds, new ExecutionOptions { Source = HistorySource.Editor }, token),
                CancellationToken.None).ConfigureAwait(true);
            await AfterStatementAsync(session, result, token).ConfigureAwait(true);
        }, ct).ConfigureAwait(true);

        if (result is null)
        {
            return false;
        }

        if (!newResultTab)
        {
            results.Clear();
        }

        Present(tab, results, stmt, result, results.NextOrdinal()); // 고정한 탭(GRID-06)이 남아 있으면 그 번호를 피한다
        var cancelled = result.Status == ExecutionStatus.Cancelled;
        MarkCancelled(session, cancelled);
        return !cancelled;
    }

    private async Task<bool> RunScriptAsync(SqlEditorViewModel tab, IReadOnlyList<SqlStatement> stmts, bool newResultTab, CancellationToken ct)
    {
        var results = Prepare(tab, out var session);
        if (session is null)
        {
            return false;
        }

        var binds = ResolveBinds(tab, stmts.SelectMany(st => st.BindNames), results);
        if (binds is null)
        {
            return false;
        }

        if (!newResultTab)
        {
            results.Clear();
        }

        Flash(tab, stmts[0].Range.Start, stmts[^1].Range.End);
        int ok = 0, failed = 0, done = 0;
        bool cancelled = false;
        var summary = new StringBuilder();
        await GuardedAsync(session, async token =>
        {
            var exec = _d.Execution();
            var options = new ExecutionOptions { Source = HistorySource.Script, Binds = binds };
            var en = exec.ExecuteScriptAsync(session, stmts, State.ErrorMode, options, token).GetAsyncEnumerator(token);
            try
            {
                while (await Task.Run(() => en.MoveNextAsync().AsTask(), CancellationToken.None).ConfigureAwait(true))
                {
                    var r = en.Current;
                    done++;
                    if (r.Status == ExecutionStatus.Succeeded)
                    {
                        ok++;
                    }
                    else if (r.Status == ExecutionStatus.Cancelled)
                    {
                        cancelled = true;
                    }
                    else
                    {
                        failed++;
                    }

                    await AfterStatementAsync(session, r, token).ConfigureAwait(true);
                    summary.AppendLine(CultureInfo.InvariantCulture, $"{StatusMark(r.Status)} {Brief(r.Statement)} — {Describe(r)}");
                    Present(tab, results, r.Statement, r, results.NextOrdinal(), scriptMode: true);
                }
            }
            finally
            {
                await en.DisposeAsync().ConfigureAwait(true);
            }
        }, ct).ConfigureAwait(true);

        var line = $"스크립트: {stmts.Count.ToString(CultureInfo.InvariantCulture)}개 중 {done.ToString(CultureInfo.InvariantCulture)}개 실행, 성공 {ok.ToString(CultureInfo.InvariantCulture)}, 실패 {failed.ToString(CultureInfo.InvariantCulture)}" + (cancelled ? ", 취소됨" : string.Empty);
        Log.AddMessage(failed > 0 ? MessageKind.Warning : MessageKind.Info, line);
        var firstQuery = results.Tabs.FirstOrDefault(t => t.HasPane && !t.IsPinned);
        results.AddMessage("스크립트", line + Environment.NewLine + summary, failed > 0);
        if (firstQuery is not null)
        {
            results.Selected = firstQuery;
        }

        MarkCancelled(session, cancelled);
        return !cancelled;
    }

    private void MarkCancelled(ISession session, bool cancelled)
    {
        if (cancelled)
        {
            _lastCancelled.Add(session.Id);
        }
        else
        {
            _lastCancelled.Remove(session.Id);
        }
    }

    /// <summary>실행하는 범위를 편집기에 잠깐 칠한다. 문서 밖에서 만든 문장(범위 없음)은 건너뛴다.</summary>
    private static void Flash(SqlEditorViewModel tab, int start, int end)
    {
        if (end > start && end <= tab.Document.TextLength)
        {
            tab.FlashRange(start, end - start);
        }
    }

    /// <summary>실행 직전 준비: 세션 확인(탭의 전용 세션 → 공유 작업 세션), 결과 영역을 편집기에 붙임.</summary>
    private ResultTabsViewModel Prepare(SqlEditorViewModel tab, out ISession? session)
    {
        session = SessionFor(tab);
        var results = EnsureResults(tab, session);
        _d.ActivatePanel(ResultsPanelId); // 결과·오류는 아래 「조회 결과」 탭에(포커스는 편집기에 남는다)
        if (session is null)
        {
            const string none = "접속이 없습니다. 접속한 뒤 실행하세요.";
            Log.AddMessage(MessageKind.Warning, none);
            results.Clear();
            results.AddMessage("메시지", none, isError: true);
        }
        else if (State.IsBroken(session.Id))
        {
            var broken = State.IsDedicated(session.Id)
                ? "전용 세션이 끊겼습니다. 이 탭을 닫고 새 전용 세션 탭을 여세요."
                : "세션이 끊겼습니다. 다시 접속한 뒤 실행하세요.";
            Log.AddMessage(MessageKind.Warning, broken);
            results.Clear();
            results.AddMessage("메시지", broken, isError: true);
            session = null;
        }

        return results;
    }

    private static ResultTabsViewModel EnsureResults(SqlEditorViewModel tab, ISession? session)
    {
        if (tab.ResultPane is not ResultTabsViewModel results)
        {
            results = new ResultTabsViewModel(session?.Profile);
            tab.ResultPane = results;
        }
        else
        {
            results.SetProfile(session?.Profile);
        }

        return results;
    }

    /// <summary>실행 상태 표시·취소 토큰을 묶어 본문을 돌린다. 끝나면 상태를 되돌린다.</summary>
    private async Task GuardedAsync(ISession session, Func<CancellationToken, Task> body, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _runs[session.Id] = cts;
        s_runningProfile.Value = session.Profile;
        State.BeginRun(session.Id);
        try
        {
            await body(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Log.AddMessage(MessageKind.Info, "취소되었습니다.");
        }
        catch (Exception e)
        {
            // 엔진 밖에서 터진 예외(끊김 등)는 메시지로 남기고 상태를 풀어 준다.
            Log.AddMessage(MessageKind.Error, "실행 중 오류: " + e.Message);
        }
        finally
        {
            _runs.Remove(session.Id);
            State.ApplyTransaction(session.Id, session.HasPendingTransaction);
            State.EndRun(session.Id);
        }
    }

    /// <summary>자동 커밋이 켜져 있으면 성공한 변경 뒤에 커밋한다.</summary>
    private async Task AfterStatementAsync(ISession session, ExecutionResult r, CancellationToken ct)
    {
        if (State.AutoCommit && r.Status == ExecutionStatus.Succeeded && r.TransactionPending && !session.Profile.ReadOnly)
        {
            await Task.Run(() => session.CommitAsync(ct), CancellationToken.None).ConfigureAwait(true);
        }
    }

    // ---- 결과 반영 ---------------------------------------------------------------------------

    private void Present(SqlEditorViewModel tab, ResultTabsViewModel results, SqlStatement stmt, ExecutionResult r, int ordinal, bool scriptMode = false)
    {
        var title = "결과 " + ordinal.ToString(CultureInfo.InvariantCulture);
        var text = Describe(r);
        switch (r.Status)
        {
            case ExecutionStatus.Succeeded:
                Log.AddMessage(MessageKind.Info, $"{Brief(stmt)} — {text}");

                // SQL*Plus 명령이 낸 글(PROMPT, DEFINE 목록, SPOOL 상태 등)은 메시지 창에 그대로 보인다.
                if (stmt.Kind == StatementKind.SqlPlus)
                {
                    foreach (var line in r.DbmsOutput)
                    {
                        Log.AddMessage(MessageKind.Info, line);
                    }
                }
                if (r.ResultSet is { } rs)
                {
                    results.AddQuery(rs, title, stmt);
                }
                else if (!scriptMode)
                {
                    results.AddMessage("메시지", text, isError: false, stmt);
                }

                foreach (var (name, value) in r.OutValues)
                {
                    if (value is IResultSet cursor)
                    {
                        results.AddQuery(cursor, ":" + name, stmt);
                    }
                    else
                    {
                        Log.AddMessage(MessageKind.Info, $":{name} = {value ?? "NULL"}");
                    }
                }

                break;

            case ExecutionStatus.Failed:
                State.SetError(stmt, r.Error, tab.TabId);
                Log.AddMessage(MessageKind.Error, $"{Brief(stmt)} — {text}");
                if (!scriptMode)
                {
                    results.AddMessage("오류", ErrorText(r), isError: true, stmt);
                }
                else
                {
                    results.AddMessage("오류 " + ordinal.ToString(CultureInfo.InvariantCulture), ErrorText(r), isError: true, stmt);
                }

                break;

            case ExecutionStatus.Denied:
                Log.AddMessage(MessageKind.Warning, $"{Brief(stmt)} — 거부됨: {r.Policy?.Message}");
                results.AddMessage("거부됨", r.Policy?.Message ?? "정책 관문이 거부했습니다.", isError: true, stmt);
                break;

            default:
                Log.AddMessage(MessageKind.Info, $"{Brief(stmt)} — 취소됨");
                if (!scriptMode)
                {
                    results.AddMessage("메시지", "취소되었습니다.", isError: false, stmt);
                }

                break;
        }
    }

    private static string StatusMark(ExecutionStatus s) => s switch
    {
        ExecutionStatus.Succeeded => "[성공]",
        ExecutionStatus.Failed => "[실패]",
        ExecutionStatus.Denied => "[거부]",
        _ => "[취소]",
    };

    private static string Describe(ExecutionResult r)
    {
        var time = r.Elapsed.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "초";
        return r.Status switch
        {
            ExecutionStatus.Succeeded when r.RowsAffected is { } n => $"{n.ToString("N0", CultureInfo.InvariantCulture)}행 처리됨 ({time})",
            ExecutionStatus.Succeeded when r.ResultSet is not null => $"조회 완료 ({time})",
            ExecutionStatus.Succeeded => $"완료 ({time})",
            ExecutionStatus.Failed => DbErrorText.Format(r.Error),
            ExecutionStatus.Denied => "거부됨",
            _ => "취소됨",
        };
    }

    private static string ErrorText(ExecutionResult r)
    {
        var e = r.Error;
        if (e is null)
        {
            return "실행에 실패했습니다.";
        }

        var sb = new StringBuilder();
        sb.AppendLine(DbErrorText.Format(e));
        if (e.Line is { } l && e.Column is { } c)
        {
            sb.Append(CultureInfo.InvariantCulture, $"위치: 문장 안 줄 {l}, 열 {c}").AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string Brief(SqlStatement s)
    {
        var line = s.Text.TrimStart().Split('\n', 2)[0].Trim();
        return line.Length > 60 ? line[..60] + "…" : line;
    }

    // ---- 오류 문장으로 이동 --------------------------------------------------------------------

    /// <summary>마지막 오류가 난 자리로 편집기 커서를 옮긴다(<c>oranda.exec.goToError</c>).</summary>
    public bool GoToError()
    {
        if (State.LastErrorStatement is not { } stmt)
        {
            return false;
        }

        var host = Host;
        var tab = (State.LastErrorTab is { } id ? host.Tabs.FirstOrDefault(t => t.TabId == id) : null) ?? host.Active;
        if (tab is null || stmt.Range.Length == 0)
        {
            return false;
        }

        int offset = Math.Min(stmt.Range.Start + Math.Max(0, State.LastError?.Offset ?? 0), tab.Document.TextLength);
        host.Active = tab;
        tab.Select(offset, 0);
        tab.Surface?.ScrollToLine(tab.PositionOf(offset).Line);
        tab.Surface?.FocusEditor();
        return true;
    }

    public void Dispose()
    {
        foreach (var cts in _runs.Values.ToList())
        {
            cts.Cancel();
        }
    }
}
