using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Oranda.App.Shell.Icons;

namespace Oranda.App.Features.Navigation;

/// <summary>
/// 테이블 레이아웃 창(Orange 의 Alt+C). 위에 탭 줄, 아래에 선택한 탭의 본문(<see cref="TableLayoutView"/>)을 보인다.
/// 표 하나가 탭 하나이고, 새 표를 열면 이 창에 탭이 늘어난다(<see cref="TableLayoutWindows"/>). 탭 머리를 창 밖으로 끌면 별도 창으로 갈라지고
/// 다른 창의 탭 줄 위에 놓으면 그 창에 합쳐진다(판정은 <see cref="TableLayoutDrag"/>). 비모달이라 창을 연 채 편집기를 계속 쓸 수 있고
/// Esc 는 지금 탭만 닫으며 마지막 탭이면 창도 닫힌다.
/// 본문 컨트롤은 탭을 옮겨도 그대로 쓰이므로 거르기 글·고른 행·확인란·별칭이 유지된다.
/// </summary>
public sealed class TableLayoutWindow : Window
{
    /// <summary>탭 줄 높이(DIP). 처음 창 높이에 더해 표가 보이는 행 수는 탭이 없을 때와 같다.</summary>
    internal const double TabStripHeight = 26;

    /// <summary>한 줄 높이(DIP). Styles/GridC.axaml 의 ListBoxItem MinHeight 와 같다.</summary>
    internal const double RowHeight = 24;

    /// <summary>처음에 보이게 할 행 수.</summary>
    internal const int InitialRows = 30;

    // 표 밖의 높이(도구 막대 + 머리글 + 상태 줄 + 여유). 헤드리스 측정으로 맞춘 값(시험이 지킨다). 탭 줄 높이는 따로 더한다.
    internal const double ChromeHeight = 96;

    private sealed record Entry(TableLayoutView View, Border Header);

