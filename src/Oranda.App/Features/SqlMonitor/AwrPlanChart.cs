using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Oranda.App.Features.SqlMonitor;

/// <summary>
/// AWR 경과/실행(ms) 그래프: snap 순 점, 점 색 = plan_hash(처음 나온 순서). 같은 plan_hash 는 선으로 잇는다.
/// 색·선은 테마 브러시(OrandaIcon*·OrandaSeparatorBrush)만 쓴다. 고른 줄의 점은 고리로 강조한다.
/// </summary>
public sealed class AwrPlanChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<AwrRowItem>?> RowsProperty =
        AvaloniaProperty.Register<AwrPlanChart, IReadOnlyList<AwrRowItem>?>(nameof(Rows));

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<AwrPlanChart, int>(nameof(SelectedIndex), -1);

    /// <summary>plan_hash 색 순서(범례와 같다).</summary>
    public static readonly IReadOnlyList<string> PaletteKeys =
    [
        "OrandaIconBlueBrush", "OrandaIconOrangeBrush", "OrandaIconTealBrush", "OrandaIconPurpleBrush", "OrandaIconGreenBrush", "OrandaIconRedBrush",
    ];

    private const double Left = 64;
    private const double Right = 12;
    private const double Top = 10;
    private const double Bottom = 24;

    static AwrPlanChart()
    {
        AffectsRender<AwrPlanChart>(RowsProperty, SelectedIndexProperty);
    }

    public AwrPlanChart()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public IReadOnlyList<AwrRowItem>? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>plan 색 번호 → 브러시 키.</summary>
    public static string PaletteKey(int index) => PaletteKeys[((index % PaletteKeys.Count) + PaletteKeys.Count) % PaletteKeys.Count];

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var rows = Rows ?? [];
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < Left + Right + 20 || h < Top + Bottom + 20)
        {
            return;
        }

        var text = GetValue(TextElement.ForegroundProperty) ?? Brushes.Gray;
        var grid = Brush("OrandaSeparatorBrush") ?? Brushes.Gray;
        var gridPen = new Pen(grid, 1);
        var plotW = w - Left - Right;
        var plotH = h - Top - Bottom;
        var points = rows.Select((r, i) => (Row: r, Index: i)).Where(p => p.Row.MsPerExec is not null).ToList();
        var max = points.Count == 0 ? 1 : Math.Max(points.Max(p => p.Row.MsPerExec!.Value), 0.001);
        var top = NiceCeiling(max);

        // 가로 눈금 4줄과 왼쪽 값.
        for (var k = 0; k <= 4; k++)
        {
            var y = Top + plotH - (plotH * k / 4);
            context.DrawLine(gridPen, new Point(Left, y), new Point(w - Right, y));
            var label = Format(top * k / 4);
            var ft = Text(label, text);
            context.DrawText(ft, new Point(Left - 6 - ft.Width, y - (ft.Height / 2)));
        }

        if (rows.Count == 0)
        {
            return;
        }

        var snaps = rows.Select(r => r.Row.SnapId).Distinct().OrderBy(s => s).ToList();
        double X(long snap)
        {
            var i = snaps.IndexOf(snap);
            return snaps.Count == 1 ? Left + (plotW / 2) : Left + (plotW * i / (snaps.Count - 1));
        }

        double Y(double ms) => Top + plotH - (plotH * Math.Min(ms / top, 1));

        // 아래 snap 시각: 처음·가운데·끝.
        var picks = new[] { 0, snaps.Count / 2, snaps.Count - 1 }.Distinct().ToList();
        foreach (var i in picks)
        {
            var row = rows.First(r => r.Row.SnapId == snaps[i]);
            var ft = Text(row.SnapText, text);
            var x = Math.Clamp(X(snaps[i]) - (ft.Width / 2), Left - 4, w - Right - ft.Width);
            context.DrawText(ft, new Point(x, Top + plotH + 5));
        }

        // plan_hash 마다 선 + 점.
        foreach (var group in points.GroupBy(p => p.Row.PlanIndex))
        {
            var brush = Brush(PaletteKey(group.Key)) ?? text;
            var pen = new Pen(brush, 1.5);
            Point? prev = null;
            foreach (var p in group)
            {
                var pt = new Point(X(p.Row.Row.SnapId), Y(p.Row.MsPerExec!.Value));
                if (prev is { } a)
                {
                    context.DrawLine(pen, a, pt);
                }

                prev = pt;
            }

            foreach (var p in group)
            {
                var pt = new Point(X(p.Row.Row.SnapId), Y(p.Row.MsPerExec!.Value));
                context.DrawEllipse(brush, null, pt, 3.5, 3.5);
                if (p.Index == SelectedIndex)
                {
                    context.DrawEllipse(null, new Pen(text, 1.5), pt, 7, 7);
                }
            }
        }
    }

    /// <summary>1·2·5 단위로 올린 눈금 끝 값.</summary>
    internal static double NiceCeiling(double value)
    {
        if (value <= 0)
        {
            return 1;
        }

        var exp = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var m in new[] { 1d, 2d, 5d, 10d })
        {
            if (value <= m * exp)
            {
                return m * exp;
            }
        }

        return 10 * exp;
    }

    private static string Format(double ms) => ms switch
    {
        0 => "0",
        >= 1000 => (ms / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " s",
        >= 1 => ms.ToString("0.#", CultureInfo.InvariantCulture) + " ms",
        _ => ms.ToString("0.###", CultureInfo.InvariantCulture) + " ms",
    };

    // 글자 크기는 앱 자원 OrandaFontSizeSmall(없으면 11).
    private FormattedText Text(string s, IBrush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default,
            this.TryFindResource("OrandaFontSizeSmall", ActualThemeVariant, out var v) && v is double size ? size : 11, brush);

    private IBrush? Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : null;
}
