using System.Globalization;
using System.Text;
using Oranda.App.Features.Binds;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.PlsqlEditor;
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

// ExecutionController 의 일부: 실행·연결 확인(ExecutionController.cs 에서 나눔, 동작 불변).
public sealed partial class ExecutionController
{
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
    /// 그다음 탭의 작업 공간을 세션에 묶는다(<see cref="ExecutionDeps.PrepareSession"/>, TSK-14-01: 자기 세션이면 첫 실행 때 연다).
    /// true = 이제 탭에 접속이 있다(묶인 접속이 열려 있거나 묶이지 않았다).
    /// </summary>
    public async Task<bool> EnsureConnectionAsync(SqlEditorViewModel tab, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (_d.MissingConnection(tab) is not { } profileId)
        {
            return await _d.PrepareSession(tab, ct).ConfigureAwait(true);
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

                _d.UseConnection(tab, current);
                break;
            default:
                return false;
        }

        if (Host.WorkspaceOf(tab) is null || _d.MissingConnection(tab) is not null || !await _d.PrepareSession(tab, ct).ConfigureAwait(true))
        {
            return false; // 접속하는 사이에 탭을 닫았거나 다른 접속으로 옮겼다(또는 작업 공간의 세션을 열지 못했다)
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
}
