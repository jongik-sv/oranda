using System.Text;

namespace Oranda.Tibero.Schema;

/// <summary>
/// Tibero 트리거 생성 DDL 조립기(세션 없는 정적 부품). Tibero 의 <c>DBMS_METADATA.GET_DDL('TRIGGER')</c> 는 동작하지 않아
/// (<c>ODP-14001</c>, docs/design/tibero-catalog-map.md) <c>*_SOURCE</c>(TYPE = 'TRIGGER') 글로 만든다. T3 DDL 추출과 T5 표 다시 만들기(TB4)가 같이 쓴다.
/// <list type="bullet">
/// <item>다른 길은 없다(6.2.15 실측): <c>*_TRIGGERS</c> 에는 Oracle 의 DESCRIPTION·TRIGGER_BODY 열이 없다
/// (열 = OWNER, TRIGGER_NAME, TRIGGER_TYPE, TRIGGERING_EVENT, TABLE_OWNER, TABLE_NAME, REFERENCING_NAMES, WHEN_CLAUSE, STATUS).</item>
/// <item>결과는 <c>CREATE OR REPLACE TRIGGER "OWNER"."NAME" …</c> 생성 문장 하나다. 끝의 <c>/</c>·상태 문장은 붙이지 않는다 —
/// Oracle 다시 만들기도 생성 문장만 남기고 사용 여부는 따로 다룬다(<see cref="DisableStatement"/>).</item>
/// <item>트리거 이름 자리는 늘 <paramref name="owner"/>·<paramref name="name"/> 으로 바꿔 쓴다(다른 스키마로 다시 만들 때 소유자를 바꿀 수 있게).
/// 대상 표 이름(<c>ON …</c>)은 원문 그대로 둔다.</item>
/// </list>
/// </summary>
internal static class TiberoTriggerDdl
{
    /// <summary>
    /// <c>*_SOURCE</c>(TYPE = 'TRIGGER') 줄 → 생성 DDL. 원문 첫머리는 <c>TRIGGER 이름 …</c> 이다(Oracle 과 같은 모양). 앞에
    /// <c>CREATE [OR REPLACE] [EDITIONABLE|NONEDITIONABLE]</c> 가 붙어 와도 받는다. 줄이 없거나 <c>TRIGGER</c> 로 시작하지 않으면 null.
    /// </summary>
    internal static string? FromSource(string owner, string name, IReadOnlyList<string?> lines)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(lines);
        var text = Join(lines);
        var i = SkipSpace(text, 0);
        foreach (var word in (ReadOnlySpan<string>)["CREATE", "OR", "REPLACE", "EDITIONABLE", "NONEDITIONABLE"])
        {
            if (TryWord(text, i, word, out var next))
            {
                i = SkipSpace(text, next);
            }
        }

        if (!TryWord(text, i, "TRIGGER", out var afterKeyword))
        {
            return null;
        }

        var rest = ReplaceLeadingName(text, SkipSpace(text, afterKeyword), owner, name);
        return rest is null ? null : Finish(rest);
    }

    /// <summary>사용 안 함 상태로 되돌리는 문장(<c>ALTER TRIGGER "OWNER"."NAME" DISABLE</c>).</summary>
    internal static string DisableStatement(string owner, string name) => $"ALTER TRIGGER {Qualified(owner, name)} DISABLE";

    /// <summary>따옴표로 감싼 <c>"OWNER"."NAME"</c>(안의 따옴표는 두 번).</summary>
    internal static string Qualified(string owner, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentException.ThrowIfNullOrEmpty(name);
        return Quote(owner) + "." + Quote(name);
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>줄을 잇는다. 줄 끝 줄바꿈이 없는 줄에는 붙인다(사전 뷰마다 다르다).</summary>
    private static string Join(IReadOnlyList<string?> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            if (line is null)
            {
                continue;
            }

            text.Append(line);
            if (!line.EndsWith('\n'))
            {
                text.Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary><paramref name="start"/> 의 (스키마로 한정됐을 수 있는) 이름을 <c>"OWNER"."NAME"</c> 로 바꾼 <c>TRIGGER …</c> 앞머리 없는 나머지 글.</summary>
    private static string? ReplaceLeadingName(string text, int start, string owner, string name)
    {
        var end = SkipIdentifierPath(text, start);
        return end == start ? null : "CREATE OR REPLACE TRIGGER " + Qualified(owner, name) + text[end..];
    }

    /// <summary><c>a</c>, <c>"a"</c>, <c>a.b</c>, <c>"a"."b"</c> 모양의 이름 끝 위치(이름이 없으면 <paramref name="start"/>).</summary>
    private static int SkipIdentifierPath(string text, int start)
    {
        var i = start;
        while (true)
        {
            var partEnd = SkipIdentifier(text, i);
            if (partEnd == i)
            {
                return i == start ? start : i - 1; // 점 뒤에 이름이 없으면 점 앞까지
            }

            i = partEnd;
            if (i < text.Length && text[i] == '.')
            {
                i++;
                continue;
            }

            return i;
        }
    }

    private static int SkipIdentifier(string text, int i)
    {
        if (i < text.Length && text[i] == '"')
        {
            var j = i + 1;
            while (j < text.Length)
            {
                if (text[j] == '"')
                {
                    if (j + 1 < text.Length && text[j + 1] == '"')
                    {
                        j += 2;
                        continue;
                    }

                    return j + 1;
                }

                j++;
            }

            return i; // 닫히지 않은 따옴표
        }

        var k = i;
        while (k < text.Length && (char.IsLetterOrDigit(text[k]) || text[k] is '_' or '$' or '#'))
        {
            k++;
        }

        return k;
    }

    private static int SkipSpace(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        return i;
    }

    /// <summary><paramref name="i"/> 에 낱말 <paramref name="word"/>(대소문자 무시, 뒤가 이름 글자가 아님)가 있으면 그 끝.</summary>
    private static bool TryWord(string text, int i, string word, out int end)
    {
        end = i + word.Length;
        return end <= text.Length
            && string.Compare(text, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0
            && (end == text.Length || !(char.IsLetterOrDigit(text[end]) || text[end] is '_' or '$' or '#' or '"'));
    }

    /// <summary>끝 공백·SQL*Plus 끝 줄(<c>/</c>)을 지운다.</summary>
    private static string Finish(string sql)
    {
        var trimmed = sql.TrimEnd();
        if (trimmed.EndsWith("\n/", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^2].TrimEnd();
        }

        return trimmed;
    }
}
