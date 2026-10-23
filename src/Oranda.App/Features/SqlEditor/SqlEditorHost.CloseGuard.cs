using Oranda.Core.Workspaces;

namespace Oranda.App.Features.SqlEditor;

// SqlEditorHost 의 일부: 닫기 가드와 닫기·종료 확인(TSK-10-07 에서 나눔, 동작 불변).
public sealed partial class SqlEditorHost
{
    /// <summary>
    /// 닫기 가드를 등록한다. 가드는 등록한 순서대로 불리고, 하나라도 취소하면 닫기가 중단된다(나머지 가드는 부르지 않는다).
    /// 돌려받은 값을 <see cref="IDisposable.Dispose"/> 하면 이 등록만 해제한다. 같은 가드를 두 번 등록하면 두 번 불린다.
    /// 여러 delegate 를 합친 가드는 결과를 하나만 볼 수 있어 받지 않는다(따로 등록한다).
    /// </summary>
    public IDisposable RegisterCloseGuard(SqlCloseGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        if (guard.GetInvocationList().Length > 1)
        {
            throw new ArgumentException("합쳐진 delegate 는 등록할 수 없다. 가드마다 따로 등록한다.", nameof(guard));
        }

        var entry = new CloseGuardEntry(guard);
        _closeGuards.Add(entry);
        return new CloseGuardRegistration(this, entry);
    }

    /// <summary>가드가 확인을 하는 중이면 true. 이때 들어온 닫기 요청은 취소로 처리된다(false).</summary>
    public bool IsCloseGuardRunning => _guardsRunning;

