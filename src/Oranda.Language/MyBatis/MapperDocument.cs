using System.Text;
using Oranda.Core.MyBatis;

namespace Oranda.Language.MyBatis;

/// <summary>
/// 매퍼 원문 모델 구현(MB-01, TRD 3.1·4) — TSK-02-01. <see cref="MapperParser"/> 가 만든다.
/// 편집은 원문 조각 바꾸기 목록으로 바꿔 뒤에서부터 적용한다. 편집 범위 밖 글자는 그대로다.
/// <para>
/// id 는 MyBatis 처럼 종류 묶음(문장 4종·sql·resultMap·그 밖은 요소 이름)마다 따로 본다: 다른 묶음끼리 같은 id 는 합법이다.
/// 한 id 가 여러 묶음에 있으면 문장 → sql → 그 밖 순으로 고른다. 고른 묶음 안에서 겹치면 편집은 모호하다.
/// </para>
/// </summary>
internal sealed class MapperDocument : IMyBatisMapperDocument
{
    private const string StatementGroup = "statement";
    private const string SqlGroup = "sql";

    private readonly int rootTagEnd;
    private readonly int rootCloseStart;

    public MapperDocument(string text, MapperFileFormat format, MapperScan scan)
    {
        Text = text;
        Format = format;
        MapperNamespace = scan.Namespace;
        Elements = scan.Elements;
        rootTagEnd = scan.RootTagEnd;
        rootCloseStart = scan.RootCloseStart;
        Hash = MapperHash.Compute(text);
    }

    public string Text { get; }

    public MapperFileFormat Format { get; }

    public string? MapperNamespace { get; }

    public IReadOnlyList<MyBatisElement> Elements { get; }

    public string Hash { get; }

    public MyBatisElement? Find(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var group = Candidates(id);
        return group.Count == 0 ? null : group[0];
    }

    public string Apply(IReadOnlyList<MapperEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        var changes = new List<Change>(edits.Count);
        var touched = new HashSet<MyBatisElement>(ReferenceEqualityComparer.Instance);
        var deleted = new HashSet<MyBatisElement>(ReferenceEqualityComparer.Instance);
        var insertAfter = new List<MyBatisElement>();
        for (int order = 0; order < edits.Count; order++)
        {
            var edit = edits[order];
            switch (edit)
            {
                case MapperEdit.ReplaceElement r:
                {
                    var e = Target(r.Id, touched);
                    changes.Add(new Change(e.ElementRange.Start, e.ElementRange.Length, Normalize(r.NewText), order));
                    break;
                }

                case MapperEdit.DeleteElement d:
                {
                    var e = Target(d.Id, touched);
                    deleted.Add(e);
                    var (start, end) = DeleteRange(e);
                    changes.Add(new Change(start, end - start, string.Empty, order));
                    break;
                }

                case MapperEdit.RenameElement n:
                {
                    var e = Target(n.Id, touched);
                    changes.Add(RenameChange(e, n.NewId, order));
                    break;
                }

                case MapperEdit.InsertElement i:
                {
                    if (i.AfterId is null)
                    {
                        // 같은 자리의 「요소 뒤 넣기」보다 뒤에 온다(마지막 요소 끝 = 「</mapper>」 자리일 때).
                        changes.Add(InsertBeforeClose(i.NewText, order) with { Rank = 1 });
                    }
                    else
                    {
                        var after = Resolve(i.AfterId);
                        insertAfter.Add(after);
                        changes.Add(new Change(InsertAfterPoint(after), 0, Format.NewLine + IndentOf(after) + Normalize(i.NewText), order));
                    }

                    break;
                }

                default:
                    throw new ArgumentException($"모르는 편집입니다: {edit?.GetType().Name}", nameof(edits));
            }
        }

        if (insertAfter.Exists(deleted.Contains))
        {
            throw new ArgumentException("지우는 요소 뒤에 새 요소를 넣을 수 없습니다.", nameof(edits));
        }

        return ApplyChanges(changes);
    }

