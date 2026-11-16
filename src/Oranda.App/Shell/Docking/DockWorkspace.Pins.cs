using System;
using System.Collections.Generic;
using System.Linq;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace Oranda.App.Shell.Docking;

/// <summary>
/// 핀(자동 숨김): 저장된 핀 되살리기, 핀 창의 원래 묶음 바로잡기, 사이드바 펼침 접기.
/// </summary>
public sealed partial class DockWorkspace
{
    /// <summary>마지막으로 지은 배치에서 핀을 다시 빼야 할 도구 창 ID. 보기가 그 배치를 화면에 붙이면 비운다.</summary>
    private IReadOnlyList<string> _pendingPins = [];

    /// <summary>
    /// 저장된 배치에서 핀을 빼 두었던 도구 창을 다시 사이드바로 접는다. 배치 트리(원래 묶음)에 있는 창만 — 닫았거나 등록에서 빠진 창은 건너뛴다.
    /// 이것이 없으면 다음 실행·배치 다시 짓기(다른 도구 창 보이기 등)에서 핀이 풀리거나 닫힌 창이 되었다(2026-11-13 사용자 신고).
    /// <c>DockControl.Layout</c> 을 갈아 끼우면 모델의 핀 목록이 비워지므로, 보기(<see cref="DockHostView"/>)가 Layout 을 넣은 바로 뒤에 다시 부른다(<paramref name="fromView"/>).
    /// 이미 핀이 빠진 창은 건드리지 않는다(PinDockable 은 핀을 넣고 빼는 토글이다).
    /// </summary>
    internal void RestorePins(bool fromView)
    {
        var pinnedNow = PinnedTools();
        foreach (var id in _pendingPins)
        {
            if (_tools.TryGetValue(id, out var tool) && !pinnedNow.Contains(tool)
                && tool.Owner is IToolDock { VisibleDockables: { } tabs } && tabs.Contains(tool))
            {
                Factory.PinDockable(tool);
            }
        }

        RepairPinnedOwners();
        if (fromView)
        {
            _pendingPins = [];
        }
    }

    /// <summary>핀을 뺀 도구 창의 원래 묶음(핀을 뺄 때 적는다, <see cref="RepairPinnedOwners"/> 가 쓴다).</summary>
    private readonly Dictionary<string, IToolDock> _pinHomes = new(StringComparer.Ordinal);

    /// <summary>
    /// 핀 창의 Owner 를 원래 묶음으로 바로잡는다. 보기(DockControl)가 화면에 붙으며 배치를 초기화하면 Dock 이 핀 목록 창의 Owner 를 root 로 바꾼다 —
    /// 시작 직후처럼 핀을 먼저 뺀 뒤 보기가 붙으면 보기 메뉴 체크가 꺼지고, 핀을 빼면 묶음이 아닌 곳에 붙고, 배치를 다시 지으면 닫힌 창이 되었다(DK2-01).
    /// 보기가 붙을 때(<see cref="DockHostView"/>)와 핀을 다시 뺀 뒤 부른다.
    /// </summary>
    internal void RepairPinnedOwners()
    {
        foreach (var tool in PinnedTools())
        {
            if (tool.Owner is not IToolDock && _pinHomes.TryGetValue(tool.Id, out var home) && InMainTree(home))
            {
                tool.Owner = home;
            }
        }
    }

    /// <summary>핀을 빼 가장자리 사이드바에 접혀 있는 도구 창(메인 배치 트리의 묶음 탭에는 없다).</summary>
    private List<PanelTool> PinnedTools()
    {
        if (Layout is not IRootDock root)
        {
            return [];
        }

        return new[] { root.LeftPinnedDockables, root.RightPinnedDockables, root.TopPinnedDockables, root.BottomPinnedDockables }
            .SelectMany(list => list ?? [])
            .OfType<PanelTool>()
            .ToList();
    }

    private PanelTool? PinnedTool(string toolId) => PinnedTools().FirstOrDefault(t => t.Id == toolId);

    /// <summary>
    /// 핀 창의 원래 묶음. 사이드바에서 펼친 동안(<see cref="IRootDock.PinnedDock"/> 에 들어 있는 동안) Dock 은 Owner 를 그 미리보기 칸으로 바꾸고
    /// 원래 묶음을 <see cref="IDockable.OriginalOwner"/> 에 둔다.
    /// </summary>
    private IDockable? PinnedHome(PanelTool tool) =>
        Layout is IRootDock { PinnedDock: { VisibleDockables: { } previewing } } && previewing.Contains(tool) ? tool.OriginalOwner : tool.Owner;

    /// <summary>
    /// 사이드바에서 펼친 핀 창을 접는다. 펼친 채 배치를 다시 지으면 그 창이 미리보기 칸(배치 트리 밖)에 남아 묶음에도 사이드바에도 없게 된다(DK-01).
    /// </summary>
    private void ClosePinnedPreview()
    {
        if (Layout is IRootDock { PinnedDock.VisibleDockables.Count: > 0 } root)
        {
            Factory.HidePreviewingDockables(root);
            // 「펼친 채 두기」(KeepPinnedDockableVisible) 창은 위가 건너뛴다: 남은 창은 하나씩 접는다(지금 앱은 그 옵션을 쓰지 않는다).
            foreach (var left in root.PinnedDock?.VisibleDockables?.ToList() ?? [])
            {
                Factory.TogglePreviewPinnedDockable(left);
            }
        }
    }
}
