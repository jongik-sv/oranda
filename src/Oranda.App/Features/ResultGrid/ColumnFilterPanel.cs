using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Oranda.App.Features.Localization;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

/// <summary>
/// 열 머리글 ▾ 에서 여는 자동 필터 팝업의 내용(TSK-13-26): 값 검색 칸, 가져온 행의 서로 다른 값 목록(개수 포함, 「(NULL)」 항목 포함),
/// 조건(포함·같음·시작·범위·NULL), 적용·지우기·닫기. 코드로 만든다(대화상자처럼 <see cref="UiText"/> 로 글자를 얻는다).
/// 서로 다른 값이 많아 목록이 잘렸으면 체크를 푼 값만 빼는 방식(<see cref="ColumnFilter.Excluded"/>)으로 거른다.
/// </summary>
public sealed class ColumnFilterPanel : Border
{
    private static readonly ConditionKind?[] Kinds =
    [
        null, ConditionKind.Contains, ConditionKind.Equals, ConditionKind.StartsWith, ConditionKind.Between, ConditionKind.IsNull, ConditionKind.IsNotNull,
    ];

    private readonly List<ValueItem> _items = [];
    private readonly ValueItem? _nullItem;
    private readonly bool _truncated;
    private readonly TextBox _search = new() { PlaceholderText = UiText.Get("ColumnFilter.search", "값 검색") };
    private readonly ListBox _list = new() { Height = 200 };
    private readonly ComboBox _kind = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _text1 = new();
    private readonly TextBox _text2 = new() { PlaceholderText = UiText.Get("ColumnFilter.to", "끝") };

    public ColumnFilterPanel(ResultColumn column, ColumnValueList values, ColumnFilter? current)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(values);
        Column = column;
        _truncated = values.Truncated;
        foreach (var v in values.Values)
        {
            var on = current is null || (current.Values is { } include ? include.Contains(v.Text) : current.Excluded?.Contains(v.Text) != true);
            _items.Add(new ValueItem(v.Text, v.Count, on));
        }

        if (values.NullCount > 0)
        {
            _nullItem = new ValueItem("(NULL)", (int)Math.Min(int.MaxValue, values.NullCount), current?.IncludeNull ?? true);
        }

        Padding = new Thickness(10);
        Width = 300;
        MinWidth = 300;
        CornerRadius = new CornerRadius(4);
        BorderThickness = new Thickness(1);
        AutomationProperties.SetName(this, UiText.Get("ColumnFilter.title", "열 필터") + " " + column.Name);

