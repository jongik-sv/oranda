namespace Oranda.App.Features.MyBatisWorkspace.Save;

/// <summary>「바뀌는 부분」 diff 줄의 종류.</summary>
public enum MapperDiffKind
{
    Context,
    Added,
    Removed,

    /// <summary>앞뒤 같은 줄이 길어 접은 자리(「…」).</summary>
    Gap,
}

/// <summary>diff 한 줄. 줄 번호는 매퍼 파일 기준(1 부터). 빠진 줄은 새 번호, 더한 줄은 옛 번호가 없다.</summary>
public sealed record MapperDiffLine(MapperDiffKind Kind, int? OldLine, int? NewLine, string Text);

/// <summary>나란히 보기 한 행: 왼쪽 = 옛 줄, 오른쪽 = 새 줄. 한쪽만 있으면 다른 쪽은 null.</summary>
public sealed record MapperDiffPair(MapperDiffLine? Left, MapperDiffLine? Right);

/// <summary>
/// 문장 원문 두 개의 줄 diff(design.md 5.1 「한 줄·나란히 diff」, 시안 SaveDiff). git diff 와 같은 모양: 앞뒤 같은 줄 <see cref="ContextLines"/> 줄만 남기고 접는다.
/// 앞뒤 공통 줄을 먼저 잘라 내고 가운데만 LCS 로 맞춘다. 가운데가 <see cref="MaxLcsCells"/> 칸을 넘으면 통째로 바뀐 것으로 본다(<c>PlanComparer</c> 와 같은 상한).
/// </summary>
public static class MapperLineDiff
{
    public const int MaxLcsCells = 4_000_000;

    public const int ContextLines = 3;

