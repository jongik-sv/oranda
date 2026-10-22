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
    private readonly Dictionary<IReadOnlyList<object?>, TextBlock> _markers = new(ReferenceEqualityComparer.Instance);
    private readonly Border _headerHost = new();
    private readonly List<ListBoxItem> _items = [];
    private IReadOnlyList<object?>? _current;
    private int _overflow;

    public DetailTable()
    {
        var status = new Border { Classes = { "panel-status" }, Child = _status };
        DockPanel.SetDock(status, Avalonia.Controls.Dock.Bottom);
        var root = new DockPanel();
        root.Children.Add(status);
        root.Children.Add(_scroll);
        Content = root;

        var body = new DockPanel();
        DockPanel.SetDock(_headerHost, Avalonia.Controls.Dock.Top);
        body.Children.Add(_headerHost);
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

    /// <summary>행을 눌렀다. 값은 그 행의 칸들.</summary>
    public event EventHandler<IReadOnlyList<object?>>? RowActivated;

    /// <summary>고른 행(표 순서).</summary>
    internal IReadOnlyList<IReadOnlyList<object?>> SelectedRows
        => _items.Where(i => i.IsSelected).Select(i => (IReadOnlyList<object?>)i.Tag!).ToList();

    internal ListBox List => _list;

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
        if (change.Property == ColumnsProperty || change.Property == RowsProperty)
        {
            Build();
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
            copy.IsEnabled = copyHeaders.IsEnabled = _items.Any(i => i.IsSelected);
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
        _current = null;
        _overflow = 0;
        _list.ItemsSource = null;
        if (columns.Count == 0)
        {
            _headerHost.Child = null;
            UpdateStatus();
            return;
        }

        var shown = Math.Min(rows.Count, MaxRows);
        _overflow = rows.Count - shown;
        for (var r = 0; r < shown; r++)
        {
            var row = rows[r];
            var marker = new TextBlock { Classes = { "grid-c-marker" } };
            _markers[row] = marker;
            var head = new DockPanel
            {
                MinWidth = 28,
                Children = { marker, new TextBlock { Text = (r + 1).ToString(CultureInfo.InvariantCulture), Classes = { "num", "dim" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center } },
            };
            var cells = new List<Control> { head };
            for (var c = 0; c < columns.Count; c++)
            {
                var value = c < row.Count ? row[c] : null;
                cells.Add(new TextBlock
                {
                    Text = Format(value),
                    TextAlignment = IsNumber(value) && c < columns.Count - 1 ? TextAlignment.Right : TextAlignment.Left, // 늘어나는 마지막 열은 왼쪽
                    Opacity = value is null ? 0.45 : 1,
                });
            }

            _items.Add(new ListBoxItem { Tag = row, Content = GridC.Row(cells, rowHead: true, Group) });
        }

        _list.ItemsSource = _items.ToList();
        var titles = new List<string> { string.Empty };
        titles.AddRange(columns);
        _headerHost.Child = GridC.Header(titles, Group);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var total = Rows?.Count ?? 0;
        var picked = _items.Count(i => i.IsSelected);
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
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.Tag is IReadOnlyList<object?> row)
        {
            SetCurrent(row);
            RowActivated?.Invoke(this, row);
        }
    }

    // 키보드로 옮겨 간 행도 현재 행(▶)으로 친다.
    private void OnListKeyUp(object? sender, KeyEventArgs e)
    {
        if ((TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.Tag is IReadOnlyList<object?> row)
        {
            SetCurrent(row);
        }
    }

    private void SetCurrent(IReadOnlyList<object?> row)
    {
        if (_current is { } old && _markers.TryGetValue(old, out var before))
        {
            before.Text = string.Empty;
        }

        _current = row;
        if (_markers.TryGetValue(row, out var now))
        {
            now.Text = "▶";
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
