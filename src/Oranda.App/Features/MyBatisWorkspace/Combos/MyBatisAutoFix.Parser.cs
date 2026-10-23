using Oranda.Core.Text;

namespace Oranda.App.Features.MyBatisWorkspace.Combos;

// MyBatisAutoFix 의 일부: 최상위 자식 노드를 나누는 xml 훑기(머리말 건너뛰기·태그 읽기)(MyBatisAutoFix.cs 에서 나눔, 동작 불변).
internal static partial class MyBatisAutoFix
{
    /// <summary>
    /// 루트 요소의 최상위 자식 노드를 순서대로 돌려준다. 태그 이름이 맞지 않거나 닫히지 않는 등 깨진 xml 이면 null.
    /// </summary>
    private static List<Node>? ParseTopLevel(string s)
    {
        var p = SkipProlog(s);
        if (p < 0 || p >= s.Length || s[p] != '<')
        {
            return null;
        }

        if (!TryScanTag(s, p, out var rootEnd, out var rootSelfClosing, out var rootName))
        {
            return null;
        }

        var children = new List<Node>();
        if (rootSelfClosing)
        {
            return children;
        }

        var open = new Stack<(string Name, int Start)>();
        p = rootEnd;
        while (p < s.Length)
        {
            if (s[p] != '<')
            {
                var next = s.IndexOf('<', p);
                if (next < 0)
                {
                    return null;
                }

                if (open.Count == 0)
                {
                    children.Add(new Node(NodeKind.Text, p, next, string.Empty));
                }

                p = next;
            }
            else if (At(s, p, "<!--"))
            {
                var close = s.IndexOf("-->", p + 4, StringComparison.Ordinal);
                if (close < 0)
                {
                    return null;
                }

                if (open.Count == 0)
                {
                    children.Add(new Node(NodeKind.Comment, p, close + 3, string.Empty));
                }

                p = close + 3;
            }
            else if (At(s, p, "<![CDATA["))
            {
                var close = s.IndexOf("]]>", p + 9, StringComparison.Ordinal);
                if (close < 0)
                {
                    return null;
                }

                if (open.Count == 0)
                {
                    children.Add(new Node(NodeKind.CData, p, close + 3, string.Empty));
                }

                p = close + 3;
            }
            else if (At(s, p, "<?"))
            {
                var close = s.IndexOf("?>", p + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    return null;
                }

                p = close + 2;
            }
            else if (At(s, p, "</"))
            {
                var close = s.IndexOf('>', p + 2);
                if (close < 0)
                {
                    return null;
                }

                var name = s.AsSpan(p + 2, close - p - 2).Trim().ToString();
                if (open.Count == 0)
                {
                    return name == rootName ? children : null;
                }

                var top = open.Pop();
                if (top.Name != name)
                {
                    return null;
                }

                if (open.Count == 0)
                {
                    children.Add(new Node(NodeKind.Element, top.Start, close + 1, top.Name));
                }

                p = close + 1;
            }
            else
            {
                if (!TryScanTag(s, p, out var tagEnd, out var selfClosing, out var tagName))
                {
                    return null;
                }

                if (selfClosing)
                {
                    if (open.Count == 0)
                    {
                        children.Add(new Node(NodeKind.Element, p, tagEnd, tagName));
                    }
                }
                else
                {
                    open.Push((tagName, p));
                }

                p = tagEnd;
            }
        }

        return null;
    }

    /// <summary>루트 앞의 공백·xml 선언·주석·DOCTYPE 를 건너뛴 위치. 깨졌으면 -1.</summary>
    private static int SkipProlog(string s)
    {
        var p = 0;
        while (p < s.Length)
        {
            if (char.IsWhiteSpace(s[p]) || s[p] == '﻿')
            {
                p++;
            }
            else if (At(s, p, "<!--"))
            {
                var close = s.IndexOf("-->", p + 4, StringComparison.Ordinal);
                if (close < 0)
                {
                    return -1;
                }

                p = close + 3;
            }
            else if (At(s, p, "<?"))
            {
                var close = s.IndexOf("?>", p + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    return -1;
                }

                p = close + 2;
            }
            else if (At(s, p, "<!"))
            {
                var close = s.IndexOf('>', p + 2);
                if (close < 0)
                {
                    return -1;
                }

                p = close + 1;
            }
            else
            {
                break;
            }
        }

        return p;
    }

    /// <summary>
    /// 여는 태그 하나를 훑는다. 속성값은 따옴표 안의 <c>&gt;</c>·<c>&lt;</c> 를 태그 경계로 보지 않는다.
    /// </summary>
    private static bool TryScanTag(string s, int start, out int end, out bool selfClosing, out string name)
    {
        end = 0;
        selfClosing = false;
        name = string.Empty;

        var p = start + 1;
        var nameStart = p;
        while (p < s.Length && !char.IsWhiteSpace(s[p]) && s[p] != '>' && s[p] != '/')
        {
            p++;
        }

        if (p == nameStart || p >= s.Length)
        {
            return false;
        }

        name = s[nameStart..p];
        while (p < s.Length)
        {
            var c = s[p];
            if (c == '"' || c == '\'')
            {
                var close = s.IndexOf(c, p + 1);
                if (close < 0)
                {
                    return false;
                }

                p = close + 1;
            }
            else if (c == '>')
            {
                selfClosing = s[p - 1] == '/';
                end = p + 1;
                return true;
            }
            else
            {
                p++;
            }
        }

        return false;
    }

    private static bool At(string s, int position, string literal) =>
        string.CompareOrdinal(s, position, literal, 0, literal.Length) == 0;
}
