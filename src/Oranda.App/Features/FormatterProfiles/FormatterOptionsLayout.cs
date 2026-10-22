using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;

namespace Oranda.App.Features.FormatterProfiles;

/// <summary>
/// 옵션 묶음(테두리 상자)을 단으로 놓는 판. 폭이 되면 3단, 안 되면 2단, 그래도 안 되면 1단(TSK-13-42).
/// 단 배치는 묶음 이름(<see cref="Control.Tag"/>)으로 정한 순서를 따르고, 모르는 묶음은 가장 짧은 단 끝에 붙인다.
/// 단 폭 = 그 단에서 가장 넓은 묶음의 폭 + 남는 폭을 그 비율로 나눈 몫.
/// </summary>
public sealed class OptionGroupsPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<OptionGroupsPanel, double>(nameof(Spacing), 8);

    private static readonly string[][][] Plans =
    [
        [[FormatterOptionSchema.CaseGroup, FormatterOptionSchema.LineBreakGroup, FormatterOptionSchema.CommaGroup, FormatterOptionSchema.IndentGroup, FormatterOptionSchema.AlignGroup, FormatterOptionSchema.EtcGroup]],
        [[FormatterOptionSchema.CaseGroup, FormatterOptionSchema.IndentGroup, FormatterOptionSchema.AlignGroup], [FormatterOptionSchema.LineBreakGroup, FormatterOptionSchema.CommaGroup, FormatterOptionSchema.EtcGroup]],
        [[FormatterOptionSchema.CaseGroup, FormatterOptionSchema.IndentGroup], [FormatterOptionSchema.LineBreakGroup, FormatterOptionSchema.CommaGroup], [FormatterOptionSchema.AlignGroup, FormatterOptionSchema.EtcGroup]],
    ];

    private List<List<Control>> _columns = [];
    private double[] _widths = [];
    private double[] _required = [0, 0, 0];

    static OptionGroupsPanel()
    {
        AffectsMeasure<OptionGroupsPanel>(SpacingProperty);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>마지막 배치의 단 수(1~3).</summary>
    public int Columns { get; private set; } = 3;

    /// <summary>마지막 측정에서 <paramref name="columns"/> 단(1~3)에 필요한 폭(묶음 본래 폭 + 사이 간격).</summary>
    public double RequiredWidth(int columns) => _required[Math.Clamp(columns, 1, 3) - 1];

    /// <summary>마지막 배치에서 단마다 놓인 묶음 이름(시험용).</summary>
    public IReadOnlyList<IReadOnlyList<string>> ColumnGroups => _columns.Select(c => (IReadOnlyList<string>)c.Select(x => x.Tag as string ?? string.Empty).ToList()).ToList();

    protected override Size MeasureOverride(Size availableSize)
    {
        var kids = Children.Where(c => c.IsVisible).ToList();
        var natural = new Dictionary<Control, double>();
        foreach (var c in kids)
        {
            c.Measure(Size.Infinity);
            natural[c] = c.DesiredSize.Width;
        }

        var width = availableSize.Width;
        for (var n = 1; n <= 3; n++)
        {
            _required[n - 1] = Plan(kids, n).Select(col => col.Count == 0 ? 0 : col.Max(c => natural[c])).Sum() + (Spacing * (n - 1));
        }

        for (var n = 3; n >= 1; n--)
        {
            var cols = Plan(kids, n);
            var need = cols.Select(col => col.Count == 0 ? 0 : col.Max(c => natural[c])).ToArray();
            var total = need.Sum() + (Spacing * (n - 1));
            if (n > 1 && !double.IsInfinity(width) && total > width)
            {
                continue;
            }

            Columns = n;
            _columns = cols;
            var extra = double.IsInfinity(width) ? 0 : Math.Max(0, width - total);
            var sum = need.Sum();
            _widths = need.Select(w => w + (sum > 0 ? extra * w / sum : extra / n)).ToArray();
            if (n == 1 && !double.IsInfinity(width))
            {
                _widths[0] = width; // 1단은 판 폭에 맞춘다(라디오 줄은 접혀 내려간다).
            }

            break;
        }

        double height = 0;
        for (var i = 0; i < _columns.Count; i++)
        {
            double h = 0;
            foreach (var c in _columns[i])
            {
                c.Measure(new Size(_widths[i], double.PositiveInfinity));
                h += c.DesiredSize.Height;
            }

            h += Spacing * Math.Max(0, _columns[i].Count - 1);
            height = Math.Max(height, h);
        }

        var used = _widths.Sum() + (Spacing * Math.Max(0, _columns.Count - 1));
        return new Size(double.IsInfinity(width) ? used : width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var used = _widths.Sum() + (Spacing * Math.Max(0, _columns.Count - 1));
        var extra = Math.Max(0, finalSize.Width - used);
        var sum = _widths.Sum();
        double x = 0;
        for (var i = 0; i < _columns.Count; i++)
        {
            var w = _widths[i] + (sum > 0 ? extra * _widths[i] / sum : 0);
            double y = 0;
            foreach (var c in _columns[i])
            {
                c.Arrange(new Rect(x, y, w, c.DesiredSize.Height));
                y += c.DesiredSize.Height + Spacing;
            }

            x += w + Spacing;
        }

        return finalSize;
    }

    private static List<List<Control>> Plan(List<Control> kids, int n)
    {
        var plan = Plans[n - 1];
        var cols = plan.Select(names => names.Select(name => kids.FirstOrDefault(k => k.Tag as string == name)).OfType<Control>().ToList()).ToList();
        foreach (var k in kids.Where(k => !cols.Any(c => c.Contains(k))))
        {
            cols.OrderBy(c => c.Sum(x => x.DesiredSize.Height)).First().Add(k);
        }

        return cols;
    }
}

/// <summary>
/// 옵션 행(<see cref="OptionRow"/>)으로 묶음 상자를 만든다(TSK-13-42 A안): 대소문자 = 라디오 표, 들여쓰기 = 증감 2개씩,
/// 정렬·공백 = 체크 2개씩, 나머지 = 「라벨 | 라디오·체크·증감」 줄. 줄마다 앞에 점(파랑 = 기본과 다름, 주황 = 저장 안 함)이 있고
/// 점이 있는 옵션은 라벨이 굵다. 옵션 칸마다 <c>Tag</c> = JSON 경로(시험이 찾는다).
/// </summary>
public static class FormatterOptionGroups
{
    /// <summary>점 칸의 <c>Tag</c> 앞머리(뒤에 옵션 경로).</summary>
    public const string MarkerTag = "marker:";

    private static readonly IValueConverter Weight = new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.SemiBold : FontWeight.Normal);

    // 다른 옵션 때문에 효과가 없는 옵션은 흐리게(고칠 수는 있다).
    private static readonly IValueConverter Fade = new FuncValueConverter<bool, double>(b => b ? 1.0 : 0.45);

    // 묶음 안 표시 순서(고르기 → 체크 → 숫자, 짝을 이루는 것은 붙여서). 여기 없는 옵션은 묶음 끝에 선언 순서로.
    private static readonly string[] Order =
    [
        "case.keyword", "case.function", "case.identifier",
        "indent.size", "indent.subquery", "indent.case", "indent.plsqlBlock", "indent.useTabs",
        "lineBreaks.selectList", "lineBreaks.groupOrderBy", "lineBreaks.orderBy", "lineBreaks.andOr", "lineBreaks.joinClause", "lineBreaks.inListThreshold", "lineBreaks.maxLineWidth",
        "clauseLayout", "commaPosition", "keywordAlign",
        "align.alias", "align.assignment", "align.columnDefinition", "align.equals", "align.comparison", "operatorSpacing", "parenInnerSpace",
        "comments.preserve", "comments.maxBlankLines", "statementTerminator", "honorFormatterOff",
    ];

    /// <summary>묶음 판을 만든다. 이름 = <c>OptionList</c>.</summary>
    public static OptionGroupsPanel Build(IReadOnlyList<OptionRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var panel = new OptionGroupsPanel { Name = "OptionList", Classes = { "fmt-options" } };
        foreach (var group in FormatterOptionSchema.Groups.Concat(rows.Select(r => r.Group)).Distinct())
        {
            var mine = rows.Where(r => r.Group == group).OrderBy(r => Array.IndexOf(Order, r.Path) is var i and >= 0 ? i : Order.Length).ToList();
            if (mine.Count == 0)
            {
                continue;
            }

            Control body = group switch
            {
                FormatterOptionSchema.CaseGroup => CaseTable(mine),
                FormatterOptionSchema.IndentGroup => Pairs(mine, numbersFirst: true),
                FormatterOptionSchema.AlignGroup => Pairs(mine, numbersFirst: false),
                _ => Lines(mine),
            };
            panel.Children.Add(Box(group, body, group == FormatterOptionSchema.IndentGroup ? "단계 = 들여쓰기 몇 번" : null));
        }

        return panel;
    }

    private static Border Box(string title, Control body, string? note)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 2) };
        head.Children.Add(new TextBlock { Text = title, Classes = { "fmt-box-title" } });
        if (note is not null)
        {
            head.Children.Add(new TextBlock { Text = note, Classes = { "fmt-box-note" } });
        }

        return new Border { Classes = { "fmt-box" }, Tag = title, Child = new StackPanel { Children = { head, body } } };
    }

    /// <summary>대소문자: 고르기 값이 같은 옵션끼리 「라벨 × 값」 라디오 표. 다른 옵션은 아래 줄로.</summary>
    private static StackPanel CaseTable(List<OptionRow> rows)
    {
        var table = rows.Where(r => r.IsChoice).GroupBy(r => string.Join(",", r.ChoiceItems.Select(c => c.Name))).OrderByDescending(g => g.Count()).FirstOrDefault()?.ToList() ?? [];
        var stack = new StackPanel();
        if (table.Count > 0)
        {
            var heads = table[0].ChoiceItems;
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("12,Auto") };
            foreach (var _ in heads)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto) { MinWidth = 58 }); // 값 이름 폭(영어도 겹치지 않게), 최소 58
            }

            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var i = 0; i < heads.Count; i++)
            {
                Place(g, new TextBlock { Text = heads[i].Label, Classes = { "fmt-col-head" }, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(4, 0) }, 0, 2 + i);
            }

            var row = 1;
            foreach (var r in table)
            {
                g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Place(g, Marker(r), row, 0);
                var label = Label(r);
                label.Margin = new Thickness(0, 0, 12, 0);
                Place(g, label, row, 1);
                for (var i = 0; i < r.ChoiceItems.Count && i < heads.Count; i++)
                {
                    var radio = Radio(r, r.ChoiceItems[i], text: false);
                    radio.HorizontalAlignment = HorizontalAlignment.Center;
                    Place(g, radio, row, 2 + i);
                }

                row++;
            }

            stack.Children.Add(Cell(table, g));
        }

        var rest = rows.Except(table).ToList();
        if (rest.Count > 0)
        {
            stack.Children.Add(Lines(rest));
        }

        return stack;
    }

    /// <summary>들여쓰기·정렬·공백: 숫자(증감)·체크를 두 개씩 한 줄에. 고르기 옵션은 아래 줄로.</summary>
    private static StackPanel Pairs(List<OptionRow> rows, bool numbersFirst)
    {
        var stack = new StackPanel();
        var numbers = rows.Where(r => r.IsInt).ToList();
        var bools = rows.Where(r => r.IsBool).ToList();
        if (numbers.Count > 0)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("12,Auto,Auto,16,12,Auto,Auto") };
            for (var i = 0; i < numbers.Count; i++)
            {
                if (i % 2 == 0)
                {
                    g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                }

                var r = numbers[i];
                var col = (i % 2) * 4;
                var label = Label(r);
                label.Margin = new Thickness(0, 0, 8, 0);
                var cell = new StackPanel { Orientation = Orientation.Horizontal, Children = { Number(r) } };
                Place(g, Marker(r), i / 2, col);
                Place(g, label, i / 2, col + 1);
                Place(g, Cell([r], cell), i / 2, col + 2);
            }

            stack.Children.Add(g);
        }

        if (bools.Count > 0)
        {
            // 두 칸은 Auto 로 둔다(* 로 반씩 나누면 가장 좁은 폭에서 긴 라벨 쪽이 잘린다).
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions(numbersFirst ? "12,*" : "12,Auto,16,12,Auto") };
            var per = numbersFirst ? 1 : 2;
            for (var i = 0; i < bools.Count; i++)
            {
                if (i % per == 0)
                {
                    g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                }

                var r = bools[i];
                Place(g, Marker(r), i / per, (i % per) * 3);
                Place(g, Cell([r], Check(r)), i / per, ((i % per) * 3) + 1);
            }

            stack.Children.Add(g);
        }

        var rest = rows.Except(numbers).Except(bools).ToList();
        if (rest.Count > 0)
        {
            stack.Children.Add(Lines(rest));
        }

        return stack;
    }

    /// <summary>「라벨 | 값」 줄. 체크는 라벨을 품고 두 칸을 쓴다. 「주석 보존」 바로 뒤 숫자 옵션은 같은 줄에 둔다.</summary>
    private static Grid Lines(List<OptionRow> rows)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("12,Auto,*") };
        var line = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Place(g, Marker(r), line, 0);
            if (r.IsBool)
            {
                var next = i + 1 < rows.Count && rows[i + 1].IsInt && r.Path == "comments.preserve" ? rows[i + 1] : null;
                if (next is null)
                {
                    Place(g, Cell([r], Check(r)), line, 1, 2);
                }
                else
                {
                    var nextLabel = Label(next);
                    nextLabel.Margin = new Thickness(0, 0, 6, 0);
                    var marker = Marker(next);
                    marker.Margin = new Thickness(18, 0, 0, 0);
                    var pair = new StackPanel { Orientation = Orientation.Horizontal };
                    pair.Children.Add(Cell([r], Check(r)));
                    pair.Children.Add(marker);
                    pair.Children.Add(nextLabel);
                    pair.Children.Add(Cell([next], NumberWithUnit(next)));
                    Place(g, pair, line, 1, 2);
                    i++;
                }
            }
            else
            {
                var label = Label(r);
                label.Margin = new Thickness(0, 0, 10, 0);
                Place(g, label, line, 1);
                Control value = r.IsInt ? NumberWithUnit(r) : new WrapPanel { Orientation = Orientation.Horizontal };
                if (value is WrapPanel wrap)
                {
                    // 좁은 1단에서는 라디오가 다음 줄로 접힌다.
                    foreach (var c in r.ChoiceItems)
                    {
                        var radio = Radio(r, c, text: true);
                        radio.Margin = new Thickness(wrap.Children.Count == 0 ? 0 : 8, 0, 0, 0); // 끝 단추 뒤에는 여백을 두지 않는다
                        wrap.Children.Add(radio);
                    }
                }

                Place(g, Cell([r], value), line, 2);
            }

            line++;
        }

        return g;
    }

    /// <summary>옵션 칸 표시(시험이 경로로 찾는다). 여러 옵션이면 첫 경로 외에는 <see cref="OptionPaths"/> 에 둔다.</summary>
    private static Control Cell(List<OptionRow> rows, Control content)
    {
        content.Tag = rows[0].Path;
        content.Classes.Add("fmt-option");
        if (rows.Count == 1)
        {
            Faded(content, rows[0]);
            if (content is not CheckBox)
            {
                content.Bind(ToolTip.TipProperty, new Binding(nameof(OptionRow.InapplicableReason)) { Source = rows[0] });
            }
        }

        if (rows.Count > 1)
        {
            OptionPaths.Set(content, rows.Select(r => r.Path).ToList());
        }

        return content;
    }

    private static TextBlock Label(OptionRow r)
    {
        var t = new TextBlock { Text = r.ShortLabel, VerticalAlignment = VerticalAlignment.Center };
        t.Bind(TextBlock.FontWeightProperty, new Binding(nameof(OptionRow.IsMarked)) { Source = r, Converter = Weight });
        t.Bind(ToolTip.TipProperty, new Binding(nameof(OptionRow.Tip)) { Source = r });
        Faded(t, r);
        return t;
    }

    private static void Faded(Control c, OptionRow r)
        => c.Bind(Visual.OpacityProperty, new Binding(nameof(OptionRow.IsApplicable)) { Source = r, Converter = Fade });

    /// <summary>점: 파랑 = 기본과 다름, 주황 = 저장 안 한 변경.</summary>
    private static Panel Marker(OptionRow r)
    {
        var blue = Dot("OrandaIconBlueBrush", "기본과 다름");
        blue.Bind(Visual.IsVisibleProperty, new Binding(nameof(OptionRow.ShowsChanged)) { Source = r });
        var orange = Dot("OrandaIconOrangeBrush", "저장 안 한 변경");
        orange.Bind(Visual.IsVisibleProperty, new Binding(nameof(OptionRow.IsDirty)) { Source = r });
        return new Panel { Width = 12, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Children = { blue, orange }, Classes = { "fmt-marker" }, Tag = MarkerTag + r.Path };
    }

    private static Ellipse Dot(string brush, string tip)
    {
        var e = new Ellipse { Width = 6, Height = 6, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        e.Bind(Shape.FillProperty, e.GetResourceObservable(brush));
        ToolTip.SetTip(e, tip);
        return e;
    }

    /// <summary>라디오 하나(자기 그룹 — 고른 값은 ViewModel 이 정한다).</summary>
    private static Border Radio(OptionRow r, ChoiceItem item, bool text)
    {
        var radio = new RadioButton { VerticalAlignment = VerticalAlignment.Center };
        if (text)
        {
            radio.Content = item.Label;
        }

        AutomationProperties.SetName(radio, r.Label + " " + item.Label);
        radio.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(ChoiceItem.IsChecked)) { Source = item, Mode = BindingMode.TwoWay });
        // 라디오마다 부모를 따로 둬서 Avalonia 의 부모 단위 묶음이 서로 끄지 않게 한다.
        return new Border { Child = radio, VerticalAlignment = VerticalAlignment.Center };
    }

    private static CheckBox Check(OptionRow r)
    {
        var c = new CheckBox { Content = r.ShortLabel, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(c, r.Label);
        c.Bind(ToolTip.TipProperty, new Binding(nameof(OptionRow.Tip)) { Source = r });
        c.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(OptionRow.BoolValue)) { Source = r, Mode = BindingMode.TwoWay });
        c.Bind(TemplatedControl.FontWeightProperty, new Binding(nameof(OptionRow.IsMarked)) { Source = r, Converter = Weight });
        return c;
    }

    private static NumericUpDown Number(OptionRow r)
    {
        var n = new NumericUpDown
        {
            Minimum = r.Minimum,
            Maximum = r.Maximum,
            FormatString = "0",
            Increment = 1,
            Width = r.Maximum >= 100 ? 70 : 60,
            HorizontalContentAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(n, r.Label);
        n.Bind(NumericUpDown.ValueProperty, new Binding(nameof(OptionRow.NumberValue)) { Source = r, Mode = BindingMode.TwoWay });
        return n;
    }

    private static StackPanel NumberWithUnit(OptionRow r)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        sp.Children.Add(Number(r));
        if (r.Unit is { } unit)
        {
            sp.Children.Add(new TextBlock { Text = unit, Classes = { "fmt-unit" }, VerticalAlignment = VerticalAlignment.Center });
        }

        return sp;
    }

    private static void Place(Grid g, Control c, int row, int col, int span = 1)
    {
        Grid.SetRow(c, row);
        Grid.SetColumn(c, col);
        Grid.SetColumnSpan(c, span);
        g.Children.Add(c);
    }
}

/// <summary>옵션 칸 하나가 여러 옵션을 품을 때(대소문자 표) 그 경로들.</summary>
public static class OptionPaths
{
    public static readonly AttachedProperty<IReadOnlyList<string>?> PathsProperty =
        AvaloniaProperty.RegisterAttached<Control, IReadOnlyList<string>?>("Paths", typeof(OptionPaths));

    public static IReadOnlyList<string>? Get(Control control) => control?.GetValue(PathsProperty);

    public static void Set(Control control, IReadOnlyList<string>? value) => control?.SetValue(PathsProperty, value);
}
