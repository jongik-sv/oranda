using System;
using System.Collections.Generic;
using System.Linq;
using Oranda.Core.Shell;

namespace Oranda.App.Shell.Layout;

/// <summary>
/// 도킹 배치 계획(순수 로직, Dock 라이브러리를 모른다). 기본 배치 만들기(ui-shell.md 2.1)와
/// 저장된 배치를 지금 등록된 도구 창에 맞추는 일(2.1 「복원할 때」)을 한다.
/// 저장 파일은 <see cref="LayoutDocument"/> DTO 이므로 이 계획도 DTO 만 다룬다.
/// </summary>
public static class LayoutPlanner
{
    /// <summary>왼쪽 도구 창이 가로에서 차지하는 비율(280 DIP / 1280 DIP 기준 약 0.22).</summary>
    public const double LeftProportion = 0.22;

    public const double RightProportion = 0.23;

    /// <summary>아래 도구 창이 세로에서 차지하는 비율(220 DIP / 800 DIP 기준 약 0.28).</summary>
    public const double BottomProportion = 0.28;

    /// <summary>도구 창을 끼워 넣을 때 문서 영역이 이보다 좁아지지 않게 한다.</summary>
    public const double MinDocumentsProportion = 0.2;

    /// <summary>바인드 변수·템플릿 창을 기본으로 여는 이주(TSK-13-16)의 표지 이름.</summary>
    public const string RightToolsOpenMigration = "rightToolsOpen";

    /// <summary>
    /// 오른쪽 도구 창(템플릿·바인드 변수 …)을 문서 옆에서 [문서 / 아래 묶음] 열의 오른쪽으로 옮기는 일회성 이주의 표지 이름.
    /// 표지 없는 저장 배치에서 문서 바로 옆(아래 묶음 위)에 붙은 오른쪽 창을 한 번 옮긴다(D-048). 이후 사용자가 끌어 놓은 배치는 그대로 둔다.
    /// </summary>
    public const string RightToolsFullHeightMigration = "rightToolsFullHeight";

    /// <summary>
    /// 표지 없는 옛 배치에 한 번 여는 오른쪽 도구 창(위에서 아래 순서, TSK-13-16 이주). 새 기본 배치는 이 창들도 닫아 둔다(D-019).
    /// </summary>
    private static readonly string[] OpenRightTools = ["oranda.panel.binds", "oranda.panel.templates"];

    /// <summary>
    /// 기본 배치의 아래 묶음에 넣는 도구 창(D-019, TSK-09-05): 조회 결과·메시지·DBMS 출력. 실행 계획·컴파일 오류·바인드 변수는
    /// 필요할 때 기능이 <c>ActivateTool</c> 로 자기 기본 위치에 열고, 실행 이력·변경 일지는 보기 > 도구 창에서 연다.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultBottomTools = ["oranda.panel.results", "oranda.panel.messages", "oranda.panel.output"];

    /// <summary>
    /// 아래 묶음에 함께 두는 MyBatis 조합 검사 창: MyBatis 문서에서만 보이는 문서 범위 창이라(DockWorkspace.ScopedTools) SQL 탭에서는
    /// 보이지 않는다. 기본에서 빼면 MyBatis 문서를 열어도 나타나지 않아 그대로 둔다.
    /// </summary>
    private const string MyBatisCombosTool = "oranda.panel.mybatisCombos";

    /// <summary>
    /// 기본 배치에서 닫아 두는 도구 창(위치와 상관없이 hiddenTools 로 간다). 「세션」 창(TSK-14-04, D-034)은 작업 세션이 2개 이상일 때
    /// 기능이 보이므로 처음부터 열어 두지 않는다. 저장된 배치에 없는 창이 새로 등록돼도 열지 않고 닫힌 창으로 둔다.
    /// </summary>
    private static readonly string[] DefaultHiddenTools = ["oranda.panel.sessions"];

