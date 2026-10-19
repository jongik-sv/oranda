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

    /// <summary>키를 처리했으면 true(이벤트를 삼킨다). 수식키만 눌린 입력은 무시한다.</summary>
    public bool Handle(Key key, KeyModifiers modifiers)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.None)
        {
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
