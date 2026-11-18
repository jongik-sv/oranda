using System.Globalization;
using System.Text.RegularExpressions;

namespace Oranda.Core.Schema.TableEdit.Partitions.Advisor;

/// <summary>
/// 실제 값 가림 금고(설계 PA-3). 보내기 전에 (열, 값) 마다 자리표시자 <c>V1</c>, <c>V2</c>… 를 주고, 받은 뒤 되돌린다.
/// 같은 (열, 값) 은 같은 자리표시자. 열 이름·자리표시자는 대소문자 무시, 값은 그대로 비교한다. 한 요청(프롬프트 하나)에 하나.
/// </summary>
public sealed partial class EvidenceValueVault
{
    private readonly Dictionary<(string Column, string Value), string> _tokens = [];
    private readonly Dictionary<string, (string Column, string Value)> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>준 자리표시자 수.</summary>
    public int Count => _values.Count;

    /// <summary>값의 자리표시자. 처음 보는 (열, 값) 이면 다음 번호.</summary>
    public string Mask(string column, string value)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(value);
        var key = (column.ToUpperInvariant(), value);
        if (_tokens.TryGetValue(key, out var token))
        {
            return token;
        }

        token = "V" + (_values.Count + 1).ToString(CultureInfo.InvariantCulture);
        _tokens[key] = token;
        _values[token] = key;
        return token;
    }

    /// <summary>자리표시자 → 실제 값. 그 열에서 준 자리표시자가 아니면 null.</summary>
    public string? Unmask(string column, string token)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(token);
        return _values.TryGetValue(token.Trim(), out var entry) && string.Equals(entry.Column, column, StringComparison.OrdinalIgnoreCase)
            ? entry.Value
            : null;
    }

    /// <summary>자리표시자 꼴(<c>V</c> + 숫자, 대소문자 무시)인가. 금고가 준 것인지는 보지 않는다.</summary>
    public static bool LooksLikeToken(string? text) => text is not null && TokenPattern().IsMatch(text.Trim());

    [GeneratedRegex(@"^[Vv][0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