    /// <summary>
    /// 닫아도 되는지 가드에 차례로 묻는다. 모두 동의하면 true. 가드가 이미 도는 중이거나(두 번째 ×, <see cref="IsCloseGuardRunning"/>) 가드가 예외를 던지면 false.
    /// 닫을 탭이 없으면 가드를 부르지 않고 true. 닫기 자체는 하지 않는다: 호출한 쪽이 true 를 받은 뒤 닫는다.
    /// </summary>
    public async Task<bool> CanCloseAsync(IReadOnlyList<SqlEditorViewModel> tabs, SqlCloseReason reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        if (tabs.Count == 0 || _closeGuards.Count == 0)
        {
            return true;
        }

        if (_guardsRunning)
        {
            return false;
        }

        _guardsRunning = true;
        try
        {
            foreach (var entry in _closeGuards.ToList())
            {
                if (!await entry.Guard(tabs, reason, ct).ConfigureAwait(true))
                {
                    return false;
                }
            }

            return true;
        }
#pragma warning disable CA1031 // 가드가 실패하면 글을 잃지 않도록 닫기를 취소한다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            System.Diagnostics.Trace.TraceError("닫기 가드 실패(닫기 취소): {0}", ex);
            return false;
        }
        finally
        {
            _guardsRunning = false;
        }
    }

    /// <summary>탭을 닫는다(가드 이유 <see cref="SqlCloseReason.Tab"/>). 가드가 취소하면 닫지 않고 false.</summary>
    public async Task<bool> CloseAsync(SqlEditorViewModel tab, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (WorkspaceOf(tab) is null)
        {
            return false;
        }

        if (!await CanCloseAsync([tab], SqlCloseReason.Tab, ct).ConfigureAwait(true))
        {
            return false;
        }

        if (WorkspaceOf(tab) is null)
        {
            return false; // 대화상자가 떠 있는 동안 다른 경로로 이미 닫혔다
        }

        Close(tab);
        return true;
    }

    /// <summary>작업 공간을 닫는다(가드 이유 <see cref="SqlCloseReason.Workspace"/>). 가드가 취소하면 닫지 않고 false.</summary>
    public async Task<bool> TryCloseWorkspaceAsync(SqlWorkspaceViewModel workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        // design.md 5.6: 저장 안 한 문장이 있는 매퍼는 탭이 몇 개든(0개여도) 저장 훅이 「저장 / 저장 안 함 / 취소」를 먼저 묻는다.
        if (workspace.MyBatis is { IsDirty: true } && MyBatisHooks.SaveHandler is { } saver
            && !await saver.ConfirmCloseAsync(workspace, ct).ConfigureAwait(true))
        {
            return false;
        }

        if (!await CanCloseAsync(workspace.Tabs.ToList(), SqlCloseReason.Workspace, ct).ConfigureAwait(true))
        {
            return false;
        }

        if (!Workspaces.Contains(workspace))
        {
            return false;
        }

        await CloseWorkspaceAsync(workspace, ct).ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// 작업 공간을 복원한다(<see cref="Restore(Workspace)"/>). 같은 ID 의 작업 공간이 열려 있어 그 탭이 교체되면 가드
    /// (<see cref="SqlCloseReason.Replace"/>)에 먼저 묻고, 취소하면 복원하지 않고 null.
    /// </summary>
    public async Task<SqlWorkspaceViewModel?> RestoreAsync(Workspace workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (Workspaces.FirstOrDefault(w => w.Id == workspace.Id) is { } open)
        {
            var asked = open.Tabs.ToList();
            if (!await CanCloseAsync(asked, SqlCloseReason.Replace, ct).ConfigureAwait(true))
            {
                return null;
            }

            // 확인하는 동안 탭이 바뀌었으면 묻지 않은 탭을 버리게 되므로 복원하지 않는다.
            if (Workspaces.FirstOrDefault(w => w.Id == workspace.Id) is not { } now || !now.Tabs.SequenceEqual(asked))
            {
                return null;
            }
        }

        return Restore(workspace);
    }

    /// <summary>앱을 끝내도 되는지 모든 탭을 두고 가드에 묻는다(<see cref="SqlCloseReason.AppExit"/>). 끝내기 전에 셸이 부른다.</summary>
    public Task<bool> CanShutdownAsync(CancellationToken ct) => CanCloseAsync(Tabs.ToList(), SqlCloseReason.AppExit, ct);

    /// <summary>
    /// 고친 SQL 탭이 닫히기 전에 저장할지 묻는다. 탭 닫기·같은 ID 복원으로 교체될 때만 묻는다:
    /// 작업 공간 닫기와 앱 종료는 작업 공간 파일이 내용을 보존한다. 이름 없는 탭의 저장은 「다른 이름으로」이고, 파일 고르기를 취소하면 닫기도 취소다.
    /// </summary>
    private async Task<bool> ConfirmSaveAsync(IReadOnlyList<SqlEditorViewModel> tabs, SqlCloseReason reason, CancellationToken ct)
    {
        if (reason is not (SqlCloseReason.Tab or SqlCloseReason.Replace))
        {
            return true;
        }

        foreach (var tab in tabs.Where(NeedsSaveConfirm))
        {
            if (!await AskAndSaveAsync(tab, tab.Title, ct).ConfigureAwait(true))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 탭 하나의 저장 확인(저장 / 저장 안 함 / 취소)과 저장. 닫아도 되면 true, 취소하거나 저장하지 못했으면 false.
    /// PL/SQL 편집기 탭의 닫기 확인(TSK-13-27)도 같은 흐름·문구를 쓴다. <paramref name="name"/> 은 대화상자에 보일 이름.
    /// </summary>
    internal async Task<bool> AskAndSaveAsync(SqlEditorViewModel tab, string name, CancellationToken ct)
    {
        switch (await Dialogs.AskSaveChangesAsync(name, ct).ConfigureAwait(true))
        {
            case SaveChoice.Save:
                try
                {
                    return await SaveAsync(tab, ct).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 저장하지 못했으니 닫지 않는다. 아무 말 없이 창만 사라진 것처럼 보이지 않게 알린다.
                    await Dialogs.ShowMessageAsync("저장", $"'{name}' 을(를) 저장하지 못했다: {ex.Message}", ct).ConfigureAwait(true);
                    return false;
                }

            case SaveChoice.DontSave:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 파일에 묶인 탭은 고쳤을 때, 이름 없는 탭은 사용자가 글을 쳤을 때(처음 글 그대로·빈 글이면 묻지 않는다) 묻는다. 읽기 전용 탭은 고칠 수 없으니 묻지 않는다.
    /// 복원 때 파일을 못 읽어 비어 있는 탭도 묻지 않는다: 저장하면 빈 내용이 원본을 덮어쓴다.
    /// </summary>
    private bool NeedsSaveConfirm(SqlEditorViewModel tab)
    {
        if (WorkspaceOf(tab)?.MyBatis is not null)
        {
            return false; // design.md 5.6: MyBatis 문장 탭은 탭 닫기에서 묻지 않는다(고침은 세션에 남고, 작업 공간을 닫을 때 훅이 묻는다)
        }

        if (tab.IsReadOnly)
        {
            return false;
        }

        if (tab.FilePath is null)
        {
            return tab.HasUserEdits;
        }

        return tab.IsDirty && !(tab.FileUnreadable && string.IsNullOrWhiteSpace(tab.Text));
    }

    private sealed class CloseGuardEntry(SqlCloseGuard guard)
    {
        public SqlCloseGuard Guard { get; } = guard;
    }

    private sealed class CloseGuardRegistration(SqlEditorHost host, CloseGuardEntry entry) : IDisposable
    {
        public void Dispose() => host._closeGuards.Remove(entry);
    }

    /// <summary>탭 머리 메뉴 「다른 탭 닫기」/「오른쪽 탭 닫기」: 기준 탭을 뺀 대상 탭들을 가드 확인 뒤 닫는다.</summary>
    public async Task<bool> CloseTabsAsync(SqlEditorViewModel anchor, bool rightOnly, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (WorkspaceOf(anchor) is not { } ws)
        {
            return false;
        }

        var at = ws.Tabs.IndexOf(anchor);
        var targets = ws.Tabs.Where((t, i) => rightOnly ? i > at : i != at).ToList();
        if (targets.Count == 0 || !await CanCloseAsync(targets, SqlCloseReason.Tab, ct).ConfigureAwait(true))
        {
            return false;
        }

        foreach (var t in targets.Where(t => WorkspaceOf(t) is not null))
        {
            Close(t);
        }

        return true;
    }
}
