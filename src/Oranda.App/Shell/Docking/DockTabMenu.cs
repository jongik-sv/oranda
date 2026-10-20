using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Dock.Avalonia.Controls;

namespace Oranda.App.Shell.Docking;

/// <summary>
/// <see cref="IDockTabActions"/> 문서의 탭 머리 오른쪽 클릭 메뉴(이름 바꾸기·닫기). 이런 문서는 떼어 낼 수 없으므로
/// Dock 기본 메뉴(떼어 내기 포함) 대신 이 메뉴를 보인다. 다른 문서는 Dock 기본 메뉴 그대로.
/// </summary>
internal static class DockTabMenu
{
    private static bool _registered;

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
        new ContextMenu { ItemsSource = new[] { rename, close } }.Open(item);
        e.Handled = true;
    }
}
