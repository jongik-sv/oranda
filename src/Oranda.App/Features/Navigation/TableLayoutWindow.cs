using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Oranda.App.Features.Localization;
using Oranda.App.Shell.Icons;

namespace Oranda.App.Features.Navigation;

/// <summary>
/// 테이블 레이아웃 창(Orange 의 Alt+C). 제목은 「소유자.표」, 위에 별칭 칸과 확인란 4개, 아래에 열 표.
/// 이름 칸(어느 칸이든 그 행)을 두 번 누르면 그 열을, 여러 행을 고르고 Enter 를 누르면 고른 순서대로 편집기에 넣는다. Esc 로 닫는다.
/// 비모달이라 창을 연 채 편집기를 계속 쓸 수 있다.
/// </summary>
public sealed class TableLayoutWindow : Window
{
    // 「형식」「NULL」은 literals-en.json 의 기존 번역(Format·Nullable)과 달라서 언어를 보고 직접 고른다.
    private static readonly string[] HeadersKo = ["", "이름", "형식", "NULL", "기본값", "주석"];
    private static readonly string[] HeadersEn = ["", "Name", "Type", "Nullable", "Default", "Comment"];
    private static TableLayoutWindow? s_open;

    private readonly TableLayoutSession _session;
    private readonly TextBox _alias = new() { MinWidth = 140, MinHeight = 0, Padding = new Thickness(4, 2), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly CheckBox _comma = Check("쉼표 넣기");
    private readonly CheckBox _lower = Check("소문자");
    private readonly CheckBox _pk = Check("PK 표시");
    private readonly CheckBox _comment = Check("주석 넣기");
    private readonly ListBox _list = new()
    {
        SelectionMode = SelectionMode.Multiple,
        Padding = new Thickness(0),
        BorderThickness = new Thickness(0),
    };

    public TableLayoutWindow(TableLayoutSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        Title = session.Model.Title;
        Width = 640;
        Height = 360;
        MinWidth = 360;
        MinHeight = 160;
        ShowInTaskbar = false;
        FontSize = 12;
        Content = BuildContent();

        var o = session.Options;
        _alias.Text = o.Alias;
        _comma.IsChecked = o.InsertComma;
        _lower.IsChecked = o.LowerCase;
        _pk.IsChecked = o.ShowPk;
        _comment.IsChecked = o.ShowComment;
        Rebuild();

        _alias.TextChanged += (_, _) => OptionsChanged();
        foreach (var c in new[] { _comma, _lower, _pk, _comment })
        {
            c.IsCheckedChanged += (_, _) => OptionsChanged();
        }

        _list.DoubleTapped += OnDoubleTapped;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => _list.Focus();
        Closed += (_, _) =>
        {
            if (ReferenceEquals(s_open, this))
            {
                s_open = null;
            }
        };
    }

    /// <summary>이미 떠 있는 창이 있으면 닫고 새로 연다. 편집기 View 가 없으면(시험) 아무것도 하지 않는다.</summary>
    public static void Show(TableLayoutSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Tab.Surface is not SqlEditor.SqlEditorView view || TopLevel.GetTopLevel(view) is not Window owner)
        {
            return;
        }

        s_open?.Close();
        var window = new TableLayoutWindow(session);
        var caret = view.TextEditorControl.TextArea.Caret.CalculateCaretRectangle();
        var at = view.TextEditorControl.TextArea.TextView.PointToScreen(new Point(caret.X, caret.Bottom + 4));
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = at;
        s_open = window;
        window.Show(owner);
    }

    /// <summary>열려 있는 창을 닫는다.</summary>
    public static void CloseOpen()
    {
        s_open?.Close();
        s_open = null;
    }

    private static CheckBox Check(string text) => new() { Content = text, Margin = new Thickness(0, 0, 14, 0), MinHeight = 0, Padding = new Thickness(4, 0, 0, 0) };

