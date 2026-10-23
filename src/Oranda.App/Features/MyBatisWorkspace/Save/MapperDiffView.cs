using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.App.Features.Localization;
using Oranda.App.Shell.Appearance;
using Oranda.App.Shell.Icons;

namespace Oranda.App.Features.MyBatisWorkspace.Save;

/// <summary>
/// diff 목록 한 행. 머리 줄(<see cref="Header"/>: 「@@ id · 줄 a–b @@」) · 한 줄 보기 한 줄(<see cref="Line"/>) · 나란히 보기 한 행(<see cref="Pair"/>) 중 하나만 채워진다.
/// </summary>
public sealed record MapperDiffRow(string? Header, MapperDiffLine? Line, MapperDiffPair? Pair)
{
    /// <summary>
    /// 문장 하나의 행들: 머리 줄(<paramref name="header"/> 가 null 이 아니면) + diff 줄.
    /// 한 줄 보기는 <paramref name="lines"/> 순서 그대로, 나란히 보기는 <see cref="MapperLineDiff.SideBySide"/>.
    /// </summary>
    public static IReadOnlyList<MapperDiffRow> Section(string? header, IReadOnlyList<MapperDiffLine> lines, bool sideBySide)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var rows = new List<MapperDiffRow>(lines.Count + 1);
        if (header is not null)
        {
            rows.Add(new MapperDiffRow(header, null, null));
        }

        if (sideBySide)
        {
            rows.AddRange(MapperLineDiff.SideBySide(lines).Select(p => new MapperDiffRow(null, null, p)));
        }
        else
        {
            rows.AddRange(lines.Select(l => new MapperDiffRow(null, l, null)));
        }

        return rows;
    }
}

/// <summary>
/// diff 줄 목록 컨트롤(시안 SaveDiff 오른쪽). 행 = 옛 줄 번호 · 새 줄 번호 · 부호 · 글(고정폭), 더한 줄·빠진 줄은 <c>Border.diff.added/removed</c> 배경.
/// 나란히 보기는 왼쪽(옛)·오른쪽(새) 반쪽에 각각 줄 번호 + 부호 + 글. 가상화되는 <see cref="ItemsControl"/> 이라 큰 diff 도 보이는 행만 만든다.
/// 저장 창과 「차이 보고 고르기」 창이 함께 쓴다.
/// </summary>
public sealed class MapperDiffView : UserControl
{
    private readonly ItemsControl _list;
    private IReadOnlyList<MapperDiffRow> _rows = [];

    public MapperDiffView()
    {
        FontFamily = MapperSaveUi.Mono;
        _list = new ItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<MapperDiffRow>((_, _) => new MapperDiffRowControl()),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        Content = _list;
        this.Bind(FontSizeProperty, this.GetResourceObservable("OrandaGridFontSize"));
    }

    /// <summary>그릴 행들. 바꾸면 목록을 다시 채운다.</summary>
    public IReadOnlyList<MapperDiffRow> Rows
    {
        get => _rows;
        set
        {
            _rows = value ?? [];
            _list.ItemsSource = _rows;
        }
    }

    /// <summary>그 행이 보이도록 스크롤한다(없는 번호면 무시).</summary>
    public void ScrollToRow(int index)
    {
        if (index >= 0 && index < _rows.Count)
        {
            _list.ScrollIntoView(index);
        }
    }
}

// 행 하나. 가상화가 컨테이너를 재사용하므로 DataContext 가 바뀔 때마다 처음부터 다시 만든다(클래스·배경이 앞 행 것으로 남지 않게).
internal sealed class MapperDiffRowControl : Border
{
    private const double NumberWidth = 40;
    private const double SignWidth = 16;

    public MapperDiffRowControl()
    {
        DataContextChanged += (_, _) => Rebuild();
    }

    private static Border Cell(MapperDiffLine? line, bool oldNumber, bool newNumber)
    {
        var grid = new Grid();
        var columns = new List<string>();
        if (oldNumber)
        {
            columns.Add(NumberWidth.ToString(CultureInfo.InvariantCulture));
        }

        if (newNumber)
        {
            columns.Add(NumberWidth.ToString(CultureInfo.InvariantCulture));
        }

        columns.Add(SignWidth.ToString(CultureInfo.InvariantCulture));
        columns.Add("*");
        grid.ColumnDefinitions = new ColumnDefinitions(string.Join(',', columns));
        int col = 0;
        if (oldNumber)
        {
            grid.Children.Add(Place(Number(line?.OldLine), col++));
        }

        if (newNumber)
        {
            grid.Children.Add(Place(Number(line?.NewLine), col++));
        }

        var sign = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeight.Bold };
        if (line?.Kind == MapperDiffKind.Added)
        {
            sign.Text = "+";
            sign.Bind(TextBlock.ForegroundProperty, sign.GetResourceObservable("OrandaEnvDevelopmentBrush"));
        }
        else if (line?.Kind == MapperDiffKind.Removed)
        {
            sign.Text = "−";
            sign.Bind(TextBlock.ForegroundProperty, sign.GetResourceObservable("OrandaDangerForegroundBrush"));
        }

