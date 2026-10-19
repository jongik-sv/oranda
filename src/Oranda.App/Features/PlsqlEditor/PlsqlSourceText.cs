using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Oranda.App.Features.PlsqlEditor;

/// <summary>소스 글자 다루기(머리 읽기·줄끝 정리·차이). 화면·서버 없이 시험할 수 있다. 소유: TSK-08-01.</summary>
public static class PlsqlSourceText
{
    /// <summary>편집기에 보이는 소스 앞에 붙이는 말. 서버 소스 첫 줄(<c>PACKAGE BODY x AS</c>)과 같은 줄에 붙여 줄 번호가 서버 오류와 맞는다.</summary>
    public const string CreatePrefix = "CREATE OR REPLACE ";

    private static readonly Regex Header = new(
        @"^\s*(?:CREATE\s+(?:OR\s+REPLACE\s+)?(?:(?:NON)?EDITIONABLE\s+)?)?(?<type>PACKAGE\s+BODY|PACKAGE|PROCEDURE|FUNCTION|TRIGGER|TYPE\s+BODY|TYPE)\s+(?<a>""[^""]+""|[\w$#]+)(?:\s*\.\s*(?<b>""[^""]+""|[\w$#]+))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>소스를 가진 객체 유형(Oracle OBJECT_TYPE).</summary>
    public static IReadOnlySet<string> SourceTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "PACKAGE", "PACKAGE BODY", "PROCEDURE", "FUNCTION", "TRIGGER", "TYPE", "TYPE BODY",
    };

    /// <summary>명세/본문 쌍이 있는 유형의 반대쪽. 없으면 null.</summary>
    public static string? SiblingType(string objectType) => objectType switch
    {
        "PACKAGE" => "PACKAGE BODY",
        "PACKAGE BODY" => "PACKAGE",
        "TYPE" => "TYPE BODY",
        "TYPE BODY" => "TYPE",
        _ => null,
    };

