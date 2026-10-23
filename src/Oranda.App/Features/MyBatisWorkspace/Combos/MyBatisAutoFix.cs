using Oranda.Core.Text;

namespace Oranda.App.Features.MyBatisWorkspace.Combos;

/// <summary>자동 고침의 종류. <c>SET</c> 키워드를 <c>&lt;set&gt;</c> 으로, <c>WHERE</c> 키워드를 <c>&lt;where&gt;</c> 로 감싼다.</summary>
internal enum AutoFixKind
{
    WrapSet,
    WrapWhere,
}

/// <summary>고침 제안 하나.</summary>
/// <param name="Kind">고침 종류.</param>
/// <param name="Range">문장 원문 안에서 바꿀 범위(키워드 시작부터 마지막 감싼 블록의 닫는 태그 끝까지).</param>
/// <param name="Replacement">그 범위에 넣을 글.</param>
/// <param name="FirstLine">키워드가 있는 줄(1부터).</param>
/// <param name="LastLine">마지막 감싼 블록의 닫는 태그가 있는 줄(1부터).</param>
internal sealed record AutoFixProposal(AutoFixKind Kind, TextRange Range, string Replacement, int FirstLine, int LastLine);

/// <summary>
/// MyBatis 문장 「자동 고침」 엔진(design.md 11장). 문장 요소 원문에서 최상위 <c>SET</c>·<c>WHERE</c> 키워드 바로 뒤에
/// <c>&lt;if&gt;</c>·<c>&lt;choose&gt;</c> 블록이 이어지는 자리를 찾아 <c>&lt;set&gt;</c>·<c>&lt;where&gt;</c> 로 감싸는 제안을 만든다.
/// 순수 문자열 처리이며, 깨진 xml 은 예외 없이 빈 목록을 돌려준다.
/// </summary>
internal static partial class MyBatisAutoFix
{
    private enum NodeKind
    {
        Text,
        Comment,
        CData,
        Element,
    }

    private readonly record struct Node(NodeKind Kind, int Start, int End, string Name);

    private readonly record struct LastWord(int Start, int End);

    /// <summary>문장 원문에서 고침 제안을 원문 앞 순서로 찾는다. 제안이 없거나 xml 이 깨졌으면 빈 목록.</summary>
    public static IReadOnlyList<AutoFixProposal> Find(string statementText)
    {
        ArgumentNullException.ThrowIfNull(statementText);

        var nodes = ParseTopLevel(statementText);
        if (nodes is null)
        {
            return [];
        }

        var eol = DetectEol(statementText);
        var proposals = new List<AutoFixProposal>();
        var i = 0;
        while (i < nodes.Count)
        {
            var j = i;
            while (j < nodes.Count && nodes[j].Kind != NodeKind.Element)
            {
                j++;
            }

            if (j >= nodes.Count)
            {
                break;
            }

            var next = j + 1;
            if (j > i && IsBlock(nodes[j]))
            {
                var lastBlock = FindLastBlock(statementText, nodes, j);
                var proposal = TryPropose(statementText, nodes, i, j, lastBlock, eol);
                if (proposal is not null)
                {
                    proposals.Add(proposal);
                    next = lastBlock + 1;
                }
            }

            i = next;
        }

        return proposals;
    }

    /// <summary>제안 하나를 적용한 새 원문을 돌려준다. 다른 제안을 쓰려면 결과에 <see cref="Find"/> 를 다시 부른다.</summary>
    public static string Apply(string statementText, AutoFixProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(statementText);
        ArgumentNullException.ThrowIfNull(proposal);

        var range = proposal.Range;
        if (range.Start < 0 || range.Length < 0 || range.End > statementText.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(proposal), "고침 범위가 원문을 벗어났다.");
        }

