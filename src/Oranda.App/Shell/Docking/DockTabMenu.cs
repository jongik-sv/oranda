using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Dock.Avalonia.Controls;

namespace Oranda.App.Shell.Docking;

/// <summary>
/// <see cref="IDockTabActions"/> 문서의 탭 머리 오른쪽 클릭 메뉴(이름 바꾸기·닫기). 이런 문서는 떼어 낼 수 없으므로
/// Dock 기본 메뉴(떼어 내기 포함) 대신 이 메뉴를 보인다. 다른 문서는 Dock 기본 메뉴 그대로.
/// 명령 레지스트리의 <c>context:documentTab</c> 자리 명령(예 「MyBatis 작업 공간으로 바꾸기…」, TSK-03-05)은 구분선 뒤에 덧붙인다(docs/oranda1/design.md 4.1).
/// </summary>
internal static class DockTabMenu
{
    private static bool _registered;

    /// <summary>
    /// 지금 탭 메뉴를 띄운 문서의 Panel(메뉴를 띄우기 직전에 정하고, 메뉴가 닫히면 null). 셸의 우클릭 메뉴 공급(<c>ShellContextMenus.Factory</c>)은
    /// 매개변수를 넘기지 못하므로, <c>context:documentTab</c> 에 붙는 명령 처리기(03-05 「MyBatis 작업 공간으로 바꾸기…」 등)는
    /// 어느 문서를 우클릭했는지 이것으로 안다. 메뉴 항목을 눌러 처리기가 도는 동안은 값이 남아 있다(닫힘 처리는 UI 큐 뒤로 미룬다).
    /// </summary>
    internal static object? TargetPanel { get; private set; }

    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        InputElement.ContextRequestedEvent.AddClassHandler<DocumentTabStripItem>(OnContextRequested, RoutingStrategies.Tunnel);
    }

    private static void OnContextRequested(DocumentTabStripItem item, ContextRequestedEventArgs e)
    {
        if (item.DataContext is not PanelDocument { Panel: IDockTabActions actions })
        {
            return;
        }

        var rename = new MenuItem { Header = "이름 바꾸기" };
        rename.Click += async (_, _) => await actions.RenameFromTabAsync(default).ConfigureAwait(true);
        var close = new MenuItem { Header = "닫기" };
        close.Click += async (_, _) => await actions.CloseFromTabAsync(default).ConfigureAwait(true);
        var items = new List<object> { rename, close };
        if (Menus.ShellContextMenus.Factory?.Invoke("documentTab") is { Count: > 0 } extra)
        {
            items.Add(new Separator());
            items.AddRange(extra);
        }

        var panel = actions;
        TargetPanel = panel;
        var menu = new ContextMenu { ItemsSource = items };
        menu.Closed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(TargetPanel, panel))
            {
                TargetPanel = null;
            }
        });
        menu.Open(item);
        e.Handled = true;
    }
}
