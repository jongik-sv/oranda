using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Oranda.App.Shell.Controls;

/// <summary>
/// 머리글 Grid + 줄 템플릿 Grid 로 만든 표(ListBox <c>Classes="table"</c>·<c>grid-c</c>)의 열 폭 끌기.
/// 붙이는 법: 표를 감싼 컨트롤에 <c>ctl:TableColumns.Scope="화면-키"</c>, 머리글 Grid 에 <c>ctl:TableColumns.Header="True"</c>,
/// 줄 템플릿 Grid 에 <c>ctl:TableColumns.Row="True"</c>. 머리글과 줄의 <c>ColumnDefinitions</c> 는 같은 개수로 둔다.
/// <list type="bullet">
/// <item>머리글의 픽셀 열(<c>124</c>)은 오른쪽 경계를 끌어 폭을 바꾼다. 최소 폭 = 그 열의 <c>MinWidth</c>(없으면 24).</item>
/// <item>마지막 <c>*</c> 열은 남은 폭을 받는다. 그 열의 오른쪽 경계를 끌면 바로 뒤 픽셀 열이 반대로 바뀐다. 다른 * 열·묶음 없는 Auto 열은 끌 수 없다.</item>
/// <item><c>SharedSizeGroup</c> 을 단 Auto 열(내용 폭 표, <c>Styles/GridC</c>)은 처음엔 내용 폭이고, 끌면 그 폭(픽셀)이 된다. 두 번 누르기·「열 폭 처음으로」 = 다시 내용 폭.</item>
/// <item>두 번 누르면 그 열은 처음 폭(XAML 값)으로 돌아간다. 머리글 오른쪽 클릭 「열 폭 처음으로」 = 모든 열.</item>
/// <item>폭은 화면 키마다 앱 데이터 폴더의 <c>table-columns.json</c> 에 기본과 다른 열만 남는다(<c>plan-columns.json</c> 과 같은 관례: 손상·IO 오류 = 기본 폭).</item>
/// </list>
/// 줄 Grid 는 화면에 붙을 때 범위(<see cref="ScopeProperty"/>)를 찾아 등록하고, 떨어지면 빠진다(가상화로 다시 쓰이는 줄도 같다).
/// </summary>
public static class TableColumns
{
    /// <summary>표 범위. 값 = 폭을 기억하는 화면 키(<c>change-journal</c> …).</summary>
    public static readonly AttachedProperty<string?> ScopeProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, string?>("Scope");

    /// <summary>머리글 Grid 표시. 열 폭의 정본이며 경계에 끌기 손잡이가 붙는다.</summary>
    public static readonly AttachedProperty<bool> HeaderProperty =
        AvaloniaProperty.RegisterAttached<Grid, Grid, bool>("Header");

    /// <summary>줄 Grid 표시. 머리글의 열 폭을 따라간다.</summary>
    public static readonly AttachedProperty<bool> RowProperty =
        AvaloniaProperty.RegisterAttached<Grid, Grid, bool>("Row");

    private static readonly AttachedProperty<TableColumnState?> StateProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, TableColumnState?>("State");

    private static readonly AttachedProperty<TableColumnState?> JoinedProperty =
        AvaloniaProperty.RegisterAttached<Grid, Grid, TableColumnState?>("Joined");

    static TableColumns()
    {
        ScopeProperty.Changed.AddClassHandler<Control>((host, e) =>
            host.SetValue(StateProperty, e.NewValue is string { Length: > 0 } key ? new TableColumnState(key) : null));
        HeaderProperty.Changed.AddClassHandler<Grid>((grid, e) => Hook(grid, e.NewValue is true, header: true));
        RowProperty.Changed.AddClassHandler<Grid>((grid, e) => Hook(grid, e.NewValue is true, header: false));
    }

    /// <summary>폭을 저장할 폴더(시험이 바꾼다). null = 저장하지 않음.</summary>
    internal static Func<string?> DataDirectoryProvider { get; set; } = () =>
        (Application.Current as App)?.Services?.GetService<Oranda.Core.Storage.IAppDataPaths>()?.DataDirectory;

    public static string? GetScope(Control control) => control.GetValue(ScopeProperty);

    public static void SetScope(Control control, string? value) => control.SetValue(ScopeProperty, value);

    public static bool GetHeader(Grid grid) => grid.GetValue(HeaderProperty);

    public static void SetHeader(Grid grid, bool value) => grid.SetValue(HeaderProperty, value);

    public static bool GetRow(Grid grid) => grid.GetValue(RowProperty);

