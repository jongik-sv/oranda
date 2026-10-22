using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace Oranda.App.Styles;

/// <summary>
/// 격자 표(C안)와 도구 창 틀을 코드로 만드는 도우미. 모양은 <c>Styles/GridC.axaml</c> 의 클래스가 정한다
/// (<c>ListBox.grid-c</c>·<c>Border.grid-c-header</c>·<c>Border.grid-c-cell</c>·<c>.row-head</c>·<c>TextBlock.grid-c-marker</c>).
/// 테이블 레이아웃 창과 객체 정보 창이 쓰고, 객체 상세 표도 같은 모양으로 쓸 수 있다.
/// </summary>
public static class GridC
{
    /// <summary>
    /// 격자 한 줄. 칸마다 <c>grid-c-cell</c> 테두리를 씌우고, <paramref name="rowHead"/> 면 첫 칸을 행 머리로 칠한다.
    /// 머리글과 행이 같은 <see cref="Grid.IsSharedSizeScopeProperty"/> 안에서 같은 <paramref name="group"/> 이름을 쓰면 칸 너비가 맞는다(마지막 칸만 남는 너비를 차지).
    /// </summary>
    public static Grid Row(IReadOnlyList<Control> cells, bool rowHead = true, string group = "gc", double lastMinWidth = 80)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var grid = new Grid();
        for (var i = 0; i < cells.Count; i++)
        {
            var last = i == cells.Count - 1;
            grid.ColumnDefinitions.Add(new ColumnDefinition(last ? GridLength.Star : GridLength.Auto)
            {
                SharedSizeGroup = last ? null : group + i,
                MinWidth = last ? lastMinWidth : 0,
            });
            cells[i].VerticalAlignment = VerticalAlignment.Center;
            var cell = new Border { Classes = { "grid-c-cell" }, Child = cells[i] };
            if (i == 0 && rowHead)
            {
                cell.Classes.Add("row-head");
            }

            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }

        return grid;
    }

    /// <summary>
    /// 칸 너비를 처음부터 전체 행 기준으로 고정하는 숨은 줄(TSK-13-40). 목록이 가상화돼 화면에 있는 줄만 <c>SharedSizeGroup</c> 에 참여하면
    /// 스크롤로 긴 글이 든 줄이 나타날 때 칸이 넓어지며 표가 흔들린다. 그래서 칸마다 글이 가장 넓은 행 몇 개(<paramref name="perColumn"/>)만 골라
    /// 같은 칸 만들기(<paramref name="makeCells"/>)로 칸을 만들어 높이 0 인 줄에 얹는다 — 머리글·화면 줄과 같은 묶음 이름을 쓰므로 너비 최댓값이 처음부터 전체 행을 반영한다.
    /// 호출한 쪽은 반환값을 표와 같은 <see cref="Grid.IsSharedSizeScopeProperty"/> 범위 안(머리글 옆)에 두고, 데이터·필터·열이 바뀔 때마다 새로 만들어 바꾼다.
    /// </summary>
    /// <param name="weight">(행, 칸 번호) → 글 폭 추정(<see cref="TextWeight"/>). 0 이면 그 행은 그 칸의 후보가 아니다.</param>
    /// <param name="makeCells">행의 칸들(<see cref="Row"/> 에 넘길 것과 같은 모양·같은 개수). 화면 줄과 같은 클래스·글꼴 설정을 써야 폭이 같다.</param>
    public static Control Probe<T>(IReadOnlyList<T> rows, int columnCount, Func<T, int, double> weight, Func<T, IReadOnlyList<Control>> makeCells, string group = "gc", int perColumn = 4)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(weight);
        ArgumentNullException.ThrowIfNull(makeCells);
        var host = new StackPanel { Height = 0, ClipToBounds = true, IsHitTestVisible = false, Focusable = false };
        host.Bind(TemplatedControl.FontSizeProperty, host.GetResourceObservable("OrandaGridFontSize"));
        Avalonia.Automation.AutomationProperties.SetAccessibilityView(host, Avalonia.Automation.AccessibilityView.Raw);
        if (rows.Count == 0 || columnCount < 2)
        {
            return host;
        }

        // 묶음에 참여하는 칸은 마지막 칸(남는 너비)을 뺀 나머지. 칸마다 폭 추정이 큰 행 perColumn 개(같으면 뒤쪽 행 먼저).
        var best = new List<int>[columnCount - 1];
        for (var c = 0; c < best.Length; c++)
        {
            var top = new List<(double W, int I)>();
            for (var i = 0; i < rows.Count; i++)
            {
                var w = weight(rows[i], c);
                if (w <= 0)
                {
                    continue;
                }

                if (top.Count < perColumn)
                {
                    top.Add((w, i));
                }
                else
                {
                    var min = 0;
                    for (var k = 1; k < top.Count; k++)
                    {
                        if (top[k].W < top[min].W)
                        {
                            min = k;
                        }
                    }

                    if (w >= top[min].W)
                    {
                        top[min] = (w, i);
                    }
                }
            }

            best[c] = top.OrderByDescending(t => t.W).ThenByDescending(t => t.I).Select(t => t.I).ToList();
        }

        for (var k = 0; k < perColumn; k++)
        {
            var cells = new List<Control>(columnCount);
            var any = false;
            for (var c = 0; c < columnCount; c++)
            {
                if (c < best.Length && k < best[c].Count)
                {
                    cells.Add(makeCells(rows[best[c][k]])[c]);
                    any = true;
                }
                else
                {
                    cells.Add(new Panel());
                }
            }

            if (any)
            {
                host.Children.Add(Row(cells, rowHead: true, group));
            }
        }

        return host;
    }

    /// <summary>
    /// 글 폭 추정(글꼴 크기 1 기준 em 합). 한글·한자는 넓고 i·l·. 같은 글자는 좁다. 정확한 폭이 아니라 「가장 넓은 행 고르기」용 순위 값이라
    /// 비례 글꼴에서 글자 수만 보는 것보다 덜 틀린다. 빈 글은 0.
    /// </summary>
    public static double TextWeight(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var sum = 0.0;
        foreach (var ch in text)
        {
            sum += ch switch
            {
                >= '\u1100' and <= '\u115F' or >= '\u2E80' and <= '\uA4CF' or >= '\uAC00' and <= '\uD7A3' or >= '\uF900' and <= '\uFAFF' or >= '\uFE30' and <= '\uFE6F' or >= '\uFF00' and <= '\uFF60' or >= '\uFFE0' and <= '\uFFE6' => 1.0,
                ' ' or 'i' or 'j' or 'l' or 't' or 'f' or 'I' or '.' or ',' or ':' or ';' or '\'' or '|' or '!' or '(' or ')' or '[' or ']' => 0.3,
                'm' or 'w' or 'M' or 'W' => 0.85,
                >= 'A' and <= 'Z' => 0.68,
                >= '0' and <= '9' => 0.56,
                >= 'a' and <= 'z' => 0.52,
                _ => 0.6,
            };
        }

        return sum;
    }

    /// <summary>머리글 줄(<see cref="Row"/> 를 <c>grid-c-header</c> 로 감싼 것).</summary>
    public static Border Header(IReadOnlyList<string> titles, string group = "gc", double lastMinWidth = 80)
    {
        ArgumentNullException.ThrowIfNull(titles);
        return new Border
        {
            Classes = { "grid-c-header" },
            Child = Row(titles.Select(t => (Control)new TextBlock { Text = t }).ToList(), rowHead: false, group, lastMinWidth),
        };
    }

    /// <summary>
    /// 도구 창 틀: 위 도구 막대(<c>panel-toolbar</c>) / 본문 / 아래 상태 줄(<c>panel-status</c>, 왼쪽 글 · 오른쪽 힌트).
    /// </summary>
    public static DockPanel Frame(Control toolbar, Control body, Control statusLeft, Control? statusRight = null)
    {
        ArgumentNullException.ThrowIfNull(toolbar);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(statusLeft);
        var foot = new DockPanel();
        if (statusRight is not null)
        {
            statusRight.Margin = new Thickness(12, 0, 0, 0);
            statusRight.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(statusRight, Avalonia.Controls.Dock.Right);
            foot.Children.Add(statusRight);
        }

        statusLeft.VerticalAlignment = VerticalAlignment.Center;
        foot.Children.Add(statusLeft);
        var top = new Border { Classes = { "panel-toolbar" }, Child = toolbar };
        var status = new Border { Classes = { "panel-status" }, Child = foot };

        var root = new DockPanel();
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(status, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(status);
        root.Children.Add(body);
        return root;
    }
}
