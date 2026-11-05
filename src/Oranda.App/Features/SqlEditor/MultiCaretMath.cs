namespace Oranda.App.Features.SqlEditor;

/// <summary>커서 하나의 선택 범위(<paramref name="Start"/>~<paramref name="End"/>)와 커서 위치. 선택이 없으면 Start = End = Caret.</summary>
internal readonly record struct CaretSlot(int Start, int End, int Caret, bool IsMain)
{
    public bool IsEmpty => Start == End;

    /// <summary>선택의 반대쪽 끝(선택이 없으면 커서 자신).</summary>
    public int Anchor => Caret == End ? Start : End;

    public int Length => End - Start;
}

/// <summary>다중 커서의 컨트롤과 무관한 순수 계산(시험하기 쉽게 분리).</summary>
internal static class MultiCaretMath
{
    /// <summary>겹치거나 맞닿은 슬롯을 합친다. 합쳐진 슬롯이 주 커서를 품으면 주 커서의 선택 방향(커서가 선택의 끝쪽인가)을 이어받는다.</summary>
    public static List<CaretSlot> Merge(IEnumerable<CaretSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        var result = new List<CaretSlot>();
        foreach (var s in slots.OrderBy(x => x.Start).ThenBy(x => x.End))
        {
            if (result.Count > 0 && Overlaps(result[^1], s))
            {
                var a = result[^1];
                int start = Math.Min(a.Start, s.Start), end = Math.Max(a.End, s.End);
                var lead = s.IsMain || !a.IsMain ? s : a; // 방향(커서가 선택의 끝쪽인가)은 주 커서, 없으면 나중 슬롯을 따른다
                result[^1] = new CaretSlot(start, end, lead.Caret == lead.End ? end : start, a.IsMain || s.IsMain);
            }
            else
            {
                result.Add(s);
            }
        }

        return result;
    }

    // a.Start <= b.Start 로 정렬된 두 슬롯이 겹치는가. 맞닿은 경우(b.Start = a.End)는 한쪽이 빈 커서일 때만 합친다.
    private static bool Overlaps(CaretSlot a, CaretSlot b)
        => b.Start < a.End || (b.Start == a.End && (a.IsEmpty || b.IsEmpty));

    /// <summary>
    /// 클립보드 글을 커서 <paramref name="count"/> 개에 나눈다. 줄 수(끝의 줄바꿈 하나는 줄 수에 넣지 않는다)가 커서 수와 같으면
    /// 커서마다 한 줄씩, 다르면 전체를 모든 커서에 넣는다.
    /// </summary>
    public static IReadOnlyList<string> DistributePaste(string clipboard, int count)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        var lines = SplitLines(clipboard);
        if (count > 1 && lines.Count == count)
        {
            return lines;
        }

        return [.. Enumerable.Repeat(clipboard, count)];
    }

    /// <summary>줄바꿈(\r\n·\n·\r)으로 줄을 나눈다. 글이 줄바꿈 하나로 끝나면 그 뒤의 빈 줄은 줄로 세지 않는다.</summary>
    public static List<string> SplitLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is not ('\r' or '\n'))
            {
                continue;
            }

            lines.Add(text[start..i]);
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            start = i + 1;
        }

        if (start < text.Length || lines.Count == 0)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    /// <summary>입력 글에서 쓸 수 없는 제어 문자(\b·\x1b 등)를 거른다. 탭·줄바꿈은 남긴다.</summary>
    public static string FilterTypedText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n')))
        {
            return text;
        }

        return string.Concat(text.Where(c => !char.IsControl(c) || c is '\t' or '\r' or '\n'));
    }
}
