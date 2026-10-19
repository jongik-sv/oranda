using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Oranda.App.Shell.Keymaps;

/// <summary>
/// 화면이 자기 포커스 자리(<c>sqlText</c>·<c>sqlGrid</c>·<c>plsql</c>·<c>schema</c>·<c>monitor</c>, commands.md 2.2)를 알리는 붙임 속성.
/// 주 창이 포커스가 옮겨질 때 가장 가까운 조상의 값을 문맥 키로 옮긴다. 그래야 <c>when: sqlDocFocus</c> 같은 단축키가 동작한다.
/// </summary>
public static class FocusSlot
{
    public static readonly AttachedProperty<string?> NameProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Name", typeof(FocusSlot));

    public static string? GetName(Control control) => control.GetValue(NameProperty);

    public static void SetName(Control control, string? value) => control.SetValue(NameProperty, value);

    /// <summary><paramref name="start"/> 부터 위로 올라가며 처음 만나는 자리. 없으면 null.</summary>
    public static string? Find(Visual? start)
    {
        for (var v = start; v is not null; v = v.GetVisualParent())
        {
            if (v is Control c && GetName(c) is { } slot)
            {
                return slot;
            }
        }

        return null;
    }
}