    public MapperWriteCheck Check(string newText, string diskHash)
    {
        ArgumentNullException.ThrowIfNull(newText);
        ArgumentNullException.ThrowIfNull(diskHash);
        bool changedOnDisk = !string.Equals(Hash, diskHash, StringComparison.Ordinal);
        MapperScan scan;
        try
        {
            scan = MapperParser.Scan(newText);
        }
        catch (FormatException ex)
        {
            return new MapperWriteCheck(ex.Message, [], changedOnDisk);
        }

        var duplicates = scan.Elements
            .Where(e => e.Id is not null)
            .GroupBy(e => (Group: GroupOf(e), Id: e.Id!, DatabaseId: DatabaseIdOf(e)))
            .Where(g => g.Count() > 1)
            .Select(g => g.Key.Id)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        return new MapperWriteCheck(null, duplicates, changedOnDisk);
    }

    /// <summary>databaseId 속성. MyBatis 는 같은 id 라도 databaseId 가 다르면 합법이라 겹침 검사 키에 넣는다(편집은 id 만 받으므로 모호하면 거부).</summary>
    private static string? DatabaseIdOf(MyBatisElement e) =>
        e.Attributes.FirstOrDefault(a => a.Key == "databaseId").Value;

    private static string GroupOf(MyBatisElement e) => e.IsStatement ? StatementGroup
        : e.Kind == MyBatisElementKind.Sql ? SqlGroup
        : e.Name;

    /// <summary>id 가 가리키는 묶음의 요소들(문장 → sql → 그 밖 순으로 처음 찾은 묶음, 원문 순서).</summary>
    private List<MyBatisElement> Candidates(string id)
    {
        var all = Elements.Where(e => string.Equals(e.Id, id, StringComparison.Ordinal)).ToList();
        if (all.Count <= 1)
        {
            return all;
        }

        var group = all.Exists(e => e.IsStatement) ? StatementGroup
            : all.Exists(e => e.Kind == MyBatisElementKind.Sql) ? SqlGroup
            : GroupOf(all[0]);
        return all.FindAll(e => GroupOf(e) == group);
    }

    private MyBatisElement Resolve(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var group = Candidates(id);
        return group.Count switch
        {
            0 => throw new ArgumentException($"id 가 {id} 인 요소가 없습니다.", nameof(id)),
            1 => group[0],
            _ => throw new ArgumentException($"id 가 {id} 인 요소가 여럿이라 고를 수 없습니다.", nameof(id)),
        };
    }

    /// <summary>바꾸기·지우기·id 바꾸기 대상. 한 요소에는 편집 하나만 둔다.</summary>
    private MyBatisElement Target(string id, HashSet<MyBatisElement> touched)
    {
        var e = Resolve(id);
        if (!touched.Add(e))
        {
            throw new ArgumentException($"id 가 {id} 인 요소에 편집이 둘 이상입니다.", nameof(id));
        }

        return e;
    }