    private static readonly Regex StoredHeader = new(
        @"^(?<type>PACKAGE\s+BODY|PACKAGE|PROCEDURE|FUNCTION|TRIGGER|TYPE\s+BODY|TYPE)(?<ws>\s+)(?<name>""[^""]+""|(?>[\w$#]+))(?!\s*\.)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 서버 소스에 <c>CREATE OR REPLACE</c> 를 붙인 편집기 글자. 줄끝은 그대로(LF).
    /// 서버는 소스를 저장할 때 소유자 이름(<c>SCOTT.</c>)을 공백으로 바꿔 두므로, 다른 스키마로 컴파일되지 않게 첫 줄에 소유자를 다시 적는다.
    /// <paramref name="line1Shift"/> = 그 때문에 첫 줄의 이름 뒤 글자가 오른쪽으로 밀린 칸 수(서버 오류의 1줄 열 보정용, 앞에 붙인 말 제외).
    /// </summary>
    public static string ToEditorText(string serverSource, string? owner, out int line1Shift)
    {
        var text = serverSource.TrimStart();
        line1Shift = 0;
        if (!string.IsNullOrEmpty(owner) && StoredHeader.Match(text) is { Success: true } m)
        {
            var ws = m.Groups["ws"];
            var qualifier = " \"" + owner.Replace("\"", "\"\"", StringComparison.Ordinal) + "\".";
            line1Shift = qualifier.Length - ws.Length;
            text = string.Concat(text.AsSpan(0, ws.Index), qualifier, text.AsSpan(ws.Index + ws.Length));
        }

        return CreatePrefix + text;
    }

    public static string ToEditorText(string serverSource) => ToEditorText(serverSource, null, out _);

    /// <summary>컴파일에 보낼 글자: 줄끝 LF, 끝의 빈 줄·<c>/</c> 단독 줄 제거. 끝의 <c>;</c> 는 남긴다(PL/SQL 단위가 필요로 한다).</summary>
    public static string ToCompileText(string editorText)
    {
        var text = Normalize(editorText).TrimEnd();
        var lines = text.Split('\n').ToList();
        while (lines.Count > 0 && lines[^1].Trim() is "" or "/")
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join('\n', lines);
    }

    /// <summary>비교용 정규화: 줄끝 LF, 줄 끝 공백·문서 끝 빈 줄 제거, <c>CREATE OR REPLACE</c> 머리 제거.</summary>
    public static string ForCompare(string text)
    {
        var lines = Normalize(text).Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var joined = string.Join('\n', lines);
        var m = Header.Match(joined);
        if (m.Success && joined.TrimStart().StartsWith("CREATE", StringComparison.OrdinalIgnoreCase))
        {
            var typeStart = m.Groups["type"].Index;
            joined = joined[typeStart..];
        }

        return joined;
    }

    public static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>소스 머리에서 유형·이름을 읽는다. 읽을 수 없으면 null. 따옴표 없는 이름은 대문자로.</summary>
    public static (string ObjectType, string? Owner, string Name)? ParseHeader(string text)
    {
        var m = Header.Match(StripLeadingComments(text));
        if (!m.Success)
        {
            return null;
        }

        var type = Regex.Replace(m.Groups["type"].Value.ToUpperInvariant(), @"\s+", " ");
        var a = Unquote(m.Groups["a"].Value);
        return m.Groups["b"].Success ? (type, a, Unquote(m.Groups["b"].Value)) : (type, null, a);
    }

    /// <summary>
    /// 서버가 소스로 저장하는 시작 위치(유형 낱말 <c>PACKAGE BODY</c> 등). 서버 오류의 줄·열은 여기서부터 센다.
    /// 반환 = (앞선 줄 수, 그 줄 안에서 앞선 글자 수). 읽을 수 없으면 (0, 0).
    /// </summary>
    public static (int LineOffset, int ColumnOffset) SourceStart(string text)
    {
        var normalized = Normalize(text);
        var stripped = StripLeadingComments(normalized);
        var m = Header.Match(stripped);
        if (!m.Success)
        {
            return (0, 0);
        }

        var offset = normalized.Length - stripped.Length + m.Groups["type"].Index;
        var before = normalized[..offset];
        var line = before.Count(c => c == '\n');
        var col = offset - (before.LastIndexOf('\n') + 1);
        return (line, col);
    }

    private static string Unquote(string id) => id.StartsWith('"') ? id.Trim('"') : id.ToUpperInvariant();

    private static string StripLeadingComments(string text)
    {
        var s = text.AsSpan().TrimStart();
        while (true)
        {
            if (s.StartsWith("--"))
            {
                var nl = s.IndexOf('\n');
                s = nl < 0 ? [] : s[(nl + 1)..].TrimStart();
            }
            else if (s.StartsWith("/*"))
            {
                var end = s.IndexOf("*/");
                s = end < 0 ? [] : s[(end + 2)..].TrimStart();
            }
            else
            {
                return s.ToString();
            }
        }
    }

    /// <summary>줄 단위 차이(LCS). 내 쪽에만 있는 줄 <c>- </c>, 서버에만 있는 줄 <c>+ </c>, 같은 줄 <c>  </c>. 같으면 빈 문자열.</summary>
    public static string BuildDiff(string local, string server)
    {
        var a = ForCompare(local).Split('\n');
        var b = ForCompare(server).Split('\n');
        if (a.SequenceEqual(b))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var pre = 0;
        while (pre < a.Length && pre < b.Length && a[pre] == b[pre])
        {
            pre++;
        }

        var suf = 0;
        while (suf < a.Length - pre && suf < b.Length - pre && a[a.Length - 1 - suf] == b[b.Length - 1 - suf])
        {
            suf++;
        }

        for (var k = 0; k < pre; k++)
        {
            sb.Append("  ").Append(a[k]).Append('\n');
        }

        var am = a[pre..(a.Length - suf)];
        var bm = b[pre..(b.Length - suf)];
        if ((long)am.Length * bm.Length > 4_000_000)
        {
            // 너무 크면 가운데를 통째로 바뀐 것으로 본다.
            foreach (var l in am)
            {
                sb.Append("- ").Append(l).Append('\n');
            }

            foreach (var l in bm)
            {
                sb.Append("+ ").Append(l).Append('\n');
            }
        }
        else
        {
            var lcs = new int[am.Length + 1, bm.Length + 1];
            for (var i = am.Length - 1; i >= 0; i--)
            {
                for (var j = bm.Length - 1; j >= 0; j--)
                {
                    lcs[i, j] = am[i] == bm[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }

            int x = 0, y = 0;
            while (x < am.Length || y < bm.Length)
            {
                if (x < am.Length && y < bm.Length && am[x] == bm[y])
                {
                    sb.Append("  ").Append(am[x]).Append('\n');
                    x++;
                    y++;
                }
                else if (y < bm.Length && (x == am.Length || lcs[x, y + 1] >= lcs[x + 1, y]))
                {
                    sb.Append("+ ").Append(bm[y]).Append('\n');
                    y++;
                }
                else
                {
                    sb.Append("- ").Append(am[x]).Append('\n');
                    x++;
                }
            }
        }

        for (var k = a.Length - suf; k < a.Length; k++)
        {
            sb.Append("  ").Append(a[k]).Append('\n');
        }

        return sb.ToString();
    }
}
