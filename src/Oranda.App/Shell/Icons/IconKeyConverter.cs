using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Oranda.App.Shell.Icons;

/// <summary>XAML 에서 아이콘 키(문자열)를 아이콘 컨트롤로 바꾼다. 매개변수는 크기(DIP, 기본 16).</summary>
public sealed class IconKeyConverter : IValueConverter
{
    public static readonly IconKeyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var size = parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 16;
        return ShellIcons.Create(value as string, size);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
