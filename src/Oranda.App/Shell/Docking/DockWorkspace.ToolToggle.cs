using System;
using System.Collections.Generic;
using System.Linq;
using Dock.Model.Controls;
using Dock.Model.Core;
using Oranda.App.Shell.Layout;
using Oranda.Core.Shell;
using DockNodeKind = Oranda.App.Shell.Layout.DockNodeKind;
using DockOrientation = Oranda.App.Shell.Layout.DockOrientation;

namespace Oranda.App.Shell.Docking;

/// <summary>
/// 도구 창 하나 켜기·끄기(보기 메뉴·기능). 배치를 다시 짓지 않고 그 창 하나만 넣고 뺀다 — 다시 지으면 도킹 화면 전체를 갈아 끼워
/// 메인 창이 깜박인다(2026-11-16 사용자 신고 「도구 창을 추가할 때마다 화면 전체가 껌벅인다」).
/// <list type="bullet">
/// <item>켜기: 배치 규칙(<see cref="LayoutPlanner.ShowTool"/>)이 고른 묶음이 화면에 있으면 거기에 끼운다. 그 묶음이 비었거나(문서별 잠시 숨김·핀) 접혔으면
/// 끼운 뒤 보이는 크기로 펼친다. 묶음이 없으면 그 자리(아래·오른쪽·왼쪽)에 새 묶음 하나만 지금 배치 트리에 끼운다. 뜬 창에 있던 창은 메인 창의 규칙 위치로 옮긴다.</item>
/// <item>끄기: 그 탭만 뺀다. 묶음이 비면 그 묶음(과 분할선)만 치우고 몫은 문서 쪽 칸에 준다. 잠시 숨긴 창·핀 창이 남은 묶음은 접힌 채 둔다.</item>
/// </list>
/// 배치를 다시 짓는 것은 불러오기(시작·이름 붙인 레이아웃)와 기본 배치로 되돌리기 몫이다.
/// </summary>
public sealed partial class DockWorkspace
{
    /// <summary>도구 묶음이 분할 안에서 이 비율보다 작으면 접힌 것(높이·너비 0)으로 본다.</summary>
    private const double MinShownProportion = 0.05;

