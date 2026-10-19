using System;
using System.Collections.Generic;

namespace Oranda.App.Shell.Menus;

/// <summary>툴바 넘침 계산(ui-shell.md 1장: 폭이 모자라면 오른쪽부터 「…」 메뉴로 접는다). 순수 계산.</summary>
public static class ToolbarOverflow
{
    /// <summary>
    /// 앞에서부터 몇 개가 <paramref name="available"/> 안에 들어가는지. 전부 들어가면 개수 전체,
    /// 아니면 「…」 단추 폭(<paramref name="overflowButtonWidth"/>)을 빼고 센다.
    /// </summary>
    public static int VisibleCount(IReadOnlyList<double> widths, double available, double spacing, double overflowButtonWidth)
    {
        ArgumentNullException.ThrowIfNull(widths);
        if (Fits(widths, widths.Count, available, spacing))
        {
            return widths.Count;
        }

        var budget = available - overflowButtonWidth - spacing;
        var count = 0;
        while (count < widths.Count && Fits(widths, count + 1, budget, spacing))
        {
            count++;
        }

        return count;
    }

    private static bool Fits(IReadOnlyList<double> widths, int count, double available, double spacing)
    {
        var total = 0.0;
        for (var i = 0; i < count; i++)
        {
            total += widths[i] + (i > 0 ? spacing : 0);
        }

        return total <= available;
    }
}
