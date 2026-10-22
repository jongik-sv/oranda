using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace Oranda.App.Shell.Docking;

/// <summary>
/// SQL 창에 딸린 도구 창(결과·메시지·출력·계획·컴파일 오류·바인드·행 보기)을 활성 문서가 SQL·PL/SQL 편집기가 아닐 때 잠시 숨긴다(TSK-13-34).
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
/// </summary>
public sealed partial class DockWorkspace
{
    /// <summary>SQL 창에 딸린 도구 창 ID.</summary>
    internal static readonly IReadOnlySet<string> SqlScopedToolIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "oranda.panel.results",
        "oranda.panel.messages",
        "oranda.panel.output",
        "oranda.panel.plan",
        "oranda.panel.compileErrors",
        "oranda.panel.binds",
        "oranda.panel.rowView",
    };

    /// <summary>위 도구 창을 보이는 문서(SQL·PL/SQL 편집기).</summary>
    internal static readonly IReadOnlySet<string> SqlDocumentIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "oranda.panel.sqlEditor",
        "oranda.panel.plsqlEditor",
    };

    private readonly List<ScopedHide> _scopedHidden = [];
    private Dictionary<IDockable, double>? _scopedProportions;
    private bool _scopedUpdating;
    private bool _scopedPending;
    private string? _showingTool;
    private PanelDocument? _scopedDocument;

    /// <summary>숨긴 도구 창 하나. <paramref name="Index"/> 는 그 묶음의 전체 탭 목록(숨긴 창 포함) 안 자리.</summary>
    private sealed record ScopedHide(PanelTool Tool, IToolDock Dock, int Index, bool WasActive);

    /// <summary>지금 잠시 숨긴 도구 창 ID(시험·진단용).</summary>
    internal IReadOnlyList<string> ScopedHiddenToolIds => _scopedHidden.Select(h => h.Tool.Id).ToList();

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

    /// <summary>활성 문서를 보고 SQL 창 도구 창을 숨기거나 되보인다.</summary>
    internal void UpdateScopedTools()
    {
        // 같은 문서 안에서 도구 창 탭·포커스만 바뀐 것은 무시한다(기능이 숨긴 창을 직접 보였으면 그 문서에 있는 동안 그대로 둔다).
        if (_scopedUpdating || Layout is null || ActiveDocument is not { } doc || ReferenceEquals(doc, _scopedDocument))
        {
            return;
        }

        _scopedDocument = doc;
        if (SqlDocumentIds.Contains(doc.Definition.Id))
        {
            RestoreScopedTools();
        }
        else
        {
            HideScopedTools();
        }
    }

    private void HideScopedTools(string? keep = null)
    {
        if (Layout is null)
        {
            return;
        }

        // 자리는 그 묶음의 전체 탭 목록(앞서 숨긴 창 포함) 기준으로 적는다 — 되넣을 때 작은 자리부터 넣으면 원래 순서가 된다.
        var targets = new List<(PanelTool Tool, IToolDock Dock, int Index, bool Active)>();
        Visit(Layout, d =>
        {
            if (d is IToolDock { VisibleDockables: { } list } group)
            {
                var full = ScopedToolIds(group).Ids;
                targets.AddRange(list.OfType<PanelTool>()
                    .Where(t => SqlScopedToolIds.Contains(t.Id) && !string.Equals(t.Id, keep, StringComparison.Ordinal))
                    .Select(t => (t, group, full.IndexOf(t.Id), ReferenceEquals(group.ActiveDockable, t))));
            }
        });
        if (targets.Count == 0)
        {
            return;
        }

        _scopedUpdating = true;
        try
        {
            foreach (var (tool, dock, index, active) in targets)
            {
                _scopedHidden.Add(new ScopedHide(tool, dock, index, active));
                Factory.RemoveDockable(tool, collapse: false);
            }

            // 빈 묶음은 Dock 이 접고 옆 칸 비율을 늘린다(다음 배치 계산 때). 그 영향을 받는 칸(빈 묶음과 그 위로 함께 빈 칸들의 형제)만
            // 지금 값을 적어 두었다가 되돌린다 — 상관없는 칸을 숨긴 동안 끌어 바꾼 크기는 건드리지 않는다.
            _scopedProportions ??= new Dictionary<IDockable, double>(ReferenceEqualityComparer.Instance);
            foreach (var dock in targets.Select(t => t.Dock).Distinct().Where(d => d.IsEmpty))
            {
                IDockable node = dock;
                while (node.Owner is IDock parent)
                {
                    foreach (var child in (parent.VisibleDockables ?? []).Where(c => c is not IProportionalDockSplitter))
                    {
                        _scopedProportions.TryAdd(child, child.Proportion);
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
    }

    /// <summary>숨긴 도구 창을 모두 원래 자리에 되넣고 접혔던 칸 비율을 돌린다. 숨긴 것이 없으면 아무것도 안 한다.</summary>
    internal void RestoreScopedTools()
    {
        if (_scopedHidden.Count == 0 || Layout is null)
        {
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

            RestoreProportions(inTree, _scopedProportions?.Keys.ToList() ?? []);
        }
        finally
        {
            _scopedHidden.Clear();
            _scopedProportions = null;
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
            if (wasEmpty && _scopedProportions is { } saved)
            {
                // 그 묶음 위로 올라가며 적어 둔 형제 칸 비율만 돌린다.
                var chain = new List<IDockable>();
                IDockable node = h.Dock;
                while (node.Owner is IDock parent)
                {
                    chain.AddRange((parent.VisibleDockables ?? []).Where(saved.ContainsKey));
                    node = parent;
                }

                RestoreProportions(inTree, chain);
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

    private void RestoreProportions(HashSet<IDockable> inTree, IEnumerable<IDockable> which)
    {
        if (_scopedProportions is not { } saved)
        {
            return;
        }

        foreach (var dockable in which)
        {
            if (inTree.Contains(dockable) && saved.TryGetValue(dockable, out var p) && !Equals(dockable.Proportion, p))
            {
                dockable.Proportion = p;
            }
        }
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
    /// 숨기던 중이었고 활성 문서가 여전히 SQL 편집기가 아니면 새 배치에서 다시 숨긴다 — 단 지금 보이라고 한 창(<see cref="ShowTool"/>)은 둔다.
    /// </summary>
    private void EndScopedApply(bool rehide)
    {
        _scopedHidden.Clear();
        _scopedProportions = null;
        _scopedUpdating = false;
        if (rehide && ActiveDocument is { } doc && !SqlDocumentIds.Contains(doc.Definition.Id))
        {
            HideScopedTools(_showingTool);
        }
    }

    /// <summary>저장할 비율: 숨겨서 접힌 칸과 그 형제는 숨기기 직전 값.</summary>
    private double ScopedProportion(IDockable dockable) =>
        _scopedProportions is { } saved && saved.TryGetValue(dockable, out var p) ? p : dockable.Proportion;

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
