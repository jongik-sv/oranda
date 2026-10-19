using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Input;

namespace Oranda.App.Shell.Keymaps;

/// <summary>
/// 키맵 파일 키 표기 ↔ Avalonia <see cref="KeyGesture"/> (data-model.md 2.4, commands.md 5.1).
/// 수식키 순서 <c>Primary+Ctrl+Alt+Shift+Meta+키</c>. <c>Primary</c> = Windows Ctrl, macOS Cmd(<see cref="KeyModifiers.Meta"/>).
/// </summary>
public static class KeyChord
{
    private static readonly string[] ModifierOrder = ["Primary", "Ctrl", "Alt", "Shift", "Meta"];

    /// <summary>
    /// 키 조합 문자열(연속 키는 공백 하나, 최대 2단)을 플랫폼 제스처로 바꾼다. 틀린 표기는 <see cref="InvalidDataException"/>.
    /// </summary>
    public static IReadOnlyList<KeyGesture> Parse(string text, bool macOS)
    {
        ArgumentNullException.ThrowIfNull(text);
        var steps = text.Split(' ');
        if (steps.Length is 0 or > 2)
        {
            throw new InvalidDataException($"키 조합 '{text}': 연속 키는 2단까지다.");
        }

        var result = new KeyGesture[steps.Length];
        for (var i = 0; i < steps.Length; i++)
        {
            result[i] = ParseStep(steps[i], macOS, text);
        }

        return result;
    }

    /// <summary>수식키가 정규 순서(<c>Primary+Ctrl+Alt+Shift+Meta</c>)이고 겹치지 않으면 true.</summary>
    public static bool IsNormalized(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var step in text.Split(' '))
        {
            var parts = step.Split('+');
            var last = -1;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var index = Array.IndexOf(ModifierOrder, parts[i]);
                if (index <= last)
                {
                    return false;
                }

                last = index;
            }
        }

        return true;
    }

    private static KeyGesture ParseStep(string step, bool macOS, string whole)
    {
        var parts = step.Split('+');
        var modifiers = KeyModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            modifiers |= parts[i] switch
            {
                "Primary" => macOS ? KeyModifiers.Meta : KeyModifiers.Control,
                "Ctrl" => KeyModifiers.Control,
                "Alt" => KeyModifiers.Alt,
                "Shift" => KeyModifiers.Shift,
                "Meta" => KeyModifiers.Meta,
                _ => throw new InvalidDataException($"키 조합 '{whole}': 모르는 수식키 '{parts[i]}'."),
            };
        }

        var keyName = parts[^1];
        if (keyName.Length == 0 || char.IsDigit(keyName[0]) || !Enum.TryParse<Key>(keyName, ignoreCase: false, out var key))
        {
            throw new InvalidDataException($"키 조합 '{whole}': 모르는 키 '{keyName}'.");
        }

        return new KeyGesture(key, modifiers);
    }
}
