using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.PlsqlWorkspace;

/// <summary>
/// PL/SQL 탭을 어느 PL/SQL 작업 공간에 넣을지 정한다(결정 59): 가까운 작업 공간(스펙/바디 전환·탭 줄의 +) → 활성 문서인 작업 공간 →
/// 마지막으로 활성이던 작업 공간 → 새로 만든다. 열린 객체는 모든 작업 공간에서 찾는다. 도킹 하나에 하나씩 둔다.
/// 활성 문서가 바뀌면 <see cref="PlsqlEditorViewModel.Active"/> 를 보이는 작업 공간의 활성 탭으로 맞추고, PL/SQL 이 아닌 문서면 비운다(문맥 키도 꺼진다).
/// </summary>
public sealed class PlsqlWorkspaceRouter
{
    public const string PanelId = "oranda.panel.plsqlWorkspace";

    private static readonly ConditionalWeakTable<DockWorkspace, PlsqlWorkspaceRouter> Routers = new();

    private readonly DockWorkspace _docking;
    private readonly IServiceProvider? _services;

    /// <summary>활성이 된 차례(앞이 최근). 닫힌 작업 공간은 뺀다.</summary>
    private readonly List<PlsqlWorkspaceViewModel> _recent = [];

    private readonly HashSet<PlsqlWorkspaceViewModel> _attached = [];

    private PlsqlWorkspaceRouter(DockWorkspace docking, IServiceProvider? services)
    {
        _docking = docking;
        _services = services;
        docking.ActiveDocumentChanged += (_, _) => FollowActiveDocument();
        docking.DocumentClosed += OnDocumentClosed;
        PlsqlEditorCommandHandler.InstallCloseConfirm(docking);
    }

    /// <summary>이 도킹의 라우터(처음 부를 때 만든다).</summary>
    public static PlsqlWorkspaceRouter For(DockWorkspace docking, IServiceProvider? services = null)
    {
        ArgumentNullException.ThrowIfNull(docking);
        return Routers.GetValue(docking, d => new PlsqlWorkspaceRouter(d, services));
    }

    /// <summary>셸 도킹의 라우터. 셸이 없으면(시험) null.</summary>
    public static PlsqlWorkspaceRouter? Of(IServiceProvider? services) =>
        (services?.GetService(typeof(IShellUi)) as IShellUi)?.Docking is { } docking ? For(docking, services) : null;

    /// <summary>열린 PL/SQL 작업 공간(문서 영역 순서).</summary>
    public IReadOnlyList<PlsqlWorkspaceViewModel> Workspaces =>
        _docking.Documents.Select(d => d.Panel).OfType<PlsqlWorkspaceViewModel>().ToList();

    /// <summary>마지막으로 활성이던 열린 작업 공간(저장 때 다음 시작의 활성 작업 공간). 미리보기 전용 작업 공간은 건너뛴다. 없으면 null.</summary>
    public PlsqlWorkspaceViewModel? LastActive => _recent.FirstOrDefault(w => !w.IsTransient && Workspaces.Contains(w));

    public PanelDocument? DocumentOf(PlsqlWorkspaceViewModel workspace) =>
        _docking.Documents.FirstOrDefault(d => ReferenceEquals(d.Panel, workspace));

    /// <summary>탭이 든 작업 공간. 없으면 null.</summary>
    public PlsqlWorkspaceViewModel? WorkspaceOf(PlsqlEditorViewModel tab) => Workspaces.FirstOrDefault(w => w.Tabs.Contains(tab));

    /// <summary>같은 접속(DbKey)의 같은 객체(소유자·이름·유형)를 보이는 탭. PACKAGE 와 PACKAGE BODY 는 다른 객체다.</summary>
    public (PlsqlWorkspaceViewModel Workspace, PlsqlEditorViewModel Tab)? FindOpen(DbObjectRef target, ISession session)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(session);
        foreach (var w in Workspaces)
        {
            if (w.Tabs.FirstOrDefault(t => Same(t, target, session)) is { } tab)
            {
                return (w, tab);
            }
        }

