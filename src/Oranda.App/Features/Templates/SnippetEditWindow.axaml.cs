using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using AvaloniaEdit;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Appearance;

namespace Oranda.App.Features.Templates;

/// <summary>
/// 사용자 조각 편집 대화상자. 본문 칸은 SQL 편집기와 같은 렉서 강조(<see cref="SqlSyntaxColorizer"/>)와 편집기 글꼴 설정을 쓴다.
/// 저장은 검사(<see cref="SnippetEditViewModel.Validate"/>)를 통과해야 닫히고 true 를 돌려준다.
/// </summary>
public partial class SnippetEditWindow : Window
{
    private readonly TextEditor _body = new()
    {
        ShowLineNumbers = true,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
    };

    private SqlSyntaxColorizer? _colorizer;

    public SnippetEditWindow()
    {
        InitializeComponent();
        Styles.Add(new StyleInclude(new Uri("avares://Oranda.App/"))
        {
            Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"),
        });
        _body.Bind(TextEditor.FontFamilyProperty, _body.GetResourceObservable(ThemeService.EditorFontFamilyKey));
        _body.Bind(TextEditor.FontSizeProperty, _body.GetResourceObservable(ThemeService.EditorFontSizeKey));
        Avalonia.Automation.AutomationProperties.SetName(_body, "본문");
        BodyHost.Content = _body;

        // 강조기는 편집기가 화면에 붙어 있는 동안만 건다(SqlCodeView 와 같다).
        _body.AttachedToVisualTree += (_, _) => _colorizer ??= new SqlSyntaxColorizer(_body);
        _body.DetachedFromVisualTree += (_, _) =>
        {
            _colorizer?.Detach();
            _colorizer = null;
        };

        _body.TextChanged += (_, _) =>
        {
            if (DataContext is SnippetEditViewModel vm && vm.Body != _body.Text)
            {
                vm.Body = _body.Text;
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SnippetEditViewModel vm)
            {
                _body.Document.Text = vm.Body;
            }
        };
        SaveButton.Click += (_, _) => Save();
        CancelButton.Click += (_, _) => Close(false);
        Opened += (_, _) =>
        {
            PrefixBox.Focus();
            PrefixBox.SelectAll();
        };
    }

    /// <summary>본문 편집기(시험용).</summary>
    public TextEditor BodyEditor => _body;

    /// <summary>지금 걸린 강조기(창이 닫혀 있으면 null).</summary>
    public SqlSyntaxColorizer? Colorizer => _colorizer;

    /// <summary>검사를 통과하면 true 로 닫는다. 아니면 오류를 보이고 연 채로 둔다.</summary>
    public bool Save()
    {
        if (DataContext is not SnippetEditViewModel vm || !vm.Validate())
        {
            return false;
        }

        Close(true);
        return true;
    }
}
