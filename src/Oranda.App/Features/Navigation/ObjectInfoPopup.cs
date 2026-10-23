using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.App.Features.Localization;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Appearance;
using Oranda.App.Shell.Icons;
using Oranda.App.Styles;

namespace Oranda.App.Features.Navigation;

/// <summary>
/// 표·뷰가 아닌 객체 정보·참조 목록 창(<see cref="TableLayoutWindow"/> 과 같은 비모달 작은 창, 같은 도구 막대·상태 줄). 글은 편집기 글꼴(고정폭)로 보이고
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
        };
        window.Content = Frame(info, text, window);
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

    // 테이블 레이아웃 창과 같은 틀: 도구 막대(복사) / 본문 / 상태 줄(객체 이름 | 키 힌트).
    internal static DockPanel Frame(ObjectInfo info, SelectableTextBlock text, Window window)
    {
        var copy = new Button { Classes = { "toolbar" }, Content = "복사", Focusable = false };
        ButtonIcon.SetKey(copy, "copy");
        ButtonIcon.SetIconOnly(copy, true);
        copy.Click += async (_, _) =>
        {
            try
            {
                if (window.Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(text.Text ?? string.Empty).ConfigureAwait(true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
            {
                // 다른 프로그램이 클립보드를 쥐고 있으면(Windows) 이번 복사만 건너뛴다.
            }
        };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Children = { copy } };
        var hint = new TextBlock { Text = LiteralTranslator.IsEnglish ? "Esc = close" : "Esc = 닫기" };
        var title = new TextBlock { Text = info.Title, TextTrimming = TextTrimming.CharacterEllipsis };
        return GridC.Frame(toolbar, new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = text,
        }, title, hint);
    }

    public static void Close()
    {
        s_open?.Close();
        s_open = null;
    }
}
