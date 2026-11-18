using System.Globalization;
using System.Text.RegularExpressions;

namespace Oranda.Core.Ai.Features;

/// <summary>자리표시로 바꾸는 값의 종류. 자리표시 접두가 된다(<c>USER1</c>·<c>HOST1</c>).</summary>
public enum AiAliasKind
{
    /// <summary>DB 사용자·OS 사용자.</summary>
    User,

    /// <summary>머신(클라이언트 PC) 이름.</summary>
    Host,

    /// <summary>프로그램 이름.</summary>
    Program,

    /// <summary>모듈 이름.</summary>
    Module,
}

/// <summary>
/// 사용자명·머신 이름·프로그램을 <c>USER1</c>·<c>HOST1</c> 꼴 자리표시로 바꾸는 사전(결정 AE-3: 「계획」 수준이면 가린다).
/// 같은 값(대소문자 무시)은 같은 자리표시다. <see cref="Restore"/> 는 받은 답을 이 PC 화면에 보일 때만 쓴다.
/// </summary>
public sealed partial class AiAliasMap
{
    private readonly Dictionary<(AiAliasKind Kind, string Value), string> _aliases = new(new KeyComparer());
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<AiAliasKind, int> _counts = [];

    /// <summary>기억한 값 수.</summary>
    public int Count => _values.Count;

    /// <summary>값의 자리표시. 비었으면 null.</summary>
    public string? Alias(AiAliasKind kind, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var key = (kind, value.Trim());
        if (_aliases.TryGetValue(key, out var alias))
        {
            return alias;
        }

        var number = _counts.GetValueOrDefault(kind) + 1;
        _counts[kind] = number;
        alias = kind.ToString().ToUpperInvariant() + number.ToString(CultureInfo.InvariantCulture);
        _aliases[key] = alias;
        _values[alias] = key.Item2;
        return alias;
    }

    /// <summary>글 속 자리표시(낱말 단위)를 원래 값으로 되돌린다. 모르는 자리표시는 그대로 둔다.</summary>
    public string Restore(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _values.Count == 0 ? text : AliasShape().Replace(text, m => _values.TryGetValue(m.Value, out var v) ? v : m.Value);
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?:USER|HOST|PROGRAM|MODULE)\d+(?![A-Za-z0-9_])")]
    private static partial Regex AliasShape();

    private sealed class KeyComparer : IEqualityComparer<(AiAliasKind Kind, string Value)>
    {
        public bool Equals((AiAliasKind Kind, string Value) x, (AiAliasKind Kind, string Value) y) =>
            x.Kind == y.Kind && string.Equals(x.Value, y.Value, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((AiAliasKind Kind, string Value) obj) =>
            HashCode.Combine(obj.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Value));
    }
}
