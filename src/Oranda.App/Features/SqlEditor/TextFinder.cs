using System.Text.RegularExpressions;

namespace Oranda.App.Features.SqlEditor;

/// <summary>찾기 옵션(EDIT-07).</summary>
public sealed record FindOptions
{
    public string Pattern { get; init; } = string.Empty;

    public bool UseRegex { get; init; }

    public bool MatchCase { get; init; }

    public bool WholeWord { get; init; }
}

/// <summary>찾기·바꾸기 엔진. 정규식 오류는 예외 대신 <see cref="Error"/> 로 알린다. UI 와 무관.</summary>
public sealed class TextFinder
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private readonly Regex? _regex;

    public TextFinder(FindOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        if (options.Pattern.Length == 0)
        {
            return;
        }

        try
        {
            var body = options.UseRegex ? options.Pattern : Regex.Escape(options.Pattern);
            if (options.WholeWord)
            {
                body = $@"(?<![\w$#]){body}(?![\w$#])";
            }

            var flags = RegexOptions.CultureInvariant | RegexOptions.Multiline;
            if (!options.MatchCase)
            {
                flags |= RegexOptions.IgnoreCase;
            }

            _regex = new Regex(body, flags, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
        }
    }

    public FindOptions Options { get; }

    /// <summary>패턴이 잘못되었으면 이유. 정상이면 null.</summary>
    public string? Error { get; }

    public bool IsReady => _regex is not null;

    /// <summary><paramref name="from"/> 이후(포함) 첫 일치. 끝까지 없으면 처음부터 한 바퀴(<paramref name="wrap"/>).</summary>
    public (int Start, int Length)? FindNext(string text, int from, bool wrap = true)
    {
        if (_regex is null)
        {
            return null;
        }

        from = Math.Clamp(from, 0, text.Length);
        var m = Safe(() => _regex.Match(text, from));
        if (m is { Success: true })
        {
            return (m.Index, m.Length);
        }

        if (wrap && from > 0)
        {
            m = Safe(() => _regex.Match(text, 0));
            if (m is { Success: true })
            {
                return (m.Index, m.Length);
            }
        }

        return null;
    }

    /// <summary><paramref name="before"/> 보다 앞에서 시작하는 마지막 일치.</summary>
    public (int Start, int Length)? FindPrevious(string text, int before, bool wrap = true)
    {
        if (_regex is null)
        {
            return null;
        }

        (int, int)? last = null, lastAny = null;
        var matches = Safe(() => _regex.Matches(text));
        if (matches is null)
        {
            return null;
        }

        foreach (Match m in matches)
        {
            if (m.Length == 0 && m.Index >= text.Length)
            {
                continue;
            }

            lastAny = (m.Index, m.Length);
            if (m.Index < before)
            {
                last = (m.Index, m.Length);
            }
        }

        return last ?? (wrap ? lastAny : null);
    }

    public int Count(string text)
    {
        if (_regex is null)
        {
            return 0;
        }

        return Safe(() => _regex.Count(text));
    }

    /// <summary>모두 바꾼 새 글과 바꾼 횟수. 정규식이면 <c>$1</c> 같은 그룹 참조를 쓴다.</summary>
    public (string Text, int Count) ReplaceAll(string text, string replacement)
    {
        if (_regex is null)
        {
            return (text, 0);
        }

        int n = 0;
        var result = Safe(() => _regex.Replace(text, m =>
        {
            n++;
            return Options.UseRegex ? m.Result(replacement) : replacement;
        }));
        return result is null ? (text, 0) : (result, n);
    }

    /// <summary>선택이 지금 일치하면 그 선택의 치환 결과, 아니면 null.</summary>
    public string? ReplacementFor(string text, int start, int length, string replacement)
    {
        if (_regex is null)
        {
            return null;
        }

        var m = Safe(() => _regex.Match(text, start));
        if (m is not { Success: true } || m.Index != start || m.Length != length)
        {
            return null;
        }

        return Options.UseRegex ? m.Result(replacement) : replacement;
    }

    private static T? Safe<T>(Func<T> f)
    {
        try
        {
            return f();
        }
        catch (RegexMatchTimeoutException)
        {
            return default;
        }
    }
}
