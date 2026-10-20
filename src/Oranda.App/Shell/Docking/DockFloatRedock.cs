using System;
using System.Linq;
using Avalonia.Input;
using Avalonia.Interactivity;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace Oranda.App.Shell.Docking;

/// <summary>
/// 떠 있는 창 안의 문서 탭을 두 번 누르면 메인 창의 문서 영역으로 되돌린다(Visual Studio 와 같다).
/// Dock 기본 동작은 어느 창에서든 두 번 누르기를 「떼어 내기」로 처리하므로, 떠 있는 창에서는 새 창이 겹겹이 생긴다.
/// 메인 창의 탭은 기존대로 Dock 이 떼어 낸다.
/// </summary>
internal static class DockFloatRedock
{
    private static bool _registered;

    /// <summary>탭 항목의 두 번 누르기를 Dock 보다 먼저 받는 클래스 처리기를 건다(여러 번 불러도 한 번만 건다).</summary>
    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        InputElement.DoubleTappedEvent.AddClassHandler<DocumentTabStripItem>(OnDoubleTapped, RoutingStrategies.Bubble);
    }

    private static void OnDoubleTapped(DocumentTabStripItem item, TappedEventArgs e)
    {
        if (item.DataContext is PanelDocument { Panel: IDockTabActions actions })
        {
            // SQL 작업 공간 문서: 두 번 누르기 = 이름 바꾸기(떼어 내기·제자리 붙이기 아님).
            _ = actions.RenameFromTabAsync(default);
            e.Handled = true;
            return;
        }

        if (item.DataContext is IDockable dockable && Redock(dockable))
        {
            // Dock 의 탭 항목 처리기는 처리된 이벤트를 받지 않으므로, 여기서 처리됨으로 표시하면 다시 떼어 내지 않는다.
            e.Handled = true;
        }
    }

    /// <summary>
    /// 떠 있는 창 안의 문서를 메인 문서 영역으로 옮긴다. 떠 있는 창 안에 있지 않거나 옮길 곳이 없으면 false.
    /// </summary>
    internal static bool Redock(IDockable dockable)
    {
        if (dockable.Owner is not IDock { Factory: { } factory } source || RootOf(source) is not { } root)
        {
            return false;
        }

        // 메인 배치의 Window 도 비어 있지 않으므로, 「다른 최상위 도킹의 Windows 목록에 들어 있다」로 떠 있는 창을 가린다.
        var main = factory.DockControls
            .Select(c => c.Layout)
            .OfType<IRootDock>()
            .FirstOrDefault(m => !ReferenceEquals(m, root) && (m.Windows ?? []).Any(w => ReferenceEquals(w.Layout, root)));
        var target = FindTarget(main, dockable);
        if (target is null)
        {
            return false;
        }

        factory.MoveDockable(source, target, dockable, null);
        factory.SetActiveDockable(dockable);
        factory.SetFocusedDockable(target, dockable);
        return true;
    }

    private static IRootDock? RootOf(IDockable node)
    {
        for (var current = node; current is not null; current = current.Owner)
        {
            if (current is IRootDock root)
            {
                return root;
            }
        }

        return null;
    }

    /// <summary>원래 있던 문서 영역이 메인 배치에 남아 있으면 그곳, 아니면 메인 배치의 첫 문서 영역.</summary>
    private static IDock? FindTarget(IDockable? main, IDockable dockable)
    {
        if (main is null)
        {
            return null;
        }

        if (dockable.OriginalOwner is IDock original && Contains(main, original))
        {
            return original;
        }

        return dockable is IDocument ? FirstDocumentDock(main) : null;
    }

    private static bool Contains(IDockable node, IDockable wanted)
    {
        if (ReferenceEquals(node, wanted))
        {
            return true;
        }

        return node is IDock dock && (dock.VisibleDockables ?? []).Any(child => Contains(child, wanted));
    }

    private static IDocumentDock? FirstDocumentDock(IDockable node)
    {
        if (node is IDocumentDock found)
        {
            return found;
        }

        return node is IDock dock
            ? (dock.VisibleDockables ?? []).Select(FirstDocumentDock).FirstOrDefault(d => d is not null)
            : null;
    }
}
