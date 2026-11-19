using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.App.Shell.Icons;
using Path = Avalonia.Controls.Shapes.Path;

namespace Oranda.App.Features.SchemaBrowser;

/// <summary>뱃지 모양의 경로(8×8 격자). 작게 그려도 모양으로 갈리게 단순한 도형만 쓴다.</summary>
public static class SchemaBadgeGeometry
{
    private static readonly Dictionary<BadgeShape, Geometry> Cache = Build();

    public static Geometry Of(BadgeShape shape) => Cache[shape];

    private static Dictionary<BadgeShape, Geometry> Build() => new()
    {
        [BadgeShape.Dot] = Geometry.Parse("M4 0.5A3.5 3.5 0 1 1 4 7.5A3.5 3.5 0 1 1 4 0.5Z"),
        [BadgeShape.Ring] = Geometry.Parse("F1 M4 0.5A3.5 3.5 0 1 1 4 7.5A3.5 3.5 0 1 1 4 0.5Z M4 2.4A1.6 1.6 0 1 0 4 5.6A1.6 1.6 0 1 0 4 2.4Z"),
        [BadgeShape.Square] = Geometry.Parse("M1 1H7V7H1Z"),
        [BadgeShape.Diamond] = Geometry.Parse("M4 0L8 4L4 8L0 4Z"),
        [BadgeShape.Bars] = Geometry.Parse("M0.5 1H7.5V3.2H0.5Z M0.5 4.8H7.5V7H0.5Z"),
        [BadgeShape.Bar] = Geometry.Parse("M0.5 2.8H7.5V5.2H0.5Z"),
        [BadgeShape.Plus] = Geometry.Parse("M2.9 0.5H5.1V2.9H7.5V5.1H5.1V7.5H2.9V5.1H0.5V2.9H2.9Z"),
        [BadgeShape.Cross] = Geometry.Parse("M1.4 0L4 2.6L6.6 0L8 1.4L5.4 4L8 6.6L6.6 8L4 5.4L1.4 8L0 6.6L2.6 4L0 1.4Z"),
        [BadgeShape.Play] = Geometry.Parse("M1.2 0.3L7.6 4L1.2 7.7Z"),
        [BadgeShape.Warning] = Geometry.Parse("M4 0.2L7.9 7.6H0.1Z"),
    };
}

/// <summary>
/// <see cref="SchemaLook"/> → 아이콘 컨트롤: 기본 아이콘(<see cref="ShellIcons"/>, 16 DIP) 위 모서리에 7 DIP 뱃지를 얹는다. 뱃지 색은 아이콘 색 토큰
/// (<c>OrandaIcon&lt;Tone&gt;Brush</c>, 밝게·어둡게 두 벌), 테두리는 <c>OrandaBadgeOutlineBrush</c>(바탕색)로 둘러 아이콘과 떨어져 보인다. 꺼진 것은 아이콘을 흐리게.
/// </summary>
public sealed class SchemaLookConverter : IValueConverter
{
    public static readonly SchemaLookConverter Instance = new();

    /// <summary>뱃지 한 변(DIP).</summary>
    public const double BadgeSize = 7;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SchemaLook look ? Create(look) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    /// <summary>아이콘과 뱃지를 겹친 컨트롤. 뱃지가 없고 흐리지 않으면 기본 아이콘 그대로.</summary>
    public static Control? Create(SchemaLook look)
    {
        ArgumentNullException.ThrowIfNull(look);
        var icon = ShellIcons.Create(look.IconKey);
        if (look.Badges.Count == 0 && !look.Dimmed)
        {
            return icon;
        }

        var panel = new Panel { Width = 16, Height = 16, Classes = { "schema-look" } };
        if (icon is not null)
        {
            icon.Opacity = look.Dimmed ? 0.4 : 1;
            panel.Children.Add(icon);
        }

        foreach (var badge in look.Badges)
        {
            var path = new Path
            {
                Data = SchemaBadgeGeometry.Of(badge.Shape),
                Width = BadgeSize,
                Height = BadgeSize,
                Stretch = Stretch.Uniform,
                StrokeThickness = 0.8,
                HorizontalAlignment = badge.Corner is BadgeCorner.TopLeft or BadgeCorner.BottomLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                VerticalAlignment = badge.Corner is BadgeCorner.TopLeft or BadgeCorner.TopRight ? VerticalAlignment.Top : VerticalAlignment.Bottom,
                Margin = new Thickness(-1),
                Classes = { "schema-badge" },
                Tag = badge,
            };
            path.Bind(Shape.FillProperty, path.GetResourceObservable("OrandaIcon" + badge.Tone + "Brush"));
            path.Bind(Shape.StrokeProperty, path.GetResourceObservable("OrandaBadgeOutlineBrush"));
            panel.Children.Add(path);
        }

        return panel;
    }
}
