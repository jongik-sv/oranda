using System;
using Avalonia.Input;

namespace Oranda.App.Shell.Keymaps;

/// <summary>
/// macOS 에서 Option+문자가 키맵 명령으로 실행되지 않고 글자(c·ç)로 편집기에 들어가는 경로를 막는다.
/// 입력기(한국어 2벌식 등)가 Option+문자를 먼저 글자로 바꾸면 KeyDown 이 명령과 맞지 않거나, 맞아도 뒤따라 TextInput 이 오기 때문이다.
/// 직전 Option 키 입력(<see cref="NoteKeyDown"/>)을 기억해 두었다가, 바로 뒤의 TextInput 을 삼킬지 <see cref="ShouldSwallowText"/> 로 정한다.
/// 바인딩 없는 Option+문자는 삼키지 않으므로 ⌥C → ç 입력은 그대로다.
/// </summary>
public sealed class OptionTextGuard(KeyDispatcher dispatcher, Func<DateTime>? clock = null)
{
    /// <summary>Option 키 입력과 그 뒤 TextInput 사이에 허용하는 시간.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(250);

    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private (Key Key, KeyModifiers Modifiers, bool Handled, DateTime At)? _last;

    /// <summary>창의 KeyDown 처리 결과를 알린다. Option 만 섞인 입력(⌘·⌃ 없음)만 기억한다.</summary>
    public void NoteKeyDown(Key key, KeyModifiers modifiers, bool handled)
    {
        if (key is Key.LeftAlt or Key.RightAlt
            || !modifiers.HasFlag(KeyModifiers.Alt)
            || modifiers.HasFlag(KeyModifiers.Meta)
            || modifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        _last = (key, modifiers, handled, _now());
    }

    /// <summary>
    /// 방금 Option 키 입력에 딸린 글자 입력이면 true(편집기에 넣지 않는다).
    /// KeyDown 이 명령과 맞았으면 삼킨다. 입력기가 키를 None 으로 넘겨 맞지 못했다면 글자로 키를 되짚어 명령을 실행한다.
    /// </summary>
    public bool ShouldSwallowText(string? text)
    {
        if (_last is not { } last)
        {
            return false;
        }

        _last = null;
        if (_now() - last.At > Window)
        {
            return false;
        }

        if (last.Handled)
        {
            return true;
        }

        if (last.Key == Key.None && text is { Length: 1 } && char.IsAsciiLetter(text[0])
            && Enum.TryParse<Key>(char.ToUpperInvariant(text[0]).ToString(), out var key))
        {
            return dispatcher.Handle(key, last.Modifiers);
        }

        return false;
    }
}