    /// <summary>문서 영역 노드는 트리에 정확히 1개이고, 그 노드의 <see cref="DockNode.Tools"/> 는 쓰지 않는다.</summary>
    public static LayoutDocument CreateDefault(IEnumerable<ToolPanelDefinition> panels)
    {
        var tools = panels.Where(p => p.Kind == ToolPanelKind.Tool).ToList();
        var left = IdsAt(tools, DockLocation.Left).Where(id => !DefaultHiddenTools.Contains(id)).ToList();
        var allBottom = IdsAt(tools, DockLocation.Bottom);
        var bottom = allBottom.Where(id => DefaultBottomTools.Contains(id) || id == MyBatisCombosTool).ToList();

        // 아래는 조회 결과·메시지·DBMS 출력만 열고, 나머지 아래 창과 오른쪽 창 전부(바인드 변수·템플릿 포함)는 기본 닫힘(D-019):
        // 트리에 넣지 않고 hiddenTools 에 둔다. 처음 열 때는 자기 기본 위치로 간다(LayoutPlanner.ShowTool).
        // 닫힌 창 순서는 등록 순서(왼쪽 → 아래 → 오른쪽)를 따른다(DockWorkspace.Capture 와 같게).
        var hidden = IdsAt(tools, DockLocation.Left).Where(DefaultHiddenTools.Contains)
            .Concat(allBottom.Where(id => !bottom.Contains(id))).Concat(IdsAt(tools, DockLocation.Right)).ToList();
        var documents = new DockNode { Kind = DockNodeKind.Documents };

        // 가로: [왼쪽 묶음(창 전체 높이), 세로: [문서, 아래 묶음]] — 오렌지처럼 스키마 브라우저가 왼쪽을 위아래로 다 쓰고,
        // 아래 묶음(조회 결과·DBMS 출력 …)은 문서 영역 밑에만 둔다.
        DockNode column = documents;
        if (bottom.Count > 0)
        {
            column = new DockNode
            {
                Kind = DockNodeKind.Split,
                Orientation = DockOrientation.Vertical,
                Children = [documents with { Proportion = 1 - BottomProportion }, Group(bottom, BottomProportion)],
            };
        }

        DockNode root = column;
        if (left.Count > 0)
        {
            root = new DockNode
            {
                Kind = DockNodeKind.Split,
                Orientation = DockOrientation.Horizontal,
                Children = [Group(left, LeftProportion), column with { Proportion = 1 - LeftProportion }],
            };
        }

        // 이주 표지는 남긴다: 새 기본 배치에 옛 이주(바인드 변수·템플릿 한 번 열기)가 다시 돌지 않게.
        return new LayoutDocument { Root = root, HiddenTools = hidden, Migrations = [RightToolsOpenMigration, RightToolsFullHeightMigration] };
    }

    /// <summary>
    /// 저장된 배치를 현재 등록 목록에 맞춘다: 등록되지 않은 도구 창 ID 는 버리고(플러그인 제거 등),
    /// 새로 등록된 도구 창은 자기 기본 위치에 더한다. 문서 영역이 1개가 아니면 기본 배치로 되돌린다.
    /// </summary>
    public static LayoutDocument Reconcile(LayoutDocument? saved, IEnumerable<ToolPanelDefinition> panels)
    {
        var all = panels.ToList();
        if (saved?.Root is null)
        {
            var fresh = CreateDefault(all);
            return saved is null ? fresh : saved with { Root = fresh.Root, HiddenTools = fresh.HiddenTools, Migrations = fresh.Migrations };
        }

        var known = all.Where(p => p.Kind == ToolPanelKind.Tool).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var root = Prune(saved.Root, known, seen);
        if (root is null || CountDocuments(root) != 1)
        {
            var fresh = CreateDefault(all);
            return saved with { Root = fresh.Root, HiddenTools = fresh.HiddenTools, Migrations = fresh.Migrations };
        }

        var hidden = (saved.HiddenTools ?? [])
            .Where(id => known.Contains(id) && !seen.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // 일회성 이주: 표지가 없는 옛 배치는 바인드 변수·템플릿 창을 한 번만 연다. 표지를 남기므로 이후 사용자가 닫은 창은 닫힌 채 유지된다.
        var migrations = (saved.Migrations ?? []).ToList();
        if (!migrations.Contains(RightToolsOpenMigration))
        {
            var toOpen = OpenRightTools.Where(id => known.Contains(id) && !seen.Contains(id)).ToList();
            hidden = hidden.Where(id => !toOpen.Contains(id)).ToList();
            if (toOpen.Count > 0)
            {
                root = InsertRight(root, StackedGroups(toOpen));
                seen.UnionWith(toOpen);
            }

            migrations.Add(RightToolsOpenMigration);
        }

        // 일회성 이주(D-048): 문서 바로 옆(아래 묶음 위)에 붙은 오른쪽 창을 [문서 / 아래 묶음] 열의 오른쪽으로 옮겨 세로 전체를 쓰게 한다.
        if (!migrations.Contains(RightToolsFullHeightMigration))
        {
            root = LiftRightBesideColumn(root);
            migrations.Add(RightToolsFullHeightMigration);
        }

        var hiddenSet = hidden.ToHashSet(StringComparer.Ordinal);

        var order = all.Where(p => p.Kind == ToolPanelKind.Tool)
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Order, StringComparer.Ordinal);
        foreach (var def in all.Where(p => p.Kind == ToolPanelKind.Tool).OrderBy(p => p.Order))
        {
            if (!seen.Contains(def.Id) && !hiddenSet.Contains(def.Id))
            {
                if (DefaultHiddenTools.Contains(def.Id))
                {
                    hidden.Add(def.Id);
                    continue;
                }

                root = AddTool(root, def.Id, def.DefaultLocation, order);
                seen.Add(def.Id);
            }
        }

        return saved with { Root = Rebalance(root), HiddenTools = hidden, Migrations = migrations };
    }

