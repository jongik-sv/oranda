using System.Globalization;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

// ResultGridModel 의 일부: 텍스트 모드·열 숨김·고정(ResultGridModel.cs 에서 나눔, 동작 불변).
public sealed partial class ResultGridModel
{
    // ---- 텍스트 모드 ----

    /// <summary>텍스트 결과 모드의 머리글 줄.</summary>
    public string GetTextHeader() => string.Join(" | ", _visible.Select(c => PadCell(Columns[c].Name, _textWidths[c], rightAlign: false)))
        + Environment.NewLine + string.Join("-+-", _visible.Select(c => new string('-', _textWidths[c])));

    /// <summary>텍스트 결과 모드의 데이터 줄.</summary>
    public string GetTextLine(long viewRow) =>
        string.Join(" | ", _visible.Select(c => PadCell(GetCellText(viewRow, c), _textWidths[c], IsRightAligned(c))));

    private static string PadCell(string s, int width, bool rightAlign)
    {
        var w = DisplayWidth(s);
        if (w > width)
        {
            var sb = new System.Text.StringBuilder();
            var acc = 0;
            foreach (var ch in s)
            {
                var cw = CharWidth(ch);
                if (acc + cw > width - 1)
                {
                    break;
                }

                sb.Append(ch);
                acc += cw;
            }

            return sb.Append('…').Append(' ', Math.Max(0, width - acc - 1)).ToString();
        }

        var pad = new string(' ', width - w);
        return rightAlign ? pad + s : s + pad;
    }

    /// <summary>동아시아 전각 문자는 2칸으로 센 표시 너비.</summary>
    public static int DisplayWidth(string s)
    {
        var w = 0;
        foreach (var ch in s)
        {
            w += CharWidth(ch);
        }

        return w;
    }

    private static int CharWidth(char ch) =>
        ch is (>= 'ᄀ' and <= 'ᅟ') or (>= '⺀' and <= '꓏') or (>= '가' and <= '힣')
            or (>= '豈' and <= '﫿') or (>= '︰' and <= '﹯') or (>= '＀' and <= '｠') or (>= '￠' and <= '￦') ? 2 : 1;

    public void ToggleTextMode()
    {
        IsTextMode = !IsTextMode;
        RaiseChanged();
    }

    public void ToggleRepeatHide()
    {
        HideRepeatedValues = !HideRepeatedValues;
        RaiseChanged();
    }

    // ---- 열 숨김·고정 ----

    public void HideColumn(int ordinal)
    {
        var pos = _visible.IndexOf(ordinal);
        if (pos < 0 || _visible.Count == 1)
        {
            return; // 마지막 열은 숨기지 않는다
        }

        _visible.RemoveAt(pos);
        if (pos < FrozenCount)
        {
            FrozenCount--;
        }

        ClampSelection();
        RaiseChanged();
        RefreshQuickFilterColumns();
    }

    public void ShowAllColumns()
    {
        _visible.Clear();
        _visible.AddRange(Enumerable.Range(0, Columns.Count));
        RaiseChanged();
        RefreshQuickFilterColumns();
    }

    /// <summary>
    /// 보이는 열 <paramref name="from"/> 을 <paramref name="insertBefore"/> 앞으로 옮긴다(TSK-13-17, 머리글 끌기). 위치는 둘 다 <see cref="VisibleColumns"/> 의 위치다.
    /// 고정 열은 옮기지도 그 안으로 놓지도 못한다. 이 결과의 화면 순서만 바꾸며 열 정체성(ordinal)·정렬·필터는 그대로다.
    /// 옮겼으면 true, 제자리거나 막혀서 그대로면 false.
    /// </summary>
    public bool MoveColumn(int from, int insertBefore)
    {
        if (from < FrozenCount || from >= _visible.Count || insertBefore < FrozenCount || insertBefore > _visible.Count)
        {
            return false;
        }

        var to = insertBefore > from ? insertBefore - 1 : insertBefore;
        if (to == from)
        {
            return false;
        }

        var ordinal = _visible[from];
        _visible.RemoveAt(from);
        _visible.Insert(to, ordinal);
        if (HasSelection)
        {
            // 선택은 열 위치를 따르므로 어긋난다. 보던 행은 그대로 두고 옮긴 열의 그 칸 하나만 고른다(행 보기 창이 행을 따라가므로 행을 바꾸지 않는다).
            Anchor = Active = Clamp(new GridCell(Active.Row, to));
        }

        RaiseChanged();
        return true;
    }

    /// <summary>보이는 열 <paramref name="count"/> 개를 왼쪽에 고정한다. 이미 같은 수면 해제한다.</summary>
    public void FreezeColumns(int count)
    {
        count = Math.Clamp(count, 0, Math.Max(0, _visible.Count - 1));
        FrozenCount = FrozenCount == count ? 0 : count;
        RaiseChanged();
    }

    /// <summary>활성 셀 열까지(포함) 고정/해제.</summary>
    public void FreezeUpToActive() => FreezeColumns(HasSelection ? Active.Col + 1 : 0);
}