    /// <summary>
    /// 바꿀 글의 줄 끝(「\r\n」·「\r」·「\n」)을 문서의 줄 끝으로 맞춘다(문장 탭 편집기 글이 LF 일 수 있다).
    /// <see cref="string.ReplaceLineEndings(string)"/> 는 U+2028 등도 바꾸므로 쓰지 않는다(SQL 리터럴 안 글자가 바뀐다).
    /// </summary>
    private string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.AsSpan().IndexOfAny('\r', '\n') < 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length + 16);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                sb.Append(Format.NewLine);
            }
            else if (c == '\n')
            {
                sb.Append(Format.NewLine);
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 요소 뒤에 넣을 자리. 같은 줄 요소 뒤에 xml 주석 하나만 있으면(<c>&lt;select …/&gt; &lt;!-- 설명 --&gt;</c>) 그 주석은 요소 것이므로 주석 뒤.
    /// </summary>
    private int InsertAfterPoint(MyBatisElement e)
    {
        int end = e.ElementRange.End;
        int lineEnd = LineEnd(end);
        var rest = Text.AsSpan(end, lineEnd - end).Trim();
        if (rest.StartsWith("<!--", StringComparison.Ordinal) && rest.EndsWith("-->", StringComparison.Ordinal)
            && rest.IndexOf("-->", StringComparison.Ordinal) == rest.Length - 3)
        {
            return end + Text.AsSpan(end, lineEnd - end).TrimEnd().Length;
        }

        return end;
    }

    /// <summary>새 요소를 「&lt;/mapper&gt;」 바로 앞에 넣는다. 들여쓰기는 앞 요소를 따른다.</summary>
    private Change InsertBeforeClose(string newText, int order)
    {
        var last = Elements.Count == 0 ? null : Elements[^1];
        var indent = last is null ? "  " : IndentOf(last);
        var body = indent + Normalize(newText);
        if (rootCloseStart < 0)
        {
            // <mapper/>: 「/>」 를 여는·닫는 태그로 펼친다.
            int slash = rootTagEnd - 2;
            return new Change(slash, 2, ">" + Format.NewLine + body + Format.NewLine + "</mapper>", order);
        }

        int lineStart = LineStart(rootCloseStart);
        if (IsBlank(lineStart, rootCloseStart))
        {
            return new Change(lineStart, 0, body + Format.NewLine, order);
        }

        // 「</mapper>」 앞에 다른 글이 같은 줄에 있다(<mapper></mapper> 등): 줄을 나눠 넣는다.
        return new Change(rootCloseStart, 0, Format.NewLine + body + Format.NewLine, order);
    }

    /// <summary>
    /// 지울 범위. 요소가 줄 전체를 차지하면 그 줄들을 줄 끝까지 지우고, 그 결과 빈 줄이 둘 이어지면 뒤 빈 줄 하나도 지운다.
    /// 같은 줄에 다른 글이 있으면 요소만 지운다. 요소 앞 주석은 남긴다.
    /// </summary>
    private (int Start, int End) DeleteRange(MyBatisElement e)
    {
        int start = e.ElementRange.Start;
        int end = e.ElementRange.End;
        int lineStart = LineStart(start);
        int lineEnd = LineEnd(end);
        if (!IsBlank(lineStart, start) || !IsBlank(end, lineEnd))
        {
            return (start, end);
        }

        int next = AfterNewLine(lineEnd);
        if (lineStart > 0 && next < Text.Length)
        {
            // 앞 줄 = 이 줄 바로 앞 줄 끝(「\r\n」 이면 「\r」)에서 거슬러 올라간 줄.
            int prevEnd = lineStart - 1;
            if (Text[prevEnd] == '\n' && prevEnd > 0 && Text[prevEnd - 1] == '\r')
            {
                prevEnd--;
            }

            int nextEnd = LineEnd(next);
            if (IsBlank(LineStart(prevEnd), prevEnd) && IsBlank(next, nextEnd))
            {
                next = AfterNewLine(nextEnd);
            }
        }

        return (lineStart, next);
    }

    /// <summary>여는 태그의 id 속성 값만 바꾼다. 따옴표·속성 순서는 그대로, 값은 그 따옴표에 맞게 이스케이프한다.</summary>
    private Change RenameChange(MyBatisElement e, string newId, int order)
    {
        ArgumentException.ThrowIfNullOrEmpty(newId);
        int tagStart = e.ElementRange.Start;
        int tagEnd = MapperParser.StartTagEnd(Text, tagStart);
        var (valueStart, valueEnd, quote) = AttributeValue(tagStart, tagEnd, "id")
            ?? throw new ArgumentException($"{e.Name} 에 id 속성이 없습니다.", nameof(e));
        return new Change(valueStart, valueEnd - valueStart, Escape(newId, quote), order);
    }

    /// <summary>여는 태그 [<paramref name="tagStart"/>, <paramref name="tagEnd"/>) 에서 속성 값 범위(따옴표 안쪽)를 찾는다.</summary>
    private (int Start, int End, char Quote)? AttributeValue(int tagStart, int tagEnd, string name)
    {
        int i = tagStart + 1;
        while (i < tagEnd && !IsXmlSpace(Text[i]) && Text[i] is not ('>' or '/'))
        {
            i++;
        }

        while (i < tagEnd)
        {
            while (i < tagEnd && IsXmlSpace(Text[i]))
            {
                i++;
            }

            int nameStart = i;
            while (i < tagEnd && Text[i] != '=' && !IsXmlSpace(Text[i]) && Text[i] is not ('>' or '/'))
            {
                i++;
            }

            if (i == nameStart)
            {
                return null;
            }

            var attr = Text.AsSpan(nameStart, i - nameStart);
            while (i < tagEnd && (IsXmlSpace(Text[i]) || Text[i] == '='))
            {
                i++;
            }

            char quote = Text[i];
            int valueStart = i + 1;
            int valueEnd = Text.IndexOf(quote, valueStart);
            if (attr.SequenceEqual(name))
            {
                return (valueStart, valueEnd, quote);
            }

            i = valueEnd + 1;
        }

        return null;
    }

    private static string Escape(string value, char quote)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            sb.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '"' when quote == '"' => "&quot;",
                '\'' when quote == '\'' => "&apos;",

                // 속성 값 정규화로 공백이 되지 않게 문자 참조로 둔다(다시 읽은 id 가 NewId 와 같도록).
                '\t' => "&#9;",
                '\n' => "&#10;",
                '\r' => "&#13;",
                _ => c.ToString(),
            });
        }

        return sb.ToString();
    }

    /// <summary>요소 줄 앞 공백(줄 시작 ~ 요소 사이가 공백뿐일 때). 아니면 빈 글.</summary>
    private string IndentOf(MyBatisElement e)
    {
        int start = e.ElementRange.Start;
        int lineStart = LineStart(start);
        return IsBlank(lineStart, start) ? Text[lineStart..start] : string.Empty;
    }

    /// <summary>조각 바꾸기를 뒤에서부터 적용한다. 같은 자리 넣기는 편집 목록 순서를 지키고, 범위가 겹치면 <see cref="ArgumentException"/>.</summary>
    private string ApplyChanges(List<Change> changes)
    {
        changes.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start)
            : a.Length != b.Length ? a.Length.CompareTo(b.Length)
            : a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank)
            : a.Order.CompareTo(b.Order));
        for (int k = 1; k < changes.Count; k++)
        {
            var prev = changes[k - 1];
            if (prev.Start + prev.Length > changes[k].Start)
            {
                throw new ArgumentException("편집 범위가 겹칩니다.", nameof(changes));
            }
        }

        var sb = new StringBuilder(Text);
        for (int k = changes.Count - 1; k >= 0; k--)
        {
            var c = changes[k];
            sb.Remove(c.Start, c.Length).Insert(c.Start, c.NewText);
        }

        return sb.ToString();
    }

    private int LineStart(int offset)
    {
        int i = offset;
        while (i > 0 && Text[i - 1] is not ('\n' or '\r'))
        {
            i--;
        }

        return i;
    }

    /// <summary>줄 끝 글자(「\r」·「\n」)의 위치. 마지막 줄이면 원문 길이.</summary>
    private int LineEnd(int offset)
    {
        int i = Text.AsSpan(offset).IndexOfAny('\r', '\n');
        return i < 0 ? Text.Length : offset + i;
    }

    private int AfterNewLine(int lineEnd)
    {
        if (lineEnd >= Text.Length)
        {
            return lineEnd;
        }

        return Text[lineEnd] == '\r' && lineEnd + 1 < Text.Length && Text[lineEnd + 1] == '\n' ? lineEnd + 2 : lineEnd + 1;
    }

    private bool IsBlank(int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (!IsXmlSpace(Text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsXmlSpace(char c) => c is ' ' or '\t' or '\r' or '\n';

    /// <summary>원문 조각 바꾸기 하나: [Start, Start + Length) 를 NewText 로. Order = 편집 목록 순서, Rank = 같은 자리 넣기의 앞뒤(작을수록 앞).</summary>
    private readonly record struct Change(int Start, int Length, string NewText, int Order, int Rank = 0);
}
