using System.Globalization;
using System.Text;

namespace Oranda.Core.Metadata;

/// <summary>
/// 소스 본문 검색(<c>dbinfo.source_search</c>)에 묶을 바인드 값을 만든다. 서버·화면 없이 시험한다.
/// Oracle 정규식(POSIX ERE)에는 <c>\b</c> 가 없어서, 단어 단위는 앞뒤에 「줄 처음·끝 또는 식별자 글자가 아닌 글자」를 붙여 흉내 낸다.
/// 일반 글자 검색도 이스케이프한 정규식으로 맞추고, 대소문자 무시 <c>INSTR</c> 로 먼저 걸러 정규식 평가를 줄인다.
/// </summary>
public static class SourceSearchPattern
{
    /// <summary>Oracle 정규식 패턴 최대 길이(바이트, ORA-12733).</summary>
    public const int MaxPatternBytes = 512;

    /// <summary>PL/SQL 식별자에 들어가지 않는 글자 하나.</summary>
    public const string NonWord = "[^[:alnum:]_$#]";

    /// <summary>Oracle 정규식에서 뜻이 있는 글자(앞에 <c>\</c> 를 붙여 글자 그대로 맞춘다). <c>]</c>·<c>}</c> 는 짝 없이 쓰면 글자 그대로라 뺐다.</summary>
    private const string Meta = "\\.^$|?*+()[{";

    /// <summary>
    /// 검색 조건 → 바인드 값. 검색어가 비었거나 패턴이 너무 길면 <see cref="SearchPatternException"/>.
    /// 정규식 문법 자체는 여기서 따지지 않는다(서버가 판정, <see cref="Describe"/>).
    /// </summary>
    public static SourceSearchBinds Build(SourceSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var text = query.Text;
        if (string.IsNullOrEmpty(text))
        {
            throw new SearchPatternException("검색어가 비었다");
        }

        var core = query.UseRegex ? text : Escape(text);
        var pattern = query.WholeWord
            ? $"(^|{NonWord})({(query.UseRegex ? ShiftBackReferences(core, 2) : core)})($|{NonWord})"
            : core;
        if (Encoding.UTF8.GetByteCount(pattern) > MaxPatternBytes)
        {
            throw new SearchPatternException(string.Create(CultureInfo.InvariantCulture,
                $"정규식이 너무 길다 — 단어 단위로 붙는 앞뒤 조건을 합쳐 {MaxPatternBytes}바이트까지 쓸 수 있다"));
        }

        return new SourceSearchBinds(query.UseRegex ? null : text, pattern, query.MatchCase ? "c" : "i");
    }

    /// <summary>글자를 Oracle 정규식에서 글자 그대로 맞도록 이스케이프한다.</summary>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            if (Meta.Contains(ch, StringComparison.Ordinal))
            {
                sb.Append('\\');
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 역참조(<c>\1</c>~<c>\9</c>) 번호를 <paramref name="by"/> 만큼 민다(앞에 괄호 묶음을 더 붙일 때). 다른 <c>\x</c> 는 그대로 둔다.
    /// 밀어서 9 를 넘으면 <see cref="SearchPatternException"/>.
    /// </summary>
    public static string ShiftBackReferences(string pattern, int by)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var sb = new StringBuilder(pattern.Length + 4);
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch != '\\' || i + 1 >= pattern.Length)
            {
                sb.Append(ch);
                continue;
            }

            var next = pattern[++i];
            if (next is >= '1' and <= '9')
            {
                var n = next - '0' + by;
                if (n > 9)
                {
                    throw new SearchPatternException(string.Create(CultureInfo.InvariantCulture,
                        $"단어 단위 검색에서는 역참조를 \\{9 - by} 까지만 쓸 수 있다"));
                }

                sb.Append('\\').Append((char)('0' + n));
            }
            else
            {
                sb.Append('\\').Append(next);
            }
        }

        return sb.ToString();
    }

    /// <summary>서버가 정규식을 거부한 오류(ORA-12722~12733)인지.</summary>
    public static bool IsRegexError(int oracleErrorNumber) => oracleErrorNumber is >= 12722 and <= 12733;

    /// <summary>정규식 오류 번호 → 사람이 읽는 설명(「정규식이 올바르지 않다 — … (ORA-nnnnn)」).</summary>
    public static string Describe(int oracleErrorNumber)
    {
        var what = oracleErrorNumber switch
        {
            12725 => "괄호 ( ) 짝이 맞지 않는다",
            12726 => "대괄호 [ ] 짝이 맞지 않는다",
            12727 => "역참조(\\1 등)가 가리키는 묶음이 없다",
            12728 => "범위(a-z 등)가 잘못됐다",
            12729 => "문자 클래스([:alpha:] 등) 이름이 잘못됐다",
            12730 => "동치 클래스([= =])가 잘못됐다",
            12731 => "정렬 요소([. .])가 잘못됐다",
            12732 => "반복 횟수({m,n})가 잘못됐다",
            12733 => string.Create(CultureInfo.InvariantCulture, $"정규식이 너무 길다({MaxPatternBytes}바이트까지)"),
            _ => "서버가 정규식을 받아들이지 않았다",
        };
        return string.Create(CultureInfo.InvariantCulture, $"정규식이 올바르지 않다 — {what} (ORA-{oracleErrorNumber:D5})");
    }
}

/// <summary>
/// <c>dbinfo.source_search</c> 바인드 값. <see cref="Needle"/> = 대소문자 무시 <c>INSTR</c> 로 먼저 거를 글자(정규식 모드면 null),
/// <see cref="Pattern"/> = <c>REGEXP_LIKE</c> 패턴, <see cref="Flags"/> = <c>c</c>(대소문자 구분) 또는 <c>i</c>.
/// </summary>
public sealed record SourceSearchBinds(string? Needle, string Pattern, string Flags);
