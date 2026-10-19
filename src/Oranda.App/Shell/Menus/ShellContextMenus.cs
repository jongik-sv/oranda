using System;
using System.Collections.Generic;
using Avalonia.Controls;

namespace Oranda.App.Shell.Menus;

/// <summary>
/// 기능 화면이 오른쪽 클릭 메뉴를 다는 곳. 화면은 표면 이름(<c>schema</c>·<c>editor</c>·<c>grid</c>·<c>monitor</c>)만 알리고,
/// 항목은 셸이 명령 레지스트리의 <c>context:&lt;표면&gt;</c> 배치로 메뉴를 열 때마다 만든다(명령이 늘어도 화면은 그대로).
/// 화면은 DI 없이 ViewLocator 가 만들므로 주 창이 붙을 때 <see cref="Factory"/> 를 채운다.
/// </summary>
public static class ShellContextMenus
{
    /// <summary>표면 이름 → 메뉴 항목. 주 창이 설정한다(시험은 직접 넣는다).</summary>
    public static Func<string, IReadOnlyList<object>>? Factory { get; set; }

    /// <summary><paramref name="target"/> 에 <paramref name="surface"/> 표면의 오른쪽 클릭 메뉴를 단다.</summary>
    public static void Attach(Control target, string surface)
    {
        ArgumentNullException.ThrowIfNull(target);
        var menu = new ContextMenu();
        menu.Opening += (_, e) =>
        {
            var items = Factory?.Invoke(surface) ?? [];
            menu.ItemsSource = items;
            e.Cancel = items.Count == 0;
        };
        target.ContextMenu = menu;
    }
}