        grid.Children.Add(Place(sign, col++));
        var text = new TextBlock { Text = line?.Text ?? string.Empty, TextWrapping = TextWrapping.Wrap };
        grid.Children.Add(Place(text, col));
        var cell = new Border { Child = grid };
        cell.Classes.Add("diff");
        cell.Classes.Set("added", line?.Kind == MapperDiffKind.Added);
        cell.Classes.Set("removed", line?.Kind == MapperDiffKind.Removed);
        return cell;
    }

    private static TextBlock Number(int? value)
    {
        var block = new TextBlock { Text = value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 6, 0) };
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable("SystemControlForegroundBaseMediumBrush"));
        return block;
    }

    private static T Place<T>(T control, int column)
        where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private static TextBlock Gap(MapperDiffLine line)
    {
        var text = new TextBlock { Text = line.Text, FontStyle = FontStyle.Italic, Margin = new Thickness(NumberWidth, 0, 0, 0) };
        text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("SystemControlForegroundBaseMediumBrush"));
        return text;
    }

    private void Rebuild()
    {
        Classes.Clear();
        ClearValue(BackgroundProperty);
        Child = null;
        switch (DataContext)
        {
            case MapperDiffRow { Header: { } header }:
                var title = new TextBlock { Text = header, FontStyle = FontStyle.Italic, Margin = new Thickness(NumberWidth, 1, 0, 1) };
                title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("SystemControlForegroundBaseMediumBrush"));
                Child = title;
                this.Bind(BackgroundProperty, this.GetResourceObservable("SystemControlBackgroundBaseLowBrush"));
                break;

            case MapperDiffRow { Line: { Kind: MapperDiffKind.Gap } gap }:
                Child = Gap(gap);
                break;

            case MapperDiffRow { Line: { } line }:
                Child = Cell(line, oldNumber: true, newNumber: true);
                break;

            case MapperDiffRow { Pair: { } pair }:
                Child = Pair(pair);
                break;
        }
    }

    private static Control Pair(MapperDiffPair pair)
    {
        if (pair.Left is { Kind: MapperDiffKind.Gap } gap)
        {
            return Gap(gap);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var left = Cell(pair.Left, oldNumber: true, newNumber: false);
        var right = Cell(pair.Right, oldNumber: false, newNumber: true);
        Grid.SetColumn(right, 1);
        if (right is Border rightBorder)
        {
            rightBorder.BorderThickness = new Thickness(1, 0, 0, 0);
            rightBorder.Bind(BorderBrushProperty, rightBorder.GetResourceObservable("OrandaSeparatorBrush"));
        }

        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }
}

/// <summary>저장 창·고르기 창·알림 띠가 함께 쓰는 작은 도우미(코드로 만든 화면용).</summary>
internal static class MapperSaveUi
{
    public static FontFamily Mono => MonoFonts.Code;

    /// <summary>주 창(대화상자의 주인). 없으면 null(헤드리스 시험).</summary>
    public static Window? MainWindow() => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public static string Format(string key, string fallback, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, UiText.Get(key, fallback), args);

    public static void BindResource(Control target, AvaloniaProperty property, string resourceKey) =>
        target.Bind(property, target.GetResourceObservable(resourceKey));

    /// <summary>아이콘 + (짧은 글) 단추. 툴팁·접근성 이름은 늘 붙인다. <paramref name="label"/> 가 null 이면 아이콘만.</summary>
    public static Button ToolButton(string iconKey, string tip, string? label = null)
    {
        var button = new Button { Padding = new Thickness(6, 3) };
        button.Classes.Add("tool");
        var icon = ShellIcons.Create(iconKey);
        if (label is null)
        {
            button.Content = icon;
        }
        else
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            if (icon is not null)
            {
                panel.Children.Add(icon);
            }

            panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            button.Content = panel;
        }

        ToolTip.SetTip(button, tip);
        AutomationProperties.SetName(button, label ?? tip);
        return button;
    }

    /// <summary>아이콘 토글 단추(한 줄 보기 / 나란히 보기).</summary>
    public static ToggleButton ToolToggle(string iconKey, string label)
    {
        var button = new ToggleButton { Padding = new Thickness(6, 3) };
        button.Classes.Add("tool");
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        if (ShellIcons.Create(iconKey) is { } icon)
        {
            panel.Children.Add(icon);
        }

        panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        button.Content = panel;
        ToolTip.SetTip(button, label);
        AutomationProperties.SetName(button, label);
        return button;
    }
}