    /// <summary>
    /// 도구 창을 보이게 한다: 이미 트리에 있으면 그 묶음에서 활성으로, 없으면 기본 위치 묶음에 더한다(없으면 새 묶음).
    /// <paramref name="layout"/> 은 <see cref="Reconcile"/> 을 거친 값이어야 한다.
    /// </summary>
    public static LayoutDocument ShowTool(LayoutDocument layout, ToolPanelDefinition tool)
    {
        var root = layout.Root ?? throw new ArgumentException("Root 가 없다.", nameof(layout));
        var hidden = (layout.HiddenTools ?? []).Where(id => id != tool.Id).ToList();
        if (!ToolIds(root).Contains(tool.Id))
        {
            root = AddTool(root, tool.Id, tool.DefaultLocation);
        }

        var group = FindGroups(root).First(g => (g.Tools ?? []).Contains(tool.Id));
        root = Replace(root, group, group with { ActiveTool = tool.Id });
        return layout with { Root = root, HiddenTools = hidden };
    }

    /// <summary>도구 창을 닫는다(트리에서 빼고 <see cref="LayoutDocument.HiddenTools"/> 에 둔다).</summary>
    public static LayoutDocument HideTool(LayoutDocument layout, string toolId)
    {
        var root = layout.Root ?? throw new ArgumentException("Root 가 없다.", nameof(layout));
        var ids = ToolIds(root).Where(id => id != toolId).ToHashSet(StringComparer.Ordinal);
        var pruned = Prune(root, ids, new HashSet<string>(StringComparer.Ordinal)) ?? root;
        var hidden = (layout.HiddenTools ?? []).Append(toolId).Distinct(StringComparer.Ordinal).ToList();
        return layout with { Root = pruned, HiddenTools = hidden };
    }

    /// <summary>
    /// 트리만 다듬는다: 등록되지 않았거나 겹친 도구 창을 빼고, 비었거나 자식이 하나뿐인 분할을 접는다.
    /// 읽어낸 배치를 DTO 규칙(문서 영역 1개)에 맞출 때 쓴다. 결과가 없으면 null.
    /// </summary>
    public static DockNode? Normalize(DockNode root, IEnumerable<ToolPanelDefinition> panels)
    {
        var known = panels.Where(p => p.Kind == ToolPanelKind.Tool).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        return Prune(root, known, new HashSet<string>(StringComparer.Ordinal)) is { } pruned ? Rebalance(pruned) : null;
    }

