using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Core.Events;
using Dock.Model.Mvvm;
using Oranda.App.Shell.Layout;
using Oranda.Core.Shell;
using DockNodeKind = Oranda.App.Shell.Layout.DockNodeKind;
using DockOrientation = Oranda.App.Shell.Layout.DockOrientation;

namespace Oranda.App.Shell.Docking;

/// <summary>
/// 도킹 배치를 Dock.Avalonia 모델로 만들고(<see cref="Apply"/>) 다시 앱 DTO 로 읽어내는(<see cref="Capture"/>) 곳.
/// <c>Dock.*</c> 형식은 이 폴더 밖으로 새지 않는다(ui-shell.md 3장 조건 2). 배치 규칙은 <see cref="LayoutPlanner"/> 가 정한다.
/// 도구 창 보이기·숨기기는 「DTO 로 읽기 → 계획 바꾸기 → 다시 짓기」 한 길로만 한다(결정 가능하고 시험하기 쉽다).
/// </summary>
public sealed class DockWorkspace
{
    /// <summary>문서 영역 도킹 ID.</summary>
    public const string DocumentsId = "oranda.documents";

    private readonly IToolPanelRegistry _registry;
    private readonly Func<ToolPanelDefinition, object> _createViewModel;
    private readonly IShellText _text;
    private readonly Dictionary<string, PanelTool> _tools = new(StringComparer.Ordinal);
    private int _documentSerial;
    private bool _documentsCaptured;