        return null;
    }

    /// <summary>
    /// 탭을 작업 공간에 넣고 그 문서를 앞으로 낸다. <paramref name="preview"/> 면 미리보기 탭으로 넣고, 다른 작업 공간의 미리보기는 거둔다(미리보기는 하나).
    /// 넣을 작업 공간이 없으면 새로 만든다. 연 문서(작업 공간)를 돌려준다. 문서를 열 수 없으면 null.
    /// </summary>
    public PanelDocument? Place(PlsqlEditorViewModel tab, PlsqlWorkspaceViewModel? near = null, bool preview = false, bool focus = false)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var open = Workspaces;
        var workspace = (near is not null && open.Contains(near) ? near : null)
            ?? (_docking.ActiveDocument?.Panel as PlsqlWorkspaceViewModel)
            ?? _recent.FirstOrDefault(open.Contains)
            ?? (open.Count > 0 ? open[0] : null);
        PanelDocument? doc;
        if (workspace is null)
        {
            (workspace, doc) = Create();
            if (workspace is null || doc is null)
            {
                return null;
            }

            workspace.IsTransient = preview;
        }
        else
        {
            doc = DocumentOf(workspace);
        }

        workspace.Add(tab, preview);
        if (preview)
        {
            RetirePreviews(except: workspace);
        }

        Show(workspace, tab, focus);
        return doc;
    }

    /// <summary>탭을 보이고 그 작업 공간 문서를 앞으로 낸다. <paramref name="focus"/> 면 편집기로 키보드 포커스도 옮긴다.</summary>
    public void Show(PlsqlWorkspaceViewModel workspace, PlsqlEditorViewModel tab, bool focus)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        workspace.ActiveTab = tab;
        if (DocumentOf(workspace) is not { } doc)
        {
            return;
        }

        if (focus)
        {
            _docking.FocusDocument(doc);
        }
        else
        {
            _docking.Activate(doc);
        }

        FollowActiveDocument(); // 이미 활성 문서였으면 활성 문서 이벤트가 오지 않는다
    }

    /// <summary>
    /// 모든 작업 공간의 미리보기 탭을 거둔다(객체 상세 미리보기를 열 때·다른 작업 공간에 미리보기를 넣을 때): 고친 것은 고정, 아니면 뺀다.
    /// 미리보기만 담으려고 만든 작업 공간이 비면 문서도 닫는다.
    /// </summary>
    public void RetirePreviews(PlsqlWorkspaceViewModel? except = null)
    {
        foreach (var w in Workspaces.Where(w => !ReferenceEquals(w, except)))
        {
            if (w.PreviewTab is not { } preview)
            {
                continue;
            }

            if (preview.Editor.IsDirty)
            {
                w.Pin(preview);
                continue;
            }

            w.Remove(preview); // 미리보기 전용 작업 공간이 비면 OnTabClosed 가 문서도 닫는다
        }
    }

    /// <summary>
    /// 탭을 읽는다. 읽는 중에 그 탭이 작업 공간에서 빠지거나(다음 미리보기로 바뀜·닫음) 작업 공간 문서가 닫히면 읽기를 멈춘다 —
    /// 빠르게 훑어볼 때 지나간 객체의 조회가 세션 앞에 줄 서 있지 않고, 늦게 끝난 읽기가 닫힌 탭을 컴파일 오류 창에 올리지 않게.
    /// 멈췄으면 false.
    /// </summary>
    public async Task<bool> LoadAsync(PlsqlEditorViewModel tab, Func<CancellationToken, Task<bool>> load, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(load);
        if (WorkspaceOf(tab) is not { } workspace || DocumentOf(workspace) is not { } doc)
        {
            return false;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        void OnTabClosed(object? sender, PlsqlEditorViewModel closed)
        {
            if (ReferenceEquals(closed, tab))
            {
                cts.Cancel();
            }
        }

        void OnDocumentClosed(object? sender, PanelDocument closed)
        {
            if (ReferenceEquals(closed, doc))
            {
                cts.Cancel();
            }
        }

        workspace.TabClosed += OnTabClosed;
        _docking.DocumentClosed += OnDocumentClosed;
        try
        {
            return await load(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            workspace.TabClosed -= OnTabClosed;
            _docking.DocumentClosed -= OnDocumentClosed;
        }
    }

    private (PlsqlWorkspaceViewModel? Workspace, PanelDocument? Document) Create()
    {
        var workspace = new PlsqlWorkspaceViewModel { Name = NextName() };
        var doc = _docking.OpenDocument(PanelId, workspace, workspace.Name);
        if (doc is null)
        {
            return (null, null);
        }

        Attach(workspace);
        return (workspace, doc);
    }

    /// <summary>열린 작업 공간이 쓰지 않는 가장 작은 번호(「PL/SQL 1」, 「PL/SQL 2」 …).</summary>
    private string NextName()
    {
        var used = Workspaces.Select(w => w.Name).ToHashSet(StringComparer.Ordinal);
        var n = 1;
        while (used.Contains($"PL/SQL {n}"))
        {
            n++;
        }

        return $"PL/SQL {n}";
    }

    private void Attach(PlsqlWorkspaceViewModel workspace)
    {
        if (!_attached.Add(workspace))
        {
            return;
        }

        workspace.PropertyChanged += OnWorkspacePropertyChanged;
        workspace.TabClosed += OnTabClosed;
        workspace.NewTabRequested ??= NewTabAsync;
        workspace.BringToFront ??= BringToFront;
    }

    /// <summary>닫힌 작업 공간의 구독을 푼다(라우터는 도킹과 수명이 같아 붙잡고 있으면 탭·글이 앱 끝까지 남는다).</summary>
    private void Detach(PlsqlWorkspaceViewModel workspace)
    {
        if (!_attached.Remove(workspace))
        {
            return;
        }

        workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        workspace.TabClosed -= OnTabClosed;
        workspace.NewTabRequested = null;
        workspace.BringToFront = null;
    }

    /// <summary>저장 확인을 묻기 전에 그 작업 공간 문서를 앞으로 낸다(보이지 않는 탭을 두고 묻지 않게).</summary>
    private void BringToFront(PlsqlWorkspaceViewModel workspace)
    {
        if (DocumentOf(workspace) is { } doc && !ReferenceEquals(_docking.ActiveDocument, doc))
        {
            _docking.Activate(doc);
        }
    }

    /// <summary>탭 줄의 「+」: 새 PL/SQL 명령을 이 작업 공간 대상으로(명령 인자) 실행한다.</summary>
    private Task NewTabAsync(PlsqlWorkspaceViewModel workspace) =>
        _services?.GetService(typeof(ICommandRegistry)) is ICommandRegistry registry && _services.GetService(typeof(IContextKeys)) is IContextKeys keys
            ? registry.ExecuteAsync("oranda.file.newPlsqlEditor", keys, workspace)
            : Task.CompletedTask;

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlsqlWorkspaceViewModel.ActiveTab) && sender is PlsqlWorkspaceViewModel w
            && ReferenceEquals(_docking.ActiveDocument?.Panel, w))
        {
            Select(w.ActiveTab);
        }
    }

    private void OnTabClosed(object? sender, PlsqlEditorViewModel tab)
    {
        if (ReferenceEquals(PlsqlEditorViewModel.Active, tab))
        {
            PlsqlEditorViewModel.Active = null;
        }

        CompileErrorsViewModel.Retract(tab);

        // 미리보기만 담으려고 만든 작업 공간이 비면(미리보기가 거둬짐·사용자가 닫음) 문서도 닫는다.
        if (sender is PlsqlWorkspaceViewModel { IsTransient: true, IsEmpty: true } w && DocumentOf(w) is { } doc)
        {
            _docking.CloseDocument(doc);
        }
    }

    private void OnDocumentClosed(object? sender, PanelDocument doc)
    {
        if (doc.Panel is not PlsqlWorkspaceViewModel w)
        {
            return;
        }

        _recent.Remove(w);
        foreach (var tab in w.Tabs)
        {
            if (ReferenceEquals(PlsqlEditorViewModel.Active, tab))
            {
                PlsqlEditorViewModel.Active = null;
            }

            CompileErrorsViewModel.Retract(tab);
        }

        Detach(w);
        FollowActiveDocument();
    }

    /// <summary>활성 문서를 따라 활성 탭·문맥 키·컴파일 오류 창을 맞춘다.</summary>
    private void FollowActiveDocument()
    {
        var panel = _docking.ActiveDocument?.Panel;
        if (panel is PlsqlWorkspaceViewModel w)
        {
            Attach(w);
            _recent.Remove(w);
            _recent.Insert(0, w);
        }

        Select(panel switch
        {
            PlsqlWorkspaceViewModel ws => ws.ActiveTab,
            PlsqlEditorViewModel p => p,
            _ => null,
        });
    }

    private static void Select(PlsqlEditorViewModel? tab)
    {
        if (!ReferenceEquals(PlsqlEditorViewModel.Active, tab))
        {
            PlsqlEditorViewModel.Active = tab;
        }

        // 같은 탭을 다시 올리면 창이 고른 줄을 잃으므로 바뀔 때만 올린다.
        if (tab is not null && !ReferenceEquals(CompileErrorsViewModel.LastSource, tab))
        {
            CompileErrorsViewModel.Publish(tab);
        }
    }

    private static bool Same(PlsqlEditorViewModel tab, DbObjectRef target, ISession session) =>
        tab.Target is { } open
        && string.Equals(open.Owner, target.Owner, StringComparison.Ordinal)
        && string.Equals(open.Name, target.Name, StringComparison.Ordinal)
        && string.Equals(open.ObjectType, target.ObjectType, StringComparison.Ordinal)
        && (tab.Session is null || string.Equals(tab.Session.DbKey, session.DbKey, StringComparison.Ordinal));
}
