using System.Globalization;
using System.Text.RegularExpressions;

namespace Oranda.Core.Ai.Gate;

/// <summary>
/// 리터럴 값 사전(설계 ai-tuning-2): SQL 글에서 가린 리터럴의 원래 토큰 글을 <c>:v1</c> 꼴 자리표시와 짝지어 기억한다.
/// 수명은 AI 대화 하나(이어 말하기 포함)이고, 대화를 시작하는 쪽이 만들어 세션(<see cref="AgentRunScope.Literals"/>)에 넘긴다.
/// <b>원래 값은 LLM 으로 가는 길(프롬프트·게이트 결과)에 실리면 안 된다.</b> 그래서 직렬화하지 않고 <see cref="ToString"/> 도 값을 내지 않으며,
/// 값을 꺼내는 길은 <see cref="TryGetRaw"/>(자리표시 하나를 되돌릴 때)와 <see cref="Redact"/>(글에 섞인 값을 다시 가릴 때)뿐이다.
/// 여러 스레드가 함께 써도 안전하다.
/// </summary>
public sealed partial class LiteralVault
{
    /// <summary>기본 자리표시 접두(<c>:v1</c> 의 <c>v</c>).</summary>
    public const string DefaultPrefix = "v";

    private const int MinRedactedInnerLength = 3;
    private const int MinRedactedNumberLength = 4;

    private readonly object _gate = new();
    private readonly List<string> _raw = [];
    private readonly Dictionary<string, int> _numbers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _statements = new(StringComparer.Ordinal);

    /// <param name="prefix">자리표시 접두. 영문자로 시작하고 영문자·숫자·밑줄만 쓴다(<c>:</c> 와 번호는 붙이지 않는다).</param>
    public LiteralVault(string prefix = DefaultPrefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (!PrefixShape().IsMatch(prefix))
        {
            throw new ArgumentException("The placeholder prefix must start with a letter and contain only letters, digits and underscores.", nameof(prefix));
        }

        Prefix = prefix;
    }

    /// <summary>자리표시 접두(<c>:</c> 와 번호 제외). 예: <c>v</c>.</summary>
    public string Prefix { get; }

