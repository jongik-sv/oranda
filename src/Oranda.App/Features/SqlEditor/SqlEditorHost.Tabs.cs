using System.Globalization;
using Oranda.Core.Workspaces;

namespace Oranda.App.Features.SqlEditor;

// SqlEditorHost 의 일부: 탭 추가·복제·닫기·다시 열기·이름·이동(TSK-10-07 에서 나눔, 동작 불변).
public sealed partial class SqlEditorHost
{
    /// <summary>보고 있는 작업 공간에 빈 SQL 탭을 더한다(없으면 기본 작업 공간을 만든다).</summary>
    public SqlEditorViewModel NewDocument(string? text = null)
    {
        var ws = EnsureSqlWorkspace();
        var vm = new SqlEditorViewModel(Guid.NewGuid(), text ?? string.Empty) { Title = NextUntitledName(ws) };
        vm.IsDirty = false;
        AddTab(ws, vm);
        return vm;
    }

    /// <summary>
    /// 만든 글(AI 인덱스 문안 등)을 새 SQL 탭에 담아 연다. 실행은 하지 않으며 사용자가 직접 실행한다.
    /// 저장하지 않은 글이므로 닫을 때 저장 여부를 묻도록 변경됨으로 표시한다.
    /// </summary>
    public SqlEditorViewModel OpenTextTab(string text, string title)
    {
        var vm = NewDocument(text);
        vm.Title = title;
        vm.IsDirty = true;
        return vm;
    }

    private static string NextUntitledName(SqlWorkspaceViewModel ws)
    {
        for (int n = 1; ; n++)
        {
            var name = "SQL " + n.ToString(CultureInfo.InvariantCulture);
            if (!ws.Tabs.Any(t => t.FilePath is null && t.Title == name))
            {
                return name;
            }
        }
    }

    /// <summary>
    /// 일반 SQL 탭을 넣을 작업 공간(design.md 2.2). 보고 있는 작업 공간이 MyBatis 면 마지막으로 활성이던 일반 작업 공간으로 보내고,
    /// 없으면 새 일반 작업 공간을 연다(<see cref="NewWorkspace"/> 와 같은 이름·ID 규칙이지만 빈 탭을 더하지 않는다).
    /// </summary>
    private SqlWorkspaceViewModel EnsureSqlWorkspace()
    {
        var ws = EnsureWorkspace();
        if (ws.MyBatis is null)
        {
            return ws;
        }

        // 세션이 나중에 붙는 작업 공간도 있으니 활성이던 순서를 거슬러 지금 일반 SQL 인 것을 고른다.
        if (_activationOrder.FirstOrDefault(w => w.MyBatis is null && Workspaces.Contains(w)) is { } last)
        {
            return last;
        }

        var created = OpenWorkspace(NewWorkspaceId(), NextWorkspaceName());
        ActiveWorkspace = created;
        return created;
    }

    /// <summary>MyBatis 문장 탭을 MyBatis 작업 공간에 넣는다(분기 없음, design.md 9장). 03-01 의 열기 훅·「+ 새 문장」이 쓴다.</summary>
    internal void AddMyBatisTab(SqlWorkspaceViewModel ws, SqlEditorViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(ws);
        ArgumentNullException.ThrowIfNull(vm);
        AddTab(ws, vm);
    }

    private void AddTab(SqlWorkspaceViewModel ws, SqlEditorViewModel vm)
    {
        ws.Tabs.Add(vm);
        InsertIntoAllTabs(ws, vm);
        TabOpened?.Invoke(this, vm);
        Active = vm;
    }

    /// <summary>전체 탭 목록에서도 작업 공간 순서를 지킨다(그 작업 공간의 마지막 탭 바로 뒤).</summary>
    private void InsertIntoAllTabs(SqlWorkspaceViewModel ws, SqlEditorViewModel vm)
    {
        var at = 0;
        foreach (var w in Workspaces)
        {
            at += w.Tabs.Count(t => !ReferenceEquals(t, vm));
            if (ReferenceEquals(w, ws))
            {
                break;
            }
        }

        Tabs.Insert(Math.Min(at, Tabs.Count), vm);
    }

