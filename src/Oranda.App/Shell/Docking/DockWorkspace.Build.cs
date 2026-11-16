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
/// 배치 짓기(DTO → Dock 모델): 불러오기·기본 배치로 되돌리기·안전망 다시 짓기. 나눈 문서 칸 유지(<see cref="TakeSplitDocumentArea"/>),
/// 높이 0 묶음 되살리기(<see cref="ReviveFoldedGroups"/>), 문서 열·바깥 가로 분할 틀(<see cref="WithFrame"/>).
/// </summary>
public sealed partial class DockWorkspace
{
    /// <summary>마지막으로 적용한 배치의 이주 표지(<see cref="Capture"/> 가 저장 때 그대로 싣는다).</summary>
    private IReadOnlyList<string>? _migrations;

    /// <summary>저장된 배치를 현재 등록 목록에 맞춰 도킹 모델로 짓는다. 열려 있던 문서는 새 문서 영역으로 옮긴다.</summary>
    public void Apply(LayoutDocument layout) => Apply(layout, keepSplitDocuments: false);

    /// <param name="layout">지을 배치.</param>
    /// <param name="keepSplitDocuments">
    /// 참이면 사용자가 나눈 문서 영역을 그대로 새 배치에 옮긴다. 도구 창을 보이거나 숨기는 길이 쓴다: 세션 창 자동 표시·실행 중 도구 창 열기처럼
    /// 사용자가 배치를 고르지 않은 일로 분할이 풀리면 안 된다(2026-11-05 사용자 신고). 시작 복원·이름 붙인 레이아웃·기본 배치로 되돌리기는 거짓(한 영역으로 합친다).
    /// </param>
    private void Apply(LayoutDocument layout, bool keepSplitDocuments)
    {
        var rehide = BeginScopedApply(); // 짓는 동안 생기는 활성 변경은 숨기기 판단에 쓰지 않는다(.ScopedTools.cs)
        try
        {
            ClosePinnedPreview(); // 펼친 채 지으면 그 창의 OriginalOwner 가 옛 묶음에 남아 다음 펼침·접힘에서 닫힌 창이 된다
            ApplyCore(layout, keepSplitDocuments);
        }
        finally
        {
            EndScopedApply(rehide);
        }
    }

    private void ApplyCore(LayoutDocument layout, bool keepSplitDocuments)
    {
        _pinHomes.Clear(); // 옛 배치의 묶음이다
        var planned = LayoutPlanner.Reconcile(layout, PlanningPanels());
        planned = planned with { Root = ReviveFoldedGroups(planned.Root!) }; // 높이 0 으로 저장된 도구 묶음은 기본 크기로(DK2-01)
        var frame = WithFrame(planned.Root!); // 문서 세로 열·바깥 가로 분할을 늘 지어 둔다: 뒤에 도구 묶음을 켜면 형제로만 끼운다(.ToolToggle.cs)
        _migrations = planned.Migrations;
        var active = ActiveDocument; // 옛 영역에서 문서를 빼기 전에 읽는다(뺀 뒤에는 탭 줄이 남은 탭을 활성으로 써 넣어 다른 문서가 된다)
        var splitArea = keepSplitDocuments && Layout is not null ? TakeSplitDocumentArea() : null; // 나누지 않은 영역은 지금까지처럼 새 영역으로 옮긴다
        var inSplitArea = new HashSet<IDockable>(ReferenceEqualityComparer.Instance);
        if (splitArea is not null)
        {
            Visit(splitArea, d => inSplitArea.Add(d));
        }

        // 떠 있던 문서와 나눈 칸 밖의 문서 영역에 있던 문서는 메인 문서 영역으로 들인다.
        var keepDocuments = Layout is null ? [] : Documents.Concat(FloatingDocuments()).Where(d => !inSplitArea.Contains(d)).ToList();
        foreach (var doc in keepDocuments.Where(d => d.Owner is IDock { VisibleDockables: { } own } && own.Contains(d)))
        {
            // 옛 창이 닫힐 때 그 안의 문서까지 정리되지 않도록 옛 문서 영역에서 먼저 뺀다.
            ((IDock)doc.Owner!).VisibleDockables!.Remove(doc);
        }

        if (splitArea is not null)
        {
            if (splitArea.Owner is IDock { VisibleDockables: { } siblings })
            {
                siblings.Remove(splitArea); // 나눈 칸도 같은 이유로 옛 트리에서 뺀다
            }

            DropNonDocuments(splitArea); // 칸에 끌어 넣은 도구 창은 DTO 에서 닫힌 창이므로 도구 묶음과 겹쳐 들어가지 않게 뺀다
        }

        var rootNode = planned.Root!;
        var dockRoot = Factory.CreateRootDock();
        dockRoot.Id = "oranda.root";
        dockRoot.IsCollapsable = false;
        var content = Build(frame, frame, splitArea);
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

            if (active is not null && keepDocuments.Contains(active))
            {
                documents.ActiveDockable = active;
            }
            else if (splitArea is null || documents.ActiveDockable is null)
            {
                documents.ActiveDockable = keepDocuments.LastOrDefault() ?? documents.ActiveDockable; // 나눈 칸을 유지해도 합쳐 넣은 문서가 있는 빈 첫 칸은 활성 탭이 있어야 한다
            }
        }

