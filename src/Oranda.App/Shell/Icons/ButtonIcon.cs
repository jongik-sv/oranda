using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Oranda.App.Shell.Icons;

/// <summary>
/// 단추에 아이콘을 붙이는 붙임 속성. XAML 에서 <c>icons:ButtonIcon.Key="eraser"</c> 만 적으면 글자 앞에 아이콘이 들어간다.
/// <c>icons:ButtonIcon.IconOnly="True"</c> 면 아이콘만 그리고 글자는 툴팁·접근성 이름으로 옮긴다(Orange 의 Sub Toolbar).
/// 글자 Content 는 화면에 붙을 때 한 번 읽는다(속성 적는 순서와 상관없게).
/// </summary>
public static class ButtonIcon
{
    public static readonly AttachedProperty<string?> KeyProperty =
        AvaloniaProperty.RegisterAttached<ContentControl, string?>("Key", typeof(ButtonIcon));

    public static readonly AttachedProperty<bool> IconOnlyProperty =
        AvaloniaProperty.RegisterAttached<ContentControl, bool>("IconOnly", typeof(ButtonIcon));

    static ButtonIcon()
    {
        KeyProperty.Changed.AddClassHandler<ContentControl>((c, _) =>
        {
            c.AttachedToVisualTree -= OnAttached;
            c.AttachedToVisualTree += OnAttached;
            if (TopLevel.GetTopLevel(c) is not null)
            {
                Apply(c);
            }
        });
    }

    public static string? GetKey(ContentControl control) => control.GetValue(KeyProperty);

    public static void SetKey(ContentControl control, string? value) => control.SetValue(KeyProperty, value);

    public static bool GetIconOnly(ContentControl control) => control.GetValue(IconOnlyProperty);

    public static void SetIconOnly(ContentControl control, bool value) => control.SetValue(IconOnlyProperty, value);

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is ContentControl c)
        {
            Apply(c);
        }
    }

    private static void Apply(ContentControl c)
    {
        var label = c.Content switch
        {
            string s => s,
            Panel { Tag: "button-icon" } p when p.Children.Count > 1 && p.Children[1] is TextBlock t => t.Text,
            _ => null,
        };
        var icon = ShellIcons.Create(GetKey(c));
        if (icon is null || (label is null && c.Content is not null))
        {
            return; // 글자가 아닌 내용(바인딩된 컨트롤 등)은 건드리지 않는다.
        }

        if (GetIconOnly(c))
        {
            c.Content = icon;
            if (label is not null)
            {
                if (ToolTip.GetTip(c) is null)
                {
                    ToolTip.SetTip(c, label);
                }

                if (string.IsNullOrEmpty(AutomationProperties.GetName(c)))
                {
                    AutomationProperties.SetName(c, label);
                }
            }

            return;
        }

        c.Content = new StackPanel
        {
            Tag = "button-icon",
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Children =
            {
                icon,
                new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        if (label is not null && string.IsNullOrEmpty(AutomationProperties.GetName(c)))
        {
            AutomationProperties.SetName(c, label);
        }
    }
}