    public SqlEditorViewModel Duplicate(SqlEditorViewModel source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var ws = WorkspaceOf(source) is { MyBatis: null } own ? own : EnsureSqlWorkspace(); // MyBatis 문장은 일반 작업 공간으로 복제된다
        var vm = new SqlEditorViewModel(Guid.NewGuid(), source.Text)
        {
            Title = NextUntitledName(ws),
            ConnectionProfileId = source.ConnectionProfileId,
        };
        vm.IsDirty = false;
        vm.Select(source.SelectionStart, source.SelectionLength);
        AddTab(ws, vm);
        return vm;
    }

    /// <summary>탭을 닫는다(저장 확인은 호출한 쪽이 한다). 「닫은 탭 다시 열기」용으로 내용을 기억한다. 작업 공간은 비어도 남는다.</summary>
    public void Close(SqlEditorViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (WorkspaceOf(tab) is not { } ws)
        {
            return;
        }

        if (ws.MyBatis is null) // design.md 9장: MyBatis 문장 탭은 닫은 탭 기록에 넣지 않는다(고침은 세션이 들고 있다)
        {
            _closed.Push(SnapshotForReopen(tab));
            while (_closed.Count > ClosedHistoryLimit)
            {
                // Stack 은 아래를 못 지운다. 한도를 넘으면 다시 쌓는다(드문 경로).
                var keep = _closed.Take(ClosedHistoryLimit).Reverse().ToList();
                _closed.Clear();
                foreach (var t in keep)
                {
                    _closed.Push(t);
                }
            }
        }

        var index = ws.Tabs.IndexOf(tab);
        var wasActive = ReferenceEquals(ws.ActiveTab, tab); // 탭 줄(ListBox)이 항목을 빼는 즉시 선택을 null 로 되돌려 ActiveTab 을 먼저 비운다
        ws.Tabs.RemoveAt(index);
        Tabs.Remove(tab);
        if (wasActive || ReferenceEquals(ws.ActiveTab, tab))
        {
            // 오른쪽 이웃, 없으면 왼쪽 이웃(SQL Developer·VS Code 와 같다).
            ws.ActiveTab = ws.Tabs.Count == 0 ? null : ws.Tabs[Math.Min(index, ws.Tabs.Count - 1)];
        }

        if (ReferenceEquals(_active, tab) || (wasActive && ReferenceEquals(_activeWorkspace, ws)))
        {
            SetActive(ReferenceEquals(_activeWorkspace, ws) ? ws.ActiveTab : null);
        }

        TabClosed?.Invoke(this, tab);
    }

    private static WorkspaceTab SnapshotForReopen(SqlEditorViewModel tab)
    {
        // 다시 열 때 파일을 새로 읽지 않고 닫을 때의 내용을 쓰도록 항상 내용을 담는다.
        return tab.ToWorkspaceTab() with { Content = tab.Text };
    }

    public bool CanReopenClosed => _closed.Count > 0;

    public SqlEditorViewModel? ReopenClosed()
    {
        if (_closed.Count == 0)
        {
            return null;
        }

        var tab = _closed.Pop();
        var vm = SqlEditorViewModel.FromWorkspaceTab(tab, null);
        AddTab(EnsureSqlWorkspace(), vm);
        return vm;
    }

    /// <summary>탭 이름 바꾸기(Orange 의 Change Tab Name). 파일 탭은 화면 이름만 바뀐다.</summary>
    public async Task<bool> RenameTabAsync(SqlEditorViewModel tab, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var name = await Dialogs.PromptAsync("탭 이름 바꾸기", tab.Title, ct).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        tab.Title = name.Trim();
        return true;
    }

    /// <summary>같은 작업 공간 안에서 탭을 <paramref name="newIndex"/> 자리로 옮긴다(끌어서 순서 바꾸기). 활성 탭은 그대로 유지한다.</summary>
    public bool MoveTab(SqlEditorViewModel tab, int newIndex)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (WorkspaceOf(tab) is not { } ws)
        {
            return false;
        }

        var oldIndex = ws.Tabs.IndexOf(tab);
        newIndex = Math.Clamp(newIndex, 0, ws.Tabs.Count - 1);
        if (oldIndex == newIndex)
        {
            return false;
        }

        var active = ws.ActiveTab;
        ws.Tabs.Move(oldIndex, newIndex);
        Tabs.Remove(tab);
        InsertIntoAllTabs(ws, tab);
        if (!ReferenceEquals(ws.ActiveTab, active))
        {
            ws.ActiveTab = active; // 목록 이동이 선택을 풀었으면 되돌린다
        }

        return true;
    }
}
