using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace Oranda.App.Shell.Menus;

/// <summary>
/// 툴바 나눔 단추: 아이콘 부분은 주 동작(명령)을 실행하고 오른쪽 ▾ 부분이나 아래 화살표 키는 자식 메뉴를 연다.
/// <see cref="Button"/> 이라 기존 툴바 스타일과 넘침 「…」 메뉴(주 동작만)가 그대로 맞는다.
/// </summary>
public sealed class ToolbarSplitButton : Button
{
    /// <summary>▾ 부분의 너비(오른쪽 안쪽 여백 안). 이 안을 누르면 주 동작 대신 자식 메뉴가 열린다.</summary>
    private const double ChevronHitWidth = 12;

    private bool _chevronHit;

    public ToolbarSplitButton(object? face, MenuFlyout menu)
    {
        Menu = menu;
        var chevron = new Path
        {
            Data = Geometry.Parse("M1 1.5 L4 4.5 L7 1.5"),
            StrokeThickness = 1.2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Width = 8,
            Height = 6,
            Margin = new Thickness(3, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        chevron.Bind(Shape.StrokeProperty, chevron.GetResourceObservable("OrandaIconNeutralBrush"));
        Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { face is Control c ? c : new TextBlock { Text = face?.ToString() }, chevron } };
    }

    public MenuFlyout Menu { get; }

    /// <summary>넘침 「…」 메뉴에 주 동작 뒤로 이어 실을 자식 줄을 새로 만든다(컨트롤은 한 부모만 갖는다).</summary>
    public Func<IReadOnlyList<object>>? OverflowItems { get; init; }

    protected override Type StyleKeyOverride => typeof(Button);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _chevronHit = e.GetPosition(this).X >= Bounds.Width - Padding.Right - ChevronHitWidth;
        base.OnPointerPressed(e);
    }

    protected override void OnClick()
    {
        var chevron = _chevronHit;
        _chevronHit = false;
        if (chevron)
        {
            Menu.ShowAt(this);
            return;
        }

        base.OnClick();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e is { Key: Key.Down, Handled: false })
        {
            Menu.ShowAt(this);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