    /// <summary>기억한 리터럴 수.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _raw.Count;
            }
        }
    }

    /// <summary>번호 <paramref name="number"/>(1부터)의 자리표시 글. 예: <c>:v1</c>.</summary>
    public string Placeholder(int number) => ":" + Prefix + number.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 원래 토큰 글(따옴표·<c>q'…'</c>·<c>DATE '…'</c> 묶음 포함)을 기억하고 자리표시를 돌려준다.
    /// 같은 글은 같은 번호를 받는다(대소문자를 구분한다 — 문자열 값은 대소문자가 의미다).
    /// </summary>
    public string Add(string rawToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(rawToken);
        lock (_gate)
        {
            if (!_numbers.TryGetValue(rawToken, out var number))
            {
                _raw.Add(rawToken);
                number = _raw.Count;
                _numbers[rawToken] = number;
            }

            return Placeholder(number);
        }
    }

    /// <summary>
    /// 이 사전이 만든 자리표시의 모양인가(<c>:v</c> 접두 + 숫자). 사전에 있는지는 보지 않는다 —
    /// 모양은 맞는데 <see cref="TryGetRaw"/> 가 실패하면 LLM 이 지어낸 자리표시다. 앞의 <c>:</c> 는 있어도 없어도 된다. Oracle 바인드 이름처럼 대소문자는 가리지 않는다.
    /// </summary>
    public bool IsOwnPlaceholder(string bindText)
    {
        ArgumentNullException.ThrowIfNull(bindText);
        return TryParseNumber(bindText, out _);
    }

    /// <summary>자리표시(<c>:v1</c>, 앞의 <c>:</c> 는 없어도 된다)의 원래 토큰 글. 이 사전에 없으면 false.</summary>
    public bool TryGetRaw(string placeholder, out string raw)
    {
        ArgumentNullException.ThrowIfNull(placeholder);
        raw = string.Empty;
        if (!TryParseNumber(placeholder, out var number))
        {
            return false;
        }

        lock (_gate)
        {
            if (number < 1 || number > _raw.Count)
            {
                return false;
            }

            raw = _raw[number - 1];
            return true;
        }
    }

    /// <summary>
    /// 가린 문장 글과 그 원래 글을 짝지어 기억한다. 가린 글이 돌아오면 주석·sql_id 가 남은 원래 글을 쓰려는 용도이다.
    /// 같은 가린 글이 다시 오면 처음 짝을 유지한다.
    /// </summary>
    public void RememberStatement(string maskedText, string originalText)
    {
        ArgumentNullException.ThrowIfNull(maskedText);
        ArgumentNullException.ThrowIfNull(originalText);
        lock (_gate)
        {
            _statements.TryAdd(maskedText, originalText);
        }
    }

    /// <summary><paramref name="maskedText"/> 가 <see cref="RememberStatement"/> 로 기억한 가린 글과 정확히 같으면 그 원래 글.</summary>
    public bool TryGetStatement(string maskedText, out string originalText)
    {
        ArgumentNullException.ThrowIfNull(maskedText);
        lock (_gate)
        {
            return _statements.TryGetValue(maskedText, out originalText!);
        }
    }

    /// <summary>
    /// 글에 섞인 원래 값을 자리표시로 다시 가린다(오류 글 정화용). 토큰 전체(따옴표 포함)와, 따옴표 문자열이면 짧지 않은(3자 이상) 안쪽 글,
    /// 4자리 이상 숫자 토큰을 바꾼다. 짧은 값은 <c>ORA-00001</c> 같은 정상 글을 망가뜨리므로 건드리지 않는다. 긴 값부터 바꾼다.
    /// </summary>
    public string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return text;
        }

        List<KeyValuePair<string, int>> entries;
        lock (_gate)
        {
            entries = [.. _numbers];
        }

        // 한 번의 패스로 바꾼다: 순서대로 여러 번 바꾸면 앞서 넣은 자리표시(:v12 의 12 등)를 다음 값이 다시 건드린다.
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        var alternatives = new List<(int Length, string Pattern)>();
        foreach (var (raw, number) in entries)
        {
            var placeholder = Placeholder(number);
            if (raw.Contains('\'', StringComparison.Ordinal))
            {
                AddTarget(raw, placeholder, Regex.Escape(raw));
                var first = raw.IndexOf('\'', StringComparison.Ordinal);
                var last = raw.LastIndexOf('\'');
                if (last - first - 1 >= MinRedactedInnerLength)
                {
                    var inner = raw.Substring(first + 1, last - first - 1);
                    AddTarget(inner, placeholder, Regex.Escape(inner));
                }
            }
            else if (raw.Length >= MinRedactedNumberLength && IsNumeric(raw))
            {
                AddTarget(raw, placeholder, @"(?<![\w.])" + Regex.Escape(raw) + @"(?![\w.])");
            }
        }

        if (alternatives.Count == 0)
        {
            return text;
        }

        // 긴 값을 먼저 맞춘다(짧은 값이 긴 값의 일부를 먼저 가져가지 않게).
        alternatives.Sort((a, b) => b.Length.CompareTo(a.Length));
        var pattern = string.Join('|', alternatives.Select(a => "(?:" + a.Pattern + ")"));
        return Regex.Replace(
            text,
            pattern,
            m => targets[m.Value],
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        void AddTarget(string matched, string placeholder, string regex)
        {
            if (targets.TryAdd(matched, placeholder))
            {
                alternatives.Add((matched.Length, regex));
            }
        }
    }

    /// <summary>값을 내지 않는다: 형 이름과 개수만. 로그·예외·감사에 우연히 실려도 원래 값이 새지 않게 한다.</summary>
    public override string ToString() => nameof(LiteralVault) + "(" + Count.ToString(CultureInfo.InvariantCulture) + ")";

    private bool TryParseNumber(string bindText, out int number)
    {
        number = 0;
        var span = bindText.AsSpan();
        if (span.Length > 0 && span[0] == ':')
        {
            span = span[1..];
        }

        if (span.Length <= Prefix.Length || !span.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var digits = span[Prefix.Length..];
        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        // 선행 0(:v01)은 이 사전이 만든 모양이 아니다. 같은 값으로 읽히게 두지 않는다.
        if (digits.Length > 1 && digits[0] == '0')
        {
            return false;
        }

        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static bool IsNumeric(string raw)
    {
        foreach (var c in raw)
        {
            if (!(char.IsAsciiDigit(c) || c is '.' or '+' or '-' or 'e' or 'E'))
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixShape();
}