        _list.ItemTemplate = new FuncDataTemplate<ValueItem>((item, _) => ItemView(item), supportsRecycling: false);
        _search.TextChanged += (_, _) => Refresh();
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Apply();
            }
        };

        _kind.ItemsSource = Kinds.Select(k => ConditionLabel(k)).ToList();
        _kind.SelectedIndex = SelectedKindIndex(current?.Condition);
        _text1.Text = current?.Condition?.Text ?? string.Empty;
        _text2.Text = current?.Condition?.Text2 ?? string.Empty;
        _kind.SelectionChanged += (_, _) => UpdateConditionBoxes();
        UpdateConditionBoxes();

        var title = new TextBlock { Text = column.Name, FontWeight = Avalonia.Media.FontWeight.SemiBold };
        var selectAll = new Button { Content = UiText.Get("ColumnFilter.selectAll", "모두 선택") };
        var clearAll = new Button { Content = UiText.Get("ColumnFilter.clearAll", "모두 해제") };
        selectAll.Click += (_, _) => SetVisibleChecked(true);
        clearAll.Click += (_, _) => SetVisibleChecked(false);
        var apply = new Button { Content = UiText.Get("ColumnFilter.apply", "적용"), IsDefault = true };
        var clear = new Button { Content = UiText.Get("ColumnFilter.clear", "필터 지우기") };
        var close = new Button { Content = UiText.Get("ColumnFilter.close", "닫기"), IsCancel = true };
        apply.Click += (_, _) => Apply();
        clear.Click += (_, _) => Applied?.Invoke(this, null);
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(title);
        panel.Children.Add(_search);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { selectAll, clearAll } });
        panel.Children.Add(_list);
        if (_truncated)
        {
            panel.Children.Add(new TextBlock
            {
                Text = UiText.Get("ColumnFilter.truncated", "값이 많아 일부만 보입니다(처음 {0}개).").Replace("{0}", values.Values.Count.ToString("N0", CultureInfo.CurrentCulture), StringComparison.Ordinal),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Opacity = 0.7,
                FontSize = 12,
            });
        }

        panel.Children.Add(new TextBlock { Text = UiText.Get("ColumnFilter.condition", "조건"), Opacity = 0.7, FontSize = 12 });
        panel.Children.Add(_kind);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _text1, _text2 } });
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Children = { apply, clear, close } });
        Child = panel;
        Refresh();
    }

    public ResultColumn Column { get; }

    /// <summary>적용을 눌렀다. 값이 null 이면 이 열 필터를 지운다.</summary>
    public event EventHandler<ColumnFilter?>? Applied;

    /// <summary>닫기(Esc)를 눌렀다.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>값 목록에서 검색 글에 맞는 항목만 남긴다(체크 상태는 검색과 무관하게 항목에 남는다).</summary>
    private void Refresh()
    {
        var query = _search.Text ?? string.Empty;
        IEnumerable<ValueItem> shown = _items;
        if (_nullItem is not null)
        {
            shown = shown.Prepend(_nullItem);
        }

        _list.ItemsSource = query.Length == 0 ? shown.ToList() : shown.Where(i => i.Text.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void SetVisibleChecked(bool on)
    {
        foreach (var item in (IEnumerable<ValueItem>?)_list.ItemsSource ?? [])
        {
            item.IsChecked = on;
        }

        Refresh();
    }

    /// <summary>가상화 목록이 컨테이너를 재활용하면서 항목 없이(null) 템플릿을 부르므로 그때는 빈 컨트롤을 돌려준다.</summary>
    private static Control ItemView(ValueItem? item)
    {
        if (item is null)
        {
            return new Panel();
        }

        var check = new CheckBox
        {
            IsChecked = item.IsChecked,
            Content = $"{item.Text} ({item.Count.ToString("N0", CultureInfo.CurrentCulture)})",
            MinHeight = 0,
            Padding = new Thickness(4, 0),
        };
        check.IsCheckedChanged += (_, _) => item.IsChecked = check.IsChecked == true;
        return check;
    }

    private static string ConditionLabel(ConditionKind? kind) => kind switch
    {
        ConditionKind.Contains => UiText.Get("ColumnFilter.cond.contains", "포함"),
        ConditionKind.Equals => UiText.Get("ColumnFilter.cond.equals", "같음"),
        ConditionKind.StartsWith => UiText.Get("ColumnFilter.cond.startsWith", "시작"),
        ConditionKind.Between => UiText.Get("ColumnFilter.cond.between", "범위"),
        ConditionKind.IsNull => UiText.Get("ColumnFilter.cond.isNull", "NULL 인 행"),
        ConditionKind.IsNotNull => UiText.Get("ColumnFilter.cond.isNotNull", "NULL 이 아닌 행"),
        _ => UiText.Get("ColumnFilter.cond.none", "(조건 없음)"),
    };

    private static int SelectedKindIndex(ColumnCondition? condition) =>
        condition is null ? 0 : Math.Max(0, Array.IndexOf(Kinds, condition.Kind));

    private void UpdateConditionBoxes()
    {
        var kind = Kinds[Math.Max(0, _kind.SelectedIndex)];
        _text1.IsVisible = kind is ConditionKind.Contains or ConditionKind.Equals or ConditionKind.StartsWith or ConditionKind.Between;
        _text2.IsVisible = kind is ConditionKind.Between;
        _text1.PlaceholderText = kind is ConditionKind.Between ? UiText.Get("ColumnFilter.from", "시작") : UiText.Get("ColumnFilter.value", "값");
        _text1.Width = _text2.IsVisible ? 140 : 288;
        _text2.Width = 140;
    }

    /// <summary>지금 체크·조건 상태를 열 필터로 만든다. 아무 제한이 없으면 null(필터 없음).</summary>
    public ColumnFilter? BuildFilter()
    {
        var kind = Kinds[Math.Max(0, _kind.SelectedIndex)];
        var t1 = _text1.Text?.Trim() ?? string.Empty;
        var t2 = _text2.Text?.Trim() ?? string.Empty;
        ColumnCondition? condition = kind switch
        {
            null => null,
            ConditionKind.IsNull or ConditionKind.IsNotNull => new ColumnCondition(kind.Value),
            ConditionKind.Between => t1.Length == 0 && t2.Length == 0 ? null : new ColumnCondition(kind.Value, t1, t2),
            _ => t1.Length == 0 ? null : new ColumnCondition(kind.Value, t1),
        };

        var allOn = _items.All(i => i.IsChecked);
        var unchecked_ = _items.Where(i => !i.IsChecked).Select(i => i.Text).ToHashSet(StringComparer.Ordinal);
        var filter = new ColumnFilter
        {
            Values = _truncated || allOn ? null : _items.Where(i => i.IsChecked).Select(i => i.Text).ToHashSet(StringComparer.Ordinal),
            Excluded = _truncated && unchecked_.Count > 0 ? unchecked_ : null,
            IncludeNull = _nullItem?.IsChecked ?? true,
            Condition = condition,
        };
        return filter.IsEmpty ? null : filter;
    }

    private void Apply() => Applied?.Invoke(this, BuildFilter());

    /// <summary>값 목록 한 줄. 체크 상태는 여기에 남는다(검색으로 줄이거나 다시 그려도 유지된다).</summary>
    private sealed class ValueItem(string text, int count, bool isChecked)
    {
        public string Text { get; } = text;

        public int Count { get; } = count;

        public bool IsChecked { get; set; } = isChecked;
    }

    /// <summary>시험용: 값 항목을 이름으로 체크하거나 푼다(NULL 항목은 <c>null</c>).</summary>
    internal void SetChecked(string? text, bool on)
    {
        var item = text is null ? _nullItem : _items.FirstOrDefault(i => i.Text == text);
        if (item is not null)
        {
            item.IsChecked = on;
        }
    }

    /// <summary>시험용: 모든 값 항목을 체크하거나 푼다.</summary>
    internal void SetAllChecked(bool on)
    {
        foreach (var i in _items)
        {
            i.IsChecked = on;
        }

        if (_nullItem is not null)
        {
            _nullItem.IsChecked = on;
        }
    }

    /// <summary>시험용: 조건을 고른다.</summary>
    internal void SetCondition(ConditionKind? kind, string text1 = "", string text2 = "")
    {
        _kind.SelectedIndex = Math.Max(0, Array.IndexOf(Kinds, kind));
        _text1.Text = text1;
        _text2.Text = text2;
    }
}
