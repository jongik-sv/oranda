using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Input;
using Oranda.Core.Shell;

namespace Oranda.App.Shell.Keymaps;

/// <summary>
/// 창이 받은 키를 키맵 → 명령 레지스트리로 보낸다. 메뉴·툴바·팔레트와 같은 <see cref="ICommandRegistry.ExecuteAsync"/> 한 길이다.
/// 연속 키(예: <c>Primary+K Primary+C</c>)는 첫 단 뒤 <see cref="ChordTimeout"/> 안에 다음 키를 기다린다.
/// </summary>
public sealed class KeyDispatcher(Func<KeyBindingTable> table, ICommandRegistry registry, IContextKeys keys)
{
    public static readonly TimeSpan ChordTimeout = TimeSpan.FromSeconds(2);

    private readonly List<(Key Key, KeyModifiers Modifiers)> _pending = [];
    private DateTime _pendingAt;

    /// <summary>
    /// 입력 칸(TextBox)이 직접 처리해야 하는 글자 편집 키인지. 수식키 없는 키(글자·방향·지우기), Shift 만 누른 키,
    /// 플랫폼 기본 편집 단축키(모두 선택·복사·붙여 넣기·잘라내기·실행 취소·다시 실행, 단어 단위 이동·지우기)다.
    /// F 키와 Primary+Enter 같은 실행 단축키는 아니다(입력 칸에서도 명령으로 간다).
    /// </summary>
    public static bool IsTextEditingKey(Key key, KeyModifiers modifiers, bool macOS)
    {
        if (key is >= Key.F1 and <= Key.F24)
        {
            return false;
        }

        if (modifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            return key is not (Key.Escape or Key.Enter or Key.Tab);
        }

        var primary = macOS ? KeyModifiers.Meta : KeyModifiers.Control;
        var word = macOS ? KeyModifiers.Alt : KeyModifiers.Control;
        if (modifiers == primary || modifiers == (primary | KeyModifiers.Shift))
        {
            if (key is Key.A or Key.C or Key.V or Key.X or Key.Z or Key.Y or Key.Left or Key.Right or Key.Up or Key.Down or Key.Back or Key.Delete or Key.Home or Key.End)
            {
                return true;
            }
        }

        return (modifiers == word || modifiers == (word | KeyModifiers.Shift))
            && key is Key.Left or Key.Right or Key.Back or Key.Delete;
    }

    /// <summary>
    /// 입력기가 먼저 받은 키는 <see cref="Key.ImeProcessed"/> 로 오므로(한국어 2벌식의 ⌥C 등), 수식키(Alt·Ctrl·Meta)가 있으면
    /// 실제로 누른 자판 위치(<paramref name="physical"/>)의 영문 키로 바꿔 키맵에 맞춘다. 수식키 없는 입력(한글 조합)은 그대로 둔다.
    /// </summary>
    public static Key EffectiveKey(Key key, PhysicalKey physical, KeyModifiers modifiers)
    {
        if (key != Key.ImeProcessed || (modifiers & (KeyModifiers.Alt | KeyModifiers.Control | KeyModifiers.Meta)) == 0)
        {
            return key;
        }

        var resolved = physical.ToQwertyKey();
        return resolved == Key.None ? key : resolved;
    }

    /// <summary>연속 키의 첫 단을 받고 다음 단을 기다리는 중인가(<see cref="ChordTimeout"/> 안).</summary>
    public bool HasPending => _pending.Count > 0 && DateTime.UtcNow - _pendingAt <= ChordTimeout;

    /// <summary>키를 처리했으면 true(이벤트를 삼킨다). 수식키만 눌린 입력은 무시한다.</summary>
    public bool Handle(Key key, KeyModifiers modifiers)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.None)
        {
            KeyTrace.Ignored(key, modifiers);
            return false;
        }

        if (_pending.Count > 0 && DateTime.UtcNow - _pendingAt > ChordTimeout)
        {
            _pending.Clear();
        }

        _pending.Add((key, modifiers));
        var match = table().Find(_pending, keys);
        if (match.Kind == KeyMatchKind.None && _pending.Count > 1)
        {
            // 연속 키가 어긋났다. 이 키를 새 첫 단으로 다시 본다.
            _pending.Clear();
            _pending.Add((key, modifiers));
            match = table().Find(_pending, keys);
        }

        if (KeyTrace.Enabled)
        {
            KeyTrace.Dispatch(key, modifiers, _pending.Count, match, keys.Evaluate("editorFocus"));
        }

        switch (match.Kind)
        {
            case KeyMatchKind.Prefix:
                _pendingAt = DateTime.UtcNow;
                return true;

            case KeyMatchKind.Match:
                _pending.Clear();
                Run(match.CommandId!);
                return true;

            default:
                _pending.Clear();
                return false;
        }
    }

    /// <summary>키 입력으로 실행한 명령이 끝날 때까지 기다리고 싶은 시험이 쓴다.</summary>
    public Task? LastRun { get; private set; }

    private void Run(string commandId)
    {
        LastRun = RunAsync(commandId);
    }

    private async Task RunAsync(string commandId)
    {
        try
        {
            if (!await registry.ExecuteAsync(commandId, keys).ConfigureAwait(true))
            {
                Trace.TraceWarning("키맵 명령 '{0}' 을(를) 실행하지 않았다(등록되지 않았거나 지금은 비활성).", commandId);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError("명령 '{0}' 실행 중 예외: {1}", commandId, ex);
        }
    }
}