        return string.Concat(statementText.AsSpan(0, range.Start), proposal.Replacement, statementText.AsSpan(range.End));
    }

    private static bool IsBlock(Node node) =>
        node.Kind == NodeKind.Element && (node.Name == "if" || node.Name == "choose");

    /// <summary>첫 블록에서 시작해 공백·XML 주석만 사이에 둔 if·choose 묶음의 마지막 노드 번호.</summary>
    private static int FindLastBlock(string text, List<Node> nodes, int firstBlock)
    {
        var last = firstBlock;
        for (var n = firstBlock + 1; n < nodes.Count; n++)
        {
            var node = nodes[n];
            if (IsBlock(node))
            {
                last = n;
            }
            else if (node.Kind != NodeKind.Comment)
            {
                // 공백만 있는 텍스트는 건너뛰고, 그 밖의 노드에서 묶음이 끝난다.
                if (node.Kind != NodeKind.Text || !IsBlank(text, node))
                {
                    break;
                }
            }
        }

        return last;
    }

    private static AutoFixProposal? TryPropose(string text, List<Node> nodes, int runStart, int firstBlock, int lastBlock, string eol)
    {
        var run = nodes.GetRange(runStart, firstBlock - runStart);
        if (run.Exists(n => n.Kind == NodeKind.CData))
        {
            return null;
        }

        // 첫 블록 앞에서 공백·주석을 걷어낸 마지막 텍스트 조각.
        Node? keywordText = null;
        for (var n = run.Count - 1; n >= 0; n--)
        {
            if (run[n].Kind == NodeKind.Text && !IsBlank(text, run[n]))
            {
                keywordText = run[n];
                break;
            }
        }

        if (keywordText is null)
        {
            return null;
        }

        var word = FindLastSqlWord(text, keywordText.Value);
        if (word is null)
        {
            return null;
        }

        var keyword = text.AsSpan(word.Value.Start, word.Value.End - word.Value.Start);
        AutoFixKind kind;
        string tag;
        if (keyword.Equals("SET", StringComparison.OrdinalIgnoreCase))
        {
            kind = AutoFixKind.WrapSet;
            tag = "set";
        }
        else if (keyword.Equals("WHERE", StringComparison.OrdinalIgnoreCase))
        {
            kind = AutoFixKind.WrapWhere;
            tag = "where";
        }
        else
        {
            return null;
        }

        if (StaticConditionFollows(text, nodes, lastBlock))
        {
            return null;
        }

        var blocksEnd = nodes[lastBlock].End;
        var indent = LineIndent(text, word.Value.Start);
        var replacement = string.Concat(
            "<", tag, ">",
            text[word.Value.End..blocksEnd],
            eol, indent, "</", tag, ">");
        return new AutoFixProposal(
            kind,
            new TextRange(word.Value.Start, blocksEnd - word.Value.Start),
            replacement,
            LineOf(text, word.Value.Start),
            LineOf(text, blocksEnd - 1));
    }

    /// <summary>
    /// 블록 묶음 뒤가 문장 끝이거나 절 머리(WHERE·ORDER·GROUP·UNION…·<c>)</c>)로 시작할 때만 감쌀 수 있다.
    /// 그 밖(정적 조건 <c>AND B=1</c>, 쉼표 없는 정적 열 <c>UPDATED_AT = SYSDATE</c>, include 등 다른 요소)은 정적 글이 섞인 것으로 본다.
    /// </summary>
    private static bool StaticConditionFollows(string text, List<Node> nodes, int lastBlock)
    {
        for (var n = lastBlock + 1; n < nodes.Count; n++)
        {
            var node = nodes[n];
            switch (node.Kind)
            {
                case NodeKind.Comment:
                    continue;
                case NodeKind.Text when IsBlank(text, node):
                    continue;
                case NodeKind.Text:
                    return !StartsWithClauseHead(text.AsSpan(node.Start, node.End - node.Start));
                case NodeKind.CData:
                    return !StartsWithClauseHead(text.AsSpan(node.Start + 9, node.End - 3 - (node.Start + 9)));
                default:
                    return true;
            }
        }

        return false;
    }

    private static readonly string[] ClauseHeads =
    [
        "WHERE", "ORDER", "GROUP", "HAVING", "UNION", "INTERSECT", "MINUS", "EXCEPT", "CONNECT", "START",
        "FETCH", "OFFSET", "FOR", "RETURNING", "RETURN", "LOG", "MODEL", "WINDOW",
    ];

    private static bool StartsWithClauseHead(ReadOnlySpan<char> value)
    {
        value = value.TrimStart();
        if (value.IsEmpty || value[0] is ')' or ';')
        {
            return true;
        }

        var end = 0;
        while (end < value.Length && IsWordChar(value[end]))
        {
            end++;
        }

        var head = value[..end];
        foreach (var word in ClauseHeads)
        {
            if (head.Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
