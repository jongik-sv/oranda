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

/// <summary>모서리 표식 도형(<see cref="SchemaLookConverter.MarkSize"/> 격자에 바로 그린다). 채운 원·사각·마름모·세모만: 작게 그려도 뭉개지지 않는다.</summary>
public static class SchemaMarkGeometry
{
    private static readonly Dictionary<BadgeShape, Geometry> Cache = new()
    {
        [BadgeShape.Dot] = Geometry.Parse("M4.5 0.6A3.9 3.9 0 1 1 4.5 8.4A3.9 3.9 0 1 1 4.5 0.6Z"),
        [BadgeShape.Square] = Geometry.Parse("M1.1 1.1H7.9V7.9H1.1Z"),
        [BadgeShape.Diamond] = Geometry.Parse("M4.5 0.2L8.8 4.5L4.5 8.8L0.2 4.5Z"),
        [BadgeShape.Warning] = Geometry.Parse("M4.5 0.4L8.8 8.4H0.2Z"),
    };

    public static Geometry Of(BadgeShape shape) => Cache[shape];
}

/// <summary>
/// <see cref="SchemaLook"/> → 줄 앞 아이콘(<see cref="ShellIcons"/>, 16 DIP). 세부 종류가 있으면 아이콘을 주 종류 색(<see cref="SchemaLook.IconTone"/>, 글자 표식과 같은 토큰)으로 칠하고
/// 오른쪽 아래에 주 종류 모양(<see cref="BadgeShape"/>), 쓸 수 없으면 왼쪽 아래에 주황 세모를 <see cref="MarkSize"/> DIP 로 얹는다. 표식 테두리는
/// <c>OrandaBadgeOutlineBrush</c>(바탕색)라 아이콘과 떨어져 보인다. 표식은 16 DIP 칸 밖으로 1 DIP 삐져 나올 뿐 칸 크기는 그대로라 줄 높이가 바뀌지 않는다.
/// 꺼진 것은 아이콘 기본 색 그대로 0.4 로 흐리게. 일반 객체는 기본 아이콘 그대로.
/// </summary>
public sealed class SchemaLookConverter : IValueConverter
{
    public static readonly SchemaLookConverter Instance = new();

    /// <summary>아이콘 크기(DIP).</summary>
    public const double IconSize = 16;

    /// <summary>모서리 표식 한 변(DIP).</summary>
    public const double MarkSize = 9;

    /// <summary>모서리 표식 테두리 굵기(DIP, 바탕색).</summary>
    public const double MarkOutline = 1.5;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SchemaLook look ? Create(look) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    /// <summary>아이콘(+ 모서리 표식). 표식이 없고 흐리지 않으면 기본 아이콘 그대로.</summary>
    public static Control? Create(SchemaLook look)
    {
        ArgumentNullException.ThrowIfNull(look);
        var icon = ShellIcons.Create(look.IconKey, IconSize, look.IconTone);
        var kind = look.KindBadge;
        var warning = look.WarningBadge;
        if (kind is null && warning is null)
        {
            if (icon is not null && look.Dimmed)
            {
                icon.Opacity = 0.4;
            }

            return icon;
        }

        var panel = new Panel { Width = IconSize, Height = IconSize, Classes = { "schema-look" } };
        if (icon is not null)
        {
            panel.Children.Add(icon);
        }

        if (kind is not null)
        {
            panel.Children.Add(Mark(kind, HorizontalAlignment.Right));
        }

        if (warning is not null)
        {
            panel.Children.Add(Mark(warning, HorizontalAlignment.Left));
        }

        if (look.Dimmed)
        {
            panel.Opacity = 0.4;
        }

        return panel;
    }

    private static Path Mark(SchemaBadge badge, HorizontalAlignment side)
    {
        var path = new Path
        {
            Data = SchemaMarkGeometry.Of(badge.Shape),
            Width = MarkSize,
            Height = MarkSize,
            Stretch = Stretch.None,
            StrokeThickness = MarkOutline,
            StrokeJoin = PenLineJoin.Round,
            HorizontalAlignment = side,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(-1),
            Classes = { "schema-mark" },
            Tag = badge,
        };
        path.Bind(Shape.FillProperty, path.GetResourceObservable("OrandaIcon" + badge.Tone + "Brush"));
        path.Bind(Shape.StrokeProperty, path.GetResourceObservable("OrandaBadgeOutlineBrush"));
        return path;
    }
}

/// <summary>
/// <see cref="SchemaLook.Badges"/> → 이름 오른쪽 알약 모양 글자 표식 줄(<c>GTT·T</c>·<c>PART</c>·<c>IOT</c> …). 아이콘 색·모서리 표식(<see cref="SchemaLookConverter"/>)과
/// 같은 색 토큰을 써서 짝이 맞는다: 글자는 범례 없이 읽히고, 색(아이콘 색 토큰 <c>OrandaIcon&lt;Tone&gt;Brush</c>, 밝게·어둡게 두 벌)은 글자와 1 DIP 테두리에 쓴다.
/// 쓸 수 없음 표식(<see cref="SchemaBadge.IsWarning"/>)은 바탕을 그 색으로 채우고 글자는 바탕색(<c>OrandaBadgeOutlineBrush</c>)으로 뒤집어 더 눈에 띈다.
/// 높이 <see cref="PillHeight"/> 는 아이콘(16)보다 작아 줄 높이를 바꾸지 않는다. 표식이 없으면 null.
/// </summary>
public sealed class SchemaBadgesConverter : IValueConverter
{
    public static readonly SchemaBadgesConverter Instance = new();

    /// <summary>글자 표식 높이(DIP).</summary>
    public const double PillHeight = 14;

    /// <summary>글자 크기(DIP).</summary>
    public const double PillFontSize = 10;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SchemaLook look ? Create(look) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    public static Control? Create(SchemaLook look)
    {
        ArgumentNullException.ThrowIfNull(look);
        if (look.Badges.Count == 0)
        {
            return null;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Classes = { "schema-badges" } };
        foreach (var badge in look.Badges)
        {
            row.Children.Add(Pill(badge));
        }

        return row;
    }

    private static Border Pill(SchemaBadge badge)
    {
        var tone = "OrandaIcon" + badge.Tone + "Brush";
        var text = new TextBlock
        {
            Text = badge.Label,
            FontSize = PillFontSize,
            FontWeight = FontWeight.SemiBold,
            LineHeight = PillHeight - 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var pill = new Border
        {
            Height = PillHeight,
            MinWidth = PillHeight,
            Padding = new Thickness(4, 0),
            CornerRadius = new CornerRadius(PillHeight / 2),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = text,
            Classes = { "schema-badge" },
            Tag = badge,
        };
        ToolTip.SetTip(pill, badge.Tip);
        pill.Bind(Border.BorderBrushProperty, pill.GetResourceObservable(tone));
        if (badge.IsWarning)
        {
            pill.Bind(Border.BackgroundProperty, pill.GetResourceObservable(tone));
            text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("OrandaBadgeOutlineBrush"));
        }
        else
        {
            text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable(tone));
        }

        return pill;
    }
}
