using Avalonia.Controls;
using Oranda.App.Shell.Menus;

namespace Oranda.App.Features.MyBatisWorkspace.Editor;

/// <summary>
/// 문장 편집기의 오른쪽 클릭 메뉴. 안쪽 <c>SqlEditorView</c> 가 건 「editor」 표면(복사·붙여 넣기 등)을 지우지 않으려고,
/// 메뉴를 열 때마다 「mybatisEditor」 표면(MyBatis 전용 항목)과 「editor」 표면을 한 메뉴로 합친다.
/// 셸의 <see cref="ShellContextMenus.Attach"/> 는 표면 하나만 받기 때문에 같은 모양의 메뉴를 여기서 직접 만든다.
/// </summary>
internal static class MyBatisEditorContextMenu
{
    internal const string MyBatisSurface = "mybatisEditor";
    internal const string EditorSurface = "editor";

    public static void Attach(Control target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var menu = new ContextMenu();
        menu.Opening += (_, e) =>
        {
            var items = Build();
            menu.ItemsSource = items;
            e.Cancel = items.Count == 0;
        };
        target.ContextMenu = menu;
    }

    /// <summary>MyBatis 전용 항목, 구분선, 기존 편집기 항목 순서. 한쪽이 비어 있으면 구분선도 넣지 않는다.</summary>
    internal static List<object> Build()
    {
        var factory = ShellContextMenus.Factory;
        var items = new List<object>();
        if (factory?.Invoke(MyBatisSurface) is { Count: > 0 } own)
        {
            items.AddRange(own);
        }

        if (factory?.Invoke(EditorSurface) is { Count: > 0 } editor)
        {
            if (items.Count > 0)
            {
                items.Add(new Separator());
            }

            items.AddRange(editor);
        }

        return items;
    }
}