    private DockPanel BuildContent()
    {
        var top = new StackPanel { Margin = new Thickness(8, 6), Spacing = 4 };
        var aliasRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        aliasRow.Children.Add(new TextBlock { Text = "테이블 별칭", VerticalAlignment = VerticalAlignment.Center });
        aliasRow.Children.Add(_alias);
        top.Children.Add(aliasRow);
        top.Children.Add(new WrapPanel { Children = { _comma, _lower, _pk, _comment } });

        var header = ItemGrid((LiteralTranslator.Language == "en" ? HeadersEn : HeadersKo).Select(h => (Control)new TextBlock { Text = h, FontWeight = FontWeight.SemiBold }).ToList(), isHeader: true);
        var headerBorder = new Border { Classes = { "grid-header" }, Child = header };

        var body = new DockPanel();
        DockPanel.SetDock(headerBorder, Avalonia.Controls.Dock.Top);
        body.Children.Add(headerBorder);
        body.Children.Add(_list);
        Grid.SetIsSharedSizeScope(body, true);

        var root = new DockPanel();
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
        root.Children.Add(top);
        root.Children.Add(body);
        return root;
    }

    // 머리글과 행이 같은 SharedSizeGroup 을 써서 칸 너비가 맞는다(마지막 칸만 남는 너비를 차지).
    private static Grid ItemGrid(List<Control> cells, bool isHeader)
    {
        var grid = new Grid { Margin = new Thickness(0, isHeader ? 4 : 1) };
        for (var i = 0; i < cells.Count; i++)
        {
            var last = i == cells.Count - 1;
            grid.ColumnDefinitions.Add(new ColumnDefinition(last ? GridLength.Star : GridLength.Auto) { SharedSizeGroup = last ? null : "c" + i, MinWidth = last ? 80 : 0 });
            cells[i].Margin = new Thickness(8, 0, 12, 0);
            cells[i].VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(cells[i], i);
            grid.Children.Add(cells[i]);
        }

        return grid;
    }

    // Orange 표처럼 행마다 가는 가로줄을 둔다(구분선 색은 앱 공용 자원).
    private static Border Ruled(Control row)
    {
        var line = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = row };
        line.Bind(Border.BorderBrushProperty, line.GetResourceObservable("OrandaSeparatorBrush"));
        return line;
    }

    private void Rebuild()
    {
        var showPk = _session.Options.ShowPk;
        var items = new List<ListBoxItem>();
        foreach (var r in _session.Model.Rows)
        {
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            if (showPk && r.IsPk)
            {
                name.Children.Add((Control)ShellIcons.Create("key", 12)!);
            }

            name.Children.Add(new TextBlock { Text = r.Name });
            var cells = new List<Control>
            {
                new TextBlock { Text = r.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0.6 },
                name,
                new TextBlock { Text = r.TypeText },
                new TextBlock { Text = r.NotNull ? "Not Null" : string.Empty },
                new TextBlock { Text = r.DefaultValue ?? string.Empty, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = r.Comment ?? string.Empty, TextTrimming = TextTrimming.CharacterEllipsis },
            };
            items.Add(new ListBoxItem { Tag = r, Padding = new Thickness(0), MinHeight = 0, Content = Ruled(ItemGrid(cells, isHeader: false)) });
        }

        var selected = _list.SelectedItems?.OfType<ListBoxItem>().Select(i => i.Tag).ToHashSet() ?? [];
        _list.ItemsSource = items;
        foreach (var item in items.Where(i => selected.Contains(i.Tag)))
        {
            _list.SelectedItems?.Add(item);
        }
    }

    private void OptionsChanged()
    {
        var before = _session.Options.ShowPk;
        _session.SetOptions(new TableLayoutOptions(_alias.Text ?? string.Empty, _comma.IsChecked == true, _lower.IsChecked == true, _pk.IsChecked == true, _comment.IsChecked == true));
        if (before != _session.Options.ShowPk)
        {
            Rebuild();
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.Tag is TableLayoutRow row)
        {
            _session.Paste([row]);
            e.Handled = true;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key is Key.Enter && !_alias.IsFocused && _list.SelectedItems is { Count: > 0 } sel)
        {
            e.Handled = true;
            _session.Paste(sel.OfType<ListBoxItem>().Select(i => i.Tag).OfType<TableLayoutRow>().ToList());
        }
    }
}
