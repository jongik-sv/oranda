using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using Oranda.App.Features.Localization;
using Oranda.App.Features.MyBatisWorkspace.Save;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// 제안 편집 비교 판. 탭 둘: 「바뀐 부분 비교」(왼쪽 기존·오른쪽 변경을 나란히 보이는 줄 diff, 먼저 보임)와 「현재 / 제안」(글 전체 두 칸, 제안 칸은 고칠 수 있음).
/// 제안 칸을 고치면 「바뀐 부분 비교」 탭으로 올 때 diff 를 다시 계산한다.
/// 크기는 글 길이로만 정한 값에 고정한다: 스크롤하면서 새로 그려지는 줄 때문에 내용 크기가 달라져도 창이 따라 흔들리지 않게 한다.
/// 사용자가 창 크기를 직접 바꾸면 고정을 풀어 판이 창을 채운다.
/// </summary>
internal sealed class PreviewEditPanel : UserControl
{
    // 글 길이로 크기를 어림하는 값(DIP). 편집기 글꼴은 설정을 따르므로 정확한 값이 아니라 넉넉한 어림이다.
    private const double CharWidth = 8;
    private const double LineHeight = 19;
    private const double GutterWidth = 72; // 줄 번호 칸 + 부호 칸 + 여백
    private const double ChromeHeight = 130; // 탭 머리 + 바뀐 부분 머리글 + 여백

    private readonly string _before;
    private readonly TabControl _tabs = new();
    private readonly TabItem _diffTab;
    private readonly TextBlock _changes;
    private readonly MapperDiffView _diff = new();
    private readonly SqlCodeView _proposed;
    private string? _diffFor;
    private Window? _window;

