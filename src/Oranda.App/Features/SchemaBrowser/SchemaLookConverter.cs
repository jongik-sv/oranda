using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.App.Shell.Icons;

namespace Oranda.App.Features.SchemaBrowser;

/// <summary><see cref="SchemaLook"/> → 줄 앞 아이콘(<see cref="ShellIcons"/>, 16 DIP). 꺼진 것은 흐리게. 표식은 이름 오른쪽 글자(<see cref="SchemaBadgesConverter"/>)가 맡는다.</summary>
public sealed class SchemaLookConverter : IValueConverter
{
    public static readonly SchemaLookConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SchemaLook look ? Create(look) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    /// <summary>기본 아이콘. 꺼진 것은 0.4 로 흐리게.</summary>
    public static Control? Create(SchemaLook look)
    {
        ArgumentNullException.ThrowIfNull(look);
        var icon = ShellIcons.Create(look.IconKey);
        if (icon is not null && look.Dimmed)
        {
            icon.Opacity = 0.4;
        }

        return icon;
    }
}

/// <summary>
/// <see cref="SchemaLook.Badges"/> → 이름 오른쪽 알약 모양 글자 표식 줄(<c>GTT·T</c>·<c>PART</c>·<c>IOT</c> …). 아이콘 모서리에 7 DIP 도형을 겹치던 것은
/// 작아서 무엇인지 알아보기 어려웠다: 글자는 범례 없이 읽히고, 색(아이콘 색 토큰 <c>OrandaIcon&lt;Tone&gt;Brush</c>, 밝게·어둡게 두 벌)은 글자와 1 DIP 테두리에 쓴다.
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
