using System;
using System.Collections.Generic;
using System.Linq;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace Oranda.App.Shell.Docking;

/// <summary>
/// 도킹 트리 조회: 문서 영역·뜬 창·트리 걷기.
/// </summary>
public sealed partial class DockWorkspace
{
    private List<IDocumentDock> DocumentDocks()
    {
        if (Layout is null)
        {
            return [];
        }

        var found = new List<IDocumentDock>();
        CollectInto(Layout, found);
        return found;
    }

    /// <summary>도구 창이 보이는 떠 있는 창(메인 배치 트리 밖이라 <see cref="VisibleToolIds"/> 에는 없다). 메인 배치에 있으면 null.</summary>
    private IDockWindow? FloatingWindowOf(PanelTool tool)
    {
        foreach (var window in (Layout as IRootDock)?.Windows ?? [])
        {
            if (window.Layout is { } floating && AnyIn(floating, d => ReferenceEquals(d, tool)))
            {
                return window;
            }
        }

        return null;
    }

    /// <summary>떠 있는 창 안의 문서 탭(메인 배치 트리 밖이라 <see cref="Documents"/> 에는 없다).</summary>
    private List<PanelDocument> FloatingDocuments()
    {
        var found = new List<PanelDocument>();
        foreach (var window in (Layout as IRootDock)?.Windows ?? [])
        {
            if (window.Layout is { } floating)
            {
                CollectInto(floating, found);
            }
        }

        return found;
    }

    private static void Visit(IDockable node, Action<IDockable> visit)
    {
        visit(node);
        if (node is IDock dock)
        {
            foreach (var child in dock.VisibleDockables ?? [])
            {
                Visit(child, visit);
            }
        }
    }

    /// <summary><paramref name="node"/> 와 그 아래(보이는 자식)에 <paramref name="match"/> 에 맞는 것이 있는가. 트리를 끝까지 걷는다.</summary>
    private static bool AnyIn(IDockable node, Func<IDockable, bool> match)
    {
        var found = false;
        Visit(node, d => found |= match(d));
        return found;
    }

    /// <summary><paramref name="node"/> 와 그 아래에서 <typeparamref name="T"/> 인 것을 걷는 차례대로 <paramref name="found"/> 에 더한다.</summary>
    private static void CollectInto<T>(IDockable node, List<T> found)
    {
        Visit(node, d =>
        {
            if (d is T item)
            {
                found.Add(item);
            }
        });
    }

    /// <summary>묶음에 보이는 도구 창 탭이 하나라도 있는가(잠시 숨긴 창·핀 창은 세지 않는다).</summary>
    private static bool HasToolTabs(IDock dock) => (dock.VisibleDockables ?? []).OfType<PanelTool>().Any();

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

    private static bool IsDocumentsOnly(IDockable dockable) => dockable switch
    {
        IDocumentDock => true,
        IProportionalDock split => (split.VisibleDockables ?? []).Where(k => k is not IProportionalDockSplitter).ToList() is { Count: > 0 } kids && kids.All(IsDocumentsOnly),
        _ => false,
    };

    private static IEnumerable<PanelTool> ToolsInTree(IDockable dockable) => dockable switch
    {
        PanelTool tool => [tool],
        IDock dock => (dock.VisibleDockables ?? []).SelectMany(ToolsInTree),
        _ => [],
    };

    private static bool ContainsDocuments(IDockable dockable) => AnyIn(dockable, d => d is IDocumentDock);

    private bool InMainTree(IDockable dockable) => Layout is not null && AnyIn(Layout, d => ReferenceEquals(d, dockable));
}
