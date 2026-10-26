using Oranda.App.Features.Execution;
using Oranda.App.Features.Localization;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.ConnectionManager;

// SessionRouter 의 일부: 작업 공간 → 세션 연결표(TSK-14-01, 같은 DB 여러 세션·워크스페이스별 실행).
// 작업 공간(그 안의 탭)은 프로필마다 세션 하나에 묶인다. 여러 작업 공간이 같은 세션에 묶일 수 있고(다 대 일, 트랜잭션도 함께),
// 「자기 세션」 작업 공간은 첫 실행 때 접속의 세션을 아무도 쓰지 않으면 그것을, 다른 작업 공간이 쓰면 같은 DB 에 세션을 하나 더 연다.
// 묶인 세션이 닫히면 그 작업 공간은 「접속 안 됨」 이 되어 다른 세션으로 몰래 실행하지 않는다.
public sealed partial class SessionRouter
{
    private readonly Dictionary<SqlWorkspaceViewModel, Dictionary<Guid, ISession>> _bound = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SqlWorkspaceViewModel, HashSet<Guid>> _lost = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SqlWorkspaceViewModel, HashSet<Guid>> _onPrimary = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(SqlWorkspaceViewModel Workspace, Guid ProfileId), Task<ISession?>> _opening = [];

    /// <summary>설정 「워크스페이스마다 새 세션」(작업 공간이 고른 값이 없을 때의 기본값). 조립이 설정을 넣는다. 기본 켬.</summary>
    public Func<bool> SessionPerWorkspace { get; set; } = () => true;

    /// <summary>
    /// 세션을 닫기 전 미커밋 확인(세션 → true = 이제 미커밋이 없다). 조립이 <see cref="ExecutionController.ConfirmConnectionUncommittedAsync"/> 를 넣는다.
    /// 없으면 미커밋 세션을 쓰는 작업 공간은 닫지 않는다.
    /// </summary>
    public Func<ISession, CancellationToken, Task<bool>>? ConfirmUncommitted { get; set; }

    /// <summary>
    /// 툴바 콤보의 「새 세션」 줄이 열 DB(TSK-14-03): SQL 작업 공간의 탭(전용 세션 탭 제외)이 활성이고 그 탭의 접속(없으면 지금 접속)이 열려 있으면
    /// 그 접속(기본 세션), 아니면 null(PL/SQL 문서 등은 지금처럼 접속만 고른다).
    /// </summary>
    public ISession? NewSessionTarget =>
        SqlDocumentActive && _host.Active is { } tab && _dedicated?.SessionOf(tab) is null && _host.WorkspaceOf(tab) is not null
        && (tab.ConnectionProfileId ?? Current?.Profile.Id) is { } profileId
            ? _connections.SessionOf(profileId)
            : null;

    /// <summary>활성 SQL 작업 공간(SQL 작업 공간 문서가 활성이 아니면 null).</summary>
    public SqlWorkspaceViewModel? ActiveWorkspace => SqlDocumentActive && _host.Active is { } tab ? _host.WorkspaceOf(tab) : null;

    /// <summary>
    /// 상태 표시줄 2번 구간 앞 세션 범위 글(TSK-14-03, <see cref="ExecutionState.ScopeOf"/>): 같은 DB 에 세션이 여럿일 때, 활성 작업 공간이 쓰는 세션을
    /// 다른 작업 공간도 쓰면 「공유 세션 · 다른 워크스페이스 N개와 함께」, 아니면 「워크스페이스 세션」. 그 DB 의 세션이 하나면 null
    /// (작업 공간이 몇 개든 모두 접속의 세션을 쓰는 WP-14 전과 같다, 설정을 꺼도 같다).
    /// </summary>
    public string? ScopeTextOf(Guid sessionId)
    {
        if (ActiveWorkspace is not { } ws || _connections.AllSessions.FirstOrDefault(s => s.Id == sessionId) is not { } session
            || _connections.SessionsOf(session.Profile.Id).Count < 2)
        {
            return null;
        }

        var others = WorkspacesOn(session).Count(w => !ReferenceEquals(w, ws));
        if (others > 0)
        {
            return UiText.Format("Execution.status.sharedSession", "공유 세션 · 다른 워크스페이스 {0}개와 함께", others);
        }

        return UiText.Get("Execution.status.workspaceSession", "워크스페이스 세션");
    }

