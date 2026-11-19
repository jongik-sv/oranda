using System;
using System.Diagnostics;
using System.Linq;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;

namespace Oranda.App.Shell.Docking;

/// <summary>
/// 도구 창은 도구 묶음에만, 문서는 문서 영역에만 들어가게 하는 Dock 팩터리.
/// <para>
/// 도구 창에는 <c>CanDockAsDocument = false</c> 가 있지만(<see cref="PanelTool"/>), Dock 은 도구 창 제목 줄(ToolChrome)을 끌면 탭이 아니라
/// 묶음(<see cref="IToolDock"/>) 통째를 끌고, 묶음은 그 값을 보지 않아 문서 영역에 놓으면 도구 창들이 문서 탭 줄로 들어갔다.
/// 그러면 문서 탭 템플릿이 도구 창을 그리지 못해 탭 머리가 세로로 늘어나고 내용이 겹쳤다(2026-11-20 사용자 신고, 스키마 브라우저).
/// </para>
/// <para>
/// 두 겹으로 막는다. (1) <see cref="IDockDropPolicy"/>: 끄는 동안 놓을 자리 표시와 놓기를 Dock 이 이 규칙으로 거른다(탭·묶음·떠 있는 창 모두).
/// (2) 옮기기·맞바꾸기 재정의: 끌어 놓기 밖의 길(Dock 메뉴, 분할 안쪽 칸으로 채우기 등)로 섞이려 하면 옮기지 않는다.
/// 이미 섞인 배치는 저장할 때 <see cref="DockWorkspace"/> 가 제자리로 돌린다.
/// </para>
/// </summary>
internal sealed class OrandaDockFactory : Factory, IDockDropPolicy
{
    /// <inheritdoc />
    public bool CanDrop(IDockable source, IDockable target, DockOperation operation)
    {
        // 떠 있는 창으로 떼어 내기·바깥 가장자리 분할은 끄는 것과 같은 종류의 새 묶음을 만든다.
        if (operation is DockOperation.Window or DockOperation.OuterLeft or DockOperation.OuterTop or DockOperation.OuterRight or DockOperation.OuterBottom)
        {
            return true;
        }

        if (Holds(source, IsTool) && Touches(target, operation, IsDocument, d => d is IDocumentDock))
        {
            return false;
        }

        return !(Holds(source, IsDocument) && Touches(target, operation, IsTool, d => d is IToolDock));
    }

    /// <inheritdoc />
    public override void MoveDockable(IDock sourceDock, IDock targetDock, IDockable sourceDockable, IDockable? targetDockable)
    {
        if (Mixes(sourceDockable, targetDock))
        {
            Refused(sourceDockable, targetDock);
            return;
        }

        base.MoveDockable(sourceDock, targetDock, sourceDockable, targetDockable);
    }

    /// <inheritdoc />
    public override void SwapDockable(IDock sourceDock, IDock targetDock, IDockable sourceDockable, IDockable targetDockable)
    {
        if (Mixes(sourceDockable, targetDock) || Mixes(targetDockable, sourceDock))
        {
            Refused(sourceDockable, targetDock);
            return;
        }

        base.SwapDockable(sourceDock, targetDock, sourceDockable, targetDockable);
    }

    /// <summary>창 <paramref name="dockable"/> 을 <paramref name="dock"/> 에 넣으면 도구 창과 문서가 섞이는가.</summary>
    internal static bool Mixes(IDockable dockable, IDock dock) =>
        IsTool(dockable) && dock is IDocumentDock || IsDocument(dockable) && dock is IToolDock;

    private static bool IsTool(IDockable d) => d is ITool or IToolDock;

    /// <summary>문서 탭이나 문서 영역. Dock 의 <c>Tool</c> 도 <see cref="IDocument"/> 를 구현하므로(문서로 붙이기용) 도구 창은 뺀다.</summary>
    private static bool IsDocument(IDockable d) => d is IDocument and not ITool || d is IDocumentDock;

    /// <summary>끄는 것(탭 하나 또는 묶음 통째)이 그 종류이거나 그 종류를 담고 있는가.</summary>
    private static bool Holds(IDockable node, Func<IDockable, bool> kind) =>
        kind(node) || node is IDock dock && (dock.VisibleDockables ?? []).Any(child => Holds(child, kind));

    /// <summary>
    /// 놓을 자리가 다른 종류(<paramref name="kind"/> 의 탭이나 묶음)인가. 탭 위에 놓으면 Dock 은 방향과 상관없이 그 탭의 묶음으로 옮기므로
    /// 탭은 언제나 거른다. 분할 칸에 채우기로 놓으면 Dock 이 그 안의 마지막 탭 묶음으로 옮기므로 안에 그 종류의 묶음이 있으면 거른다.
    /// </summary>
    private static bool Touches(IDockable target, DockOperation operation, Func<IDockable, bool> kind, Func<IDockable, bool> kindDock) =>
        kind(target)
        || target.Owner is { } owner && kindDock(owner)
        || operation == DockOperation.Fill && target is IProportionalDock split && Holds(split, kindDock);

    private static void Refused(IDockable dockable, IDock dock) =>
        Trace.TraceWarning("도킹: '{0}' 을(를) 다른 종류의 칸 '{1}' 에 넣지 않았다(도구 창은 도구 묶음, 문서는 문서 영역에만).", dockable.Id, dock.Id);
}
