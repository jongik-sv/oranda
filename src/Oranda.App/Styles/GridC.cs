using Avalonia;
using Avalonia.Controls;
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