    /// <param name="labels">「현재 / 제안」 탭의 두 칸 머리글. null 이면 「현재」「제안」.</param>
    /// <param name="readOnly">보기만 하는 판(이미 적용한 제안 다시 보기): 제안 칸도 고칠 수 없다.</param>
    public PreviewEditPanel(string before, string after, Size limit, (string Before, string After)? labels = null, bool readOnly = false)
    {
        _before = before ?? string.Empty;
        after ??= string.Empty;
        MinWidth = 640;
        MinHeight = 240;
        MaxWidth = limit.Width;
        MaxHeight = limit.Height;
        var size = PreferredSize(_before, after, limit);
        Width = size.Width;
        Height = size.Height;

        _changes = new TextBlock { FontWeight = FontWeight.SemiBold };
        // diff 목록(ItemsControl)에는 스크롤이 없다: 바뀐 줄이 많아도 칸 안에서 넘기도록 감싼다.
        var scroll = new ScrollViewer
        {
            Content = _diff,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 2, 0, 0),
        };
        Grid.SetRow(scroll, 1);
        var diffPage = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { _changes, scroll } };

        var current = new SqlCodeView { Text = _before };
        _proposed = new SqlCodeView { Text = after };
        _proposed.TextEditorControl.IsReadOnly = readOnly;
        _proposed.TextEditorControl.Document.UndoStack.ClearAll(); // 처음 글을 넣은 일이 되돌리기에 남으면 Ctrl+Z 로 칸이 비어 버린다
        var textPage = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,*") };
        textPage.Children.Add(Label(labels?.Before ?? UiText.Get("Shell.dialog.current", "현재"), 0));
        textPage.Children.Add(Label(labels?.After ?? UiText.Get("Shell.dialog.proposed", "제안"), 1));
        Grid.SetRow(current, 1);
        Grid.SetRow(_proposed, 1);
        Grid.SetColumn(_proposed, 1);
        textPage.Children.Add(current);
        textPage.Children.Add(_proposed);

        _diffTab = new TabItem { Header = UiText.Get("Shell.dialog.diffTab", "바뀐 부분 비교"), Content = diffPage };
        var textTab = new TabItem { Header = UiText.Get("Shell.dialog.textTab", "현재 / 제안"), Content = textPage };
        _tabs.Items.Add(_diffTab);
        _tabs.Items.Add(textTab);
        _tabs.SelectedIndex = 0;
        _tabs.SelectionChanged += (_, _) => RefreshDiff();
        Content = _tabs;
        RefreshDiff();
    }

    /// <summary>사용자가 고친 것까지 반영한 제안 글.</summary>
    public string ProposedText => _proposed.TextEditorControl.Text;

    internal TabControl Tabs => _tabs;

    internal TabItem DiffTab => _diffTab;

    internal MapperDiffView DiffView => _diff;

    internal SqlCodeView ProposedView => _proposed;

    /// <summary>글 길이로 정한 판 크기(상한 <paramref name="limit"/> 안, 하한 640×240). 같은 글이면 늘 같은 값이다.</summary>
    internal static Size PreferredSize(string before, string after, Size limit)
    {
        var lines = Math.Max(CountLines(before), CountLines(after));
        var longest = Math.Max(LongestLine(before), LongestLine(after));
        var width = (2 * ((longest * CharWidth) + GutterWidth)) + 16;
        var height = (lines * LineHeight) + ChromeHeight;
        return new Size(Math.Clamp(width, 640, Math.Max(640, limit.Width)), Math.Clamp(height, 240, Math.Max(240, limit.Height)));
    }

    /// <summary>
    /// 고정한 크기를 푼다: 판이 창 크기를 따라 늘고 준다. 상한(<see cref="Layoutable.MaxWidth"/>·<see cref="Layoutable.MaxHeight"/>)도 처음 크기를 정할 때만 쓰므로 함께 푼다.
    /// 창의 SizeToContent 는 Manual 로 돌려 한 축만 끌었을 때 다른 축이 내용 크기로 튀지 않게 한다.
    /// </summary>
    internal void ReleaseFixedSize()
    {
        if (_window is not null)
        {
            _window.SizeToContent = SizeToContent.Manual;
        }

        ClearValue(WidthProperty);
        ClearValue(HeightProperty);
        ClearValue(MaxWidthProperty);
        ClearValue(MaxHeightProperty);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
        {
            _window.Resized += OnWindowResized;
            _window.PropertyChanged += OnWindowPropertyChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null)
        {
            _window.Resized -= OnWindowResized;
            _window.PropertyChanged -= OnWindowPropertyChanged;
            _window = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private static TextBlock Label(string text, int column)
    {
        var label = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        Grid.SetColumn(label, column);
        return label;
    }

    private void OnWindowResized(object? sender, WindowResizedEventArgs e)
    {
        if (e.Reason == WindowResizeReason.User)
        {
            ReleaseFixedSize();
        }
    }

    // 최대화·전체 화면은 사용자 리사이즈로 알려지지 않을 수 있어 창 상태로도 푼다.
    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty && e.NewValue is WindowState.Maximized or WindowState.FullScreen)
        {
            ReleaseFixedSize();
        }
    }

    // 「바뀐 부분 비교」 탭을 보일 때, 제안 글이 지난번 계산과 달라졌으면 다시 계산한다.
    private void RefreshDiff()
    {
        if (!ReferenceEquals(_tabs.SelectedItem, _diffTab))
        {
            return;
        }

        var after = ProposedText;
        if (_diffFor == after)
        {
            return;
        }

        _diffFor = after;
        var lines = MapperLineDiff.Build(_before, after, 1, 1);
        var (added, removed) = MapperLineDiff.Count(lines);
        _changes.Text = string.Create(CultureInfo.InvariantCulture, $"{UiText.Get("Shell.dialog.changes", "바뀐 부분")}  +{added} −{removed}");
        _diff.Rows = MapperDiffRow.Section(null, lines, sideBySide: true);
    }

    private static int CountLines(string text)
    {
        var count = 1;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static int LongestLine(string text)
    {
        int longest = 0, run = 0;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                run = 0;
                continue;
            }

            run++;
            longest = Math.Max(longest, run);
        }

        return longest;
    }
}