    /// <summary>트리에 들어 있는 도구 창 ID(앞에서부터).</summary>
    public static IReadOnlyList<string> ToolIds(DockNode? root)
    {
        var list = new List<string>();
        Walk(root, n =>
        {
            if (n.Kind == DockNodeKind.ToolGroup)
            {
                list.AddRange(n.Tools ?? []);
            }
        });
        return list;
    }

    /// <summary>
    /// 도구 모음 노드가 문서 영역 기준 어느 쪽에 있는지(왼쪽·오른쪽·아래). 문서 영역과 같은 가로 분할에서
    /// 앞이면 왼쪽, 뒤면 오른쪽, 세로 분할에서 뒤면 아래(앞이면 왼쪽으로 본다). 찾지 못하면 null.
    /// </summary>
    public static DockLocation? LocationOf(DockNode root, DockNode group)
    {
        var path = PathTo(root, n => ReferenceEquals(n, group));
        var docs = PathTo(root, n => n.Kind == DockNodeKind.Documents);
        if (path is null || docs is null)
        {
            return null;
        }

        var depth = 0;
        while (depth < path.Count && depth < docs.Count && ReferenceEquals(path[depth], docs[depth]))
        {
            depth++;
        }

        // path[depth-1] = 둘이 갈라지는 분할, path[depth] = 그룹 쪽 가지, docs[depth] = 문서 쪽 가지.
        if (depth == 0 || depth >= path.Count || depth >= docs.Count)
        {
            return null;
        }

        var split = path[depth - 1];
        var kids = split.Children ?? [];
        var groupIndex = kids.ToList().FindIndex(c => ReferenceEquals(c, path[depth]));
        var docsIndex = kids.ToList().FindIndex(c => ReferenceEquals(c, docs[depth]));
        var after = groupIndex > docsIndex;
        return split.Orientation == DockOrientation.Horizontal
            ? (after ? DockLocation.Right : DockLocation.Left)
            : (after ? DockLocation.Bottom : DockLocation.Left);
    }

    /// <summary>같은 구조·같은 도구 구성인지(비율은 오차 허용). 저장·복원 시험용.</summary>
    public static bool SameShape(DockNode? a, DockNode? b, double tolerance = 1e-6)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (a.Kind != b.Kind || a.Orientation != b.Orientation || a.ActiveTool != b.ActiveTool)
        {
            return false;
        }

        if ((a.Proportion is null) != (b.Proportion is null)
            || (a.Proportion is { } pa && b.Proportion is { } pb && Math.Abs(pa - pb) > tolerance))
        {
            return false;
        }

        if (!(a.Tools ?? []).SequenceEqual(b.Tools ?? []))
        {
            return false;
        }