    /// <summary>
    /// 실행을 눌렀는데 탭의 세션이 다른 작업 공간의 실행으로 바쁘다(<see cref="ExecutionDeps.SharedBusy"/>): 작업 공간 위에 안내 막대를 띄우고 true.
    /// 「새 세션에서 실행」 = <see cref="NewSessionAsync"/> 뒤 <paramref name="rerun"/>. 바쁜 까닭이 이 작업 공간 자신이거나 모르면 false(지금처럼 조용히 꺼짐).
    /// </summary>
    public bool OfferNewSession(SqlEditorViewModel tab, Func<Task> rerun)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(rerun);
        if (!SessionPerWorkspace() || _dedicated?.SessionOf(tab) is not null || _host.WorkspaceOf(tab) is not { } ws || SessionFor(tab) is not { } session
            || _state.RunOwnerOf(session.Id) is not SqlWorkspaceViewModel owner || ReferenceEquals(owner, ws))
        {
            return false;
        }

        var text = UiText.Format("Execution.busyBar.text",
            "이 세션은 「{0}」 워크스페이스에서 실행 중입니다. 같은 세션이라 끝날 때까지 기다립니다. 바로 실행하려면 새 세션으로 옮기세요. 트랜잭션은 옮겨지지 않습니다.",
            owner.Name);
        ws.BusyNotice = new SessionBusyNotice(session.Id, text, () => RunInNewSessionAsync(ws, tab, rerun), () => ws.BusyNotice = null);
        return true;
    }

    private async Task RunInNewSessionAsync(SqlWorkspaceViewModel ws, SqlEditorViewModel tab, Func<Task> rerun)
    {
        if (!ReferenceEquals(_host.WorkspaceOf(tab), ws))
        {
            ws.BusyNotice = null; // 막대가 떠 있는 사이 그 탭을 닫았다: 다른 탭의 작업 공간을 옮기거나 닫은 탭의 글을 실행하지 않는다
            return;
        }

        if (!ReferenceEquals(_host.Active, tab))
        {
            _host.Active = tab; // 「새 세션」 은 활성 탭의 작업 공간을 옮긴다
        }

        if (await NewSessionAsync().ConfigureAwait(true) is null)
        {
            // 못 열었으면(이유는 메시지 창) 막대를 남긴다: 기다리거나 다시 고를 수 있게.
            ExecutionLog.For(_bus).AddMessage(MessageKind.Warning, UiText.Format("Execution.busyBar.failed", "「{0}」 을(를) 새 세션으로 옮기지 못했습니다.", ws.Name));
            return;
        }

        ws.BusyNotice = null;
        await rerun().ConfigureAwait(true);
    }

    /// <summary>
    /// 함께 쓰는 세션의 실행이 끝났거나 작업 공간이 다른 세션으로 옮겨 갔으면 안내 막대를 닫는다(「기다리기」 를 누르지 않아도).
    /// </summary>
    private void ClearFinishedNotices()
    {
        foreach (var ws in _host.Workspaces)
        {
            if (ws.BusyNotice is { } notice && (!_state.IsBusy(notice.SessionId) || SessionFor(ws.ActiveTab)?.Id != notice.SessionId))
            {
                ws.BusyNotice = null;
            }
        }
    }

    /// <summary>열린 작업 세션 모두(접속마다 그 접속과 같은 DB 에 더 연 세션). 앱 종료 미커밋 확인이 쓴다.</summary>
    public IReadOnlyList<ISession> AllSessions => _connections.AllSessions;

    /// <summary>프로필의 열린 작업 세션(첫째가 접속). 툴바 콤보가 작업 공간에 고를 세션을 보일 때 쓴다.</summary>
    public IReadOnlyList<ISession> SessionsOf(Guid profileId) => _connections.SessionsOf(profileId);

    /// <summary>작업 공간이 쓸 세션 방식: 작업 공간이 고른 값, 없으면 설정.</summary>
    public WorkspaceSessionMode ModeOf(SqlWorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return workspace.SessionMode ?? (SessionPerWorkspace() ? WorkspaceSessionMode.Own : WorkspaceSessionMode.Shared);
    }

    /// <summary>
    /// 작업 공간이 그 프로필에 묶인 세션(아직 실행하지 않았으면 null). 닫힌 세션이면 연결을 풀고 「접속 안 됨」 으로 표시한다.
    /// 다만 접속의 세션에 묶였는데 끊긴 접속을 다시 열어 같은 자리에 새 세션이 왔고 그 세션을 다른 작업 공간이 쓰지 않으면, WP-14 전처럼 새 세션을 따른다.
    /// </summary>
    public ISession? BoundSession(SqlWorkspaceViewModel workspace, Guid profileId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!_bound.TryGetValue(workspace, out var map) || !map.TryGetValue(profileId, out var session))
        {
            return null;
        }

        if (_connections.IsOpen(session))
        {
            return session;
        }

        map.Remove(profileId);
        if (_onPrimary.TryGetValue(workspace, out var primaries) && primaries.Remove(profileId)
            && _connections.SessionOf(profileId) is { } replacement && !UsedByOther(replacement, workspace))
        {
            map[profileId] = replacement; // 다시 연 접속(같은 자리의 새 세션)을 따른다. 처음 연 추가 세션이 올라온 자리면 다른 작업 공간이 쓰므로 따르지 않는다
            primaries.Add(profileId);
            return replacement;
        }

        MarkLost(workspace, profileId);
        return null;
    }

    /// <summary>세션을 쓰는(묶인) 열린 작업 공간들(연 순서).</summary>
    public IReadOnlyList<SqlWorkspaceViewModel> WorkspacesOn(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return [.. _host.Workspaces.Where(w => _bound.TryGetValue(w, out var map) && map.Values.Any(s => ReferenceEquals(s, session)))];
    }

    /// <summary>
    /// 탭이 첫 실행 때 자기 세션을 새로 연다(자기 세션 작업 공간인데 아직 묶이지 않았고 접속의 세션은 다른 작업 공간이 쓴다). 그동안 탭은 접속의
    /// 세션을 읽기용으로 보이지만(<see cref="SessionFor"/>), 실행 상태는 그 세션의 실행에 막히지 않는다(<see cref="ExecutionState.FocusPending"/>).
    /// </summary>
    public bool IsPendingOwn(SqlEditorViewModel? tab) =>
        tab?.ConnectionProfileId is { } profileId && _dedicated?.SessionOf(tab) is null
        && _host.WorkspaceOf(tab) is { } ws && ModeOf(ws) == WorkspaceSessionMode.Own
        && BoundSession(ws, profileId) is null && !IsLost(ws, profileId)
        && _connections.SessionOf(profileId) is { } primary && UsedByOther(primary, ws);

    /// <summary>
    /// 실행하기 전 탭의 작업 공간을 세션에 묶는다(<see cref="ExecutionController.EnsureConnectionAsync"/> 끝에서). 이미 묶였거나 작업 공간 밖 탭·전용 세션 탭이면 그대로 true.
    /// 함께 쓰기면 접속의 세션에, 자기 세션이면 접속의 세션을 아무도 안 쓸 때 그것에, 다른 작업 공간이 쓰면 같은 DB 에 새 세션을 열어 묶는다.
    /// 새 세션을 못 열면 메시지 창에 남기고 false(다른 세션으로 대신 실행하지 않는다).
    /// </summary>
    public async Task<bool> PrepareAsync(SqlEditorViewModel tab, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (_dedicated?.SessionOf(tab) is not null || tab.ConnectionProfileId is not { } profileId || _host.WorkspaceOf(tab) is not { } ws
            || BoundSession(ws, profileId) is not null || IsLost(ws, profileId) || _connections.SessionOf(profileId) is not { } primary)
        {
            return true;
        }

        if (ModeOf(ws) == WorkspaceSessionMode.Shared || !UsedByOther(primary, ws))
        {
            Bind(ws, primary);
            return true;
        }

        var opened = await OpenOnceAsync(ws, profileId, ct).ConfigureAwait(true);
        return opened is not null && _host.WorkspaceOf(tab) is not null && BoundSession(ws, profileId) is not null;
    }

    /// <summary>
    /// 작업 공간의 그 프로필 세션을 한 번만 연다: 여는 사이 같은 작업 공간에서 또 실행하면 같은 열기를 기다린다(세션 둘을 열어 하나가 실행 중인 채
    /// 떨어져 남지 않게).
    /// </summary>
    private Task<ISession?> OpenOnceAsync(SqlWorkspaceViewModel ws, Guid profileId, CancellationToken ct)
    {
        var key = (ws, profileId);
        if (_opening.TryGetValue(key, out var running))
        {
            return running;
        }

        var task = OpenAndForgetAsync(key, ct);
        if (!task.IsCompleted)
        {
            _opening[key] = task; // 바로 끝났으면 넣지 않는다(지우는 쪽이 이미 지나갔다)
        }

        return task;
    }

    private async Task<ISession?> OpenAndForgetAsync((SqlWorkspaceViewModel Workspace, Guid ProfileId) key, CancellationToken ct)
    {
        try
        {
            return await OpenFor(key.Workspace, key.ProfileId, ct).ConfigureAwait(true);
        }
        finally
        {
            _opening.Remove(key);
        }
    }

    /// <summary>
    /// 툴바 콤보의 「새 세션」: 활성 SQL 탭의 작업 공간에 같은 DB 의 새 세션을 열어 묶고 「자기 세션」 으로 둔다. 이전 세션에 미커밋이 있으면
    /// <see cref="ConfirmSwitch"/> 로 묻는다. 연 세션, 못 열었거나 취소했으면 null.
    /// </summary>
    public async Task<ISession?> NewSessionAsync(CancellationToken ct = default)
    {
        if (!SqlDocumentActive || _host.Active is not { } tab || _dedicated?.SessionOf(tab) is not null
            || _host.WorkspaceOf(tab) is not { } ws || (tab.ConnectionProfileId ?? Current?.Profile.Id) is not { } profileId)
        {
            return null;
        }

        if (SessionFor(tab) is { } old && old.Profile.Id == profileId && !await ReleaseForSwitchAsync(tab, old, ct).ConfigureAwait(true))
        {
            return null;
        }

        var opened = await OpenFor(ws, profileId, ct).ConfigureAwait(true);
        if (opened is not null)
        {
            tab.ConnectionProfileId = profileId;
            ws.SessionMode = WorkspaceSessionMode.Own;
        }

        return opened;
    }

    /// <summary>같은 DB 에 세션을 하나 더 열어 작업 공간에 묶는다. 못 열면 메시지 창에 이유를 남기고 null.</summary>
    private async Task<ISession?> OpenFor(SqlWorkspaceViewModel ws, Guid profileId, CancellationToken ct)
    {
        var outcome = await _connections.OpenSessionAsync(profileId, ct).ConfigureAwait(true);
        if (outcome is not { IsConnected: true, Session: { } session })
        {
            ExecutionLog.For(_bus).AddMessage(MessageKind.Warning, $"「{ws.Name}」 의 세션을 열지 못했습니다: {outcome.Message}");
            return null;
        }

        if (!_host.Workspaces.Contains(ws))
        {
            await _connections.DisconnectAsync(session, CancellationToken.None).ConfigureAwait(true); // 여는 사이에 작업 공간을 닫았다
            return null;
        }

        Bind(ws, session);
        ExecutionLog.For(_bus).AddMessage(MessageKind.Info, $"「{ws.Name}」 이(가) 자기 세션({LabelOf(session)})을 씁니다.");
        return session;
    }

    /// <summary>콤보에서 이전 세션을 떠나기 전: 실행 중이면 거절, 미커밋이면 묻는다(그 세션을 이 작업 공간만 쓸 때).</summary>
    private async Task<bool> ReleaseForSwitchAsync(SqlEditorViewModel tab, ISession old, CancellationToken ct)
    {
        if (!IsWorkspaceHeld(tab))
        {
            return true;
        }

        if (_state.IsBusy(old.Id))
        {
            ExecutionLog.For(_bus).AddMessage(MessageKind.Warning, RefusalText(SessionSelect.Busy)!);
            return false;
        }

        return ConfirmSwitch is { } confirm && await confirm(old, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 작업 공간을 세션에 묶는다(그 프로필의 「접속 안 됨」 도 푼다). 이전에 묶였던 더 연 세션은 아무도 안 쓰게 되어도 닫지 않는다:
    /// 콤보로 다시 오갈 수 있게 사용자가 「세션 닫기」·접속 끊기로 닫을 때까지 남긴다(상한 8 이 쌓임을 막는다).
    /// </summary>
    private void Bind(SqlWorkspaceViewModel ws, ISession session)
    {
        if (!_bound.TryGetValue(ws, out var map))
        {
            _bound[ws] = map = [];
        }

        map[session.Profile.Id] = session;
        if (!_onPrimary.TryGetValue(ws, out var primaries))
        {
            _onPrimary[ws] = primaries = [];
        }

        if (ReferenceEquals(_connections.SessionOf(session.Profile.Id), session))
        {
            primaries.Add(session.Profile.Id);
        }
        else
        {
            primaries.Remove(session.Profile.Id);
        }

        if (_lost.TryGetValue(ws, out var lost))
        {
            lost.Remove(session.Profile.Id);
        }

        Refocus();
        RaiseChanged();
    }

    /// <summary>
    /// 콤보에서 고른 세션에 활성 탭의 작업 공간을 묶는다(<see cref="Select"/>·<see cref="SelectAsync"/>). 다른 작업 공간과 함께 쓰거나 접속의 세션이면
    /// 「함께 쓰기」, 아니면 「자기 세션」 으로 기억한다. 세션을 고른 것이 아니라 다른 DB(접속)로 바꾸기만 했으면 묶지 않고 모드도 그대로 둔다
    /// (<see cref="ChoosesSession"/>, 세션은 첫 실행 때 고른다).
    /// </summary>
    private void BindChoice(SqlEditorViewModel tab, ISession session)
    {
        if (_host.WorkspaceOf(tab) is not { } ws || !ChoosesSession(ws, session))
        {
            return;
        }

        var shared = ReferenceEquals(_connections.SessionOf(session.Profile.Id), session) || UsedByOther(session, ws);
        Bind(ws, session);
        ws.SessionMode = shared ? WorkspaceSessionMode.Shared : WorkspaceSessionMode.Own;
    }

    /// <summary>
    /// 콤보에서 고른 것이 작업 공간의 세션 선택인지: 더 연 세션이거나, 작업 공간이 그 프로필에 이미 묶였거나 「접속 안 됨」 이면 true.
    /// 아직 묶이지 않은 프로필의 접속을 고르면 DB 를 바꾸기만 한 것이다(false).
    /// </summary>
    private bool ChoosesSession(SqlWorkspaceViewModel ws, ISession session) =>
        !ReferenceEquals(_connections.SessionOf(session.Profile.Id), session)
        || BoundSession(ws, session.Profile.Id) is not null || IsLost(ws, session.Profile.Id);

    /// <summary>
    /// 콤보로 바꾸면 작업 공간째 다른 세션으로 옮기는 탭이 지금 세션을 놓으면 안 되는지(실행 중이거나 미커밋인 세션을 이 작업 공간 밖의 탭이 쓰지 않는다).
    /// 같은 작업 공간의 다른 탭도 함께 옮기므로 「남는 탭」 으로 세지 않는다. 작업 공간 밖 탭이면 <see cref="IsHeld"/>.
    /// </summary>
    private bool IsWorkspaceHeld(SqlEditorViewModel tab) =>
        _host.WorkspaceOf(tab) is not { } ws
            ? IsHeld(tab)
            : SessionFor(tab) is { } s && (_state.IsBusy(s.Id) || _state.IsTxnPending(s.Id) || s.HasPendingTransaction)
              && !_host.Tabs.Any(t => !ReferenceEquals(_host.WorkspaceOf(t), ws) && _dedicated?.SessionOf(t) is null && ReferenceEquals(SessionFor(t), s));

    /// <summary>콤보에서 <paramref name="session"/> 을 고르면 활성 탭이 지금 세션을 놓으면 안 되는지(작업 공간째 옮기면 <see cref="IsWorkspaceHeld"/>).</summary>
    private bool IsHeldFor(SqlEditorViewModel tab, ISession session) =>
        _host.WorkspaceOf(tab) is { } ws && ChoosesSession(ws, session) ? IsWorkspaceHeld(tab) : IsHeld(tab);

    /// <summary>다른 열린 작업 공간이 이 세션에 묶였는지.</summary>
    private bool UsedByOther(ISession session, SqlWorkspaceViewModel ws) =>
        _bound.Any(kv => !ReferenceEquals(kv.Key, ws) && _host.Workspaces.Contains(kv.Key) && kv.Value.Values.Any(s => ReferenceEquals(s, session)));

    /// <summary>
    /// 작업 공간의 그 프로필 세션이 닫혀 「접속 안 됨」 인지. 그 프로필의 세션이 하나도 열려 있지 않으면 표시를 지운다(그때는 프로필이 닫힌
    /// 보통의 「접속 안 됨」 이고, 다시 접속하면 처음처럼 고른다).
    /// </summary>
    private bool IsLost(SqlWorkspaceViewModel ws, Guid profileId)
    {
        BoundSession(ws, profileId); // 닫힌 세션이면 여기서 표시한다
        if (!_lost.TryGetValue(ws, out var lost) || !lost.Contains(profileId))
        {
            return false;
        }

        if (_connections.SessionOf(profileId) is null)
        {
            lost.Remove(profileId);
            return false;
        }

        return true;
    }

    private void MarkLost(SqlWorkspaceViewModel ws, Guid profileId)
    {
        if (!_lost.TryGetValue(ws, out var lost))
        {
            _lost[ws] = lost = [];
        }

        lost.Add(profileId);
    }

    /// <summary>
    /// 「접속 안 됨」 작업 공간의 「다시 접속」·「지금 접속으로」: 그 프로필의 접속이 열려 있으면 표시만 지운다(다음 실행 때 다시 고른다). 지웠으면 true.
    /// </summary>
    private bool ClearLost(SqlEditorViewModel? tab, Guid profileId)
    {
        if (tab is null || _host.WorkspaceOf(tab) is not { } ws || !_lost.TryGetValue(ws, out var lost) || !lost.Remove(profileId))
        {
            return false;
        }

        Refocus();
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// 「접속 안 됨」 탭에서 「지금 접속으로 바꾸기」 를 골랐다(<see cref="ExecutionDeps.UseConnection"/>): 탭을 그 접속에 두고 작업 공간의 그 프로필
    /// 「접속 안 됨」 을 지운다. 세션은 실행할 때(<see cref="PrepareAsync"/>) 고른다.
    /// </summary>
    public void UseConnection(SqlEditorViewModel tab, ISession session)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(session);
        tab.ConnectionProfileId = session.Profile.Id;
        ClearLost(tab, session.Profile.Id);
    }

    /// <summary>
    /// 닫기 가드(<see cref="SqlEditorHost.RegisterCloseGuard"/>): 작업 공간을 닫으면 그 작업 공간만 쓰던 더 연 세션도 닫으므로, 실행 중이면 알리고 취소,
    /// 미커밋이면 세션마다 한 번 묻는다. 접속의 세션·다른 작업 공간과 함께 쓰는 세션은 닫지 않으므로 묻지 않는다.
    /// </summary>
    private async Task<bool> GuardAsync(IReadOnlyList<SqlEditorViewModel> tabs, SqlCloseReason reason, CancellationToken ct)
    {
        if (reason != SqlCloseReason.Workspace)
        {
            return true;
        }

        var closing = tabs.Select(_host.WorkspaceOf).OfType<SqlWorkspaceViewModel>().Distinct().ToList();
        foreach (var ws in closing)
        {
            if (!await ConfirmWorkspaceCloseAsync(ws, ct).ConfigureAwait(true))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 작업 공간 하나를 닫기 전 확인: 함께 닫을 세션(<see cref="OwnedExtras"/>)이 실행 중이면 알리고 false, 미커밋이면 세션마다 한 번 묻는다.
    /// 탭이 없는 작업 공간은 탭 가드가 불리지 않으므로 호스트가 이것을 바로 부른다(<see cref="SqlEditorHost.EmptyWorkspaceCloseGuard"/>, TSK-14-02).
    /// </summary>
    private async Task<bool> ConfirmWorkspaceCloseAsync(SqlWorkspaceViewModel ws, CancellationToken ct)
    {
        foreach (var session in OwnedExtras(ws))
        {
            if (_state.IsRunning(session.Id))
            {
                await _host.Dialogs.ShowMessageAsync(
                    "작업 공간 닫기", $"「{ws.Name}」 의 세션이 실행 중입니다. 실행을 취소하거나 끝난 뒤 닫으세요.", ct).ConfigureAwait(true);
                return false;
            }

            if ((_state.IsTxnPending(session.Id) || session.HasPendingTransaction)
                && (ConfirmUncommitted is not { } confirm || !await confirm(session, ct).ConfigureAwait(true)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 문서 탭 × 로 SQL 작업 공간을 닫기 전 확인(<see cref="Shell.Docking.DockWorkspace.CloseConfirm"/>): 함께 닫을 세션이 실행 중이거나 미커밋이면
    /// SQL 탭 닫기 가드(<see cref="SqlCloseReason.Workspace"/>)를 거친다. 아니면 null(묻지 않고 닫는다).
    /// </summary>
    public Func<CancellationToken, Task<bool>>? CloseConfirmFor(Shell.Docking.PanelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Panel is not SqlWorkspaceViewModel ws || !_host.Workspaces.Contains(ws)
            || !OwnedExtras(ws).Any(s => _state.IsRunning(s.Id) || _state.IsTxnPending(s.Id) || s.HasPendingTransaction))
        {
            return null;
        }

        // 탭이 없으면 탭 가드가 불리지 않으므로 작업 공간 단위 확인(호스트의 빈 작업 공간 가드)을 묻는다.
        return ws.Tabs.Count == 0
            ? ct => _host.CanCloseEmptyAsync(ws, ct)
            : ct => _host.CanCloseAsync(ws.Tabs.ToList(), SqlCloseReason.Workspace, ct);
    }

    /// <summary>작업 공간을 닫으면 함께 닫을 세션: 이 작업 공간만 쓰는, 접속이 아닌 열린 세션.</summary>
    private List<ISession> OwnedExtras(SqlWorkspaceViewModel ws) =>
        _bound.TryGetValue(ws, out var map)
            ? [.. map.Values.Distinct().Where(s => _connections.IsOpen(s) && !ReferenceEquals(_connections.SessionOf(s.Profile.Id), s) && !UsedByOther(s, ws))]
            : [];

    /// <summary>작업 공간이 닫혔다: 연결을 풀고, 이 작업 공간만 쓰던 더 연 세션을 닫는다(미커밋 확인은 가드가 먼저 했다).</summary>
    private void OnWorkspaceClosed(SqlWorkspaceViewModel ws)
    {
        var owned = OwnedExtras(ws);
        _bound.Remove(ws);
        _lost.Remove(ws);
        _onPrimary.Remove(ws);
        foreach (var session in owned)
        {
            _ = _connections.DisconnectAsync(session, CancellationToken.None);
        }

        Refocus(); // 닫은 작업 공간이 쓰던 접속의 세션이 비면 기다리던 작업 공간이 그것을 차지한다(「첫 실행 때 새 세션」 이 아니다)
        RaiseChanged();
    }
}
