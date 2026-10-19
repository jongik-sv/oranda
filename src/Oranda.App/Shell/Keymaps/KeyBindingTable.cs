using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using Oranda.Core.Shell;

namespace Oranda.App.Shell.Keymaps;

/// <summary>키 입력 한 번(또는 두 번 연속)과 키맵 바인딩을 맞춰 본 결과.</summary>
public enum KeyMatchKind
{
    None,

    /// <summary>연속 키의 첫 단과 맞음. 다음 키를 기다린다.</summary>
    Prefix,

    Match,
}

public readonly record struct KeyMatch(KeyMatchKind Kind, string? CommandId = null);

/// <summary>
/// 키맵 한 벌을 플랫폼 제스처로 풀어 둔 조회표(commands.md 2.1 ③). 키맵의 모르는 명령 ID 는 여기서 걸러내지 않는다
/// (레지스트리가 실행할 때 무시한다). 같은 키가 여러 <c>when</c> 에 있으면 지금 참인 것 중 포커스 범위가 있는 쪽이 이긴다.
/// </summary>
public sealed class KeyBindingTable
{
    private readonly List<Entry> _entries;

    private KeyBindingTable(List<Entry> entries) => _entries = entries;

    public static KeyBindingTable Empty { get; } = new([]);

    public static KeyBindingTable Build(Keymap keymap, bool macOS)
    {
        ArgumentNullException.ThrowIfNull(keymap);
        var entries = new List<Entry>();
        foreach (var b in keymap.Bindings)
        {
            foreach (var text in b.KeysFor(macOS))
            {
                entries.Add(new Entry(b.Command, b.When, KeyChord.Parse(text, macOS), text));
            }
        }

        return new KeyBindingTable(entries);
    }

    /// <summary>입력한 제스처들(최대 2단)과 맞는 바인딩을 찾는다.</summary>
    public KeyMatch Find(IReadOnlyList<(Key Key, KeyModifiers Modifiers)> typed, IContextKeys keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        Entry? exact = null;
        var prefix = false;
        foreach (var e in _entries)
        {
            if (e.Chord.Count < typed.Count || !Same(e.Chord, typed))
            {
                continue;
            }

            if (!keys.Evaluate(e.When))
            {
                continue;
            }

            if (e.Chord.Count > typed.Count)
            {
                prefix = true;
            }
            else if (exact is null || (exact.When is null && e.When is not null))
            {
                exact = e;
            }
        }

        return exact is not null ? new KeyMatch(KeyMatchKind.Match, exact.Command)
            : prefix ? new KeyMatch(KeyMatchKind.Prefix)
            : new KeyMatch(KeyMatchKind.None);
    }

    /// <summary>명령의 첫 단축키를 메뉴·팔레트에 보일 글자로. 없으면 null.</summary>
    public string? ShortcutText(string commandId)
    {
        var e = _entries.FirstOrDefault(x => x.Command == commandId);
        return e is null ? null : string.Join(' ', e.Chord.Select(Display));
    }

    /// <summary>명령의 첫 단축키(한 단일 때만 메뉴 InputGesture 로 쓸 수 있다).</summary>
    public KeyGesture? SingleGesture(string commandId)
    {
        var e = _entries.FirstOrDefault(x => x.Command == commandId && x.Chord.Count == 1);
        return e?.Chord[0];
    }

    /// <summary>제스처를 사람이 읽는 글자로(<c>Ctrl+OemComma</c> → <c>Ctrl+,</c>, <c>D0</c> → <c>0</c>).</summary>
    public static string Display(KeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        var key = gesture.Key switch
        {
            Key.Return => "Enter",
            Key.Back => "Backspace",
            Key.Prior => "PageUp",
            Key.Next => "PageDown",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemQuestion => "/",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemTilde => "`",
            >= Key.D0 and <= Key.D9 => ((int)gesture.Key - (int)Key.D0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => gesture.Key.ToString(),
        };

        var parts = new List<string>();
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            parts.Add(OperatingSystem.IsMacOS() ? "Cmd" : "Win");
        }

        parts.Add(key);
        return string.Join('+', parts);
    }

    private static bool Same(IReadOnlyList<KeyGesture> chord, IReadOnlyList<(Key Key, KeyModifiers Modifiers)> typed)
    {
        for (var i = 0; i < typed.Count; i++)
        {
            if (chord[i].Key != typed[i].Key || chord[i].KeyModifiers != typed[i].Modifiers)
            {
                return false;
            }
        }

        return true;
    }

    private sealed record Entry(string Command, string? When, IReadOnlyList<KeyGesture> Chord, string Text);
}