        var ca = a.Children ?? [];
        var cb = b.Children ?? [];
        return ca.Count == cb.Count && ca.Zip(cb).All(p => SameShape(p.First, p.Second, tolerance));
    }

    /// <summary>
    /// 같은 분할 안 형제들의 비율이 모두 있으면 합을 1 로 맞춘다(Dock 이 시작할 때 같은 일을 하므로, 저장값이 미리 맞아 있어야
    /// 「저장한 배치 = 복원한 배치」가 된다).
    /// </summary>
    internal static DockNode Rebalance(DockNode node)
    {
        var kids = (node.Children ?? []).Select(Rebalance).ToList();
        if (kids.Count > 1 && kids.All(k => k.Proportion is > 0))
        {
            var sum = kids.Sum(k => k.Proportion!.Value);
            if (Math.Abs(sum - 1) > 0.002)
            {
                kids = [.. kids.Select(k => k with { Proportion = Math.Round(k.Proportion!.Value / sum, 4) })];
            }
        }

        return kids.Count == 0 ? node : node with { Children = kids };
    }

    private static List<string> IdsAt(IEnumerable<ToolPanelDefinition> tools, DockLocation at) =>
        tools.Where(t => t.DefaultLocation == at)
            .OrderBy(t => t.Order)
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .Select(t => t.Id)
            .ToList();

    private static DockNode Group(List<string> ids, double proportion) => new()
    {
        Kind = DockNodeKind.ToolGroup,
        Tools = ids,
        ActiveTool = ids[0],
        Proportion = proportion,
    };

    private static DockNode? Prune(DockNode node, HashSet<string> known, HashSet<string> seen)
    {
        switch (node.Kind)
        {
            case DockNodeKind.Documents:
                return node with { Children = [], Tools = [], ActiveTool = null };

            case DockNodeKind.ToolGroup:
                var tools = (node.Tools ?? []).Where(id => known.Contains(id) && seen.Add(id)).ToList();
                if (tools.Count == 0)
                {
                    return null;
                }

                var active = node.ActiveTool is { } a && tools.Contains(a) ? a : tools[0];
                return node with { Tools = tools, ActiveTool = active, Children = [] };

            default:
                var original = node.Children ?? [];
                var pruned = original.Select(c => Prune(c, known, seen)).ToList();
                var kids = pruned.OfType<DockNode>().ToList();
                if (kids.Count == 0)
                {
                    return null;
                }

                // 빠진 칸의 몫은 문서 영역 쪽 칸이 물려받는다(오른쪽 창을 닫으면 왼쪽 창이 넓어지지 않고 편집기·결과가 넓어진다).
                if (kids.Count > 1 && kids.Count < original.Count && original.All(c => c.Proportion is > 0)
                    && kids.FindIndex(k => FindDocuments(k) is not null) is var heir and >= 0)
                {
                    var lost = original.Where((_, i) => pruned[i] is null).Sum(c => c.Proportion!.Value);
                    kids[heir] = kids[heir] with { Proportion = Math.Round(kids[heir].Proportion!.Value + lost, 4) };
                }

                // 자식이 하나 남으면 분할을 없애고 그 자식이 분할의 비율을 물려받는다.
                return kids.Count == 1 ? kids[0] with { Proportion = node.Proportion } : node with { Children = kids };
        }
    }

    /// <summary>
    /// 도구 창을 기본 위치 묶음에 더한다(없으면 새 묶음). <paramref name="order"/> 가 있으면 묶음 안에서 자기 순서 자리에
    /// 넣는다(새로 등록된 창이 저장된 배치의 맨 뒤가 아니라 기본 배치와 같은 자리에 오게). 없으면 맨 뒤에 붙인다.
    /// </summary>
    private static DockNode AddTool(DockNode root, string id, DockLocation location, Dictionary<string, int>? order = null)
    {
        var existing = FindGroups(root).FirstOrDefault(g => LocationOf(root, g) == location);
        if (existing is not null)
        {
            var tools = (existing.Tools ?? []).ToList();
            var at = tools.Count;
            if (order is not null && order.TryGetValue(id, out var rank))
            {
                var later = tools.FindIndex(t => order.TryGetValue(t, out var r) && r > rank);
                at = later < 0 ? tools.Count : later;
            }

            tools.Insert(at, id);
            return Replace(root, existing, existing with { Tools = tools });
        }

        var group = Group([id], location switch
        {
            DockLocation.Right => RightProportion,
            DockLocation.Bottom => BottomProportion,
            _ => LeftProportion,
        });

        if (location == DockLocation.Bottom)
        {
            // 아래 묶음은 문서 영역 밑에만 둔다: 왼쪽 묶음(창 전체 높이)과 오른쪽 묶음([문서 / 아래] 열 옆, D-048)은 세로 전체를 그대로 쓴다.
            var docs = FindDocuments(root)!;
            var parent = PathTo(root, n => ReferenceEquals(n, docs)) is { Count: > 1 } path ? path[^2] : null;
            if (parent is { Kind: DockNodeKind.Split, Orientation: DockOrientation.Vertical })
            {
                var kids = (parent.Children ?? []).ToList();
                var index = kids.FindIndex(k => ReferenceEquals(k, docs));
                kids[index] = docs with { Proportion = docs.Proportion is { } dp ? Math.Max(dp - BottomProportion, MinDocumentsProportion) : null };
                kids.Insert(index + 1, group);
                return Replace(root, parent, parent with { Children = kids });
            }

            var column = new DockNode
            {
                Kind = DockNodeKind.Split,
                Orientation = DockOrientation.Vertical,
                Proportion = docs.Proportion,
                Children = [docs with { Proportion = 1 - BottomProportion }, group],
            };
            return Replace(root, docs, column);
        }

        if (location == DockLocation.Left)
        {
            // 왼쪽 묶음은 창 전체 높이(맨 바깥 가로 분할의 첫 칸).
            return new DockNode
            {
                Kind = DockNodeKind.Split,
                Orientation = DockOrientation.Horizontal,
                Children = [group, root with { Proportion = 1 - group.Proportion!.Value }],
            };
        }

        return InsertRight(root, group, location);
    }

    /// <summary>
    /// 오른쪽(또는 문서 옆 왼쪽)에 노드를 끼운다. <paramref name="node"/> 의 비율이 차지할 몫이다.
    /// 오른쪽은 문서 영역 자체가 아니라 문서를 담은 세로 열([문서 / 아래 묶음])의 옆에 끼워, 편집기 위끝부터 아래 묶음 아래끝까지
    /// 세로 전체를 쓰게 한다(D-048).
    /// </summary>
    private static DockNode InsertRight(DockNode root, DockNode node, DockLocation location = DockLocation.Right)
    {
        // 기준 노드가 이미 가로 분할 안에 있으면 그 옆에 끼워 넣고(합 1 유지), 아니면 가로 분할로 감싼다.
        var docs = FindDocuments(root)!;
        if (location == DockLocation.Right)
        {
            docs = ColumnOf(root, docs);
        }

        var share = node.Proportion!.Value;
        var parent = PathTo(root, n => ReferenceEquals(n, docs)) is { Count: > 1 } path ? path[^2] : null;
        if (parent is { Kind: DockNodeKind.Split, Orientation: DockOrientation.Horizontal })
        {
            var kids = (parent.Children ?? []).ToList();
            var index = kids.FindIndex(k => ReferenceEquals(k, docs));
            kids[index] = docs with { Proportion = docs.Proportion is { } dp ? Math.Max(dp - share, MinDocumentsProportion) : null };
            kids.Insert(location == DockLocation.Right ? index + 1 : index, node);
            return Replace(root, parent, parent with { Children = kids });
        }

        var wrapped = new DockNode
        {
            Kind = DockNodeKind.Split,
            Orientation = DockOrientation.Horizontal,
            Proportion = docs.Proportion,
            Children = location == DockLocation.Right
                ? [docs with { Proportion = 1 - share }, node]
                : [node, docs with { Proportion = 1 - share }],
        };
        return Replace(root, docs, wrapped);
    }

    /// <summary>문서 영역에서 세로 분할 부모를 따라 올라간 가장 바깥 세로 열(세로 분할 안이 아니면 문서 영역 그대로).</summary>
    private static DockNode ColumnOf(DockNode root, DockNode docs)
    {
        var path = PathTo(root, n => ReferenceEquals(n, docs))!;
        var at = path.Count - 1;
        while (at > 0 && path[at - 1] is { Kind: DockNodeKind.Split, Orientation: DockOrientation.Vertical })
        {
            at--;
        }

        return path[at];
    }

    /// <summary>
    /// 이주(D-048): <c>세로[가로[…, 문서, 오른쪽…], …]</c> 처럼 문서 바로 옆 가로 분할의 문서 뒤 칸(오른쪽)을 그 세로 열 바깥 오른쪽으로 옮긴다.
    /// 창 전체 기준 폭은 그대로 둔다. 그런 모양이 아니면 그대로 돌려준다.
    /// </summary>
    internal static DockNode LiftRightBesideColumn(DockNode root)
    {
        var docs = FindDocuments(root);
        var path = docs is null ? null : PathTo(root, n => ReferenceEquals(n, docs));
        if (path is not { Count: >= 3 }
            || path[^2] is not { Kind: DockNodeKind.Split, Orientation: DockOrientation.Horizontal } row
            || path[^3] is not { Kind: DockNodeKind.Split, Orientation: DockOrientation.Vertical } column)
        {
            return root;
        }

        var kids = (row.Children ?? []).ToList();
        var index = kids.FindIndex(k => ReferenceEquals(k, docs));
        var right = kids.Skip(index + 1).ToList();
        if (right.Count == 0 || kids.Any(k => k.Proportion is not > 0))
        {
            return root;
        }

        // 가로 분할(= 세로 열 폭) 안 몫을 합 1 기준으로 잰다: 오른쪽이 차지하던 몫 r.
        var sum = kids.Sum(k => k.Proportion!.Value);
        var r = right.Sum(k => k.Proportion!.Value) / sum;
        var keep = kids.Take(index + 1).ToList();
        var newRow = keep.Count == 1
            ? keep[0] with { Proportion = row.Proportion }
            : row with { Children = [.. keep.Select(k => k with { Proportion = Math.Round(k.Proportion!.Value / sum / (1 - r), 4) })] };
        var newColumn = Replace(column, row, newRow);

        var outer = path.Count >= 4 ? path[^4] : null;
        if (outer is { Kind: DockNodeKind.Split, Orientation: DockOrientation.Horizontal } && column.Proportion is > 0)
        {
            var outerKids = outer.Children.ToList();
            var at = outerKids.FindIndex(k => ReferenceEquals(k, column));
            var width = column.Proportion!.Value;
            outerKids[at] = newColumn with { Proportion = Math.Round(width * (1 - r), 4) };
            outerKids.InsertRange(at + 1, right.Select(k => k with { Proportion = Math.Round(width * k.Proportion!.Value / sum, 4) }));
            return Replace(root, outer, outer with { Children = outerKids });
        }

        var wrapped = new DockNode
        {
            Kind = DockNodeKind.Split,
            Orientation = DockOrientation.Horizontal,
            Proportion = column.Proportion,
            Children = [newColumn with { Proportion = Math.Round(1 - r, 4) }, .. right.Select(k => k with { Proportion = Math.Round(k.Proportion!.Value / sum, 4) })],
        };
        return Replace(root, column, wrapped);
    }

    /// <summary>오른쪽 한 열: 창마다 묶음 하나씩 위에서 아래로 쌓는다(하나뿐이면 묶음 그대로).</summary>
    private static DockNode StackedGroups(List<string> ids)
    {
        if (ids.Count == 1)
        {
            return Group(ids, RightProportion);
        }

        return new DockNode
        {
            Kind = DockNodeKind.Split,
            Orientation = DockOrientation.Vertical,
            Proportion = RightProportion,
            Children = [.. ids.Select(id => Group([id], Math.Round(1.0 / ids.Count, 4)))], // Capture 가 4자리로 반올림하므로 같게(3창 이상일 때 1/3 이 어긋난다)
        };
    }

    private static DockNode Replace(DockNode root, DockNode target, DockNode replacement)
    {
        if (ReferenceEquals(root, target))
        {
            return replacement;
        }

        var kids = root.Children ?? [];
        return kids.Count == 0 ? root : root with { Children = kids.Select(k => Replace(k, target, replacement)).ToList() };
    }

    private static List<DockNode> FindGroups(DockNode root)
    {
        var found = new List<DockNode>();
        Walk(root, n =>
        {
            if (n.Kind == DockNodeKind.ToolGroup)
            {
                found.Add(n);
            }
        });
        return found;
    }

    private static DockNode? FindDocuments(DockNode root)
    {
        DockNode? found = null;
        Walk(root, n =>
        {
            if (n.Kind == DockNodeKind.Documents)
            {
                found = n;
            }
        });
        return found;
    }

    private static int CountDocuments(DockNode root)
    {
        var count = 0;
        Walk(root, n =>
        {
            if (n.Kind == DockNodeKind.Documents)
            {
                count++;
            }
        });
        return count;
    }

    private static void Walk(DockNode? node, Action<DockNode> visit)
    {
        if (node is null)
        {
            return;
        }

        visit(node);
        foreach (var c in node.Children ?? [])
        {
            Walk(c, visit);
        }
    }

    private static List<DockNode>? PathTo(DockNode node, Func<DockNode, bool> match)
    {
        if (match(node))
        {
            return [node];
        }

        foreach (var c in node.Children ?? [])
        {
            if (PathTo(c, match) is { } sub)
            {
                sub.Insert(0, node);
                return sub;
            }
        }

        return null;
    }
}
