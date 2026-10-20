using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using AvaloniaEdit;
using Oranda.App.Shell.Appearance;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// 읽기 전용 SQL·PL/SQL 보기(객체 상세 DDL·DDL 추출 결과). 편집기와 같은 렉서 강조(<see cref="SqlSyntaxColorizer"/>)와 줄 번호를 쓰고,
/// 글자는 고를 수 있지만 고칠 수 없다. 화면에 붙어 있는 동안만 강조기를 건다.
/// </summary>
public sealed class SqlCodeView : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<SqlCodeView, string?>(nameof(Text));

    private readonly TextEditor _editor = new()
    {
        IsReadOnly = true,
        ShowLineNumbers = true,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
    };

    private SqlSyntaxColorizer? _colorizer;

    public SqlCodeView()
    {
        Styles.Add(new StyleInclude(new Uri("avares://Oranda.App/"))
        {
            Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"),
        });
        // 글꼴·크기는 설정(도구 → 설정, 보기 → 확대·축소)을 따른다(ThemeService 가 앱 리소스로 내보낸다).
        _editor.Bind(TextEditor.FontFamilyProperty, _editor.GetResourceObservable(ThemeService.EditorFontFamilyKey));
        _editor.Bind(TextEditor.FontSizeProperty, _editor.GetResourceObservable(ThemeService.EditorFontSizeKey));
        Content = _editor;
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>안의 편집기(시험·복사용).</summary>
    public TextEditor TextEditorControl => _editor;

    /// <summary>지금 걸린 강조기(화면 밖이면 null).</summary>
    public SqlSyntaxColorizer? Colorizer => _colorizer;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _colorizer ??= new SqlSyntaxColorizer(_editor);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _colorizer?.Detach();
        _colorizer = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            _editor.Document.Text = Text ?? string.Empty;
            _editor.ScrollToHome();
        }
    }
}
