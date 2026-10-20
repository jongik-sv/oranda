using System;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Oranda.App.Shell.Keymaps;

namespace Oranda.App.Shell.Menus;

/// <summary>
/// macOS 시스템 메뉴 항목이 쓰는 <see cref="ICommand"/>. 메뉴 단축키(key equivalent)는 창이 키를 받기 전에 시스템이 먼저 가져가므로,
/// 입력 칸(TextBox)에 포커스가 있을 때의 글자 편집 키(⌘C·⌘X·⌘V·⌘A·⌘Z·⇧⌘Z)는 명령 대신 그 입력 칸에 전달한다.
/// 연속 키의 둘째 단은 키맵(<see cref="KeyDispatcher"/>)이 이어 받는다. 그 밖의 경우는 <see cref="RegistryCommand"/> 로 간다.
/// </summary>
internal sealed class NativeMenuCommand(RegistryCommand inner, KeyGesture? gesture, Func<object?> focused, KeyDispatcher? dispatcher = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => inner.CanExecuteChanged += value;
        remove => inner.CanExecuteChanged -= value;
    }

    public string CommandId => inner.CommandId;

    public bool CanExecute(object? parameter) => inner.CanExecute(parameter);

    public void Execute(object? parameter)
    {
        // 연속 키(예: ⌘K ⌘C)의 둘째 단을 메뉴가 먼저 가져갔으면 키맵 쪽 대기 상태로 넘겨 이어 간다.
        if (gesture is { } k && dispatcher is { HasPending: true } && dispatcher.Handle(k.Key, k.KeyModifiers))
        {
            return;
        }

        if (gesture is { } g && focused() is TextBox box && TryEdit(box, g))
        {
            return;
        }

        inner.Execute(parameter);
    }

    /// <summary>입력 칸이 직접 처리하는 편집 키면 해당 동작을 실행하고 true.</summary>
    internal static bool TryEdit(TextBox box, KeyGesture gesture)
    {
        if (!KeyDispatcher.IsTextEditingKey(gesture.Key, gesture.KeyModifiers, macOS: true))
        {
            return false;
        }

        var shift = gesture.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (gesture.Key)
        {
            case Key.C when !shift:
                box.Copy();
                return true;
            case Key.X when !shift:
                box.Cut();
                return true;
            case Key.V when !shift:
                box.Paste();
                return true;
            case Key.A when !shift:
                box.SelectAll();
                return true;
            case Key.Z when !shift:
                box.Undo();
                return true;
            case Key.Z or Key.Y:
                box.Redo();
                return true;
            default:
                return false;
        }
    }
}
