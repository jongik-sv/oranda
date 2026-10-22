using System;
using System.Collections.Concurrent;

namespace Oranda.App.Shell;

/// <summary>
/// 문맥 키 식(commands.md 2.2: 이름, <c>!</c>, <c>&amp;&amp;</c>, <c>||</c>, 괄호)을 나무로 읽어,
/// 키마다 따로 값을 매겨 계산하거나 「왜 거짓인지」를 사람 말로 찾는다(주 메뉴의 회색 항목 툴팁, TSK-13-36).
/// 계산 규칙은 <see cref="ContextKeyService.Evaluate"/> 와 같다.
/// </summary>
public static class ContextKeyReasons
{
    /// <summary>이유 문구 키의 머리. <c>Menu.disabled.hasSession</c>, 부정은 <c>Menu.disabled.not.readOnlySession</c>.</summary>
    public const string PhrasePrefix = "Menu.disabled.";

    private static readonly ConcurrentDictionary<string, Node?> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// 거짓인 <paramref name="expression"/> 의 이유 문구. 식이 참이거나 비었거나, 거짓을 만든 키에 문구가 없으면 null.
    /// <c>&amp;&amp;</c> 는 거짓인 항을 앞에서부터, <c>||</c> 는 문구가 있는 첫 항을 쓴다. <c>!x</c> 가 거짓이면(x 가 참) <c>not.x</c> 문구.
    /// </summary>
    /// <param name="value">키 하나의 지금 값.</param>
    /// <param name="phrase">문구 키(<see cref="PhrasePrefix"/> 포함) → 글자. 없으면 null.</param>
    public static string? Explain(string? expression, Func<string, bool> value, Func<string, string?> phrase)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(phrase);
        if (Parse(expression) is not { } node || node.Eval(value))
        {
            return null;
        }

        return node.Why(value, phrase);
    }

    /// <summary>키마다 <paramref name="value"/> 로 값을 매겨 식을 계산한다. 빈 식은 참, 읽을 수 없는 식은 <paramref name="malformed"/>.</summary>
    public static bool Evaluate(string? expression, Func<string, bool> value, bool malformed = true)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(expression))
        {
            return true;
        }

        return Parse(expression) is { } node ? node.Eval(value) : malformed;
    }

    private static Node? Parse(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        return Cache.GetOrAdd(expression, static e =>
        {
            try
            {
                return new Parser(e).ParseAll();
            }
            catch (FormatException)
            {
                return null;
            }
        });
    }

    private abstract class Node
    {
        public abstract bool Eval(Func<string, bool> value);

        /// <summary>이 마디가 거짓일 때의 이유 문구(없으면 null).</summary>
        public abstract string? Why(Func<string, bool> value, Func<string, string?> phrase);
    }

    private sealed class Atom(string name) : Node
    {
        public string Name { get; } = name;

        public override bool Eval(Func<string, bool> value) => value(Name);

        public override string? Why(Func<string, bool> value, Func<string, string?> phrase) => phrase(PhrasePrefix + Name);
    }

    private sealed class Not(Node inner) : Node
    {
        public override bool Eval(Func<string, bool> value) => !inner.Eval(value);

        public override string? Why(Func<string, bool> value, Func<string, string?> phrase) =>
            inner is Atom a ? phrase(PhrasePrefix + "not." + a.Name) : null;
    }

    private sealed class Binary(Node left, Node right, bool and) : Node
    {
        public override bool Eval(Func<string, bool> value) => and ? left.Eval(value) && right.Eval(value) : left.Eval(value) || right.Eval(value);

        public override string? Why(Func<string, bool> value, Func<string, string?> phrase)
        {
            // && 는 거짓인 항만, || 는(전체가 거짓이므로) 모든 항이 거짓: 앞에서부터 문구가 있는 첫 항.
            foreach (var side in (ReadOnlySpan<Node>)[left, right])
            {
                if (and && side.Eval(value))
                {
                    continue;
                }

                if (side.Why(value, phrase) is { } found)
                {
                    return found;
                }
            }

            return null;
        }
    }

    private sealed class Parser(string text)
    {
        private int _pos;

        public Node ParseAll()
        {
            var node = ParseOr();
            SkipSpace();
            if (_pos != text.Length)
            {
                throw new FormatException(text);
            }

            return node;
        }

        private Node ParseOr()
        {
            var left = ParseAnd();
            while (Match("||"))
            {
                left = new Binary(left, ParseAnd(), and: false);
            }

            return left;
        }

        private Node ParseAnd()
        {
            var left = ParseUnary();
            while (Match("&&"))
            {
                left = new Binary(left, ParseUnary(), and: true);
            }

            return left;
        }

        private Node ParseUnary()
        {
            if (Match("!"))
            {
                return new Not(ParseUnary());
            }

            if (Match("("))
            {
                var inner = ParseOr();
                if (!Match(")"))
                {
                    throw new FormatException(text);
                }

                return inner;
            }

            SkipSpace();
            var start = _pos;
            while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] == '_'))
            {
                _pos++;
            }

            if (start == _pos)
            {
                throw new FormatException(text);
            }

            return new Atom(text[start.._pos]);
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
