using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Oranda.App.Features.Localization;
using Oranda.App.Styles;

namespace Oranda.App.Shell.Controls;

/// <summary>
/// 열 이름 + 행으로 된 정보 표(DB 정보·스키마 속성·객체 상세의 구역). 테이블 레이아웃 창과 같은 C안 격자(<see cref="GridC"/>)로 그린다:
/// 전체 격자선·행 번호 머리 칸·현재 행 ▶·아래 상태 줄. 행 단위로 고른다(클릭·Shift·Ctrl) — Ctrl+C 와 오른쪽 클릭 「복사」=고른 행의 TSV,
/// 「머리글 포함 복사」=열 이름 줄을 앞에 붙인 TSV. 행을 누르면 <see cref="RowActivated"/>(첫 두 칸을 이름·값으로 쓰는 화면이 받는다).
/// 셀 글 일부만 골라 복사하던 동작은 행 복사로 바뀌었다.
/// </summary>
public sealed class DetailTable : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<string>?> ColumnsProperty =
        AvaloniaProperty.Register<DetailTable, IReadOnlyList<string>?>(nameof(Columns));

    public static readonly StyledProperty<IReadOnlyList<IReadOnlyList<object?>>?> RowsProperty =
        AvaloniaProperty.Register<DetailTable, IReadOnlyList<IReadOnlyList<object?>>?>(nameof(Rows));

    /// <summary>행 머리 칸에 행 번호를 쓸지(기본 true). 표에 자체 「#」 칸이 있으면 끈다(▶ 표시는 그대로).</summary>
    public static readonly StyledProperty<bool> RowNumbersProperty =
        AvaloniaProperty.Register<DetailTable, bool>(nameof(RowNumbers), true);

    /// <summary>
    /// true 면 <see cref="Rows"/> 가 같은 줄 수로 바뀔 때(칸 값만 고친 경우) 고른 행·현재 행을 같은 자리에 남긴다(기본 false = 지운다).
    /// 객체 상세가 인덱스 열을 읽은 뒤 위 표의 「열」 칸을 식으로 바꿀 때 쓴다.
    /// </summary>
    public static readonly StyledProperty<bool> KeepSelectionOnSameShapeProperty =
        AvaloniaProperty.Register<DetailTable, bool>(nameof(KeepSelectionOnSameShape));

    /// <summary>true 면 <see cref="Rows"/> 를 다시 만들 때 세로 스크롤 위치를 남긴다(기본 false). 자동 새로 고침하는 모니터 표가 쓴다.</summary>
    public static readonly StyledProperty<bool> KeepScrollProperty =
        AvaloniaProperty.Register<DetailTable, bool>(nameof(KeepScroll));

    /// <summary>
    /// false 면 아래 상태 줄(「N행 · M개 선택」)을 숨긴다(기본 true). 바깥 화면이 자기 상태 줄에 행 수·선택 수를 합쳐 쓸 때 끈다.
    /// 상태 글(<see cref="StatusText"/>)과 <see cref="SelectionCount"/> 는 숨겨도 갱신된다.
    /// </summary>
    public static readonly StyledProperty<bool> StatusLineVisibleProperty =
        AvaloniaProperty.Register<DetailTable, bool>(nameof(StatusLineVisible), true);

    /// <summary>한 번에 그릴 최대 행(그 뒤는 상태 줄에 「…」). 거르기로 좁혀 보게 한다.</summary>
    public const int MaxRows = 2000;

    private const string Group = "dt";

    private static bool En => LiteralTranslator.Language == "en";

    private readonly ListBox _list = new() { Classes = { "grid-c" }, SelectionMode = SelectionMode.Multiple };
    private readonly ScrollViewer _scroll = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _statusHost = new() { Classes = { "panel-status" } };
    private readonly Dictionary<IReadOnlyList<object?>, TextBlock> _markers = new(ReferenceEqualityComparer.Instance);
    private readonly Border _headerHost = new();
    private readonly Border _probeHost = new(); // 전체 행 기준 칸 너비를 고정하는 숨은 줄(GridC.Probe)
    private readonly List<ListBoxItem> _items = [];
    private IReadOnlyList<object?>? _current;
    private int _overflow;
    private int _lastPicked;

    public DetailTable()
    {
        var status = _statusHost;
        status.Child = _status;
        DockPanel.SetDock(status, Avalonia.Controls.Dock.Bottom);
        var root = new DockPanel();
        root.Children.Add(status);
        root.Children.Add(_scroll);
        Content = root;

        var body = new DockPanel();
        DockPanel.SetDock(_headerHost, Avalonia.Controls.Dock.Top);
        body.Children.Add(_headerHost);
        DockPanel.SetDock(_probeHost, Avalonia.Controls.Dock.Top);
        body.Children.Add(_probeHost);
        body.Children.Add(_list);
        Grid.SetIsSharedSizeScope(body, true);
        _scroll.Content = body;

        _list.AddHandler(PointerPressedEvent, OnPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _list.KeyUp += OnListKeyUp;
        _list.SelectionChanged += (_, _) => UpdateStatus();
        AddHandler(KeyDownEvent, OnKeyDown);
        _list.ContextMenu = BuildMenu();
        UpdateStatus();
    }

    public IReadOnlyList<string>? Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public IReadOnlyList<IReadOnlyList<object?>>? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public bool RowNumbers
    {
        get => GetValue(RowNumbersProperty);
        set => SetValue(RowNumbersProperty, value);
    }

    public bool KeepSelectionOnSameShape
    {
        get => GetValue(KeepSelectionOnSameShapeProperty);
        set => SetValue(KeepSelectionOnSameShapeProperty, value);
    }

    public bool KeepScroll
    {
        get => GetValue(KeepScrollProperty);
        set => SetValue(KeepScrollProperty, value);
    }

    public bool StatusLineVisible
    {
        get => GetValue(StatusLineVisibleProperty);
        set => SetValue(StatusLineVisibleProperty, value);
    }

    /// <summary>고른 행 수.</summary>
    public int SelectionCount => _list.SelectedItems?.Count ?? 0;

    /// <summary>고른 행 수가 바뀌었다(바깥 상태 줄이 받는다).</summary>
    public event EventHandler? SelectionCountChanged;

    /// <summary>고정폭 글꼴(앱 리소스 OrandaGridFontFamily)로 그릴 열 번호(0부터). SID·SQL ID 처럼 자리 맞춤이 필요한 열. 기본 없음. 행을 채우기 전에 정한다.</summary>
    public IReadOnlyCollection<int>? MonospaceColumns { get; set; }

    /// <summary>
    /// (열 번호, 행)을 받아 참이면 그 칸을 위험색(OrandaDangerForegroundBrush, 라이트·다크 대응) 글자로 그린다. 빈 글 칸은 무시. 기본 없음. 행을 채우기 전에 정한다.
    /// </summary>
    public Func<int, IReadOnlyList<object?>, bool>? CellEmphasis { get; set; }

    /// <summary>오른쪽 클릭 메뉴에 「복사」 아래로 덧붙일 항목을 메뉴가 열릴 때마다 돌려주는 함수(모니터의 context:monitor/* 명령 등). 없으면 복사 둘뿐.</summary>
    public Func<IReadOnlyList<object>>? ExtraMenuItems { get; set; }

    /// <summary>오른쪽 칼럼 번호(0부터)를 오른쪽 맞춤으로 그릴 열. 값이 글자로 미리 서식된 표가 쓴다(숫자 값은 기존대로 자동).</summary>
    public IReadOnlyCollection<int>? RightAlignedColumns { get; set; }

    /// <summary>행을 눌렀다. 값은 그 행의 칸들.</summary>
    public event EventHandler<IReadOnlyList<object?>>? RowActivated;

    /// <summary>현재 행(▶)이 바뀌었다 — 누르거나 키보드로 옮겨 갔을 때. 값은 그 행의 칸들.</summary>
    public event EventHandler<IReadOnlyList<object?>>? CurrentRowChanged;

    /// <summary>현재 행(▶). 없으면 null.</summary>
    internal IReadOnlyList<object?>? CurrentRow => _current;

    /// <summary>현재 행의 표 안 번호(0부터). 없으면 -1.</summary>
    internal int CurrentRowIndex => _current is null ? -1 : _items.FindIndex(i => ReferenceEquals(i.Tag, _current));

    /// <summary>고른 행(표 순서).</summary>
    internal IReadOnlyList<IReadOnlyList<object?>> SelectedRows
        => PickedItems().Select(i => (IReadOnlyList<object?>)i.Tag!).ToList();

    // 고른 칸(표 순서). 화면에 아직 붙지 않은 줄은 IsSelected 가 늦게 켜지므로 선택 모델을 본다.
    private List<ListBoxItem> PickedItems()
    {
        var picked = _list.SelectedItems?.OfType<ListBoxItem>().ToHashSet() ?? [];
        return _items.Where(picked.Contains).ToList();
    }

    internal ListBox List => _list;

    private Action? _pendingSelect;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_pendingSelect is { } pending)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(pending, Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// <paramref name="pick"/> 이 참인 행만 고르고(기존 선택은 푼다), <paramref name="current"/> 가 참인 첫 행을 현재 행(▶)으로 한다(이벤트 없이).
    /// <paramref name="current"/> 가 null 이면 현재 행은 그대로 둔다.
    /// </summary>
    public void SelectRows(Func<IReadOnlyList<object?>, bool> pick, Func<IReadOnlyList<object?>, bool>? current = null)
    {
        ArgumentNullException.ThrowIfNull(pick);
        if (!this.IsAttachedToVisualTree())
        {
            // 화면에 붙기 전에 SelectedItems.Add 하면 같은 줄이 두 번 들어간다 — 붙은 뒤에 고른다.
            _pendingSelect = () => SelectRows(pick, current);
            return;
        }

        _pendingSelect = null;
        _list.SelectedItems?.Clear();
        foreach (var item in _items)
        {
            if (item.Tag is IReadOnlyList<object?> row && pick(row))
            {
                _list.SelectedItems?.Add(item);
            }
        }

        if (current is not null)
        {
            foreach (var item in _items)
            {
                if (item.Tag is IReadOnlyList<object?> row && current(row))
                {
                    SetCurrent(row, raise: false);
                    break;
                }
            }
        }

        UpdateStatus();
    }

    /// <summary>고른 행과 현재 행(▶)을 모두 푼다(이벤트 없이).</summary>
    public void ClearSelection()
    {
        _list.SelectedItems?.Clear();
        if (_current is { } old && _markers.TryGetValue(old, out var m))
        {
            m.Text = string.Empty;
        }

        _current = null;
        UpdateStatus();
    }

    internal string StatusText => _status.Text ?? string.Empty;

    /// <summary>고른 행을 탭으로 나눈 글(TSV)로 만든다. <paramref name="withHeaders"/> 면 열 이름 줄이 앞에 붙는다. 고른 행이 없으면 빈 글.</summary>
    internal string BuildTsv(bool withHeaders)
    {
        var rows = SelectedRows;
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var columns = Columns ?? [];
        var sb = new StringBuilder();
        if (withHeaders)
        {
            sb.Append(string.Join('\t', columns.Select(Flat))).Append('\n');
        }

        foreach (var row in rows)
        {
            sb.Append(string.Join('\t', Enumerable.Range(0, columns.Count).Select(c => Flat(Format(c < row.Count ? row[c] : null))))).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>고른 행을 클립보드에 둔다(Ctrl+C·⌘C·「복사」). 고른 행이 없거나 클립보드를 못 쓰면 false.</summary>
    internal async Task<bool> CopyAsync(bool withHeaders)
    {
        var text = BuildTsv(withHeaders);
        if (text.Length == 0 || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return false;
        }

        try
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            return false; // 다른 프로그램이 클립보드를 쥐고 있으면(Windows) 이번 복사만 건너뛴다.
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StatusLineVisibleProperty)
        {
            _statusHost.IsVisible = StatusLineVisible;
        }
        else if (change.Property == ColumnsProperty || change.Property == RowNumbersProperty)
        {
            Build();
        }
        else if (change.Property == RowsProperty)
        {
            var keep = KeepSelectionOnSameShape && change.OldValue is IReadOnlyList<IReadOnlyList<object?>> old && change.NewValue is IReadOnlyList<IReadOnlyList<object?>> now && old.Count == now.Count;
            var pickedNow = keep ? PickedItems().ToHashSet() : [];
            var picked = keep ? _items.Select((item, i) => (item, i)).Where(x => pickedNow.Contains(x.item)).Select(x => x.i).ToList() : [];
            var current = keep ? CurrentRowIndex : -1;
            var hadFocus = keep && _list.IsKeyboardFocusWithin;
            var offset = KeepScroll ? _list.FindDescendantOfType<ScrollViewer>()?.Offset : null;
            Build();
            if (offset is { Y: > 0 } y)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_list.FindDescendantOfType<ScrollViewer>() is { } sv)
                    {
                        sv.Offset = y;
                    }
                }, Avalonia.Threading.DispatcherPriority.Loaded);
            }

            foreach (var i in picked.Where(i => i < _items.Count))
            {
                _list.SelectedItems?.Add(_items[i]);
            }

            if (current >= 0 && current < _items.Count)
            {
                SetCurrent((IReadOnlyList<object?>)_items[current].Tag!, raise: false);
                if (hadFocus)
                {
                    // 키보드로 이어서 옮겨 다닐 수 있게 새 줄에 포커스를 돌려준다(줄이 화면에 붙은 뒤).
                    var item = _items[current];
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => item.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
                }
            }
        }
    }

    private ContextMenu BuildMenu()
    {
        var copy = new MenuItem { Header = "복사" };
        copy.Click += (_, _) => _ = CopyAsync(false);
        var copyHeaders = new MenuItem { Header = "머리글 포함 복사" };
        copyHeaders.Click += (_, _) => _ = CopyAsync(true);
        var menu = new ContextMenu { ItemsSource = new[] { copy, copyHeaders } };
        menu.Opening += (_, e) =>
        {
            var extra = ExtraMenuItems?.Invoke() ?? [];
            menu.ItemsSource = extra.Count == 0 ? new object[] { copy, copyHeaders } : [copy, copyHeaders, new Separator(), .. extra];
            copy.IsEnabled = copyHeaders.IsEnabled = (_list.SelectedItems?.Count ?? 0) > 0;
            e.Cancel = _items.Count == 0;
        };
        return menu;
    }

    private void Build()
    {
        var columns = Columns ?? [];
        var rows = Rows ?? [];
        _items.Clear();
        _markers.Clear();
        _pendingSelect = null;
        _current = null;
        _overflow = 0;
        _list.ItemsSource = null;
        if (columns.Count == 0)
        {
            _headerHost.Child = null;
            _probeHost.Child = null;
            UpdateStatus();
            return;
        }

        var shown = Math.Min(rows.Count, MaxRows);
        _overflow = rows.Count - shown;
        for (var r = 0; r < shown; r++)
        {
            var row = rows[r];
            _items.Add(new ListBoxItem { Tag = row, Content = GridC.Row(Cells(row, r, columns.Count, register: true), rowHead: true, Group) });
        }

        _list.ItemsSource = _items.ToList();
        var titles = new List<string> { string.Empty };
        titles.AddRange(columns);
        _headerHost.Child = GridC.Header(titles, Group);

        // 칸 너비는 처음부터 전체 행 기준: 칸마다 글이 가장 넓은 행으로 숨은 줄을 만들어 같은 묶음에 참여시킨다(스크롤해도 안 바뀐다).
        var shownRows = rows.Take(shown).ToList();
        _probeHost.Child = GridC.Probe(
            shownRows,
            columns.Count + 1,
            (row, c) => c == 0 ? (RowNumbers ? shownRows.Count.ToString(CultureInfo.InvariantCulture).Length : 0) : GridC.TextWeight(Format(c - 1 < row.Count ? row[c - 1] : null)),
            row => Cells(row, shownRows.Count - 1, columns.Count, register: false),
            Group);
        UpdateStatus();
    }

    // 한 행의 칸들(행 머리 + 열마다 한 칸). register 가 거짓이면 현재 행 ▶ 표시를 등록하지 않는다(너비 재기용 사본).
    private List<Control> Cells(IReadOnlyList<object?> row, int r, int columnCount, bool register)
    {
        var marker = new TextBlock { Classes = { "grid-c-marker" } };
        if (register)
        {
            _markers[row] = marker;
        }

        var head = new DockPanel
        {
            MinWidth = 28,
            Children = { marker, new TextBlock { Text = RowNumbers ? (r + 1).ToString(CultureInfo.InvariantCulture) : string.Empty, Classes = { "num", "dim" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center } },
        };
        var cells = new List<Control> { head };
        for (var c = 0; c < columnCount; c++)
        {
            var value = c < row.Count ? row[c] : null;
            var cell = new TextBlock
            {
                Text = Format(value),
                TextAlignment = (IsNumber(value) || RightAlignedColumns?.Contains(c) == true) && c < columnCount - 1 ? TextAlignment.Right : TextAlignment.Left, // 늘어나는 마지막 열은 왼쪽
                Opacity = value is null ? 0.45 : 1,
            };
            if (MonospaceColumns?.Contains(c) == true)
            {
                cell.Bind(TextBlock.FontFamilyProperty, cell.GetResourceObservable("OrandaGridFontFamily"));
            }

            if (cell.Text.Length > 0 && CellEmphasis?.Invoke(c, row) == true)
            {
                cell.Bind(TextBlock.ForegroundProperty, cell.GetResourceObservable("OrandaDangerForegroundBrush"));
            }

            cells.Add(cell);
        }

        return cells;
    }

    private void UpdateStatus()
    {
        var total = Rows?.Count ?? 0;
        var picked = _list.SelectedItems?.Count ?? 0;
        var parts = new List<string> { En ? $"{total:N0} rows" : $"{total:N0}행" };
        if (picked > 0)
        {
            parts.Add(En ? $"{picked} selected" : $"{picked}개 선택");
        }

        if (_overflow > 0)
        {
            parts.Add(En ? $"… {_overflow:N0} more rows — narrow with Find" : $"… {_overflow:N0}행 더 있음 — 찾기로 좁혀 보세요");
        }

        _status.Text = string.Join(" · ", parts);
        if (picked != _lastPicked)
        {
            _lastPicked = picked;
            SelectionCountChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(_list).Properties;
        if (!props.IsLeftButtonPressed && !props.IsRightButtonPressed)
        {
            return;
        }

        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { Tag: IReadOnlyList<object?> row } hit)
        {
            if (props.IsRightButtonPressed)
            {
                // 오른쪽 클릭: 이미 고른 행 위면 선택을 그대로 두고(여러 행 복사), 아니면 그 행이 현재 행이 된다. 행을 누른 알림(RowActivated)은 왼쪽 클릭만.
                if (!hit.IsSelected)
                {
                    SetCurrent(row, raise: true);
                }

                return;
            }

            SetCurrent(row, raise: true);
            RowActivated?.Invoke(this, row);
        }
    }

    // 키보드로 옮겨 간 행도 현재 행(▶)으로 친다.
    private void OnListKeyUp(object? sender, KeyEventArgs e)
    {
        if ((TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.Tag is IReadOnlyList<object?> row)
        {
            SetCurrent(row, raise: true);
        }
    }

    private void SetCurrent(IReadOnlyList<object?> row, bool raise)
    {
        var changed = !ReferenceEquals(_current, row);
        if (_current is { } old && _markers.TryGetValue(old, out var before))
        {
            before.Text = string.Empty;
        }

        _current = row;
        if (_markers.TryGetValue(row, out var now))
        {
            now.Text = "▶";
        }

        if (raise && changed)
        {
            CurrentRowChanged?.Invoke(this, row);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        var keys = Application.Current?.PlatformSettings?.HotkeyConfiguration;
        if (keys?.Copy.Any(g => g.Matches(e)) == true)
        {
            e.Handled = true;
            _ = CopyAsync(false);
        }
        else if (keys?.SelectAll.Any(g => g.Matches(e)) == true)
        {
            e.Handled = true;
            _list.SelectAll();
        }
    }

    // TSV 한 칸에 탭·줄바꿈이 있으면 칸이 갈라지므로 공백으로 바꾼다.
    private static string Flat(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static bool IsNumber(object? v) => v is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static string Format(object? v) => v switch
    {
        null => string.Empty, // Orange 처럼 빈 칸
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? string.Empty,
    };
}