    /// <summary>
    /// <paramref name="oldText"/> → <paramref name="newText"/> 의 diff 줄. <paramref name="oldFirstLine"/>·<paramref name="newFirstLine"/> = 각 원문 첫 줄의 파일 줄 번호.
    /// 같은 글이면 빈 목록.
    /// </summary>
    public static IReadOnlyList<MapperDiffLine> Build(string oldText, string newText, int oldFirstLine, int newFirstLine, int context = ContextLines)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        var a = oldText.Length == 0 ? [] : SplitLines(oldText);
        var b = newText.Length == 0 ? [] : SplitLines(newText);
        var all = Align(a, b, oldFirstLine, newFirstLine);
        return all.Exists(l => l.Kind != MapperDiffKind.Context) ? Fold(all, context) : [];
    }

    /// <summary>더한 줄·빠진 줄 수(시안 「+5 −1」).</summary>
    public static (int Added, int Removed) Count(IReadOnlyList<MapperDiffLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return (lines.Count(l => l.Kind == MapperDiffKind.Added), lines.Count(l => l.Kind == MapperDiffKind.Removed));
    }

    /// <summary>나란히 보기: 이어진 빠진 줄·더한 줄 묶음을 한 행씩 짝짓는다.</summary>
    public static IReadOnlyList<MapperDiffPair> SideBySide(IReadOnlyList<MapperDiffLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var pairs = new List<MapperDiffPair>();
        int i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (line.Kind is MapperDiffKind.Context or MapperDiffKind.Gap)
            {
                pairs.Add(new MapperDiffPair(line, line));
                i++;
                continue;
            }

            var removed = new List<MapperDiffLine>();
            var added = new List<MapperDiffLine>();
            while (i < lines.Count && lines[i].Kind is MapperDiffKind.Removed or MapperDiffKind.Added)
            {
                (lines[i].Kind == MapperDiffKind.Removed ? removed : added).Add(lines[i]);
                i++;
            }

            for (int k = 0; k < Math.Max(removed.Count, added.Count); k++)
            {
                pairs.Add(new MapperDiffPair(k < removed.Count ? removed[k] : null, k < added.Count ? added[k] : null));
            }
        }

        return pairs;
    }

    /// <summary><paramref name="offset"/> 가 든 줄 번호(1 부터). 줄 끝은 「\r\n」·「\n」·「\r」.</summary>
    public static int LineOf(string text, int offset)
    {
        ArgumentNullException.ThrowIfNull(text);
        offset = Math.Clamp(offset, 0, text.Length);
        int line = 1;
        for (int i = 0; i < offset; i++)
        {
            if (text[i] == '\n' || (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')))
            {
                line++;
            }
        }

        return line;
    }

    internal static string[] SplitLines(string text) => text.ReplaceLineEndings("\n").Split('\n');

    private static List<MapperDiffLine> Align(string[] a, string[] b, int oldFirst, int newFirst)
    {
        int pre = 0;
        while (pre < a.Length && pre < b.Length && a[pre] == b[pre])
        {
            pre++;
        }

        int suf = 0;
        while (suf < a.Length - pre && suf < b.Length - pre && a[a.Length - 1 - suf] == b[b.Length - 1 - suf])
        {
            suf++;
        }

        var result = new List<MapperDiffLine>(a.Length + b.Length);
        for (int k = 0; k < pre; k++)
        {
            result.Add(new MapperDiffLine(MapperDiffKind.Context, oldFirst + k, newFirst + k, a[k]));
        }

        int ma = a.Length - pre - suf;
        int mb = b.Length - pre - suf;
        if ((long)(ma + 1) * (mb + 1) > MaxLcsCells)
        {
            for (int k = 0; k < ma; k++)
            {
                result.Add(new MapperDiffLine(MapperDiffKind.Removed, oldFirst + pre + k, null, a[pre + k]));
            }

            for (int k = 0; k < mb; k++)
            {
                result.Add(new MapperDiffLine(MapperDiffKind.Added, null, newFirst + pre + k, b[pre + k]));
            }
        }
        else
        {
            AlignMiddle(a, b, pre, ma, mb, oldFirst, newFirst, result);
        }

        for (int k = 0; k < suf; k++)
        {
            int ia = a.Length - suf + k;
            int ib = b.Length - suf + k;
            result.Add(new MapperDiffLine(MapperDiffKind.Context, oldFirst + ia, newFirst + ib, a[ia]));
        }

        return result;
    }

    // 가운데 LCS: 뒤에서부터 길이 표를 채우고 앞에서부터 따라간다. 같은 길이면 빠진 줄을 먼저 낸다(git 과 같은 순서).
    private static void AlignMiddle(string[] a, string[] b, int pre, int ma, int mb, int oldFirst, int newFirst, List<MapperDiffLine> result)
    {
        var lcs = new int[ma + 1, mb + 1];
        for (int i = ma - 1; i >= 0; i--)
        {
            for (int j = mb - 1; j >= 0; j--)
            {
                lcs[i, j] = a[pre + i] == b[pre + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        int x = 0;
        int y = 0;
        while (x < ma || y < mb)
        {
            if (x < ma && y < mb && a[pre + x] == b[pre + y])
            {
                result.Add(new MapperDiffLine(MapperDiffKind.Context, oldFirst + pre + x, newFirst + pre + y, a[pre + x]));
                x++;
                y++;
            }
            else if (x < ma && (y >= mb || lcs[x + 1, y] >= lcs[x, y + 1]))
            {
                result.Add(new MapperDiffLine(MapperDiffKind.Removed, oldFirst + pre + x, null, a[pre + x]));
                x++;
            }
            else
            {
                result.Add(new MapperDiffLine(MapperDiffKind.Added, null, newFirst + pre + y, b[pre + y]));
                y++;
            }
        }
    }

    // 바뀐 줄에서 context 줄보다 먼 같은 줄은 「…」 한 줄로 접는다.
    private static List<MapperDiffLine> Fold(List<MapperDiffLine> all, int context)
    {
        var keep = new bool[all.Count];
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].Kind == MapperDiffKind.Context)
            {
                continue;
            }

            for (int k = Math.Max(0, i - context); k <= Math.Min(all.Count - 1, i + context); k++)
            {
                keep[k] = true;
            }
        }

        var folded = new List<MapperDiffLine>();
        bool gap = false;
        for (int i = 0; i < all.Count; i++)
        {
            if (keep[i])
            {
                folded.Add(all[i]);
                gap = false;
            }
            else if (!gap)
            {
                folded.Add(new MapperDiffLine(MapperDiffKind.Gap, null, null, "…"));
                gap = true;
            }
        }

        return folded;
    }
}