    public DockWorkspace(IToolPanelRegistry registry, Func<ToolPanelDefinition, object> createViewModel, IShellText text)
    {
        _registry = registry;
        _createViewModel = createViewModel;
        _text = text;
        _text.LanguageChanged += (_, _) => Retitle();
        Factory = new Factory
        {
            HideToolsOnClose = true,
            HideDocumentsOnClose = false,
            DefaultHostWindowLocator = () => new FloatHostWindow(),
        };
        Factory.ActiveDockableChanged += (_, _) => ActiveDocumentChanged?.Invoke(this, EventArgs.Empty);
        Factory.DockableClosing += OnDockableClosing;
        Factory.DockableClosed += (_, e) =>
        {
            if (e.Dockable is PanelDocument closed)
            {
                DocumentClosed?.Invoke(this, closed);
            }

            LayoutChanged?.Invoke(this, EventArgs.Empty);
        };
        Factory.DockableMoved += (_, _) => LayoutChanged?.Invoke(this, EventArgs.Empty);
        Factory.DockableDocked += (_, _) => LayoutChanged?.Invoke(this, EventArgs.Empty);
        Factory.DockableUndocked += (_, _) => LayoutChanged?.Invoke(this, EventArgs.Empty);
        Factory.DockableHidden += (_, _) => LayoutChanged?.Invoke(this, EventArgs.Empty);
        Factory.DockableRestored += (_, _) => LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    internal Factory Factory { get; }

    /// <summary>Dock 의 최상위 도킹(보기 쪽이 <c>DockControl.Layout</c> 으로 쓴다). <see cref="Apply"/> 전에는 null.</summary>
    internal IRootDock? Layout { get; private set; }

    /// <summary>배치를 짓거나 다시 지었다.</summary>
    public event EventHandler? LayoutRebuilt;

    /// <summary>사용자가 도구 창을 옮기거나 닫는 등 배치가 바뀌었다(저장 예약용).</summary>
    public event EventHandler? LayoutChanged;

    public event EventHandler? ActiveDocumentChanged;

    /// <summary>
    /// 문서가 하나도 없을 때 문서 영역에 보일 내용(시작 화면). 배치를 다시 지어도 새 문서 영역마다 다시 만든다.
    /// 없으면 Dock 기본 글(「No documents open」).
    /// </summary>
    public Func<object?>? EmptyDocumentContent
    {
        get => _emptyDocumentContent;
        set
        {
            _emptyDocumentContent = value;
            ApplyEmptyContent();
        }
    }

    private Func<object?>? _emptyDocumentContent;

    private void ApplyEmptyContent()
    {
        if (_emptyDocumentContent is null || Layout is null)
        {
            return;
        }

        foreach (var dock in DocumentDocks().OfType<Dock.Model.Mvvm.Controls.DocumentDock>())
        {
            dock.EmptyContent = _emptyDocumentContent();
        }
    }

    /// <summary>
    /// 문서 탭이 닫히기 전 확인(탭의 × 단추·탭 닫기 명령 모두). 문서를 받아 확인이 필요하면 확인 함수(true = 닫아도 된다)를, 필요 없으면 null 을 돌려준다.
    /// 확인이 필요하면 닫기를 일단 막고 확인을 기다린 뒤 다시 닫는다. Dock 의 닫기 이벤트는 동기라서 비동기 대화상자를 기다릴 수 없기 때문이다.
    /// 확인이 필요 없는 문서는 지금처럼 바로 닫힌다(<see cref="DocumentClosed"/> 시점이 바뀌지 않는다).
    /// </summary>
    public Func<PanelDocument, Func<CancellationToken, Task<bool>>?>? CloseConfirm { get; set; }

    private PanelDocument? _confirmedClose;

    private void OnDockableClosing(object? sender, DockableClosingEventArgs e)
    {
        if (e.Dockable is not PanelDocument document || ReferenceEquals(document, _confirmedClose)
            || CloseConfirm?.Invoke(document) is not { } confirm)
        {
            return;
        }

        e.Cancel = true;
        _ = CloseAfterConfirmAsync(document, confirm);
    }

    private async Task CloseAfterConfirmAsync(PanelDocument document, Func<CancellationToken, Task<bool>> confirm)
    {
        bool ok;
        try
        {
            ok = await confirm(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 확인하다 실패하면 닫지 않는다(미커밋 변경을 잃는 쪽이 더 나쁘다).
            System.Diagnostics.Trace.TraceError("문서 닫기 확인 실패(닫기 취소): {0}", ex);
            ok = false;
        }

        if (!ok || !Documents.Contains(document))
        {
            return; // 취소했거나 확인하는 동안 다른 경로로 이미 닫혔다
        }

        _confirmedClose = document;
        try
        {
            CloseDocument(document);
        }
        finally
        {
            _confirmedClose = null;
        }
    }

    /// <summary>문서 탭이 닫혔다(탭의 × 단추·탭 닫기 명령 모두). 문서를 가진 기능이 자기 목록을 맞춘다.</summary>
    public event EventHandler<PanelDocument>? DocumentClosed;

    /// <summary>문서 내용으로 키보드 포커스를 옮겨 달라는 요청(<see cref="FocusDocument"/>). 보기 쪽(<see cref="DockHostView"/>)이 처리한다.</summary>
    public event EventHandler<PanelDocument>? DocumentFocusRequested;

    /// <summary>지금 열린 문서 탭(여러 문서 영역이 있으면 모두).</summary>
    public IReadOnlyList<PanelDocument> Documents =>
        DocumentDocks().SelectMany(d => d.VisibleDockables ?? []).OfType<PanelDocument>().ToList();

    /// <summary>
    /// 지금 보이는 활성 문서. 포커스를 가졌던 문서(<c>Factory.CurrentDockable</c>)가 속한 영역의 보이는 탭을 돌려준다:
    /// 탭 줄에서 다른 탭을 골라도 포커스 기록은 옛 문서에 남을 수 있어, 그대로 쓰면 창이 다시 활성화될 때 엉뚱한 문서가 활성이 된다.
    /// </summary>
    public PanelDocument? ActiveDocument =>
        Layout is null ? null
        : ShownIn(Factory.CurrentDockable as PanelDocument)
            ?? DocumentDocks().Select(d => d.ActiveDockable).OfType<PanelDocument>().FirstOrDefault();

    private static PanelDocument? ShownIn(PanelDocument? focused) =>
        focused?.Owner is IDock { ActiveDockable: PanelDocument shown } ? shown : focused;

    /// <summary>보이는 도구 창 ID(트리에 있는 것).</summary>
    public IReadOnlyList<string> VisibleToolIds =>
        Layout is null ? [] : LayoutPlanner.ToolIds(CaptureRoot());

    /// <summary>만들어진 도구 창 ViewModel 이 있는 ID(지연 생성 확인용).</summary>
    public IReadOnlyCollection<string> CreatedToolIds => _tools.Where(t => t.Value.IsPanelCreated).Select(t => t.Key).ToList();

    /// <summary>마지막으로 적용한 배치의 이주 표지(<see cref="Capture"/> 가 저장 때 그대로 싣는다).</summary>
    private IReadOnlyList<string>? _migrations;

    /// <summary>저장된 배치를 현재 등록 목록에 맞춰 도킹 모델로 짓는다. 열려 있던 문서는 새 문서 영역으로 옮긴다.</summary>
    public void Apply(LayoutDocument layout)
    {
        var planned = LayoutPlanner.Reconcile(layout, _registry.Panels);
        _migrations = planned.Migrations;
        var keepDocuments = Layout is null ? [] : Documents.Concat(FloatingDocuments()).ToList(); // 떠 있던 문서도 메인 문서 영역으로 들인다
        foreach (var doc in keepDocuments.Where(d => d.Owner is IDock { VisibleDockables: { } own } && own.Contains(d)))
        {
            // 옛 창이 닫힐 때 그 안의 문서까지 정리되지 않도록 옛 문서 영역에서 먼저 뺀다.
            ((IDock)doc.Owner!).VisibleDockables!.Remove(doc);
        }

        var active = ActiveDocument;

        var rootNode = planned.Root!;
        var dockRoot = Factory.CreateRootDock();
        dockRoot.Id = "oranda.root";
        dockRoot.IsCollapsable = false;
        var content = Build(rootNode, rootNode);
        dockRoot.VisibleDockables = Factory.CreateList(content);
        dockRoot.ActiveDockable = content;
        dockRoot.DefaultDockable = content;
        Layout = dockRoot;

        var documents = DocumentDocks().FirstOrDefault();
        if (documents is not null)
        {
            foreach (var doc in keepDocuments)
            {
                documents.VisibleDockables!.Add(doc);
                doc.Owner = documents;
            }

            documents.ActiveDockable = (active is not null && keepDocuments.Contains(active)) ? active : keepDocuments.LastOrDefault();
        }

        Factory.InitLayout(dockRoot);
        ApplyEmptyContent();
        LayoutRebuilt?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>지금 배치를 앱 DTO 로 읽는다. 창 위치는 <paramref name="window"/> 로 받아 그대로 싣는다.</summary>
    public LayoutDocument Capture(WindowPlacement? window = null)
    {
        var root = CaptureRoot();
        var inTree = LayoutPlanner.ToolIds(root).ToHashSet(StringComparer.Ordinal);
        var hidden = _registry.Panels
            .Where(p => p.Kind == ToolPanelKind.Tool && !inTree.Contains(p.Id))
            .Select(p => p.Id)
            .ToList();
        return new LayoutDocument { Window = window, Root = root, HiddenTools = hidden, Migrations = _migrations };
    }

    public bool IsToolVisible(string toolId) => VisibleToolIds.Contains(toolId);

    /// <summary>도구 창을 보이고 그 탭을 활성으로 한다. 등록되지 않은 ID 면 false.</summary>
    public bool ShowTool(string toolId)
    {
        if (_registry.Find(toolId) is not { Kind: ToolPanelKind.Tool } def)
        {
            return false;
        }

        var current = LayoutPlanner.Reconcile(Capture(), _registry.Panels);
        Apply(LayoutPlanner.ShowTool(current, def));
        return true;
    }

    public bool HideTool(string toolId)
    {
        if (!IsToolVisible(toolId))
        {
            return false;
        }

        var current = LayoutPlanner.Reconcile(Capture(), _registry.Panels);
        Apply(LayoutPlanner.HideTool(current, toolId));
        return true;
    }

    public bool ToggleTool(string toolId) => IsToolVisible(toolId) ? !HideTool(toolId) : ShowTool(toolId);

    /// <summary>
    /// 도구 창 탭을 앞으로 가져온다. 이미 보이면 배치를 다시 짓지 않고 그 묶음의 활성 탭만 바꾼다(키보드 포커스는 그대로 —
    /// 실행 뒤 결과 창을 보여도 편집기에서 이어서 칠 수 있게). 숨겨져 있으면 <see cref="ShowTool"/>.
    /// </summary>
    public bool ActivateTool(string toolId)
    {
        if (_tools.TryGetValue(toolId, out var tool) && tool.Owner is IDock owner && IsToolVisible(toolId))
        {
            if (!ReferenceEquals(owner.ActiveDockable, tool))
            {
                owner.ActiveDockable = tool;
            }

            return true;
        }

        return ShowTool(toolId);
    }

    /// <summary>기본 배치로 되돌린다(<c>oranda.view.resetLayout</c>).</summary>
    public void Reset() => Apply(LayoutPlanner.CreateDefault(_registry.Panels));

    /// <summary>문서 탭을 연다. 이미 열린 같은 종류(여러 개 불가)가 있으면 그 탭을 활성으로만 한다.</summary>
    public PanelDocument? OpenDocument(string panelId, object? viewModel = null, string? title = null)
    {
        if (_registry.Find(panelId) is not { Kind: ToolPanelKind.Document } def || DocumentDocks().FirstOrDefault() is not { } dock)
        {
            return null;
        }

        if (!def.AllowMultiple && Documents.FirstOrDefault(d => d.Definition.Id == panelId) is { } existing)
        {
            Activate(existing);
            return existing;
        }

        var doc = new PanelDocument(def, $"{panelId}#{++_documentSerial}", title ?? _text.Title(def), viewModel ?? _createViewModel(def));
        Factory.AddDockable(dock, doc);
        Activate(doc);
        return doc;
    }

    /// <summary>지금 미리보기 탭(<see cref="PanelDocument.IsPreview"/>). 없으면 null.</summary>
    public PanelDocument? PreviewDocument => Documents.FirstOrDefault(d => d.IsPreview);

    /// <summary>
    /// 미리보기 탭으로 연다(VS Code 방식). 미리보기 탭이 이미 있으면 그 자리에 새 탭을 넣고 옛 탭을 닫는다 — 탭 수가 늘지 않는다.
    /// 새 탭을 먼저 넣고 옛 탭을 닫으므로 문서가 하나뿐이어도 시작 화면이 끼어 보이지 않는다. 옛 탭을 닫을 때
    /// <see cref="DocumentClosed"/> 가 나가므로 문서를 가진 기능의 정리(열린 커서 닫기 등)가 그대로 돈다.
    /// 여러 개를 열 수 없는 종류는 미리보기 없이 <see cref="OpenDocument"/> 와 같다.
    /// </summary>
    public PanelDocument? OpenPreviewDocument(string panelId, object viewModel, string title)
    {
        if (_registry.Find(panelId) is not { Kind: ToolPanelKind.Document } def || DocumentDocks().FirstOrDefault() is not { } dock)
        {
            return null;
        }

        if (!def.AllowMultiple)
        {
            return OpenDocument(panelId, viewModel, title);
        }

        var doc = new PanelDocument(def, $"{panelId}#{++_documentSerial}", title, viewModel) { IsPreview = true };
        var old = PreviewDocument;
        if (old is { Owner: IDock owner } && owner.VisibleDockables?.IndexOf(old) is int index and >= 0)
        {
            Factory.InsertDockable(owner, doc, index);
            Activate(doc);
            Factory.CloseDockable(old);
        }
        else
        {
            Factory.AddDockable(dock, doc);
        }

        Activate(doc);
        return doc;
    }

    /// <summary>미리보기 탭을 고정한다(다음 미리보기가 이 탭을 바꿔 끼우지 않는다). 이미 고정이면 아무것도 안 한다.</summary>
    public void Pin(PanelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.IsPreview && Documents.Contains(document))
        {
            document.IsPreview = false;
        }
    }

    /// <summary>문서를 활성으로 하고 그 내용으로 키보드 포커스를 옮겨 달라고 알린다(보기가 화면에 붙은 뒤 옮긴다).</summary>
    public void FocusDocument(PanelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Activate(document);
        DocumentFocusRequested?.Invoke(this, document);
    }

    public void Activate(PanelDocument document)
    {
        if (document.Owner is IDock owner)
        {
            Factory.SetActiveDockable(document);
            Factory.SetFocusedDockable(owner, document);
        }
    }

    public bool CloseDocument(PanelDocument document)
    {
        if (!Documents.Contains(document))
        {
            return false;
        }

        Factory.CloseDockable(document);
        ActiveDocumentChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void CloseAllDocuments()
    {
        foreach (var d in Documents.ToList())
        {
            Factory.CloseDockable(d);
        }

        ActiveDocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>활성 문서에서 <paramref name="step"/> 칸 옆 탭을 활성으로(끝에서 돌아온다).</summary>
    public void CycleDocument(int step)
    {
        if (ActiveDocument is not { Owner: IDock owner } active)
        {
            return;
        }

        var tabs = (owner.VisibleDockables ?? []).OfType<PanelDocument>().ToList();
        if (tabs.Count < 2)
        {
            return;
        }

        var next = tabs[(tabs.IndexOf(active) + step + tabs.Count) % tabs.Count];
        Activate(next);
    }

    /// <summary>활성 문서를 새 문서 영역으로 분할한다(오른쪽 또는 아래).</summary>
    public bool SplitActiveDocument(bool right)
    {
        if (ActiveDocument is not { } active)
        {
            return false;
        }

        if (right)
        {
            Factory.NewHorizontalDocumentDock(active);
        }
        else
        {
            Factory.NewVerticalDocumentDock(active);
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private List<IDocumentDock> DocumentDocks()
    {
        if (Layout is null)
        {
            return [];
        }

        var found = new List<IDocumentDock>();
        Visit(Layout, d =>
        {
            if (d is IDocumentDock dd)
            {
                found.Add(dd);
            }
        });
        return found;
    }

    /// <summary>떠 있는 창 안의 문서 탭(메인 배치 트리 밖이라 <see cref="Documents"/> 에는 없다).</summary>
    private List<PanelDocument> FloatingDocuments()
    {
        var found = new List<PanelDocument>();
        foreach (var window in (Layout as IRootDock)?.Windows ?? [])
        {
            if (window.Layout is { } floating)
            {
                Visit(floating, d =>
                {
                    if (d is PanelDocument doc)
                    {
                        found.Add(doc);
                    }
                });
            }
        }

        return found;
    }

    private static void Visit(IDockable node, Action<IDockable> visit)
    {
        visit(node);
        if (node is IDock dock)
        {
            foreach (var child in dock.VisibleDockables ?? [])
            {
                Visit(child, visit);
            }
        }
    }

    private IDockable Build(DockNode node, DockNode root)
    {
        switch (node.Kind)
        {
            case DockNodeKind.Split:
                {
                    var split = Factory.CreateProportionalDock();
                    split.Id = "oranda.split." + Guid.NewGuid().ToString("N")[..8];
                    split.Orientation = node.Orientation == DockOrientation.Horizontal ? Orientation.Horizontal : Orientation.Vertical;
                    split.Proportion = node.Proportion ?? double.NaN;
                    var kids = Factory.CreateList<IDockable>();
                    foreach (var child in node.Children ?? [])
                    {
                        if (kids.Count > 0)
                        {
                            kids.Add(Factory.CreateProportionalDockSplitter());
                        }

                        kids.Add(Build(child, root));
                    }

                    split.VisibleDockables = kids;
                    split.ActiveDockable = kids.FirstOrDefault(k => k is not IProportionalDockSplitter);
                    return split;
                }

            case DockNodeKind.ToolGroup:
                {
                    var group = Factory.CreateToolDock();
                    var ids = node.Tools ?? [];
                    group.Id = "oranda.tools." + ids[0];
                    group.Proportion = node.Proportion ?? double.NaN;
                    group.Alignment = LayoutPlanner.LocationOf(root, node) switch
                    {
                        DockLocation.Right => Alignment.Right,
                        DockLocation.Bottom => Alignment.Bottom,
                        _ => Alignment.Left,
                    };
                    var tools = Factory.CreateList<IDockable>();
                    foreach (var id in ids)
                    {
                        tools.Add(GetTool(id));
                    }

                    group.VisibleDockables = tools;
                    group.ActiveDockable = tools.FirstOrDefault(t => t.Id == node.ActiveTool) ?? tools[0];
                    return group;
                }

            default:
                {
                    var docs = Factory.CreateDocumentDock();
                    docs.Id = DocumentsId;
                    docs.Proportion = node.Proportion ?? double.NaN;
                    docs.CanCreateDocument = false;

                    // 문서가 하나도 없어도 문서 영역 자리는 접지 않는다(접으면 도구 창이 화면을 다 차지한다).
                    docs.IsCollapsable = false;
                    docs.VisibleDockables = Factory.CreateList<IDockable>();
                    return docs;
                }
        }
    }

    /// <summary>
    /// 언어가 바뀌었을 때 도구 창과 문서 탭의 제목을 새 언어로 다시 단다.
    /// 문서는 이름 그대로(옛 언어의 기본 제목과 같은) 탭만 바꾼다 — 사용자가 붙인 이름·파일 이름은 건드리지 않는다.
    /// </summary>
    private void Retitle()
    {
        foreach (var tool in _tools.Values)
        {
            tool.Title = _text.Title(tool.Definition);
        }

        var other = _text.Language == "ko" ? "en" : "ko";
        foreach (var doc in Documents)
        {
            var old = _text.LookupIn(other, "Panel." + doc.Definition.Id);
            if (string.Equals(doc.Title, old, StringComparison.Ordinal))
            {
                doc.Title = _text.Title(doc.Definition);
            }
        }
    }

    private PanelTool GetTool(string id)
    {
        if (!_tools.TryGetValue(id, out var tool))
        {
            var def = _registry.Find(id)!;
            tool = new PanelTool(def, _text.Title(def), () => _createViewModel(def));
            _tools[id] = tool;
        }

        return tool;
    }

    private DockNode CaptureRoot()
    {
        _documentsCaptured = false;
        var content = Layout?.VisibleDockables?.FirstOrDefault();
        var node = content is null ? null : CaptureNode(content);
        return (node is null ? null : LayoutPlanner.Normalize(node, _registry.Panels))
            ?? new DockNode { Kind = DockNodeKind.Documents };
    }

    private DockNode? CaptureNode(IDockable dockable)
    {
        double? Proportion() => double.IsNaN(dockable.Proportion) ? null : Math.Round(dockable.Proportion, 4);

        switch (dockable)
        {
            case IProportionalDock split:
                {
                    var kids = (split.VisibleDockables ?? [])
                        .Where(k => k is not IProportionalDockSplitter)
                        .Select(CaptureNode)
                        .OfType<DockNode>()
                        .ToList();
                    if (kids.Count == 0)
                    {
                        return null;
                    }

                    return new DockNode
                    {
                        Kind = DockNodeKind.Split,
                        Orientation = split.Orientation == Orientation.Horizontal ? DockOrientation.Horizontal : DockOrientation.Vertical,
                        Proportion = Proportion(),
                        Children = kids,
                    };
                }

            case IToolDock group:
                {
                    var ids = (group.VisibleDockables ?? []).OfType<PanelTool>().Select(t => t.Id).ToList();
                    if (ids.Count == 0)
                    {
                        return null;
                    }

                    return new DockNode
                    {
                        Kind = DockNodeKind.ToolGroup,
                        Proportion = Proportion(),
                        Tools = ids,
                        ActiveTool = (group.ActiveDockable as PanelTool)?.Id ?? ids[0],
                    };
                }

            case IDocumentDock:
                // 문서 영역이 분할되어 여럿이어도 DTO 에는 첫째만 둔다(탭 목록은 작업 공간 파일 몫).
                if (_documentsCaptured)
                {
                    return null;
                }

                _documentsCaptured = true;
                return new DockNode { Kind = DockNodeKind.Documents, Proportion = Proportion() };

            default:
                return null;
        }
    }
}
