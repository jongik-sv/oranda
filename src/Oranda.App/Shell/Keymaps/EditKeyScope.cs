using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Oranda.App.Shell.Keymaps;

/// <summary>
/// 글자 편집 키(복사·붙여 넣기·모두 선택 등, <see cref="KeyDispatcher.IsTextEditingKey"/>)를 스스로 처리하는 영역 표시(TSK-03-14).
/// 이 영역 안에 포커스가 있으면 주 창은 그 키를 키맵으로 보내지 않는다. 포커스 자리(<see cref="FocusSlot"/>)가 없는 도구 창은
/// 직전 자리(편집기·결과 그리드)를 그대로 두므로, 막지 않으면 바인드 변수 창의 Ctrl+C 가 편집기 복사로 가 버린다.
/// </summary>
public static class EditKeyScope
{
    public static readonly AttachedProperty<bool> OwnsProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Owns", typeof(EditKeyScope));

    public static bool GetOwns(Control control) => control.GetValue(OwnsProperty);

    public static void SetOwns(Control control, bool value) => control.SetValue(OwnsProperty, value);

    /// <summary><paramref name="start"/> 또는 그 위 조상이 글자 편집 키를 스스로 처리하는 영역인가.</summary>
    public static bool Contains(Visual? start)
    {
        for (var v = start; v is not null; v = v.GetVisualParent())
        {
            if (v is Control c && GetOwns(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 시스템 메뉴가 키를 먼저 가져간 경우(macOS ⌘C 등): 포커스가 이 영역 안이면 같은 키를 그 요소에 KeyDown 으로 다시 보낸다.
    /// 영역이 처리했으면 true.
    /// </summary>
    public static bool TryRoute(object? focused, KeyGesture gesture)
    {
        if (focused is not InputElement element || !Contains(element)
            || !KeyDispatcher.IsTextEditingKey(gesture.Key, gesture.KeyModifiers, OperatingSystem.IsMacOS()))
        {
            return false;
        }

        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = gesture.Key,
            KeyModifiers = gesture.KeyModifiers,
            Source = element,
        };
        element.RaiseEvent(args);
        return args.Handled;
    }
}
