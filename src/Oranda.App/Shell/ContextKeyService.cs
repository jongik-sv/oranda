using System;
using System.Collections.Generic;
using System.Linq;
using Oranda.App.Shell.Keymaps;
using Oranda.Core.Shell;

namespace Oranda.App.Shell;

/// <summary>
/// 문맥 키 저장소와 식 계산기(commands.md 2.2). 식 문법: 이름(글자·숫자·<c>_</c>·<c>.</c>), <c>!</c>, <c>&amp;&amp;</c>, <c>||</c>, 괄호.
/// 모르는 이름은 거짓. 상태 키는 <see cref="Set"/>, 포커스 키는 <see cref="SetFocusSlot"/> 으로 바꾼다
/// (포커스 자리는 한 순간에 하나만 참, <see cref="FocusScopes"/>).
/// </summary>
public sealed class ContextKeyService : IContextKeys
{
    /// <summary>
    /// 주 창에 붙은 셸의 문맥 키. DI 없이 ViewLocator 가 만드는 화면(결과 그리드 등)이 상태 키를 알릴 때 쓴다. 주 창이 설정한다.
    /// </summary>
    public static ContextKeyService? Shared { get; set; }

    private readonly object _gate = new();
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private string? _focusSlot;

    /// <summary>키 값이 바뀌었을 때(메뉴·툴바 활성 상태를 다시 계산하라는 신호).</summary>
    public event EventHandler? Changed;

    /// <summary>개발 빌드 여부(<c>devBuild</c> 키).</summary>
    public ContextKeyService(bool devBuild = false)
    {
        if (devBuild)
        {
            _keys.Add("devBuild");
        }
    }

    /// <summary>지금 참인 상태 키(포커스 키 제외) 이름들.</summary>
    public IReadOnlyCollection<string> TrueKeys
    {
        get
        {
            lock (_gate)
            {
                return [.. _keys];
            }
        }
    }

    public string? FocusSlot
    {
        get
        {
            lock (_gate)
            {
                return _focusSlot;
            }
        }
    }

    public void Set(string key, bool value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        bool changed;
        lock (_gate)
        {
            changed = value ? _keys.Add(key) : _keys.Remove(key);
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>여러 상태 키를 한 번에 바꾼다. 하나라도 바뀌었으면 <see cref="Changed"/> 를 한 번만 낸다(기능 게이트의 <c>feature.*</c> 키).</summary>
    public void SetMany(IEnumerable<(string Key, bool Value)> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var changed = false;
        lock (_gate)
        {
            foreach (var (key, value) in values)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(key);
                changed |= value ? _keys.Add(key) : _keys.Remove(key);
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool Get(string key)
    {
        lock (_gate)
        {
            return _keys.Contains(key) || IsFocusKey(key);
        }
    }

    /// <summary>포커스 자리(<c>sqlText</c>, <c>sqlGrid</c>, <c>plsql</c>, <c>schema</c>, <c>monitor</c>)를 바꾼다. null = 어느 자리도 아님.</summary>
    public void SetFocusSlot(string? slot)
    {
        lock (_gate)
        {
            if (_focusSlot == slot)
            {
                return;
            }

            _focusSlot = slot;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Evaluate(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return true;
        }

        var parser = new Parser(expression, Get);
        return parser.ParseAll();
    }

    private bool IsFocusKey(string key)
    {
        if (_focusSlot is null || !FocusScopes.Keys.Contains(key))
        {
            return false;
        }

        return FocusScopes.SlotsOf(key).Contains(_focusSlot);
    }

    private sealed class Parser(string text, Func<string, bool> lookup)
    {
        private int _pos;

        public bool ParseAll()
        {
            var value = ParseOr();
            SkipSpace();
            if (_pos != text.Length)
            {
                throw new FormatException($"조건식 '{text}' 의 {_pos} 번째 글자에서 읽을 수 없다.");
            }

            return value;
        }

        private bool ParseOr()
        {
            var left = ParseAnd();
            while (Match("||"))
            {
                var right = ParseAnd();
                left = left || right;
            }

            return left;
        }

        private bool ParseAnd()
        {
            var left = ParseUnary();
            while (Match("&&"))
            {
                var right = ParseUnary();
                left = left && right;
            }

            return left;
        }

        private bool ParseUnary()
        {
            if (Match("!"))
            {
                return !ParseUnary();
            }

            if (Match("("))
            {
                var inner = ParseOr();
                if (!Match(")"))
                {
                    throw new FormatException($"조건식 '{text}': 닫는 괄호가 없다.");
                }

                return inner;
            }

            SkipSpace();
            var start = _pos;
            // 점은 이름의 일부다(기능 키 feature.<id>, TRD 10.6).
            while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] is '_' or '.'))
            {
                _pos++;
            }

            if (start == _pos)
            {
                throw new FormatException($"조건식 '{text}' 의 {_pos} 번째 글자에서 이름이 와야 한다.");
            }

            return lookup(text[start.._pos]);
        }

        private bool Match(string token)
        {
            SkipSpace();
            if (string.CompareOrdinal(text, _pos, token, 0, token.Length) == 0)
            {
                _pos += token.Length;
                return true;
            }

            return false;
        }

        private void SkipSpace()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
            {
                _pos++;
            }
        }
    }
}
