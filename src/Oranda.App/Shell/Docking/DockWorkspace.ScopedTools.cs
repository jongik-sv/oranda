using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace Oranda.App.Shell.Docking;

/// <summary>
/// SQL 편집기 문서에서만 쓰는 도구 창(결과·메시지·출력·계획·컴파일 오류·바인드·행 보기·실행 이력·변경 일지·템플릿·확장 시험·개요)을 활성 문서가 SQL·PL/SQL 편집기가 아닐 때 잠시 숨긴다(TSK-13-34).
/// <para>
/// 배치를 다시 짓지 않는다: 그 도구 창을 자기 묶음(<see cref="IToolDock"/>)의 <c>VisibleDockables</c> 에서 잠시 빼고(묶음·자리·활성 여부를 기억),
/// SQL 편집기로 돌아오면 같은 자리에 되넣는다. 묶음이 비면 Dock 이 그 칸을 접는다. 접힐 때 옆 칸 비율이 늘어나므로
/// 숨기기 직전 비율을 모두 적어 두었다가 되돌린다(사용자가 둔 크기 그대로).
/// </para>
/// <para>
/// 숨김은 배치 파일(layout.json)에 남지 않는다: <see cref="Capture"/> 는 숨긴 창을 원래 자리·비율로 적는다.
/// 사용자가 닫은 창은 트리에 없으므로 숨김 대상이 아니고 다시 열리지도 않는다. 활성 문서가 없으면(null) 지금 상태를 둔다.
/// 배치를 새로 지으면(<see cref="Apply"/>) 숨김 기록을 버린다 — 새 배치는 숨긴 창이 들어간 <see cref="Capture"/> 결과이거나 다른 배치다.
/// </para>
/// <para>
/// TSK-01-02 확장(design.md 3.2): 숨길지는 도구 창마다 따로 판정한다(<see cref="DecideTool"/>). 문서의 Panel 이 <see cref="IToolScopeDocument"/> 이고
/// 범위 글자가 <see cref="MyBatisToolScope"/> 면 MyBatis 문서다 — MyBatis 도구 창 2개(<see cref="MyBatisToolIds"/>: 매퍼 쿼리·조합 검사. 펼친 SQL 은 TSK-03-10 부터 쿼리 탭 안 분할)는 그때만 보이고,
/// 개요·템플릿 같은 SQL 본문 전용 창(<see cref="SqlTextOnlyToolIds"/>)은 MyBatis 문서에서 숨는다. 활성 문서가 없으면(시작 화면)
/// MyBatis 창만 숨기고 SQL 전용 창은 지금 상태를 둔다. 같은 문서의 종류가 바뀌는 신호(<see cref="IToolScopeDocument.ToolScopeChanged"/>)는
/// 활성 문서에 한해 구독해 <see cref="RefreshScopedTools"/> 로 다시 판정한다. 갈아탈 때는 「보여야 하는데 숨긴 창은 되보이고, 숨겨야 하는데 보이는 창은 숨긴다」.
/// </para>
/// <para>
/// PL/SQL 문서(<see cref="PlsqlDocumentIds"/>)에서는 질의 결과용 창(<see cref="QueryOnlyToolIds"/>: 조회 결과·실행 계획·행 보기)도 숨기고,
/// 숨긴 창이 묶음의 활성 탭이었으면 DBMS 출력을 앞에 둔다(2026-10-26 사용자 요청).
/// </para>
/// </summary>
public sealed partial class DockWorkspace
{
    /// <summary>SQL·PL/SQL 편집기 문서에서만 보이는 도구 창 ID. 실행 이력·변경 일지도 SQL 작업의 기록이라 다른 문서에서는 숨겨 문서가 아래까지 차지하게 한다(TSK-13-41).</summary>
    internal static readonly IReadOnlySet<string> SqlOnlyToolIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "oranda.panel.results",
        "oranda.panel.messages",
        "oranda.panel.output",
        "oranda.panel.plan",
        "oranda.panel.compileErrors",
        "oranda.panel.binds",
        "oranda.panel.rowView",
        "oranda.panel.history",
        "oranda.panel.changeJournal",
        "oranda.panel.templates",
        "oranda.panel.extensionTest",
        "oranda.panel.outline",
    };

    /// <summary>MyBatis 문서(<see cref="IToolScopeDocument.ToolScope"/> 값)를 가리키는 범위 글자.</summary>
    internal const string MyBatisToolScope = "mybatis";

    /// <summary>MyBatis 문서에서만 보이는 도구 창 ID(design.md 3.1). 아직 등록되지 않은 ID 도 집합에는 둔다.</summary>
    internal static readonly IReadOnlySet<string> MyBatisToolIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "oranda.panel.mybatisStatements",
        "oranda.panel.mybatisCombos",
    };

    /// <summary>
    /// <see cref="SqlOnlyToolIds"/> 중 SQL 본문(문장 하나를 통째로 다루는 SQL 편집기)에만 맞아 MyBatis 문서에서는 숨기는 창(design.md 3.2).
    /// 바인드 변수 창은 MyBatis 문서에서 문장 파라미터를 보이므로 넣지 않는다(TSK-03-02, 2026-10-23 사용자 결정).
    /// </summary>
    internal static readonly IReadOnlySet<string> SqlTextOnlyToolIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "oranda.panel.outline",
        "oranda.panel.templates",
        "oranda.panel.compileErrors",
        "oranda.panel.extensionTest",
    };

    /// <summary>PL/SQL 문서(편집기·작업 공간). 소스를 고치고 컴파일·실행하는 문서라 <see cref="QueryOnlyToolIds"/> 를 숨기고 DBMS 출력(<see cref="PlsqlFrontToolId"/>)을 앞에 둔다.</summary>
    internal static readonly IReadOnlySet<string> PlsqlDocumentIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "oranda.panel.plsqlEditor",
        "oranda.panel.plsqlWorkspace",
    };

    /// <summary><see cref="SqlOnlyToolIds"/> 중 질의 결과에만 쓰여 PL/SQL 문서에서는 숨기는 창(조회 결과·실행 계획·행 보기). 실행이 결과를 내면 그 기능이 창을 직접 앞으로 가져온다.</summary>
    internal static readonly IReadOnlySet<string> QueryOnlyToolIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "oranda.panel.results",
        "oranda.panel.plan",
        "oranda.panel.rowView",
    };

    /// <summary>PL/SQL 문서로 오면서 묶음의 활성 탭(조회 결과 등)을 숨겼을 때 대신 앞에 둘 창.</summary>
    internal const string PlsqlFrontToolId = "oranda.panel.output";

    /// <summary>도구 창 하나를 어떤 활성 문서에서 어떻게 할지: 보임 · 숨김 · 지금 상태 유지(활성 문서 없음 + SQL 전용 창).</summary>
    private enum ToolScopeDecision
    {
        Show,
        Hide,
        Keep,
    }

    /// <summary>도구 창 <paramref name="toolId"/> 의 판정(design.md 3.2 표). <paramref name="doc"/> 이 null 이면 활성 문서 없음.</summary>
    private static ToolScopeDecision DecideTool(string toolId, PanelDocument? doc)
    {
        if (MyBatisToolIds.Contains(toolId))
        {
            // 지금 규칙의 「null 이면 그대로」를 쓰지 않는다: 시작 화면에 빈 MyBatis 창이 새지 않게 늘 숨긴다.
            return doc is not null && SqlDocumentIds.Contains(doc.Definition.Id) && IsMyBatisScope(doc) ? ToolScopeDecision.Show : ToolScopeDecision.Hide;
        }

        if (!SqlOnlyToolIds.Contains(toolId))
        {
            return ToolScopeDecision.Show;
        }

        if (doc is null)
        {
            return ToolScopeDecision.Keep;
        }

        if (!SqlDocumentIds.Contains(doc.Definition.Id))
        {
            return ToolScopeDecision.Hide;
        }

        if (QueryOnlyToolIds.Contains(toolId) && PlsqlDocumentIds.Contains(doc.Definition.Id))
        {
            return ToolScopeDecision.Hide;
        }

        return SqlTextOnlyToolIds.Contains(toolId) && IsMyBatisScope(doc) ? ToolScopeDecision.Hide : ToolScopeDecision.Show;
    }

    private static bool IsMyBatisScope(PanelDocument doc) =>
        doc.Panel is IToolScopeDocument { ToolScope: { } scope } && string.Equals(scope, MyBatisToolScope, StringComparison.Ordinal);

    /// <summary>이 작업 공간에서의 판정: 「늘 보임」으로 정한 MyBatis 도구 창(<see cref="SetToolAlwaysShown"/>)은 문서와 상관없이 보인다.</summary>
    private ToolScopeDecision DecideToolHere(string toolId, PanelDocument? doc) =>
        _alwaysShownTools.Contains(toolId) ? ToolScopeDecision.Show : DecideTool(toolId, doc);

    /// <summary>
    /// MyBatis 도구 창(<see cref="MyBatisToolIds"/> 만)을 활성 문서 종류와 상관없이 보이게(true) 하거나 원래 규칙으로 돌린다(false).
    /// 「문장」 창을 매퍼 폴더 모드(MyBatis 문서 없이)에서 쓰도록 03-07 이 폴더를 열 때 부른다. 바꾸면 도구 창 보임을 다시 판정한다.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="toolId"/> 가 MyBatis 도구 창이 아니다.</exception>
    public void SetToolAlwaysShown(string toolId, bool alwaysShown)
    {
        if (!MyBatisToolIds.Contains(toolId))
        {
            throw new ArgumentException("MyBatis 도구 창만 늘 보임으로 정할 수 있다: " + toolId, nameof(toolId));
        }

        if (alwaysShown ? _alwaysShownTools.Add(toolId) : _alwaysShownTools.Remove(toolId))
        {
            RefreshScopedTools();
        }
    }

    /// <summary>
    /// 이 도구 창을 활성 문서 <paramref name="doc"/> 에서 보이는가(문서의 범위 글자까지 본다). 활성 문서가 없으면(null) MyBatis 창만 false 이고,
    /// SQL 전용 창은 「지금 상태 유지」라 true 로 돌려준다(호출한 쪽이 현재 상태를 본다).
    /// </summary>
    internal static bool IsToolShownFor(string toolId, PanelDocument? doc) => DecideTool(toolId, doc) != ToolScopeDecision.Hide;

    /// <summary>
    /// 이 도구 창을 활성 문서 <paramref name="documentId"/> 에서 보이는가. 표에 없는 도구 창(스키마 브라우저·스크립트 같은 탐색 창, 확장이 더한 창)은 늘 보인다.
    /// 정의 ID 만 보는 판정이라 MyBatis 문서 여부는 모른다 — 그것까지 보려면 <see cref="IsToolShownFor(string, PanelDocument?)"/>. 조사 표는 TSK-13-41 참고.
    /// </summary>
    internal static bool IsToolShownFor(string toolId, string documentId) =>
        !SqlOnlyToolIds.Contains(toolId) || (SqlDocumentIds.Contains(documentId) && !(QueryOnlyToolIds.Contains(toolId) && PlsqlDocumentIds.Contains(documentId)));

    /// <summary>위 도구 창을 보이는 문서(SQL·PL/SQL 편집기, PL/SQL 작업 공간).</summary>
    internal static readonly IReadOnlySet<string> SqlDocumentIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "oranda.panel.sqlEditor",
        "oranda.panel.plsqlEditor",
        "oranda.panel.plsqlWorkspace",
    };

    private readonly List<ScopedHide> _scopedHidden = [];
    private readonly HashSet<string> _alwaysShownTools = new(StringComparer.Ordinal);
    private readonly List<CollapseSnapshot> _collapses = [];
    private bool _scopedUpdating;
    private bool _scopedPending;
    private string? _showingTool;
    private PanelDocument? _scopedDocument;
    private IToolScopeDocument? _scopeSource;

    /// <summary>
    /// 묶음 <paramref name="Dock"/> 이 비어 접힐 때(도구 창을 모두 숨긴 때) 영향받는 칸들의 그때 비율. 접힘마다 따로 적는다 —
    /// 오래 접혀 있는 묶음(예: 일반 SQL 문서에서 늘 숨는 MyBatis 창)의 기록이 나중 접힘에서 사용자가 바꾼 비율을 덮지 않게.
    /// </summary>
    private sealed record CollapseSnapshot(IToolDock Dock, Dictionary<IDockable, double> Values);

    /// <summary>숨긴 도구 창 하나. <paramref name="Index"/> 는 그 묶음의 전체 탭 목록(숨긴 창 포함) 안 자리.</summary>
    private sealed record ScopedHide(PanelTool Tool, IToolDock Dock, int Index, bool WasActive);

    /// <summary>지금 잠시 숨긴 SQL 전용 도구 창 ID(시험·진단용). MyBatis 창(<see cref="MyBatisToolIds"/>)은 SQL 문서에서도 늘 숨으므로 빼고 따로 본다.</summary>
    internal IReadOnlyList<string> ScopedHiddenToolIds => _scopedHidden.Select(h => h.Tool.Id).Where(id => !MyBatisToolIds.Contains(id)).ToList();

    /// <summary>지금 잠시 숨긴 MyBatis 도구 창 ID(시험·진단용, TSK-01-02).</summary>
    internal IReadOnlyList<string> MyBatisScopedHiddenToolIds => _scopedHidden.Select(h => h.Tool.Id).Where(MyBatisToolIds.Contains).ToList();

    internal bool IsScopedHidden(string toolId) => _scopedHidden.Exists(h => string.Equals(h.Tool.Id, toolId, StringComparison.Ordinal));

    /// <summary>
    /// 활성 문서가 바뀌었다는 알림을 받으면 숨기기 판단을 UI 작업 큐 뒤로 미룬다. Dock 이 닫기·끌어 놓기·활성 바꾸기를 하는 도중에
    /// 트리를 고치지 않고, 한 번에 여러 번 온 알림은 한 번으로 합친다.
    /// </summary>
    private void ScheduleScopedUpdate()
    {
        if (_scopedPending)
        {
            return;
        }

        _scopedPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scopedPending = false;
            UpdateScopedTools();
        });
    }

    /// <summary>
    /// 활성 문서는 그대로인데 그 문서의 종류가 바뀌었다(<see cref="IToolScopeDocument.ToolScopeChanged"/>): 같은 문서를 다시 판정하게 한다.
    /// 판정은 UI 작업 큐 뒤로 미루며 여러 번 온 신호는 한 번으로 합친다.
    /// </summary>
    internal void RefreshScopedTools()
    {
        _scopedDocument = null;
        ScheduleScopedUpdate();
    }

    /// <summary>활성 문서의 범위 신호만 듣는다. 문서가 바뀌면 옛 구독을 푼다(null 이면 구독 없음).</summary>
    private void WatchScopeSource(IToolScopeDocument? source)
    {
        if (ReferenceEquals(_scopeSource, source))
        {
            return;
        }

        if (_scopeSource is not null)
        {
            _scopeSource.ToolScopeChanged -= OnToolScopeChanged;
        }

        _scopeSource = source;
        if (source is not null)
        {
            source.ToolScopeChanged += OnToolScopeChanged;
        }
    }

    private void OnToolScopeChanged(object? sender, EventArgs e) => RefreshScopedTools();

    /// <summary>활성 문서를 보고 도구 창을 창마다 숨기거나 되보인다.</summary>
    internal void UpdateScopedTools()
    {
        if (_scopedUpdating || Layout is null)
        {
            return;
        }

        if (ActiveDocument is not { } doc)
        {
            // 활성 문서가 없다(시작 화면): MyBatis 창만 숨긴다. 숨겼으면 다음에 같은 문서가 돌아와도 다시 판정하게 비운다.
            WatchScopeSource(null);
            if (_alwaysShownTools.Count > 0)
            {
                RestoreScopedTools(_alwaysShownTools.Contains); // 문서 없이도 보이라고 한 창은 되보인다
            }

            if (HideScopedTools(null))
            {
                _scopedDocument = null;
            }

            return;
        }

        WatchScopeSource(doc.Panel as IToolScopeDocument);

        // 같은 문서 안에서 도구 창 탭·포커스만 바뀐 것은 무시한다(기능이 숨긴 창을 직접 보였으면 그 문서에 있는 동안 그대로 둔다).
        if (ReferenceEquals(doc, _scopedDocument))
        {
            return;
        }

        _scopedDocument = doc;
        RestoreScopedTools(id => DecideToolHere(id, doc) != ToolScopeDecision.Hide);
        HideScopedTools(doc);
    }

    /// <summary>
    /// <paramref name="doc"/>(활성 문서, 없으면 null)에서 숨겨야 하는데 보이는 도구 창을 숨긴다. <paramref name="keep"/> 은 둔다.
    /// <paramref name="only"/> 가 있으면 그 조건에 맞는 창만 본다. 하나라도 숨겼으면 true.
    /// </summary>
    private bool HideScopedTools(PanelDocument? doc, string? keep = null, Func<string, bool>? only = null)
    {
        if (Layout is null)
        {
            return false;
        }

        // 자리는 그 묶음의 전체 탭 목록(앞서 숨긴 창 포함) 기준으로 적는다 — 되넣을 때 작은 자리부터 넣으면 원래 순서가 된다.
        var targets = new List<(PanelTool Tool, IToolDock Dock, int Index, bool Active)>();
        Visit(Layout, d =>
        {
            if (d is IToolDock { VisibleDockables: { } list } group)
            {
                var full = ScopedToolIds(group).Ids;
                targets.AddRange(list.OfType<PanelTool>()
                    .Where(t => DecideToolHere(t.Id, doc) == ToolScopeDecision.Hide && !string.Equals(t.Id, keep, StringComparison.Ordinal) && (only?.Invoke(t.Id) ?? true))
                    .Select(t => (t, group, full.IndexOf(t.Id), ReferenceEquals(group.ActiveDockable, t))));
            }
        });
        if (targets.Count == 0)
        {
            return false;
        }

        _scopedUpdating = true;
        try
        {
            foreach (var (tool, dock, index, active) in targets)
            {
                _scopedHidden.Add(new ScopedHide(tool, dock, index, active));
                Factory.RemoveDockable(tool, collapse: false);
            }

            if (doc is not null && PlsqlDocumentIds.Contains(doc.Definition.Id))
            {
                // 앞에 있던 조회 결과 등을 숨긴 묶음은 Dock 이 고른 옆 탭 대신 DBMS 출력을 앞에 둔다.
                foreach (var dock in targets.Where(t => t.Active).Select(t => t.Dock).Distinct())
                {
                    if (dock.VisibleDockables?.OfType<PanelTool>().FirstOrDefault(t => string.Equals(t.Id, PlsqlFrontToolId, StringComparison.Ordinal)) is { } front)
                    {
                        dock.ActiveDockable = front;
                    }
                }
            }

            // 빈 묶음은 Dock 이 접고 옆 칸 비율을 늘린다(다음 배치 계산 때). 그 영향을 받는 칸(빈 묶음과 그 위로 함께 빈 칸들의 형제)만
            // 지금 값을 묶음별로 적어 두었다가 되돌린다 — 상관없는 칸을 숨긴 동안 끌어 바꾼 크기는 건드리지 않는다.
            // targets 는 보이는 도구 창이 있던 묶음이라 여기서 빈 묶음은 방금 접힌 것이다: 옛 접힘의 기록이 있어도 그 위에 새로 적는다.
            foreach (var dock in targets.Select(t => t.Dock).Distinct().Where(d => d.IsEmpty))
            {
                _collapses.RemoveAll(c => ReferenceEquals(c.Dock, dock));
                var values = new Dictionary<IDockable, double>(ReferenceEqualityComparer.Instance);
                _collapses.Add(new CollapseSnapshot(dock, values));
                IDockable node = dock;
                while (node.Owner is IDock parent)
                {
                    foreach (var child in (parent.VisibleDockables ?? []).Where(c => c is not IProportionalDockSplitter))
                    {
                        values[child] = child.Proportion;
                    }

                    if (!parent.IsEmpty)
                    {
                        break;
                    }

                    node = parent;
                }
            }
        }
        finally
        {
            _scopedUpdating = false;
        }

        return true;
    }

    /// <summary>
    /// 숨긴 도구 창을 원래 자리에 되넣고 접혔던 칸 비율을 돌린다. 숨긴 것이 없으면 아무것도 안 한다.
    /// <paramref name="shouldShow"/> 가 있으면 그 조건에 맞는 창만 되넣고 나머지는 숨긴 채 둔다(문서 종류가 바뀌어 일부만 되보일 때).
    /// </summary>
    internal void RestoreScopedTools(Func<string, bool>? shouldShow = null)
    {
        if (_scopedHidden.Count == 0 || Layout is null)
        {
            return;
        }

        if (shouldShow is not null && _scopedHidden.Exists(h => !shouldShow(h.Tool.Id)))
        {
            // 일부는 계속 숨긴다: 창마다 되넣는다(접혔던 칸 비율은 그 묶음이 비었던 창을 되넣을 때 돌린다).
            foreach (var h in _scopedHidden.Where(h => shouldShow(h.Tool.Id)).OrderBy(h => h.Index).ToList())
            {
                RestoreScopedTool(h.Tool.Id);
            }

            if (_scopedHidden.Count == 0)
            {
                _collapses.Clear();
            }

            return;
        }

        var lost = new List<string>();
        _scopedUpdating = true;
        try
        {
            var inTree = TreeSet();
            foreach (var h in _scopedHidden.OrderBy(h => h.Index).ToList())
            {
                RestoreOne(h, inTree, lost);
            }

            RestoreProportions(inTree, EarliestProportions());
        }
        finally
        {
            _scopedHidden.Clear();
            _collapses.Clear();
            _scopedUpdating = false;
        }

        foreach (var id in lost)
        {
            ShowTool(id);
        }
    }

    /// <summary>숨긴 창 하나만 되넣는다(기능이 그 창을 직접 앞으로 가져올 때). 나머지는 숨긴 채 둔다.</summary>
    private void RestoreScopedTool(string toolId)
    {
        if (Layout is null || _scopedHidden.Find(h => string.Equals(h.Tool.Id, toolId, StringComparison.Ordinal)) is not { } h)
        {
            return;
        }

        var lost = new List<string>();
        _scopedUpdating = true;
        try
        {
            var inTree = TreeSet();
            var wasEmpty = h.Dock.IsEmpty;
            RestoreOne(h, inTree, lost);
            if (wasEmpty && _collapses.Find(c => ReferenceEquals(c.Dock, h.Dock)) is { } snapshot)
            {
                // 이 묶음이 접힐 때 적어 둔 칸 비율만 돌린다(아직 접힌 다른 묶음의 기록은 그대로 둔다).
                RestoreProportions(inTree, snapshot.Values);
                _collapses.Remove(snapshot);
            }
        }
        finally
        {
            _scopedUpdating = false;
        }

        foreach (var id in lost)
        {
            ShowTool(id);
        }
    }

    private HashSet<IDockable> TreeSet()
    {
        var inTree = new HashSet<IDockable>(ReferenceEqualityComparer.Instance);
        Visit(Layout!, d => inTree.Add(d));
        return inTree;
    }

    private void RestoreOne(ScopedHide h, HashSet<IDockable> inTree, List<string> lost)
    {
        _scopedHidden.Remove(h);
        if (!inTree.Contains(h.Dock))
        {
            lost.Add(h.Tool.Id); // 숨긴 동안 묶음이 사라졌다(끌어 옮기기 등) → 배치 규칙대로 다시 보인다
            return;
        }

        // 전체 목록 자리에서 아직 숨긴 채인 앞쪽 창 수를 빼면 지금 목록의 자리다.
        var list = h.Dock.VisibleDockables ??= Factory.CreateList<IDockable>();
        var before = _scopedHidden.Count(o => ReferenceEquals(o.Dock, h.Dock) && o.Index < h.Index);
        Factory.InsertDockable(h.Dock, h.Tool, Math.Clamp(h.Index - before, 0, list.Count));
        if (h.WasActive || h.Dock.ActiveDockable is null)
        {
            h.Dock.ActiveDockable = h.Tool;
        }
    }

    private static void RestoreProportions(HashSet<IDockable> inTree, IReadOnlyDictionary<IDockable, double> saved)
    {
        foreach (var (dockable, p) in saved)
        {
            if (inTree.Contains(dockable) && !Equals(dockable.Proportion, p))
            {
                dockable.Proportion = p;
            }
        }
    }

    /// <summary>
    /// 지금 접혀 있는 묶음들의 기록을 합친다. 같은 칸이 여럿에 있으면 가장 먼저 접힌 쪽 값이 이긴다 —
    /// 먼저 접힌 묶음 때문에 옆 칸이 이미 늘어난 뒤에 적은 값은 원래 크기가 아니다.
    /// </summary>
    private Dictionary<IDockable, double> EarliestProportions()
    {
        var merged = new Dictionary<IDockable, double>(ReferenceEqualityComparer.Instance);
        foreach (var collapse in _collapses)
        {
            foreach (var (dockable, p) in collapse.Values)
            {
                merged.TryAdd(dockable, p);
            }
        }

        return merged;
    }

    /// <summary>「닫기」(<see cref="HideTool"/>)가 숨긴 창을 가리키면 기록에서 빼기만 한다 — 그 창은 정말 닫힌 것으로 저장되고, 나머지는 숨긴 채다.</summary>
    private bool ForgetScopedTool(string toolId)
    {
        var removed = _scopedHidden.RemoveAll(h => string.Equals(h.Tool.Id, toolId, StringComparison.Ordinal)) > 0;
        if (removed)
        {
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    /// <summary>배치를 새로 짓기 시작: 짓는 동안(옛 문서 영역에서 문서를 빼는 등) 생기는 활성 변경으로 숨기지 않게 막는다. 숨기던 중이었는지 돌려준다.</summary>
    private bool BeginScopedApply()
    {
        var wasHidden = _scopedHidden.Count > 0;
        _scopedUpdating = true;
        return wasHidden;
    }

    /// <summary>
    /// 배치를 다 지었다: 옛 숨김 기록을 버린다(새 배치는 숨긴 창이 들어간 <see cref="Capture"/> 결과이거나 다른 배치다).
    /// 숨기던 중이었으면 활성 문서에 맞춰 새 배치에서 다시 숨긴다 — 단 지금 보이라고 한 창(<see cref="ShowTool"/>)은 둔다.
    /// 숨기던 중이 아니어도 MyBatis 창은 활성 문서에 맞지 않으면(활성 문서 없음 포함) 숨긴다 — 새 배치가 시작 화면에 빈 MyBatis 창을 내지 않게.
    /// </summary>
    private void EndScopedApply(bool rehide)
    {
        _scopedHidden.Clear();
        _collapses.Clear();
        _scopedUpdating = false;
        if (Layout is not null)
        {
            HideScopedTools(ActiveDocument, _showingTool, rehide ? null : MyBatisToolIds.Contains);
        }
    }

    /// <summary>저장할 비율: 숨겨서 접힌 칸과 그 형제는 숨기기 직전 값.</summary>
    private double ScopedProportion(IDockable dockable) =>
        _collapses.Select(c => c.Values).FirstOrDefault(v => v.ContainsKey(dockable)) is { } saved ? saved[dockable] : dockable.Proportion;

    /// <summary>저장할 묶음 탭 목록: 숨긴 창을 원래 자리에 끼운다. 숨긴 창이 활성이었으면 그것이 활성 탭.</summary>
    private (List<string> Ids, string? Active) ScopedToolIds(IToolDock group)
    {
        var ids = (group.VisibleDockables ?? []).OfType<PanelTool>().Select(t => t.Id).ToList();
        string? active = (group.ActiveDockable as PanelTool)?.Id;
        foreach (var h in _scopedHidden.Where(h => ReferenceEquals(h.Dock, group)).OrderBy(h => h.Index))
        {
            ids.Insert(Math.Min(h.Index, ids.Count), h.Tool.Id);
            if (h.WasActive)
            {
                active = h.Tool.Id;
            }
        }

        return (ids, active);
    }
}

/// <summary>
/// 문서 영역 탭의 Panel 이 구현하면 도구 창 보임 규칙이 그 문서의 범위 글자를 함께 본다(design.md 3.3).
/// Shell 은 Features 를 모르므로 인터페이스를 여기에 둔다. 예: MyBatis 작업 공간의 <see cref="ToolScope"/> 는 <c>"mybatis"</c>.
/// </summary>
public interface IToolScopeDocument
{
    /// <summary>도구 창 보임 규칙이 보는 범위 글자. 없으면 null(일반 문서).</summary>
    string? ToolScope { get; }

    /// <summary>같은 문서 안에서 <see cref="ToolScope"/> 가 바뀌었다(Dock 활성 문서 이벤트가 오지 않는 변화).</summary>
    event EventHandler? ToolScopeChanged;
}