        Factory.InitLayout(dockRoot);
        _pendingPins = planned.PinnedTools ?? [];
        ApplyEmptyContent();
        LayoutRebuilt?.Invoke(this, EventArgs.Empty); // 보기가 Layout 을 갈아 끼우며 핀을 다시 뺀다(RestorePins)
        RestorePins(fromView: false); // 보기가 아직 없을 때(시작 직후·시험)
    }

    /// <summary>기본 배치로 되돌린다(<c>oranda.view.resetLayout</c>).</summary>
    public void Reset() => Apply(LayoutPlanner.CreateDefault(_registry.Panels));

    /// <summary>
    /// 지금 배치를 그대로 다시 짓는다(분할 문서 칸은 유지). 도구 창 켜기·끄기는 창 하나만 움직이므로 다시 짓는 길은 시험이 직접 부를 때 쓴다.
    /// </summary>
    internal void Rebuild() => Apply(LayoutPlanner.Reconcile(Capture(), PlanningPanels()), keepSplitDocuments: true);

    private IDockable Build(DockNode node, DockNode root, IProportionalDock? splitArea)
    {
        switch (node.Kind)
        {
            case DockNodeKind.Split:
                {
                    var split = Factory.CreateProportionalDock();
                    split.Id = NewSplitId();
                    split.Orientation = node.Orientation == DockOrientation.Horizontal ? Orientation.Horizontal : Orientation.Vertical;
                    split.Proportion = node.Proportion ?? double.NaN;
                    var kids = Factory.CreateList<IDockable>();
                    foreach (var child in node.Children ?? [])
                    {
                        if (kids.Count > 0)
                        {
                            kids.Add(Factory.CreateProportionalDockSplitter());
                        }

                        kids.Add(Build(child, root, splitArea));
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
                    group.Alignment = AlignmentOf(LayoutPlanner.LocationOf(root, node));
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
                    if (splitArea is not null)
                    {
                        splitArea.Proportion = node.Proportion ?? double.NaN; // 칸 안쪽 비율은 사용자가 둔 그대로 둔다
                        return splitArea;
                    }

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
    /// 사용자가 나눈 문서 칸 전체를 하나의 분할로 돌려준다(DTO 의 Documents 노드 하나와 대응). 나누지 않았으면 null.
    /// 첫 문서 영역에서 위로, 문서 영역만 담은 분할까지 올라간다. 도구 창과 한 분할에 나란히 펼쳐진 연속 문서 칸
    /// (위아래 분할: 세로 [문서, 문서, 도구]) 은 새 분할로 감싼다 - 이때 칸을 옛 부모에서 떼어 낸다.
    /// 인접하지 않은 문서 영역은 포함하지 않으므로 그 문서는 첫 영역으로 합쳐진다.
    /// </summary>
    private IProportionalDock? TakeSplitDocumentArea()
    {
        var area = DocumentsArea();

        // 자식 하나짜리 틀 분할(WithFrame)은 사용자가 나눈 칸이 아니다: 안으로 내려간다(옮겨 가면 다시 지을 때마다 틀이 겹겹이 쌓인다).
        while (area is IProportionalDock { VisibleDockables: { } only } && only.Count(k => k is not IProportionalDockSplitter) == 1)
        {
            area = only.First(k => k is not IProportionalDockSplitter);
        }

        if (area is null)
        {
            return null;
        }

        if (area.Owner is not IProportionalDock { VisibleDockables: { } siblings } host)
        {
            return area as IProportionalDock;
        }

        var cells = siblings.Where(k => k is not IProportionalDockSplitter).ToList();
        var first = cells.IndexOf(area);
        var last = first;
        while (first > 0 && IsDocumentsOnly(cells[first - 1]))
        {
            first--;
        }

        while (last < cells.Count - 1 && IsDocumentsOnly(cells[last + 1]))
        {
            last++;
        }

        if (first == last)
        {
            return area as IProportionalDock;
        }

        var run = cells.GetRange(first, last - first + 1);
        var shares = run.Select(ScopedProportion).ToList();
        var wrapper = Factory.CreateProportionalDock();
        wrapper.Id = NewSplitId();
        wrapper.Orientation = host.Orientation;
        var total = shares.Sum();
        var known = shares.All(s => !double.IsNaN(s) && s > 0);
        wrapper.Proportion = known ? total : double.NaN; // 바깥 비율은 Build 가 DTO 값으로 다시 쓴다
        var kids = Factory.CreateList<IDockable>();
        for (var i = 0; i < run.Count; i++)
        {
            if (kids.Count > 0)
            {
                kids.Add(Factory.CreateProportionalDockSplitter());
            }

            siblings.Remove(run[i]); // 옛 부모가 닫힐 때 문서까지 정리되지 않도록 떼어 낸다
            run[i].Proportion = known ? shares[i] / total : 1.0 / run.Count; // 안쪽 비율은 합이 1 이 되게 다시 맞춘다
            run[i].Owner = wrapper;
            kids.Add(run[i]);
        }

        wrapper.VisibleDockables = kids;
        wrapper.ActiveDockable = run[0];
        return wrapper;
    }

    /// <summary>유지하는 문서 칸 안의 문서 아닌 탭(끌어 넣은 도구 창)을 뺀다. 빠진 탭이 활성이었으면 남은 마지막 탭을 활성으로 한다.</summary>
    private static void DropNonDocuments(IDockable area)
    {
        if (area is not IDock dock)
        {
            return;
        }

        if (dock is IDocumentDock { VisibleDockables: { } tabs } documentDock)
        {
            foreach (var extra in tabs.Where(t => t is not PanelDocument).ToList())
            {
                tabs.Remove(extra);
                extra.Owner = null;
            }

            if (documentDock.ActiveDockable is not null && !tabs.Contains(documentDock.ActiveDockable))
            {
                documentDock.ActiveDockable = tabs.LastOrDefault();
            }

            return;
        }

        foreach (var child in (dock.VisibleDockables ?? []).ToList())
        {
            DropNonDocuments(child);
        }
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
                ScaleUnfolded(list, folded);
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
    /// 접힌 칸(<paramref name="folded"/>)에 몫을 돌려줄 때 나머지 칸 비율을 같은 비로 줄여 합이 1 - 접힌 몫 합이 되게 한다.
    /// 나머지 칸 중 비율이 없는 칸이 있으면(합이 NaN) 그대로 둔다. 접힌 칸 자신의 값은 부르는 쪽이 넣는다.
    /// </summary>
    private static void ScaleUnfolded(List<DockNode> list, List<(int At, double Share)> folded)
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
    }

    private static string NewSplitId() => "oranda.split." + Guid.NewGuid().ToString("N")[..8];

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
}
