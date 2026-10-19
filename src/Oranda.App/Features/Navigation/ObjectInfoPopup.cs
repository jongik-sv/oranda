using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Features.Navigation;

/// <summary>객체 정보·참조 목록 팝업. 커서 아래에 뜨고 바깥을 누르거나 Esc 로 닫힌다. 편집기 View 가 없으면(시험) 아무것도 하지 않는다.</summary>
public static class ObjectInfoPopup
{
    private static Popup? s_open;

    public static void Show(SqlEditorViewModel tab, ObjectInfo info)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(info);
        if (tab.Surface is not SqlEditorView view)
        {
            return;
        }

        Close();
        var area = view.TextEditorControl.TextArea;
        var caret = area.Caret.CalculateCaretRectangle();
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = info.Title, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = string.Join(Environment.NewLine, info.Lines),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 12,
        });

        var popup = new Popup
        {
            PlacementTarget = area.TextView,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.BottomLeft,
            PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight,
            PlacementRect = new Rect(caret.X, caret.Y, Math.Max(1, caret.Width), Math.Max(1, caret.Height)),
            IsLightDismissEnabled = true,
            Child = new Border
            {
                Padding = new Thickness(8),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Gray,
                Background = new SolidColorBrush(Color.FromRgb(0x2b, 0x2b, 0x2b)),
                MaxWidth = 720,
                MaxHeight = 420,
                Child = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto },
            },
        };
        if (view.Content is not Panel host)
        {
            return;
        }

        host.Children.Add(popup);
        popup.Closed += (_, _) => host.Children.Remove(popup);
        s_open = popup;
        popup.IsOpen = true;
    }

    public static void Close()
    {
        if (s_open is { IsOpen: true })
        {
            s_open.IsOpen = false;
        }

        s_open = null;
    }
}
