using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Converters;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace Oranda.App.Shell.Keymaps;

/// <summary>
/// 창 안 메뉴(<see cref="MenuItem"/>)가 단축키 글을 그릴 때 쓰는 변환기. Avalonia 기본(<see cref="PlatformKeyGestureConverter"/>)과 같되
/// <see cref="Key.Cancel"/> 만 「Break」 로 그린다(Windows 는 Ctrl+Break 를 VK_CANCEL 로 보내 「Ctrl+Cancel」 로 보였다). 키 자체는 바꾸지 않는다.
/// </summary>
public sealed class ShortcutGestureConverter : IValueConverter
{
    private const string GestureTextPart = "PART_InputGestureText";

    private static readonly PlatformKeyGestureConverter Platform = new();

    private static readonly Dictionary<Key, string> BreakName = new() { [Key.Cancel] = "Break" };

    private static bool _installed;

    public static ShortcutGestureConverter Instance { get; } = new();

    /// <summary>
    /// 모든 <see cref="MenuItem"/> 의 단축키 글(Fluent 템플릿의 <c>PART_InputGestureText</c>)을 이 변환기로 다시 묶는다.
    /// 템플릿 안 바인딩은 스타일보다 앞서므로 템플릿이 붙을 때 지역 값으로 묶는다. 앱 시작 때 한 번 부른다.
    /// </summary>
    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<MenuItem>(static (item, e) =>
        {
            if (e.NameScope.Find<TextBlock>(GestureTextPart) is { } text)
            {
                text.Bind(TextBlock.TextProperty, new Binding(nameof(MenuItem.InputGesture)) { Source = item, Converter = Instance });
            }
        });
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is KeyGesture { Key: Key.Cancel } gesture)
        {
            var platform = KeyGestureFormatInfo.GetInstance(null);
            return gesture.ToString("p", new KeyGestureFormatInfo(BreakName, platform.Meta, platform.Ctrl, platform.Alt, platform.Shift));
        }

        return Platform.Convert(value, targetType, parameter, culture);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
