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
/// 지금 배치를 앱 DTO 로 읽기(Dock 모델 → <see cref="LayoutDocument"/>). 잠시 숨긴 창·핀 창은 원래 자리에, 접힌 묶음은 접기 전 비율로 적는다.
/// </summary>
public sealed partial class DockWorkspace
{
    /// <summary>지금 배치를 앱 DTO 로 읽는다. 창 위치는 <paramref name="window"/> 로 받아 그대로 싣는다.</summary>
    public LayoutDocument Capture(WindowPlacement? window = null)
    {
        var root = CaptureRoot();
        var inTree = LayoutPlanner.ToolIds(root).ToHashSet(StringComparer.Ordinal);
        var hidden = _registry.Panels
            .Where(p => p.Kind == ToolPanelKind.Tool && !inTree.Contains(p.Id) && !IsScopedHidden(p.Id)) // 잠시 숨긴 창은 닫힌 창이 아니다
            .Select(p => p.Id)
            .ToList();
        var pinnedTools = PinnedTools().Where(t => inTree.Contains(t.Id)).ToList();
        var pinned = pinnedTools.Select(t => t.Id).ToList();
        return new LayoutDocument
        {
            Window = window, Root = root, HiddenTools = hidden, PinnedTools = pinned, PinnedSizes = PinnedSizesOf(pinnedTools), Migrations = _migrations,
        };
    }

    private DockNode CaptureRoot()
    {
        _documentsCaptured = false;
        var content = Layout?.VisibleDockables?.FirstOrDefault();
        var node = content is null ? null : CaptureNode(content);
        return (node is null ? null : LayoutPlanner.Normalize(node, PlanningPanels()))
            ?? new DockNode { Kind = DockNodeKind.Documents };
    }

    private DockNode? CaptureNode(IDockable dockable)
    {
        double? Proportion() => ScopedProportion(dockable) is var p && double.IsNaN(p) ? null : Math.Round(p, 4);

        switch (dockable)
        {
            case IProportionalDock split:
                {
                    var kids = new List<DockNode>();
                    var documentsAt = -1; // 바로 앞 칸이 문서 칸이고 kids 에 적혔으면 그 위치
                    var folded = new List<(int At, double Share)>(); // 핀으로 모두 접혀 비율 0 이 된 묶음(Dock 이 접기 전 값을 CollapsedProportion 에 둔다)
                    foreach (var child in (split.VisibleDockables ?? []).Where(k => k is not IProportionalDockSplitter))
                    {
                        var node = CaptureNode(child);
                        var isDocuments = IsDocumentsOnly(child);
                        if (node is null && isDocuments && documentsAt >= 0)
                        {
                            // 이어진 문서 칸은 DTO 에 Documents 하나로 적으므로, 뒤 칸의 비율을 앞 칸에 더한다.
                            // 그러지 않으면 [.36 문서, .36 문서, .28 도구] 가 [.36, .28] 로 기록되어 다시 지을 때마다 아래 창이 커진다.
                            var last = kids[documentsAt];
                            var extra = ScopedProportion(child);
                            kids[documentsAt] = last with { Proportion = last.Proportion is { } sum && !double.IsNaN(extra) ? Math.Round(sum + extra, 4) : null };
                            continue;
                        }

                        if (node is not null)
                        {
                            kids.Add(node);
                            if (FoldedShare(child) is { } share)
                            {
                                folded.Add((kids.Count - 1, share));
                            }
                        }

                        documentsAt = node is not null && isDocuments ? kids.Count - 1 : -1;
                    }

                    if (kids.Count == 0)
                    {
                        return null;
                    }

                    // 그대로 0 을 저장하면 다음 실행·다시 짓기에서 묶음이 높이 0 으로 지어져, 핀을 빼거나 창을 켜도 보이지 않았다(DK2-01).
                    // Dock 은 묶음을 접으며 나머지 칸을 같은 비로 늘렸다: 같은 비로 되돌린다(한 칸에서만 빼면 저장할 때마다 비율이 한쪽으로 쏠린다).
                    if (folded.Count > 0)
                    {
                        ScaleUnfolded(kids, folded); // .Build.cs
                        foreach (var (at, share) in folded)
                        {
                            kids[at] = kids[at] with { Proportion = Math.Round(share, 4) };
                        }
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
                    var (ids, active) = ScopedToolIds(group); // 잠시 숨긴 창도 원래 자리에 적는다
                    if (ids.Count == 0)
                    {
                        return null;
                    }

                    return new DockNode
                    {
                        Kind = DockNodeKind.ToolGroup,
                        Proportion = Proportion(),
                        Tools = ids,
                        ActiveTool = active ?? ids[0],
                    };
                }

            case IDocumentDock:
                // 문서 영역이 분할되어 여럿이어도 DTO 에는 첫째만 둔다(탭 목록은 작업 공간 파일 몫). 분할은 layout.json 에 남지 않고
                // 실행 중 배치를 다시 지을 때만 살아 있는 칸으로 유지한다(ApplyCore).
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

    /// <summary>
    /// 저장할 때 접힌 묶음의 비율. 묶음 창을 모두 핀으로 접으면 Dock 이 묶음 비율을 0, 문서 칸을 1 로 바꾸고 접기 전 값은 <see cref="IDockable.CollapsedProportion"/> 에 둔다.
    /// 그대로 0 을 적으면 다시 지을 때 높이 0 묶음이 되므로 접기 전 값(없으면 기본 비율)을 돌려준다. 접히지 않았으면 null.
    /// </summary>
    private double? FoldedShare(IDockable child)
    {
        // 잠시 숨김 기록이 있으면 그 값(ScopedProportion)을 본다: 이미 접힌 뒤에 적은 기록이면 그것도 0 이다.
        if (child is not IToolDock group || HasToolTabs(group)
            || ScopedProportion(group) is var now && (double.IsNaN(now) || now >= MinShownProportion))
        {
            return null;
        }

        var before = group.CollapsedProportion;
        return !double.IsNaN(before) && before >= MinShownProportion && before < 1 ? before : DefaultShare(LocationOf(group.Alignment));
    }
}