    private readonly List<Entry> _entries = [];
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal };
    private readonly Panel _bodies = new();
    private readonly Border _bar = new();
    private readonly TableLayoutWindows? _hub;
    private Entry? _current;

    // 탭 머리를 누른 채 끌고 있는 상태.
    private Entry? _pressed;
    private PixelPoint _pressPoint;
    private PixelPoint _grab;
    private bool _dragging;

    /// <summary>창 하나에 탭 하나로 연다(관리자 없이도 쓸 수 있다. 끌어 합치기는 관리자에 등록된 창끼리만 된다).</summary>
    public TableLayoutWindow(TableLayoutSession session)
        : this(new TableLayoutView(session ?? throw new ArgumentNullException(nameof(session))), null)
    {
    }

    internal TableLayoutWindow(TableLayoutView first, TableLayoutWindows? hub)
    {
        ArgumentNullException.ThrowIfNull(first);
        _hub = hub;
        Width = 760;
        MinWidth = 420;
        MinHeight = 180;
        Height = InitialHeight(first.Session.Model.Rows.Count, ScreenWorkHeight()) + TabStripHeight;
        ShowInTaskbar = false;
        Content = BuildFrame();
        AddView(first);

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => _current?.View.FocusList();
        Activated += (_, _) => _hub?.Touch(this);
        Closed += (_, _) => _hub?.Unregister(this);
        hub?.Register(this);
    }

    /// <summary>
    /// 첫 창 높이: 약 30행이 보이게 하되 컬럼이 적으면 그 행 수에 맞추고(최소 높이 아래로는 안 줄임), 화면 작업 영역 높이(<paramref name="workAreaHeight"/>, DIP)의 90% 를 넘지 않는다.
    /// 작업 영역을 모르면(0 이하) 줄이지 않는다. 탭 줄 높이(<see cref="TabStripHeight"/>)는 포함하지 않는다.
    /// </summary>
    internal static double InitialHeight(int columnCount, double workAreaHeight)
    {
        var rows = Math.Clamp(columnCount, 1, InitialRows);
        var height = ChromeHeight + (rows * RowHeight);
        if (workAreaHeight > 0)
        {
            height = Math.Min(height, workAreaHeight * 0.9);
        }

        return Math.Max(height, 180);
    }

    private double ScreenWorkHeight()
    {
        try
        {
            return Screens?.Primary is { } screen ? screen.WorkingArea.Height / screen.Scaling : 0;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    /// <summary>이 창의 탭 본문들(탭 줄 순서).</summary>
    internal IReadOnlyList<TableLayoutView> Views => _entries.Select(e => e.View).ToList();

    /// <summary>선택한 탭의 본문.</summary>
    internal TableLayoutView? Current => _current?.View;

    // 선택한 탭에 위임하는 접근자(시험·창 밖 호출용).
    internal IReadOnlyList<TableLayoutRow> VisibleRows => Current?.VisibleRows ?? [];

    internal IReadOnlyList<TableLayoutRow> SelectedRows => Current?.SelectedRows ?? [];

    internal ListBox List => Current!.List;

    internal TableLayoutRow? CurrentRow => Current?.CurrentRow;

    internal TextBox FilterBox => Current!.FilterBox;

    internal CheckBox AuditBox => Current!.AuditBox;

    internal string StatusText => Current?.StatusText ?? string.Empty;

    internal void Select(IEnumerable<TableLayoutRow> rows) => Current?.Select(rows);

    internal bool PasteSelection() => Current?.PasteSelection() == true;

    internal Task<bool> CopySelectionAsync() => Current?.CopySelectionAsync() ?? Task.FromResult(false);

    /// <summary>이미 떠 있는 창들에 탭으로 더해 연다. 편집기 View 가 없으면(시험) 아무것도 하지 않는다.</summary>
    /// <param name="host">편집기 탭이 닫히는 것을 알려 줄 곳(기본 = 앱의 편집기 호스트).</param>
    public static void Show(TableLayoutSession session, SqlEditor.SqlEditorHost? host = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Tab.Surface is not SqlEditor.SqlEditorView view || TopLevel.GetTopLevel(view) is not Window owner)
        {
            return;
        }

        TableLayoutWindows.Shared.Open(session, owner, host ?? SqlEditor.SqlEditorHost.Current);
    }

    /// <summary>창을 앞으로 가져온다. 최소화돼 있으면 먼저 되돌린다(최대화는 그대로).</summary>
    internal void Reveal()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <summary>탭을 더한다(<paramref name="index"/> 가 0 미만이면 맨 뒤). 더한 탭을 고른다.</summary>
    internal void AddView(TableLayoutView view, int index = -1)
    {
        ArgumentNullException.ThrowIfNull(view);
        var header = BuildHeader(view);
        var entry = new Entry(view, header);
        var at = index < 0 || index > _entries.Count ? _entries.Count : index;
        _entries.Insert(at, entry);
        _strip.Children.Insert(at, header);
        view.IsVisible = false;
        _bodies.Children.Add(view);
        Select(entry);
    }

    /// <summary>탭을 고른다.</summary>
    internal void SelectView(TableLayoutView view)
    {
        if (_entries.FirstOrDefault(e => ReferenceEquals(e.View, view)) is { } entry)
        {
            Select(entry);
        }
    }

    /// <summary>탭 하나를 닫는다. 마지막 탭이면 창도 닫는다.</summary>
    internal void CloseView(TableLayoutView view)
    {
        if (Detach(view) && _entries.Count == 0)
        {
            Close();
        }
    }

    /// <summary>탭을 이 창에서 떼어 낸다(본문 컨트롤은 다른 창에 붙일 수 있게 부모에서 빠진다). 떼었으면 true. 창은 닫지 않는다.</summary>
    internal bool Detach(TableLayoutView view)
    {
        var index = _entries.FindIndex(e => ReferenceEquals(e.View, view));
        if (index < 0)
        {
            return false;
        }

        var entry = _entries[index];
        _entries.RemoveAt(index);
        _strip.Children.Remove(entry.Header);
        _bodies.Children.Remove(entry.View);
        entry.View.IsVisible = true;
        if (ReferenceEquals(_current, entry))
        {
            _current = null;
            if (_entries.Count > 0)
            {
                Select(_entries[Math.Min(index, _entries.Count - 1)]);
            }
        }

        return true;
    }

    /// <summary>같은 창 안에서 탭 순서를 옮긴다(<paramref name="index"/> = 옮긴 뒤 번호).</summary>
    internal void MoveView(TableLayoutView view, int index)
    {
        var from = _entries.FindIndex(e => ReferenceEquals(e.View, view));
        if (from < 0)
        {
            return;
        }

        var entry = _entries[from];
        var to = Math.Clamp(index, 0, _entries.Count - 1);
        if (to == from)
        {
            return;
        }

        _entries.RemoveAt(from);
        _entries.Insert(to, entry);
        _strip.Children.Remove(entry.Header);
        _strip.Children.Insert(to, entry.Header);
    }

    /// <summary>탭을 다른 창으로 옮긴다: 이 창에서 떼어 <paramref name="target"/> 의 <paramref name="index"/> 자리에 붙이고 그 창을 앞으로 가져온다. 이 창에 탭이 없으면 닫는다.</summary>
    internal void MoveViewTo(TableLayoutView view, TableLayoutWindow target, int index = -1)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(target, this) || !Detach(view))
        {
            return;
        }

        target.AddView(view, index);
        target.Reveal();
        if (_entries.Count == 0)
        {
            Close();
        }
    }

    /// <summary>탭을 새 창으로 분리한다(탭이 하나뿐이면 아무것도 하지 않는다). 새 창은 이 창과 같은 크기이고 <paramref name="position"/> 에 놓인다.</summary>
    internal TableLayoutWindow? SplitOff(TableLayoutView view, PixelPoint? position = null)
    {
        if (_entries.Count < 2 || !Detach(view))
        {
            return null;
        }

        var window = new TableLayoutWindow(view, _hub)
        {
            Width = Bounds.Width > 0 ? Bounds.Width : Width,
            Height = Bounds.Height > 0 ? Bounds.Height : Height,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = position ?? new PixelPoint(Position.X + 32, Position.Y + 32),
        };
        if (Owner is Window owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }

        return window;
    }

    private void Select(Entry entry)
    {
        _current = entry;
        foreach (var e in _entries)
        {
            var on = ReferenceEquals(e, entry);
            e.View.IsVisible = on;
            ApplyHeaderState(e.Header, on);
        }

        Title = entry.View.TabTitle;
        _hub?.Touch(this);
        Dispatcher.UIThread.Post(() => entry.Header.BringIntoView(), DispatcherPriority.Loaded); // 넘친 탭도 고르면 보이게
    }

    private DockPanel BuildFrame()
    {
        var bar = _bar;
        bar.BorderThickness = new Thickness(0, 0, 0, 1);
        bar.Height = TabStripHeight;
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _strip,
        };

        // 막대가 숨겨져 있으니 휠을 가로 이동으로 바꾼다(탭이 넘쳐도 닿을 수 있게).
        scroll.PointerWheelChanged += (_, e) =>
        {
            var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
            scroll.Offset = new Vector(Math.Max(0, scroll.Offset.X - (delta * 40)), 0);
            e.Handled = true;
        };
        bar.Child = scroll;
        bar.Bind(Border.BackgroundProperty, bar.GetResourceObservable("OrandaToolbarBackgroundBrush"));
        bar.Bind(Border.BorderBrushProperty, bar.GetResourceObservable("OrandaSeparatorBrush"));

        var root = new DockPanel();
        DockPanel.SetDock(bar, Avalonia.Controls.Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_bodies);
        return root;
    }

    private Border BuildHeader(TableLayoutView view)
    {
        var title = new TextBlock
        {
            Text = view.TabTitle,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 180,
            FontSize = 12,
        };
        var close = new Button
        {
            Content = "탭 닫기",
            Focusable = false,
            Padding = new Thickness(2),
            MinHeight = 0,
            MinWidth = 0,
            Margin = new Thickness(6, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ButtonIcon.SetKey(close, "clear");
        ButtonIcon.SetIconOnly(close, true);
        ToolTip.SetTip(close, "탭 닫기");
        AutomationProperties.SetName(close, "탭 닫기");
        close.PointerEntered += (_, _) => close.Opacity = 1;
        close.PointerExited += (_, _) => close.Opacity = 0.6;
        close.Click += (_, e) =>
        {
            e.Handled = true;
            CloseView(view);
        };

        var header = new Border
        {
            Padding = new Thickness(8, 0, 4, 0),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { title, close } },
            Tag = view,
        };
        header.Bind(Border.BorderBrushProperty, header.GetResourceObservable("OrandaSeparatorBrush"));
        ToolTip.SetTip(header, view.TabTitle);
        header.PointerPressed += (_, e) => OnHeaderPressed(view, e);
        header.PointerMoved += (_, e) => OnHeaderMoved(e);
        header.PointerReleased += (_, e) => OnHeaderReleased(e);
        header.PointerCaptureLost += (_, _) => ResetDrag();

        var split = new MenuItem { Header = "새 창으로 분리" };
        split.Click += (_, _) => SplitOff(view);
        var closeItem = new MenuItem { Header = "탭 닫기" };
        closeItem.Click += (_, _) => CloseView(view);
        var menu = new ContextMenu { Items = { split, closeItem } };
        menu.Opening += (_, _) => split.IsEnabled = _entries.Count > 1;
        header.ContextMenu = menu;
        return header;
    }

    private static void ApplyHeaderState(Border header, bool selected)
        => header.Bind(Border.BackgroundProperty, header.GetResourceObservable(selected ? "OrandaTableSelectionBrush" : "OrandaToolbarBackgroundBrush"));

    private PixelPoint ScreenPoint(PointerEventArgs e) => this.PointToScreen(e.GetPosition(this));

    private void OnHeaderPressed(TableLayoutView view, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        if (_entries.FirstOrDefault(x => ReferenceEquals(x.View, view)) is not { } entry)
        {
            return;
        }

        Select(entry);
        view.FocusList();
        _pressed = entry;
        _pressPoint = ScreenPoint(e);
        _grab = new PixelPoint(_pressPoint.X - Position.X, _pressPoint.Y - Position.Y);
        _dragging = false;
        e.Pointer.Capture(entry.Header);
    }

    private void OnHeaderMoved(PointerEventArgs e)
    {
        if (_pressed is null || _dragging)
        {
            return;
        }

        if (TableLayoutDrag.Exceeds(_pressPoint, ScreenPoint(e), TableLayoutDrag.ThresholdDip * ScreenUnitsPerDip()))
        {
            _dragging = true;
            _pressed.Header.Opacity = 0.6;
        }
    }

    private void OnHeaderReleased(PointerReleasedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased)
        {
            return; // 왼쪽으로 끄는 중에 다른 버튼을 놓은 것은 끌기의 끝이 아니다.
        }

        var entry = _pressed;
        var dragged = _dragging;
        var point = ScreenPoint(e);
        e.Pointer.Capture(null);
        ResetDrag();
        if (entry is not null && dragged)
        {
            CompleteDrag(entry.View, point);
        }
    }

    private void ResetDrag()
    {
        if (_pressed is not null)
        {
            _pressed.Header.Opacity = 1;
        }

        _pressed = null;
        _dragging = false;
    }

    // 놓은 자리로 무엇을 할지는 순수 함수가 정한다. 이 창을 맨 앞에 둔 창 목록(겹치면 위쪽 우선)으로 판정한다.
    private void CompleteDrag(TableLayoutView view, PixelPoint point)
    {
        var windows = new List<TableLayoutWindow> { this };
        windows.AddRange(_hub?.WindowsByRecency().Where(w => !ReferenceEquals(w, this) && w.WindowState != WindowState.Minimized) ?? []); // 최소화된 창은 합칠 곳이 아니다
        var drop = TableLayoutDrag.Decide(point, windows.Select(w => w.Frame()).ToList(), 0, _entries.Count == 1);
        switch (drop.Kind)
        {
            case TabDropKind.Reorder:
                MoveView(view, TableLayoutDrag.ReorderedIndex(_entries.FindIndex(e => ReferenceEquals(e.View, view)), drop.Index));
                break;
            case TabDropKind.Merge:
                MoveViewTo(view, windows[drop.Window], drop.Index);
                break;
            case TabDropKind.Detach:
                SplitOff(view, InWorkArea(TableLayoutDrag.Dropped(point, _grab), point));
                break;
            case TabDropKind.MoveWindow:
                Position = InWorkArea(TableLayoutDrag.Dropped(point, _grab), point);
                break;
            default:
                break;
        }
    }

    // 놓은 자리가 있는 모니터의 작업 영역 안으로 창 위치를 붙잡는다(제목 줄이 화면 밖으로 나가지 않게).
    private PixelPoint InWorkArea(PixelPoint position, PixelPoint pointer)
    {
        var area = (Screens.ScreenFromPoint(pointer) ?? Screens.ScreenFromWindow(this) ?? Screens.Primary)?.WorkingArea;
        return area is { } a ? TableLayoutDrag.ClampToArea(position, Frame().Window.Size, a) : position;
    }

    /// <summary>탭 끌기 판정에 쓰는 이 창의 화면 좌표(픽셀) 배치.</summary>
    internal TabFrame Frame()
    {
        // 크기에 배율을 곱하지 않고 두 모서리를 화면 좌표로 바꾼다: 플랫폼마다 창 위치·크기의 배율이 달라도(macOS 의 점 단위 등) 같은 좌표 공간에서 비교된다.
        PixelRect Of(Control c)
        {
            var a = this.PointToScreen(c.TranslatePoint(default, this) ?? default);
            var b = this.PointToScreen(c.TranslatePoint(new Point(c.Bounds.Width, c.Bounds.Height), this) ?? default);
            return new PixelRect(a, b);
        }

        // 창 전체는 제목 줄까지 포함하도록 왼쪽 위를 Position(창 틀 기준)으로 잡는다.
        var window = new PixelRect(Position, this.PointToScreen(new Point(ClientSize.Width, ClientSize.Height)));
        return new TabFrame(window, Of(_bar), _entries.Select(e => Of(e.Header)).ToList());
    }

    // 화면 좌표 1 DIP 의 길이(끌기 문턱 환산).
    private double ScreenUnitsPerDip()
    {
        var origin = this.PointToScreen(default);
        var unit = this.PointToScreen(new Point(100, 0));
        var units = (unit.X - origin.X) / 100.0;
        return units > 0 ? units : 1;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _current is { } entry)
        {
            e.Handled = true;
            CloseView(entry.View);
            return;
        }

        // 별칭·거르기 칸 밖의 Enter·복사·전체 선택은 선택한 탭이 처리한다.
        _current?.View.HandleKey(e);
    }
}
