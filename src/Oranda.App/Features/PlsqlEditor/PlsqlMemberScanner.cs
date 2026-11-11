using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Oranda.Language.Lexing;

namespace Oranda.App.Features.PlsqlEditor;

/// <summary>패키지·타입 구성원 종류(「구성원」 목록, PKG1).</summary>
public enum PlsqlMemberKind
{
    Procedure,
    Function,
    Type,
    Cursor,
}

/// <summary>
/// 명세 또는 본문 글에서 찾은 구성원 하나. <see cref="Offset"/> 은 이름 토큰의 글 위치, <see cref="Ordinal"/> 은 같은 종류·이름의 몇 번째(겹쳐 쓰기, 0부터).
/// <see cref="Head"/> 는 프로시저·함수 선언 머리(<c>PROCEDURE p(a NUMBER)</c>, 공백 하나로 줄임). 다른 종류는 빈 글.
/// </summary>
public sealed record PlsqlMember(PlsqlMemberKind Kind, string Name, int Offset, int Ordinal, string Head);

/// <summary>
/// 패키지·타입 명세/본문에서 바깥 단계의 프로시저·함수·타입·커서를 찾는 가벼운 훑개(PKG1 「구성원」 목록). SQL 렉서(<see cref="SqlLexer"/>)의 토큰으로
/// 주석·문자열을 건너뛰고, <c>BEGIN</c>·<c>IF</c>·<c>LOOP</c>·<c>CASE</c> … <c>END</c> 와 서브프로그램 선언부의 깊이를 세어 안쪽(지역) 선언은 빼낸다.
/// 문법 검사기가 아니다: 글이 깨져 있어도 예외 없이 찾은 만큼 돌려준다.
/// </summary>
public static class PlsqlMemberScanner
{
    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "MEMBER", "STATIC", "CONSTRUCTOR", "MAP", "ORDER", "OVERRIDING", "FINAL", "INSTANTIABLE", "NOT",
    };

    private enum Frame
    {
        Unit,
        SubprogramDeclarations,
        Block,
    }

    /// <summary>글의 구성원(나온 차례). 패키지·타입 머리를 찾지 못하면 빈 목록.</summary>
    public static IReadOnlyList<PlsqlMember> Scan(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var toks = SqlLexer.Tokenize(text).Where(t => t.Kind is not (TokenKind.Comment or TokenKind.Hint)).ToList();
        string Word(int i) => i >= 0 && i < toks.Count && toks[i].Kind is TokenKind.Keyword or TokenKind.Identifier
            ? text.Substring(toks[i].Start, toks[i].Length).ToUpperInvariant()
            : i >= 0 && i < toks.Count && toks[i].Kind is TokenKind.Punct or TokenKind.Operator ? text.Substring(toks[i].Start, toks[i].Length) : string.Empty;

        // 머리: [CREATE [OR REPLACE] [EDITIONABLE]] PACKAGE|TYPE [BODY] 이름 … IS|AS
        var k = 0;
        while (k < toks.Count && Word(k) is "CREATE" or "OR" or "REPLACE" or "EDITIONABLE" or "NONEDITIONABLE")
        {
            k++;
        }

        var unit = Word(k);
        if (unit is not ("PACKAGE" or "TYPE"))
        {
            return [];
        }

        var body = Word(k + 1) == "BODY";
        var typeSpec = unit == "TYPE" && !body;
        var start = -1;
        var paren = 0;
        for (var i = k + 1; i < toks.Count; i++)
        {
            var w = Word(i);
            if (w == "(")
            {
                paren++;
            }
            else if (w == ")")
            {
                paren--;
            }
            else if (paren == 0 && w is "IS" or "AS" or "UNDER")
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            return [];
        }

        var found = new List<PlsqlMember>();
        var counts = new Dictionary<(PlsqlMemberKind, string), int>();
        void Add(PlsqlMemberKind kind, int nameIndex, string head)
        {
            if (nameIndex >= toks.Count || toks[nameIndex].Kind is not (TokenKind.Identifier or TokenKind.Keyword or TokenKind.QuotedIdent))
            {
                return;
            }

            var raw = text.Substring(toks[nameIndex].Start, toks[nameIndex].Length);
            var name = toks[nameIndex].Kind == TokenKind.QuotedIdent ? raw.Trim('"') : raw.ToUpperInvariant();
            var key = (kind, name);
            var ordinal = counts.GetValueOrDefault(key);
            counts[key] = ordinal + 1;
            found.Add(new PlsqlMember(kind, name, toks[nameIndex].Start, ordinal, head));
        }

        if (typeSpec)
        {
            // 객체 타입 명세: 구성원은 바깥 괄호 안에 쉼표로 늘어선다(MEMBER FUNCTION f RETURN NUMBER, …).
            paren = 0;
            for (var i = start + 1; i < toks.Count; i++)
            {
                var w = Word(i);
                if (w == "(")
                {
                    paren++;
                }
                else if (w == ")")
                {
                    paren--;
                }
                else if (paren == 1 && w is "PROCEDURE" or "FUNCTION")
                {
                    var end = DeclarationEnd(i + 2, typeMember: true, out _);
                    Add(w == "PROCEDURE" ? PlsqlMemberKind.Procedure : PlsqlMemberKind.Function, i + 1, Head(HeadStart(i), end));
                    i = Math.Max(i, end - 1);
                }
            }

            return found;
        }

        var frames = new Stack<Frame>();
        frames.Push(Frame.Unit);
        paren = 0;
        for (var i = start + 1; i < toks.Count && frames.Count > 0; i++)
        {
            var w = Word(i);
            if (w == "(")
            {
                paren++;
                continue;
            }

            if (w == ")")
            {
                paren = Math.Max(0, paren - 1);
                continue;
            }

            if (paren > 0)
            {
                continue;
            }

            var outer = frames.Count == 1;
            switch (w)
            {
                case "PROCEDURE":
                case "FUNCTION":
                    {
                        var end = DeclarationEnd(i + 2, typeMember: false, out var defines);
                        if (outer)
                        {
                            Add(w == "PROCEDURE" ? PlsqlMemberKind.Procedure : PlsqlMemberKind.Function, i + 1, Head(HeadStart(i), end));
                        }

                        if (defines)
                        {
                            frames.Push(Frame.SubprogramDeclarations);
                        }

                        i = Math.Max(i, end);
                        break;
                    }

                case "CURSOR" when outer:
                    Add(PlsqlMemberKind.Cursor, i + 1, string.Empty);
                    break;
                case "TYPE" or "SUBTYPE" when outer:
                    Add(PlsqlMemberKind.Type, i + 1, string.Empty);
                    break;
                case "BEGIN":
                    if (frames.Peek() == Frame.SubprogramDeclarations)
                    {
                        frames.Pop();
                    }

                    frames.Push(Frame.Block);
                    break;
                case "IF" or "LOOP" or "CASE":
                    frames.Push(Frame.Block);
                    break;
                case "END":
                    frames.Pop();
                    if (Word(i + 1) is "IF" or "LOOP" or "CASE")
                    {
                        i++;
                    }

                    break;
            }
        }

        return found;

        // 선언 머리의 끝(토큰 번호): 바깥 괄호 단계의 「;」(선언만) 또는 IS/AS(정의가 이어진다). 타입 명세 구성원은 「,」·닫는 괄호에서도 끝난다.
        int DeclarationEnd(int from, bool typeMember, out bool defines)
        {
            defines = false;
            var depth = 0;
            for (var j = from; j < toks.Count; j++)
            {
                var w = Word(j);
                if (w == "(")
                {
                    depth++;
                }
                else if (w == ")")
                {
                    if (depth == 0 && typeMember)
                    {
                        return j;
                    }

                    depth--;
                }
                else if (depth == 0 && (w == ";" || (typeMember && w == ",")))
                {
                    return j;
                }
                else if (depth == 0 && !typeMember && w is "IS" or "AS" && Word(j + 1) != "RESULT")
                {
                    defines = true;
                    return j;
                }
            }

            return toks.Count;
        }

        // 앞의 수식어(MEMBER·STATIC·CONSTRUCTOR …)까지 머리에 넣는다.
        int HeadStart(int keyword)
        {
            var j = keyword;
            while (j - 1 > start && Modifiers.Contains(Word(j - 1)))
            {
                j--;
            }

            return j;
        }

        // 토큰을 이어 붙인다(사이에 공백·주석이 있었으면 공백 하나): 줄 주석이 뒤 글을 먹지 않게.
        string Head(int from, int end)
        {
            var sb = new StringBuilder();
            for (var j = from; j < Math.Min(end, toks.Count); j++)
            {
                if (j > from && toks[j].Start > toks[j - 1].End)
                {
                    sb.Append(' ');
                }

                sb.Append(text, toks[j].Start, toks[j].Length);
            }

            return sb.ToString();
        }
    }

    /// <summary>명세의 프로시저·함수 선언(「본문 만들기」 뼈대가 쓴다).</summary>
    public static IEnumerable<PlsqlMember> Subprograms(string specText)
        => Scan(specText).Where(m => m.Kind is PlsqlMemberKind.Procedure or PlsqlMemberKind.Function && m.Head.Length > 0);
}
