using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Appearance;

namespace Oranda.App.Features.Navigation;

/// <summary>
/// 표·뷰가 아닌 객체 정보·참조 목록 창(<see cref="TableLayoutWindow"/> 과 같은 비모달 작은 창 틀). 글은 편집기 글꼴(고정폭)로 보이고
/// 가로·세로로 스크롤하며 선택·복사할 수 있다. 테마 색을 따르고 Esc 로 닫힌다. 편집기 View 가 없으면(시험) 아무것도 하지 않는다.
/// </summary>
public static class ObjectInfoPopup
{
    private static Window? s_open;

    public static void Show(SqlEditorViewModel tab, ObjectInfo info)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(info);
        if (tab.Surface is not SqlEditorView view || TopLevel.GetTopLevel(view) is not Window owner)
        {
            return;
        }

        Close();
        var editor = view.TextEditorControl;
        var text = new SelectableTextBlock
        {
            Text = string.Join(Environment.NewLine, info.Lines),
            FontFamily = editor.FontFamily,
            FontSize = editor.FontSize,
            Margin = new Thickness(8, 6),
        };
        text.Bind(TextBlock.FontFamilyProperty, text.GetResourceObservable(ThemeService.EditorFontFamilyKey)); // 편집기와 같은 글꼴 설정(고정폭)
        var window = new Window
        {
            Title = info.Title,
            Width = 560,
            Height = 320,
            MinWidth = 280,
            MinHeight = 120,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = text,
            },
        };
        window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                window.Close();
            }
        }, RoutingStrategies.Tunnel);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(s_open, window))
            {
                s_open = null;
            }
        };

        var caret = editor.TextArea.Caret.CalculateCaretRectangle();
        window.Position = editor.TextArea.TextView.PointToScreen(new Point(caret.X, caret.Bottom + 4));
        s_open = window;
        window.Show(owner);
    }

    public static void Close()
    {
        s_open?.Close();
        s_open = null;
    }
}
