using System.Globalization;
using System.Text.RegularExpressions;

namespace Oranda.Core.Ai.Gate;

/// <summary>
/// 리터럴 값 사전(설계 ai-tuning-2): SQL 글에서 가린 리터럴의 원래 토큰 글을 <c>:v1</c> 꼴 자리표시와 짝지어 기억한다.
/// 주석도 본문 대신 <c>/*c1*/</c>·<c>--c1</c> 꼴 자리표시로 보내므로 원래 주석 글을 따로 번호를 매겨 기억한다(리터럴 번호와 따로 1부터).
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
    private readonly List<string> _comments = [];
    private readonly Dictionary<string, int> _commentNumbers = new(StringComparer.Ordinal);

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
        return HasOwnShape(bindText, out _);
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

    /// <summary>기억한 주석 수(같은 글은 하나로 센다).</summary>
    public int CommentCount
    {
        get
        {
            lock (_gate)
            {
                return _comments.Count;
            }
        }
    }

    /// <summary>
    /// 원래 주석 토큰 글(<c>-- …</c> 또는 <c>/* … */</c>)을 기억하고 같은 종류의 자리표시(<c>--c1</c>·<c>/*c1*/</c>)를 돌려준다.
    /// 같은 글은 같은 번호를 받는다: 프롬프트와 도구 결과의 SQL 글에 나온 같은 주석이 같은 자리표시가 된다.
    /// </summary>
    public string AddComment(string rawComment)
    {
        ArgumentException.ThrowIfNullOrEmpty(rawComment);
        var line = rawComment.StartsWith("--", StringComparison.Ordinal);
        if (!line && !(rawComment.Length >= 4 && rawComment.StartsWith("/*", StringComparison.Ordinal) && rawComment.EndsWith("*/", StringComparison.Ordinal)))
        {
            throw new ArgumentException("A comment must start with -- or be enclosed in /* */.", nameof(rawComment));
        }

        lock (_gate)
        {
            if (!_commentNumbers.TryGetValue(rawComment, out var number))
            {
                _comments.Add(rawComment);
                number = _comments.Count;
                _commentNumbers[rawComment] = number;
            }

            return CommentPlaceholder(number, line);
        }
    }

    /// <summary>번호 <paramref name="number"/>(1부터)의 주석 자리표시. 줄 주석이면 <c>--c1</c>, 블록 주석이면 <c>/*c1*/</c>.</summary>
    public static string CommentPlaceholder(int number, bool lineComment) =>
        lineComment
            ? "--c" + number.ToString(CultureInfo.InvariantCulture)
            : "/*c" + number.ToString(CultureInfo.InvariantCulture) + "*/";

    /// <summary>
    /// 주석 토큰 글이 이 사전의 주석 자리표시이면 원래 주석 글. 본문 앞뒤 공백은 봐준다(<c>/* c1 */</c>·<c>-- c1</c>).
    /// 자리표시의 종류(줄·블록)가 원래 주석과 다르면 false 이다: 줄 주석을 블록 자리에 넣으면 같은 줄의 뒤 글이 주석이 된다.
    /// </summary>
    public bool TryGetComment(string commentToken, out string raw)
    {
        ArgumentNullException.ThrowIfNull(commentToken);
        raw = string.Empty;
        var span = commentToken.AsSpan().TrimEnd();
        bool line;
        if (span.StartsWith("--", StringComparison.Ordinal))
        {
            line = true;
            span = span[2..];
        }
        else if (span.Length >= 4 && span.StartsWith("/*", StringComparison.Ordinal) && span.EndsWith("*/", StringComparison.Ordinal))
        {
            line = false;
            span = span[2..^2];
        }
        else
        {
            return false;
        }

        span = span.Trim();
        if (span.Length < 2 || span[0] is not ('c' or 'C') || span[1] == '0'
            || !int.TryParse(span[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        lock (_gate)
        {
            if (number < 1 || number > _comments.Count)
            {
                return false;
            }

            var candidate = _comments[number - 1];
            if (candidate.StartsWith("--", StringComparison.Ordinal) != line)
            {
                return false;
            }

            raw = candidate;
            return true;
        }
    }

    /// <summary>
    /// 가린 글에 나온 주석 자리표시(<see cref="AddComment"/> 가 만든 모양 그대로)를 나온 차례대로 돌려준다. 같은 자리표시가 여러 번 나오면 나온 만큼 담는다.
    /// 가린 글에는 문자열이 자리표시로 바뀌어 있어 문자열 안의 같은 글자를 잘못 세지 않는다.
    /// </summary>
    public static IReadOnlyList<string> FindCommentPlaceholders(string maskedText)
    {
        ArgumentNullException.ThrowIfNull(maskedText);
        return [.. CommentPlaceholderShape().Matches(maskedText).Select(m => m.Value)];
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
    /// 기억한 주석은 토큰 전체(<c>-- …</c>·<c>/* … */</c>)와 3자 이상인 본문을 주석 자리표시로 바꾼다(되돌린 SQL 을 실행한 오류 글에 주석 본문이 실려 올 수 있다).
    /// </summary>
    public string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return text;
        }

        List<KeyValuePair<string, int>> entries;
        List<KeyValuePair<string, int>> comments;
        lock (_gate)
        {
            entries = [.. _numbers];
            comments = [.. _commentNumbers];
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
                foreach (var inner in InnerTexts(raw))
                {
                    AddTarget(inner, placeholder, Regex.Escape(inner));
                }
            }
            else if (raw.Length >= MinRedactedNumberLength && IsNumeric(raw))
            {
                // ORA-01234 같은 오류 번호는 값으로 보지 않는다(번호가 망가지면 오히려 값을 짐작하게 한다).
                AddTarget(raw, placeholder, @"(?<![\w.])(?<!ORA-)" + Regex.Escape(raw) + @"(?![\w.])");
            }
        }

        foreach (var (raw, number) in comments)
        {
            // 빈 주석(--·/**/)은 정상 글(구분선 등)과 구분되지 않고 숨길 본문도 없다.
            // 토큰 전체와, 짧지 않은(3자 이상) 본문도 바꾼다(오류 글이 주석 기호 없이 본문만 실을 수 있다).
            var line = raw.StartsWith("--", StringComparison.Ordinal);
            var body = (line ? raw[2..] : raw[2..^2]).Trim();
            if (body.Length >= MinRedactedInnerLength)
            {
                var placeholder = CommentPlaceholder(number, line);
                AddTarget(raw, placeholder, Regex.Escape(raw));
                AddTarget(body, placeholder, Regex.Escape(body));
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

    /// <summary>접두 + 숫자만인 모양인가(앞의 <c>:</c> 는 있어도 없어도 된다). 숫자가 이 사전이 만든 값인지는 보지 않는다.</summary>
    private bool HasOwnShape(string bindText, out ReadOnlySpan<char> digits)
    {
        digits = default;
        var span = bindText.AsSpan();
        if (span.Length > 0 && span[0] == ':')
        {
            span = span[1..];
        }

        if (span.Length <= Prefix.Length || !span.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = span[Prefix.Length..];
        foreach (var c in rest)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        digits = rest;
        return true;
    }

    private bool TryParseNumber(string bindText, out int number)
    {
        number = 0;
        if (!HasOwnShape(bindText, out var digits))
        {
            return false;
        }

        // 선행 0(:v01)과 너무 큰 수는 이 사전이 만든 값이 아니다. 같은 값으로 읽히게 두지 않는다.
        if (digits.Length > 1 && digits[0] == '0')
        {
            return false;
        }

        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    /// <summary>문자열 토큰 <paramref name="raw"/> 의 안쪽 글 후보: 따옴표 사이, <c>q'[…]'</c> 의 구분자 안, 겹따옴표를 푼 글. 3자 미만은 뺀다.</summary>
    private static List<string> InnerTexts(string raw)
    {
        var result = new List<string>();
        var first = raw.IndexOf('\'', StringComparison.Ordinal);
        var last = raw.LastIndexOf('\'');
        if (last - first - 1 < MinRedactedInnerLength)
        {
            return result;
        }

        var inner = raw.Substring(first + 1, last - first - 1);
        result.Add(inner);

        if (first > 0 && raw[first - 1] is 'q' or 'Q')
        {
            // q'[abc]' / nq'<abc>': 여는 구분자와 닫는 구분자를 뺀 글
            if (inner.Length - 2 >= MinRedactedInnerLength)
            {
                result.Add(inner[1..^1]);
            }
        }
        else if (inner.Contains("''", StringComparison.Ordinal))
        {
            var unescaped = inner.Replace("''", "'", StringComparison.Ordinal);
            if (unescaped.Length >= MinRedactedInnerLength)
            {
                result.Add(unescaped);
            }
        }

        return result;
    }

    private static bool IsNumeric(string raw)
    {
        foreach (var c in raw)
        {
            if (!(char.IsAsciiDigit(c) || c is '.' or '+' or '-' or 'e' or 'E' or 'f' or 'F' or 'd' or 'D'))
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixShape();

    [GeneratedRegex(@"/\*c[1-9][0-9]*\*/|--c[1-9][0-9]*(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex CommentPlaceholderShape();
}