    public static void SetRow(Grid grid, bool value) => grid.SetValue(RowProperty, value);

    private static void Hook(Grid grid, bool on, bool header)
    {
        grid.AttachedToVisualTree -= header ? OnHeaderAttached : OnRowAttached;
        grid.DetachedFromVisualTree -= OnDetached;
        if (!on)
        {
            return;
        }

        grid.AttachedToVisualTree += header ? OnHeaderAttached : OnRowAttached;
        grid.DetachedFromVisualTree += OnDetached;
        if (grid.IsAttachedToVisualTree())
        {
            (header ? (EventHandler<VisualTreeAttachmentEventArgs>)OnHeaderAttached : OnRowAttached)(grid, null!);
        }
    }

    private static TableColumnState? FindState(Grid grid) =>
        grid.GetVisualAncestors().OfType<Control>().Select(c => c.GetValue(StateProperty)).FirstOrDefault(s => s is not null);

    private static void OnHeaderAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Grid grid && FindState(grid) is { } state)
        {
            grid.SetValue(JoinedProperty, state);
            state.SetHeader(grid);
        }
    }

    private static void OnRowAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Grid grid && FindState(grid) is { } state)
        {
            grid.SetValue(JoinedProperty, state);
            state.AddRow(grid);
        }
    }

    // 떨어질 때는 조상을 다시 찾지 않고 붙을 때 기억한 범위에서 뺀다.
    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Grid grid && grid.GetValue(JoinedProperty) is { } state)
        {
            grid.ClearValue(JoinedProperty);
            state.Remove(grid);
        }
    }

    // ---- 저장 -------------------------------------------------------------------------------

    /// <summary>저장 파일 이름.</summary>
    public const string FileName = "table-columns.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static Dictionary<string, double> Load(string key)
    {
        try
        {
            if (DataDirectoryProvider() is not { } dir || !File.Exists(Path.Combine(dir, FileName)))
            {
                return [];
            }

            var all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(File.ReadAllText(Path.Combine(dir, FileName)));
            return all?.GetValueOrDefault(key) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return []; // 손상·읽기 실패 = 기본 폭
        }
    }

    internal static void Save(string key, Dictionary<string, double> changed)
    {
        try
        {
            if (DataDirectoryProvider() is not { } dir)
            {
                return;
            }

            var file = Path.Combine(dir, FileName);
            Dictionary<string, Dictionary<string, double>> all = [];
            try
            {
                if (File.Exists(file))
                {
                    all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(File.ReadAllText(file)) ?? [];
                }
            }
            catch (JsonException)
            {
                // 손상된 파일은 새로 쓴다.
            }

            if (changed.Count == 0)
            {
                all.Remove(key);
            }
            else
            {
                all[key] = changed;
            }

            Directory.CreateDirectory(dir);
            File.WriteAllText(file, JsonSerializer.Serialize(all, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 폭 저장 실패는 화면 동작을 막지 않는다.
        }
    }
}

/// <summary>
/// 한 표 범위의 열 폭. 머리글 Grid 의 첫 등록이 열의 기본·최소 폭을 정한다(머리글의 열 개수가 바뀌면 다시 정한다). UI 스레드에서 쓴다.
/// 픽셀 열의 폭은 늘 값이 있고, 공유 묶음 Auto 열은 끌기 전에는 값이 없다(NaN = 내용 폭, <c>SharedSizeGroup</c> 그대로). Auto 열을 끌면 그 열은 머리글·줄 모두 픽셀이 되고
/// 공유 묶음에서 빠진다(숨은 폭 재기 줄이 묶음에 남으면 내용 폭 아래로 줄지 않는다). 처음 폭으로 되돌리면 Auto 와 묶음 이름이 돌아온다.
/// </summary>
internal sealed class TableColumnState(string scopeKey)
{
    /// <summary>폭의 위쪽 한계(실수로 화면 밖까지 끄는 것을 막는다).</summary>
    public const double MaxWidth = 1200;

    /// <summary>MinWidth 가 없는 열의 최소 폭.</summary>
    public const double DefaultMin = 24;

    private readonly List<Grid> _rows = [];
    private Grid? _header;
    private readonly string _scopeKey = scopeKey;
    private string _key = scopeKey;
    private double[] _defaults = [];
    private double[] _mins = [];
    private double[] _widths = [];
    private bool[] _auto = [];
    private string?[] _groups = [];
    private double[] _caps = [];

    /// <summary>열마다 폭(픽셀 열·끈 Auto 열만, 나머지 NaN).</summary>
    public IReadOnlyList<double> Widths => _widths;

    /// <summary>남은 폭을 받는 열(마지막 * 열). 없으면 -1.</summary>
    public int Fill { get; private set; } = -1;

    public void SetHeader(Grid header)
    {
        if (_header == header)
        {
            return;
        }

        _header = header;
        if (_defaults.Length != header.ColumnDefinitions.Count)
        {
            Init(header.ColumnDefinitions);
        }

        AddGrips(header);
        if (header.ContextMenu is null)
        {
            var reset = new MenuItem { Header = "열 폭 처음으로" };
            reset.Click += (_, _) =>
            {
                ResetAll();
                Save();
            };
            header.ContextMenu = new ContextMenu { Items = { reset } };
            header.Background ??= Brushes.Transparent; // 글 없는 칸에서도 오른쪽 클릭이 머리글에 닿게
        }

        Apply(header);
        foreach (var row in _rows)
        {
            Apply(row);
        }
    }

    // 머리글의 열 정의로 기본·최소 폭과 끌 수 있는 열을 정하고, 그 열 개수의 저장 폭을 읽는다.
    private void Init(ColumnDefinitions defs)
    {
        _defaults = defs.Select(d => d.Width.IsAbsolute ? d.Width.Value : double.NaN).ToArray();
        _mins = defs.Select(d => d.MinWidth > 0 ? d.MinWidth : DefaultMin).ToArray();
        _auto = defs.Select(d => d.Width.IsAuto && d.SharedSizeGroup is not null).ToArray(); // 묶음 없는 Auto 열(내용이 폭을 정하는 칸)은 그대로 끌 수 없다
        _groups = defs.Select(d => d.Width.IsAuto ? d.SharedSizeGroup : null).ToArray();
        _widths = (double[])_defaults.Clone();
        _caps = defs.Select(_ => MaxWidth).ToArray();
        Fill = -1;
        for (var i = defs.Count - 1; i >= 0; i--)
        {
            if (defs[i].Width.IsStar)
            {
                Fill = i;
                break;
            }
        }

        _key = _scopeKey + "/" + defs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture); // 열 개수가 바뀐 XAML 에 옛 폭이 붙지 않게
        foreach (var (name, width) in TableColumns.Load(_key))
        {
            if (int.TryParse(name, System.Globalization.CultureInfo.InvariantCulture, out var i) && i >= 0 && i < _widths.Length
                && Sizable(i) && double.IsFinite(width))
            {
                _widths[i] = Clamp(i, width);
            }
        }
    }

    // 끌 수 있는 열 = 픽셀 열·공유 묶음 Auto 열.
    private bool Sizable(int column) => !double.IsNaN(_defaults[column]) || _auto[column];

    public void AddRow(Grid row)
    {
        if (!_rows.Contains(row))
        {
            _rows.Add(row);
        }

        Apply(row);
    }

    public void Remove(Grid grid)
    {
        _rows.Remove(grid);
        if (_header == grid)
        {
            _header = null;
        }
    }

    /// <summary>경계 <paramref name="boundary"/>(그 열의 오른쪽)를 끌 때 바뀌는 열과 방향. 끌 수 없으면 null.</summary>
    public (int Column, int Sign)? Target(int boundary)
    {
        if (boundary < 0 || boundary >= _widths.Length)
        {
            return null;
        }

        if (Sizable(boundary))
        {
            return (boundary, 1);
        }

        return boundary == Fill && boundary + 1 < _widths.Length && Sizable(boundary + 1) ? (boundary + 1, -1) : null;
    }

    /// <summary>열의 지금 폭. 끌기 전 Auto 열은 머리글에 그려진 폭(내용 폭).</summary>
    public double Get(int column)
    {
        if (!double.IsNaN(_widths[column]))
        {
            return _widths[column];
        }

        var actual = _header is { } h && column < h.ColumnDefinitions.Count ? h.ColumnDefinitions[column].ActualWidth : _mins[column];
        _caps[column] = Math.Max(MaxWidth, actual); // 내용 폭이 위쪽 한계보다 넓은 칸이 끌기 시작에 좁아지며 튀지 않게
        return actual;
    }

    public void Set(int column, double width)
    {
        if (!Sizable(column) || !double.IsFinite(width))
        {
            return;
        }

        var value = Clamp(column, width);
        if (_widths[column] == value)
        {
            return;
        }

        _widths[column] = value;
        ApplyAll();
    }

    /// <summary>열을 처음 폭으로: 픽셀 열 = XAML 값, Auto 열 = 다시 내용 폭(공유 묶음).</summary>
    public void Reset(int column)
    {
        if (_auto[column])
        {
            if (!double.IsNaN(_widths[column]))
            {
                _widths[column] = double.NaN;
                ApplyAll();
            }

            return;
        }

        Set(column, _defaults[column]);
    }

    /// <summary>모든 열을 처음 폭으로(손잡이가 화면 밖으로 밀려 두 번 누를 수 없을 때 머리글 오른쪽 클릭 메뉴로).</summary>
    public void ResetAll()
    {
        for (var i = 0; i < _widths.Length; i++)
        {
            if (Sizable(i))
            {
                Reset(i);
            }
        }
    }

    /// <summary>기본과 다른 열만 저장한다(Auto 열은 끈 것만).</summary>
    public void Save()
    {
        var changed = new Dictionary<string, double>();
        for (var i = 0; i < _widths.Length; i++)
        {
            if (!double.IsNaN(_widths[i]) && _widths[i] != _defaults[i])
            {
                changed[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = _widths[i];
            }
        }

        TableColumns.Save(_key, changed);
    }

    private double Clamp(int column, double width) =>
        Math.Round(Math.Clamp(width, _mins[column], Math.Max(_mins[column], _caps[column])));

    private void ApplyAll()
    {
        if (_header is not null)
        {
            Apply(_header);
        }

        foreach (var row in _rows)
        {
            Apply(row);
        }
    }

    private void Apply(Grid grid)
    {
        var defs = grid.ColumnDefinitions;
        for (var i = 0; i < _widths.Length && i < defs.Count; i++)
        {
            var d = defs[i];
            if (!double.IsNaN(_widths[i]))
            {
                if (_auto[i] && d.SharedSizeGroup is not null)
                {
                    d.SharedSizeGroup = null; // 묶음에 남으면 다른 줄(숨은 폭 재기 줄)의 내용 폭이 이 폭을 덮는다
                }

                if (d.Width != new GridLength(_widths[i]))
                {
                    d.Width = new GridLength(_widths[i]);
                }
            }
            else if (_auto[i])
            {
                if (!d.Width.IsAuto)
                {
                    d.Width = GridLength.Auto;
                }

                if (d.SharedSizeGroup != _groups[i])
                {
                    d.SharedSizeGroup = _groups[i];
                }
            }
        }
    }

    private void AddGrips(Grid header)
    {
        if (header.Children.OfType<TableColumnGrip>().Any())
        {
            return;
        }

        for (var i = 0; i < _widths.Length; i++)
        {
            if (Target(i) is not null)
            {
                var grip = new TableColumnGrip(this, i);
                Grid.SetColumn(grip, i);
                Grid.SetRowSpan(grip, Math.Max(1, header.RowDefinitions.Count));
                header.Children.Add(grip);
            }
        }
    }
}

/// <summary>머리글 열 오른쪽 경계의 끌기 손잡이. 끌면 열 폭이 바뀌고, 두 번 누르면 처음 폭. 놓을 때 한 번 저장한다.</summary>
internal sealed class TableColumnGrip : Control
{
    private readonly TableColumnState _state;
    private double _startX;
    private double _startWidth;
    private (int Column, int Sign)? _drag;

    public TableColumnGrip(TableColumnState state, int boundary)
    {
        _state = state;
        Boundary = boundary;
        Width = 7;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        Margin = new Thickness(0, 0, -3, 0); // 경계선 양쪽에 걸친다
        ZIndex = 10;
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
    }

    /// <summary>이 손잡이가 있는 열(그 열의 오른쪽 경계).</summary>
    public int Boundary { get; }

    /// <summary>보이지 않는 손잡이도 눌리게 투명 바탕을 깐다.</summary>
    public override void Render(DrawingContext context) => context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _state.Target(Boundary) is not { } target)
        {
            return;
        }

        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            _state.Reset(target.Column);
            _state.Save();
            return;
        }

        _drag = target;
        _startX = e.GetPosition(TopLevel.GetTopLevel(this)).X; // 스크롤로 머리글이 움직여도 튀지 않게 창 기준
        _startWidth = _state.Get(target.Column);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is { } d)
        {
            _state.Set(d.Column, _startWidth + (d.Sign * (e.GetPosition(TopLevel.GetTopLevel(this)).X - _startX)));
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is not null)
        {
            _drag = null;
            e.Pointer.Capture(null);
            e.Handled = true;
            _state.Save();
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag is not null)
        {
            _drag = null;
            _state.Save();
        }
    }
}
