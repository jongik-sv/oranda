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
/// 배치를 다시 짓는 것(DTO → Dock 모델)은 배치 불러오기(시작·이름 붙인 레이아웃)와 기본 배치로 되돌리기 때만 한다. 도구 창 켜기·끄기는 배치 규칙이
/// 고른 자리에 그 창 하나만 넣고 빼어(.ToolToggle.cs) 문서 칸·다른 묶음 컨트롤을 그대로 둔다(다시 지으면 화면 전체가 깜박인다, 2026-11-16 사용자 신고).
/// DTO 에 문서 영역은 1개뿐이므로, 다시 지을 때 사용자가 나눈 문서 영역은 살아 있는 칸을 그대로 새 배치에 옮겨 유지할 수 있다(<see cref="TakeSplitDocumentArea"/>).
/// </summary>
public sealed partial class DockWorkspace
{
    /// <summary>문서 영역 도킹 ID.</summary>
    public const string DocumentsId = "oranda.documents";

    private readonly IToolPanelRegistry _registry;
    private readonly Func<ToolPanelDefinition, object> _createViewModel;
    private readonly IShellText _text;
    private readonly Dictionary<string, PanelTool> _tools = new(StringComparer.Ordinal);
    private int _documentSerial;
    private bool _documentsCaptured;
    private PanelDocument? _lastFocused; // 마지막으로 포커스를 가진 문서(도구 창을 눌러도 남는다)
    private PanelDocument? _reported; // 마지막으로 알린 활성 문서

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
        ActiveDocumentChanged += (_, _) => _reported = ActiveDocument; // 맨 먼저 듣는다: 포커스만 옮겼을 때 바뀌었는지 견줄 값
        Factory.ActiveDockableChanged += (_, _) => ActiveDocumentChanged?.Invoke(this, EventArgs.Empty);
        Factory.FocusedDockableChanged += (_, e) => OnFocusedDockableChanged(e.Dockable);
        ActiveDocumentChanged += (_, _) => ScheduleScopedUpdate(); // SQL 창 도구 창 숨기기(.ScopedTools.cs)
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
        Factory.DockablePinned += (_, e) =>
        {
            if (e.Dockable is PanelTool { Owner: IToolDock home } pinned)
            {
                _pinHomes[pinned.Id] = home;
            }
        };
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
    /// 지금 보이는 활성 문서. 포커스를 가졌던 문서(<c>Factory.CurrentDockable</c>)가 속한 영역의 보이는 탭을 돌려준다:
    /// 탭 줄에서 다른 탭을 골라도 포커스 기록은 옛 문서에 남을 수 있어, 그대로 쓰면 창이 다시 활성화될 때 엉뚱한 문서가 활성이 된다.
    /// 도구 창으로 포커스가 옮겨 가면(<c>CurrentDockable</c> 이 도구 창) 마지막으로 포커스를 가졌던 문서의 영역을 본다: 문서 영역을 나눈 화면에서
    /// 첫 영역의 탭으로 되돌아가 SQL 작업 공간인지 판정(전용 세션·툴바 콤보)이 활성 탭과 갈리지 않게(TSK-14-10).
    /// </summary>
    public PanelDocument? ActiveDocument =>
        Layout is null ? null
        : ShownIn(Factory.CurrentDockable as PanelDocument ?? Live(_lastFocused))
            ?? DocumentDocks().Select(d => d.ActiveDockable).OfType<PanelDocument>().FirstOrDefault();

    private static PanelDocument? ShownIn(PanelDocument? focused) =>
        focused?.Owner is IDock { ActiveDockable: PanelDocument shown } ? shown : focused;

    /// <summary>아직 문서 영역(떠 있는 창 포함)에 있는 문서면 그것, 닫혔거나 영역 밖이면 null.</summary>
    private static PanelDocument? Live(PanelDocument? document) =>
        document?.Owner is IDock { VisibleDockables: { } owned } && owned.Contains(document) ? document : null;

    /// <summary>
    /// 포커스가 옮겨 갔다(Dock 은 문서·도구 창을 누르면 <c>SetFocusedDockable</c> 로 알린다). 문서면 기억하고, 나눈 화면에서 이미 보이는 탭인 다른 영역의
    /// 문서를 눌러 활성 탭은 그대로인 채 포커스만 옮겼어도(영역의 <c>ActiveDockableChanged</c> 가 없다) 활성 문서가 바뀌었으면 알린다.
    /// </summary>
    private void OnFocusedDockableChanged(IDockable? dockable)
    {
        if (dockable is PanelDocument document)
        {
            _lastFocused = document;
        }

        if (!ReferenceEquals(ActiveDocument, _reported))
        {
            ActiveDocumentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>보이는 도구 창 ID(트리에 있는 것). 핀을 빼 사이드바에 접어 둔 창도 넣는다(닫힌 창이 아니다 — 보기 메뉴에 체크로 보인다).</summary>
    public IReadOnlyList<string> VisibleToolIds =>
        Layout is null ? [] : LayoutPlanner.ToolIds(CaptureRoot()).Where(id => !IsScopedHidden(id)).ToList();

    /// <summary>만들어진 도구 창 ViewModel 이 있는 ID(지연 생성 확인용).</summary>
    public IReadOnlyCollection<string> CreatedToolIds => _tools.Where(t => t.Value.IsPanelCreated).Select(t => t.Key).ToList();

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

    /// <summary>
    /// 배치를 짓고 읽을 때 쓰는 도구 창 목록 = 레지스트리(기능 게이트로 거른 것) + 지금 배치에 떠 있는데 레지스트리에서 빠진 도구 창.
    /// 실행 중 기능을 꺼도 이미 열린 도구 창은 닫지 않는다(D-010, TRD 10.6). 사용자가 닫거나 다음 실행이면 빠진다.
    /// 다시 켠 도구 창은 레지스트리로 돌아와 기본 위치 규칙(<see cref="LayoutPlanner.ShowTool"/>)을 탄다.
    /// </summary>
    private IReadOnlyList<ToolPanelDefinition> PlanningPanels()
    {
        var panels = _registry.Panels;
        if (Layout is null)
        {
            return panels;
        }

        List<ToolPanelDefinition>? kept = null;
        foreach (var tool in ToolsInTree(Layout))
        {
            if (_registry.Find(tool.Definition.Id) is null && (kept is null || !kept.Contains(tool.Definition)))
            {
                (kept ??= []).Add(tool.Definition);
            }
        }

        return kept is null ? panels : [.. panels, .. kept];
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
}