    /// <summary>
    /// 문서별로 잠시 숨긴 창을 보이라고 했다: 그 창을 원래 자리에 되넣고 앞으로 가져온다. 지금 문서에서 숨길 까닭이 없는 다른 숨긴 창(활성 문서가 없을 때의 SQL 전용 창)도
    /// 함께 되넣는다 — 나머지는 숨긴 채. 잠시 숨긴 창이 아니면 false.
    /// </summary>
    private bool TryRestoreScopedTool(string toolId, DockLocation location)
    {
        if (!IsScopedHidden(toolId))
        {
            return false;
        }

        var doc = ActiveDocument;
        RestoreScopedTools(id => string.Equals(id, toolId, StringComparison.Ordinal) || DecideToolHere(id, doc) != ToolScopeDecision.Hide); // 숨긴 동안 묶음이 사라졌으면 ShowTool 로 규칙 위치에 보인다
        if (_tools.TryGetValue(toolId, out var tool) && tool.Owner is IDock { VisibleDockables: { } tabs } owner && tabs.Contains(tool))
        {
            owner.ActiveDockable = tool;
            Unfold(owner, PlaceOf(owner, location));
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// 닫힌(또는 뜬 창에 있는) 도구 창을 배치를 다시 짓지 않고 메인 배치에 넣고 활성으로 한다. 넣을 곳을 못 찾으면 false(부르는 쪽의 안전망이 다시 짓는다).
    /// </summary>
    private bool TryInsertTool(LayoutDocument current, ToolPanelDefinition def)
    {
        if (Layout is not IRootDock root || PinnedTool(def.Id) is not null || IsScopedHidden(def.Id) || current.Root is null)
        {
            return false;
        }

        var planned = LayoutPlanner.ShowTool(current, def);
        if (ToolGroups(planned.Root!).FirstOrDefault(g => g.Tools.Contains(def.Id)) is not { } target)
        {
            return false;
        }

        var location = LayoutPlanner.LocationOf(planned.Root!, target) ?? def.DefaultLocation;
        var before = target.Tools.Where(t => !string.Equals(t, def.Id, StringComparison.Ordinal)).ToList();
        IToolDock? live = null;
        if (before.Count > 0)
        {
            // 규칙이 고른 묶음 = 지금 화면의 묶음 중 (잠시 숨긴 창·핀 창까지 센) 탭 목록이 같은 것 하나.
            var matches = 0;
            Visit(root, d =>
            {
                if (d is IToolDock group && ScopedToolIds(group).Ids.SequenceEqual(before, StringComparer.Ordinal))
                {
                    live = group;
                    matches++;
                }
            });
            if (matches != 1)
            {
                return false;
            }
        }

        var tool = GetTool(def.Id);
        if (live is null && DocumentsArea() is null)
        {
            return false;
        }

        DetachElsewhere(tool);
        live ??= EmptyGroupAt(root, location); // × 로 마지막 탭을 닫으면 Dock 이 빈 묶음을 트리에 남긴다: 그 자리면 다시 쓴다(묶음이 쌓이지 않게)
        if (live is null)
        {
            AddGroup(root, location, tool, target.Proportion ?? DefaultShare(location)); // 그 자리에 묶음이 없다: 새 묶음 하나만 끼운다
        }
        else
        {
            InsertInto(live, tool, target.Tools);
            Unfold(live, location);
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>메인 배치에서 <paramref name="location"/> 자리에 있는 빈 도구 묶음(보이는 탭·잠시 숨긴 창·핀 창 모두 없음). 없으면 null.</summary>
    private IToolDock? EmptyGroupAt(IRootDock root, DockLocation location)
    {
        IToolDock? found = null;
        Visit(root, d =>
        {
            if (found is null && d is IToolDock { Alignment: var at } group && at == AlignmentOf(location)
                && !(group.VisibleDockables ?? []).OfType<PanelTool>().Any() && ScopedToolIds(group).Ids.Count == 0)
            {
                found = group;
            }
        });
        return found;
    }

    /// <summary>묶음의 실제 자리(Build·AddGroup 이 정렬로 적는다). 정렬이 없는 묶음(끌어 놓기로 Dock 이 만든 것)은 <paramref name="fallback"/>.</summary>
    private static DockLocation PlaceOf(IDock owner, DockLocation fallback) => owner is IToolDock { Alignment: var a } && a is Alignment.Left or Alignment.Right or Alignment.Bottom
        ? LocationOf(a)
        : fallback;

    /// <summary>창을 묶음 <paramref name="live"/> 의 규칙 자리(<paramref name="planned"/> = 계획한 탭 목록)에 끼운다. 비어 있던 묶음이면 접힐 때 적어 둔 칸 비율을 돌린다.</summary>
    private void InsertInto(IToolDock live, PanelTool tool, IReadOnlyList<string> planned)
    {
        var list = live.VisibleDockables ??= Factory.CreateList<IDockable>();
        var wasEmpty = !list.OfType<PanelTool>().Any();
        var fullAt = Math.Max(0, planned.ToList().IndexOf(tool.Id));
        for (var i = 0; i < _scopedHidden.Count; i++)
        {
            // 잠시 숨긴 창의 자리(전체 탭 목록 기준)는 끼운 창보다 뒤면 한 칸 민다.
            if (ReferenceEquals(_scopedHidden[i].Dock, live) && _scopedHidden[i].Index >= fullAt)
            {
                _scopedHidden[i] = _scopedHidden[i] with { Index = _scopedHidden[i].Index + 1 };
            }
        }

        var index = planned.Take(fullAt).Count(t => list.Any(d => d.Id == t));
        Factory.InsertDockable(live, tool, Math.Clamp(index, 0, list.Count));
        live.ActiveDockable = tool;
        if (wasEmpty && _collapses.Find(c => ReferenceEquals(c.Dock, live)) is { } snapshot)
        {
            RestoreProportions(TreeSet(), snapshot.Values);
            _collapses.Remove(snapshot);
        }
    }

    /// <summary>
    /// 창이 아직 다른 곳에 붙어 있으면 뗀다: 뜬 창(비면 그 창을 닫는다), 메인 배치의 문서 칸(끌어 넣은 도구 창은 닫힌 창으로 센다),
    /// 다시 지을 때 버린 옛 묶음의 참조, × 로 닫아 root 숨김 목록에 남은 옛 자리.
    /// </summary>
    private void DetachElsewhere(PanelTool tool)
    {
        if (tool.Owner is IRootDock { HiddenDockables: { } hidden })
        {
            hidden.Remove(tool); // × 로 닫은 창(HideToolsOnClose): 지우지 않으면 뒤의 핀·펼침이 옛 자리를 쓴다
        }

        if (tool.Owner is IDock { VisibleDockables: { } held } holder && held.Contains(tool))
        {
            if (FloatingWindowOf(tool) is { } window)
            {
                Factory.RemoveDockable(tool, collapse: true);
                CloseIfEmpty(window);
            }
            else if (InMainTree(holder))
            {
                Factory.RemoveDockable(tool, collapse: false);
            }
            else
            {
                held.Remove(tool);
            }
        }

        tool.OriginalOwner = null;
    }

    /// <summary>뜬 창에 문서·도구 창이 하나도 남지 않았으면 닫는다(빈 창이 화면에 남지 않게).</summary>
    private void CloseIfEmpty(IDockWindow window)
    {
        var left = false;
        if (window.Layout is { } layout)
        {
            Visit(layout, d => left |= d is PanelTool or PanelDocument);
        }

        if (left)
        {
            return;
        }

        window.Exit();
        if (Layout is IRootDock { Windows: { } windows } && windows.Contains(window))
        {
            Factory.RemoveWindow(window);
        }
    }

    /// <summary>
    /// 새 도구 묶음 하나(창 <paramref name="tool"/> 하나)를 지금 배치 트리의 <paramref name="location"/> 자리에 끼운다. 다른 칸은 그대로 둔다.
    /// 아래 = 문서 칸 밑, 오른쪽 = [문서 / 아래 묶음] 열 옆(세로 전체, D-048), 왼쪽 = 맨 바깥 가로 분할의 첫 칸(창 전체 높이) — <see cref="LayoutPlanner"/> 의 새 묶음 자리와 같다.
    /// </summary>
    private void AddGroup(IRootDock root, DockLocation location, PanelTool tool, double share)
    {
        var group = Factory.CreateToolDock();
        group.Id = "oranda.tools." + tool.Id;
        group.Proportion = share;
        group.Alignment = AlignmentOf(location);
        group.VisibleDockables = Factory.CreateList<IDockable>(tool);
        group.ActiveDockable = tool;

        var area = DocumentsArea()!;
        switch (location)
        {
            case DockLocation.Bottom:
                Beside(BottomAnchor(), group, Orientation.Vertical, after: true, share);
                break;
            case DockLocation.Right:
                Beside(ColumnOf(area), group, Orientation.Horizontal, after: true, share);
                break;
            default:
                if (root.VisibleDockables![0] is IProportionalDock { Orientation: Orientation.Horizontal, VisibleDockables: { } kids } outer)
                {
                    // 맨 바깥이 이미 가로 분할: 맨 앞 칸으로 끼우고 나머지 칸은 같은 비로 줄인다.
                    foreach (var kid in kids.Where(k => k is not IProportionalDockSplitter && !double.IsNaN(k.Proportion)))
                    {
                        kid.Proportion = Math.Round(kid.Proportion * (1 - share), 4);
                    }

                    Factory.InsertDockable(outer, Factory.CreateProportionalDockSplitter(), 0);
                    Factory.InsertDockable(outer, group, 0);
                    Factory.InitDockable(group, outer);
                }
                else
                {
                    Beside(root.VisibleDockables[0], group, Orientation.Horizontal, after: false, share);
                }

                break;
        }
    }

    /// <summary>
    /// <paramref name="anchor"/> 칸 옆(<paramref name="after"/> 면 뒤)에 <paramref name="cell"/> 을 끼운다. 그 칸이 같은 방향 분할 안이면 형제로 끼우고
    /// 몫(<paramref name="share"/>)은 그 칸에서 뗀다. 아니면 그 칸만 새 분할로 감싼다(감싼 칸은 새 자리로 옮겨 다시 붙는다).
    /// </summary>
    private void Beside(IDockable anchor, IDockable cell, Orientation orientation, bool after, double share)
    {
        var splitter = Factory.CreateProportionalDockSplitter();
        if (anchor.Owner is IProportionalDock { VisibleDockables: { } kids } parent && parent.Orientation == orientation)
        {
            var at = kids.IndexOf(anchor);
            if (!double.IsNaN(anchor.Proportion))
            {
                anchor.Proportion = Math.Round(Math.Max(anchor.Proportion - share, LayoutPlanner.MinDocumentsProportion), 4);
            }

            Factory.InsertDockable(parent, after ? cell : splitter, after ? at + 1 : at);
            Factory.InsertDockable(parent, after ? splitter : cell, after ? at + 1 : at);
            Factory.InitDockable(cell, parent);
            return;
        }

        if (anchor.Owner is not IDock { VisibleDockables: { } siblings } owner)
        {
            return;
        }

        var wrap = Factory.CreateProportionalDock();
        wrap.Id = "oranda.split." + Guid.NewGuid().ToString("N")[..8];
        wrap.Orientation = orientation;
        wrap.Proportion = anchor.Proportion;
        var index = siblings.IndexOf(anchor);
        anchor.Proportion = Math.Round(1 - share, 4);
        cell.Proportion = share;
        wrap.VisibleDockables = after ? Factory.CreateList<IDockable>(anchor, splitter, cell) : Factory.CreateList<IDockable>(cell, splitter, anchor);
        wrap.ActiveDockable = anchor;
        siblings[index] = wrap;
        Factory.InitDockable(wrap, owner);
        if (owner is IRootDock top)
        {
            top.ActiveDockable = wrap;
            top.DefaultDockable = wrap;
        }
    }

    /// <summary>문서 영역(나눈 문서 칸이면 그 칸들을 담은 가장 바깥 분할). 문서 영역이 없으면 null.</summary>
    private IDockable? DocumentsArea()
    {
        IDockable? area = DocumentDocks().FirstOrDefault();
        while (area?.Owner is IProportionalDock parent && IsDocumentsOnly(parent))
        {
            area = parent;
        }

        return area;
    }

    /// <summary>
    /// 새 아래 묶음을 끼울 칸: 문서 칸(좌우로 나눈 문서 칸이면 그 묶음)이 세로 분할 안이면 그 분할 안에서 이어진 문서 칸의 마지막 — 그 뒤에 형제로 끼우면
    /// 문서 칸 컨트롤이 그대로다. 세로 분할 안이 아니면 그 칸 자체(감싼다).
    /// </summary>
    private IDockable BottomAnchor()
    {
        IDockable anchor = DocumentDocks().First();
        while (anchor.Owner is IProportionalDock { Orientation: Orientation.Horizontal } row && IsDocumentsOnly(row))
        {
            anchor = row;
        }

        if (anchor.Owner is IProportionalDock { Orientation: Orientation.Vertical, VisibleDockables: { } kids })
        {
            var cells = kids.Where(k => k is not IProportionalDockSplitter).ToList();
            var at = cells.IndexOf(anchor);
            while (at + 1 < cells.Count && IsDocumentsOnly(cells[at + 1]))
            {
                at++;
            }

            anchor = cells[at];
        }

        return anchor;
    }

    /// <summary>문서 영역에서 세로 분할 부모를 따라 올라간 가장 바깥 세로 열(<see cref="LayoutPlanner"/> 의 같은 이름 함수와 같다).</summary>
    private static IDockable ColumnOf(IDockable area)
    {
        var column = area;
        while (column.Owner is IProportionalDock { Orientation: Orientation.Vertical } parent)
        {
            column = parent;
        }

        return column;
    }

    /// <summary>
    /// 칸이 분할 안에서 접혀(비율 <see cref="MinShownProportion"/> 미만) 보이지 않으면 기본 크기로 펼친다. 몫은 문서 쪽 칸(없으면 가장 큰 칸)에서 뗀다.
    /// 그 위 분할도 접혔으면 함께 펼친다. 하나라도 펼쳤으면 true.
    /// </summary>
    private static bool Unfold(IDockable node, DockLocation location)
    {
        var changed = false;
        var share = DefaultShare(location);
        var cell = node;
        while (cell.Owner is IProportionalDock { VisibleDockables: { } kids } parent)
        {
            if (double.IsNaN(cell.Proportion) || cell.Proportion >= MinShownProportion)
            {
                cell = parent;
                continue;
            }

            // 몫은 다른 칸들에서 같은 비로 뗀다(Dock 이 접을 때 나머지 칸을 같은 비로 늘린 것을 되돌린다).
            var others = kids.Where(k => k is not IProportionalDockSplitter && !ReferenceEquals(k, cell) && !double.IsNaN(k.Proportion)).ToList();
            ScaleTo(others, 1 - share);
            cell.Proportion = share;
            changed = true;
            cell = parent;
        }

        return changed;
    }

    /// <summary>칸들의 비율 합이 <paramref name="total"/> 이 되게 같은 비로 줄이거나 늘린다(합이 0 이면 그대로).</summary>
    private static void ScaleTo(IReadOnlyList<IDockable> cells, double total)
    {
        var sum = cells.Sum(c => c.Proportion);
        if (sum <= 0)
        {
            return;
        }

        foreach (var c in cells)
        {
            c.Proportion = Math.Round(c.Proportion * total / sum, 4);
        }
    }

    /// <summary>
    /// 보이는 도구 창 하나를 배치를 다시 짓지 않고 뺀다. 핀 창이면 사이드바에서 뺀다. 묶음이 비면: 잠시 숨긴 창·핀 창이 남았으면 접힌 채 두고(칸 비율을 적어 둔다),
    /// 아무것도 남지 않으면 그 묶음만 치운다. 뺄 수 없는 모양이면 false(부르는 쪽의 안전망이 다시 짓는다).
    /// </summary>
    private bool TryRemoveTool(string toolId)
    {
        if (!_tools.TryGetValue(toolId, out var tool))
        {
            return false;
        }

        if (PinnedTool(toolId) is not null)
        {
            ClosePinnedPreview();
            Factory.UnpinDockable(tool); // 원래 묶음의 탭으로 돌려 놓은 뒤 아래에서 뺀다
        }

        if (tool.Owner is not IToolDock { VisibleDockables: { } tabs } group || !tabs.Contains(tool))
        {
            return false;
        }

        // 잠시 숨긴 창의 자리(전체 탭 목록 기준, .ScopedTools.cs)는 뺀 창보다 뒤면 한 칸 당긴다.
        var at = ScopedToolIds(group).Ids.IndexOf(toolId);
        for (var i = 0; i < _scopedHidden.Count; i++)
        {
            if (ReferenceEquals(_scopedHidden[i].Dock, group) && _scopedHidden[i].Index > at)
            {
                _scopedHidden[i] = _scopedHidden[i] with { Index = _scopedHidden[i].Index - 1 };
            }
        }

        Factory.RemoveDockable(tool, collapse: false);
        if (!tabs.OfType<PanelTool>().Any())
        {
            if (_scopedHidden.Exists(h => ReferenceEquals(h.Dock, group)))
            {
                SnapshotCollapse(group); // 잠시 숨긴 창이 남는다: 접힌 채 두고, Dock 이 접기 전 칸 비율을 적어 둔다(저장·되보이기가 쓴다)
            }
            else if (ScopedToolIds(group).Ids.Count == 0)
            {
                RemoveCell(group);
            }

            // 핀 창만 남으면 접힌 채 둔다: 비율은 Dock 이 CollapsedProportion 에 두고 저장 때 FoldedShare 가 쓴다(따로 적으면 핀을 빼 다시 보인 뒤에도 옛 0 이 남는다).
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// 빈 칸을 분할에서 치운다(이웃한 분할선 하나와 함께). 몫은 문서 쪽 칸(없으면 남은 첫 칸)이 물려받는다. 분할이 통째로 비면 그 분할도 치운다.
    /// 남은 칸은 옮기지 않는다(자식이 하나만 남은 분할도 그대로 — 옮기면 그 칸 컨트롤을 다시 만든다).
    /// </summary>
    private static void RemoveCell(IDockable cell)
    {
        while (cell.Owner is IProportionalDock { VisibleDockables: { } kids } parent)
        {
            var at = kids.IndexOf(cell);
            if (at < 0)
            {
                return;
            }

            kids.RemoveAt(at);
            if (at > 0 && kids[at - 1] is IProportionalDockSplitter)
            {
                kids.RemoveAt(at - 1);
            }
            else if (at < kids.Count && kids[at] is IProportionalDockSplitter)
            {
                kids.RemoveAt(at);
            }

            cell.Owner = null;
            var cells = kids.Where(k => k is not IProportionalDockSplitter).ToList();
            if (cells.Count == 0 && parent.Owner is IProportionalDock)
            {
                cell = parent;
                continue;
            }

            var heir = cells.FirstOrDefault(ContainsDocuments) ?? cells.FirstOrDefault();
            if (heir is not null && !double.IsNaN(heir.Proportion) && !double.IsNaN(cell.Proportion))
            {
                heir.Proportion = Math.Round(heir.Proportion + cell.Proportion, 4);
            }

            if (parent.ActiveDockable is null || ReferenceEquals(parent.ActiveDockable, cell))
            {
                parent.ActiveDockable = cells.FirstOrDefault();
            }

            return;
        }
    }

    /// <summary>
    /// 묶음 <paramref name="dock"/> 이 막 비었다(탭을 뺀 직후, Dock 이 칸을 접기 전): 영향받는 칸들(그 묶음과 그 위로 함께 빈 칸들의 형제)의 비율을 적는다.
    /// 되넣을 때(<see cref="RestoreScopedTool"/>·<see cref="InsertInto"/>) 돌리고, 접혀 있는 동안 저장(<see cref="ScopedProportion"/>)이 쓴다.
    /// </summary>
    private void SnapshotCollapse(IToolDock dock)
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

    /// <summary>
    /// 저장할 때 접힌 묶음의 비율. 묶음 창을 모두 핀으로 접으면 Dock 이 묶음 비율을 0, 문서 칸을 1 로 바꾸고 접기 전 값은 <see cref="IDockable.CollapsedProportion"/> 에 둔다.
    /// 그대로 0 을 적으면 다시 지을 때 높이 0 묶음이 되므로 접기 전 값(없으면 기본 비율)을 돌려준다. 접히지 않았으면 null.
    /// </summary>
    private double? FoldedShare(IDockable child)
    {
        // 잠시 숨김 기록이 있으면 그 값(ScopedProportion)을 본다: 이미 접힌 뒤에 적은 기록이면 그것도 0 이다.
        if (child is not IToolDock group || (group.VisibleDockables ?? []).OfType<PanelTool>().Any()
            || ScopedProportion(group) is var now && (double.IsNaN(now) || now >= MinShownProportion))
        {
            return null;
        }

        var before = group.CollapsedProportion;
        return !double.IsNaN(before) && before >= MinShownProportion && before < 1 ? before : DefaultShare(LocationOf(group.Alignment));
    }

    /// <summary>
    /// 불러온 배치에서 비율 0(접힘)으로 적힌 도구 묶음을 기본 비율로 되살린다. 몫은 문서 쪽 칸(없으면 가장 큰 칸)에서 뗀다.
    /// 그 묶음 창이 모두 핀이면 짓고 나서 다시 접히므로 화면은 같고, 핀을 빼거나 창을 켜면 보이는 크기로 나온다(DK2-01).
    /// </summary>
    private static DockNode ReviveFoldedGroups(DockNode root)
    {
        DockNode Walk(DockNode node)
        {
            var kids = node.Children ?? [];
            if (kids.Count == 0)
            {
                return node;
            }

            var list = kids.Select(Walk).ToList();
            var folded = new List<(int At, double Share)>();
            for (var i = 0; i < kids.Count && kids.Count > 1; i++)
            {
                if (kids[i].Kind != DockNodeKind.ToolGroup || kids[i].Proportion is not { } p || p >= MinShownProportion)
                {
                    continue;
                }

                var share = DefaultShare(LayoutPlanner.LocationOf(root, kids[i]) ?? DockLocation.Bottom);
                folded.Add((i, share));
            }

            // 다른 칸은 같은 비로 줄인다(접힐 때 Dock 이 같은 비로 늘렸다). 비율이 없는 칸이 있으면 접힌 칸만 고친다(Dock 이 지을 때 맞춘다).
            if (folded.Count > 0)
            {
                var rest = Enumerable.Range(0, list.Count).Where(at => !folded.Exists(f => f.At == at)).ToList();
                var sum = rest.Sum(at => list[at].Proportion ?? double.NaN);
                if (!double.IsNaN(sum) && sum > 0)
                {
                    var total = 1 - folded.Sum(f => f.Share);
                    foreach (var at in rest)
                    {
                        list[at] = list[at] with { Proportion = Math.Round(list[at].Proportion!.Value * total / sum, 4) };
                    }
                }

                foreach (var (at, share) in folded)
                {
                    list[at] = list[at] with { Proportion = share };
                }
            }

            return node with { Children = list };
        }

        return Walk(root);
    }

    /// <summary>
    /// 지을 배치에 틀을 씌운다: 문서 칸이 세로 분할 안이 아니면 자식 하나짜리 세로 분할(문서 열)로, 맨 바깥이 가로 분할이 아니면 자식 하나짜리 가로 분할로 감싼다.
    /// 그러면 뒤에 아래·오른쪽·왼쪽 묶음을 켤 때 언제나 이 분할들에 형제로만 끼워(<see cref="Beside"/>) 문서 칸 컨트롤을 다시 만들지 않는다.
    /// 저장(<see cref="Capture"/>)은 자식 하나짜리 분할을 접으므로 배치 파일은 그대로다.
    /// </summary>
    private static DockNode WithFrame(DockNode root)
    {
        var path = PathTo(root, n => n.Kind == DockNodeKind.Documents);
        if (path is not null && (path.Count < 2 || path[^2] is not { Kind: DockNodeKind.Split, Orientation: DockOrientation.Vertical }))
        {
            var docs = path[^1];
            var column = new DockNode { Kind = DockNodeKind.Split, Orientation = DockOrientation.Vertical, Proportion = docs.Proportion, Children = [docs with { Proportion = 1 }] };
            root = ReplaceNode(root, docs, column);
        }

        return root is { Kind: DockNodeKind.Split, Orientation: DockOrientation.Horizontal }
            ? root
            : new DockNode { Kind = DockNodeKind.Split, Orientation = DockOrientation.Horizontal, Proportion = root.Proportion, Children = [root with { Proportion = 1 }] };
    }

    private static List<DockNode>? PathTo(DockNode node, Func<DockNode, bool> match)
    {
        if (match(node))
        {
            return [node];
        }

        foreach (var child in node.Children ?? [])
        {
            if (PathTo(child, match) is { } sub)
            {
                sub.Insert(0, node);
                return sub;
            }
        }

        return null;
    }

    private static DockNode ReplaceNode(DockNode node, DockNode target, DockNode replacement) =>
        ReferenceEquals(node, target) ? replacement
        : (node.Children ?? []).Count == 0 ? node
        : node with { Children = [.. node.Children!.Select(c => ReplaceNode(c, target, replacement))] };

    private static bool HasDocuments(DockNode node) => node.Kind == DockNodeKind.Documents || (node.Children ?? []).Any(HasDocuments);

    private static bool ContainsDocuments(IDockable dockable)
    {
        var found = false;
        Visit(dockable, d => found |= d is IDocumentDock);
        return found;
    }

    private static double DefaultShare(DockLocation location) => location switch
    {
        DockLocation.Right => LayoutPlanner.RightProportion,
        DockLocation.Bottom => LayoutPlanner.BottomProportion,
        _ => LayoutPlanner.LeftProportion,
    };

    private static Alignment AlignmentOf(DockLocation? location) => location switch
    {
        DockLocation.Right => Alignment.Right,
        DockLocation.Bottom => Alignment.Bottom,
        _ => Alignment.Left,
    };

    private static DockLocation LocationOf(Alignment alignment) => alignment switch
    {
        Alignment.Right => DockLocation.Right,
        Alignment.Bottom => DockLocation.Bottom,
        _ => DockLocation.Left,
    };

    private bool InMainTree(IDockable dockable)
    {
        var found = false;
        if (Layout is not null)
        {
            Visit(Layout, d => found |= ReferenceEquals(d, dockable));
        }

        return found;
    }

    private static IEnumerable<DockNode> ToolGroups(DockNode node) =>
        node.Kind == DockNodeKind.ToolGroup ? [node] : (node.Children ?? []).SelectMany(ToolGroups);
}
