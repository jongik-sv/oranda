using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Oranda.App.Features.Localization;
using Oranda.App.Shell.Icons;
using Oranda.App.Styles;

namespace Oranda.App.Features.Navigation;

/// <summary>
/// 테이블 레이아웃 창(Orange 의 Alt+C). 도구 막대(넣기·복사 | 옵션 토글 4개 | 별칭 · 거르기) / 격자 표(행 머리 칸·현재 행 ▶) /
/// 상태 줄(객체·종류·컬럼 수·선택 수·PK | 키 힌트).
/// 행은 클릭·Shift(범위)·⌘ 또는 Ctrl(개별)·⌘A 로 고르고, Enter·「넣기」·두 번 누르기로 편집기에 넣고 ⌘C 로 같은 글을 복사한다.
/// 넣는 글은 늘 표 순서다(<see cref="TableLayoutPaste.Build"/>). 비모달이라 창을 연 채 편집기를 계속 쓸 수 있고 Esc 로 닫는다.
/// </summary>
public sealed class TableLayoutWindow : Window
{
    // 「형식」「NULL」은 literals-en.json 의 기존 번역(Format·Nullable)과 달라서 언어를 보고 직접 고른다.
    private static readonly string[] HeadersKo = ["", "이름", "형식", "Null", "기본값", "주석"];
    private static readonly string[] HeadersEn = ["", "Name", "Type", "Null", "Default", "Comment"];
    private const string Group = "tl";
    private static TableLayoutWindow? s_open;

    private readonly TableLayoutSession _session;
    private readonly TextBox _alias = new() { Classes = { "compact" }, Width = 64, PlaceholderText = "별칭", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _filter = new() { Classes = { "compact", "search" }, Width = 150, PlaceholderText = "컬럼 거르기", VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _comma = Toggle(Glyph(","), "쉼표 넣기");
    private readonly ToggleButton _lower = Toggle(Glyph("aa"), "소문자");
    private readonly ToggleButton _pk = Toggle(ShellIcons.Create("key", 14)!, "PK 표시");
    private readonly ToggleButton _comment = Toggle(Glyph("--"), "주석 넣기");
    private readonly CheckBox _audit = new()
    {
        Content = new TextBlock { Text = "감사 컬럼 빼기", FontSize = 12 },
        MinHeight = 0,
        Padding = new Thickness(4, 0, 0, 0),
        Margin = new Thickness(6, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Focusable = false,
    };

    private readonly Border _probeHost = new(); // 전체 컬럼 기준 칸 너비를 고정하는 숨은 줄(GridC.Probe)
    private int _hidden;
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Dictionary<TableLayoutRow, TextBlock> _markers = [];
    private TableLayoutRow? _current;
    private readonly ListBox _list = new()
    {
        Classes = { "grid-c" },
        SelectionMode = SelectionMode.Multiple,
    };

    // 두 번 누르기의 첫 클릭이 선택을 그 행 하나로 줄이기 전에 잡아 둔 선택(누른 행이 이 안에 있으면 선택 전체를 넣는다).
    private List<TableLayoutRow> _pressSelection = [];

    public TableLayoutWindow(TableLayoutSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        Title = session.Model.Title;
        Width = 760;
        MinWidth = 420;
        MinHeight = 180;
        Height = InitialHeight(session.Model.Rows.Count, ScreenWorkHeight());
        ShowInTaskbar = false;
        Content = BuildContent();

        var o = session.Options;
        _alias.Text = o.Alias;
        _comma.IsChecked = o.InsertComma;
        _lower.IsChecked = o.LowerCase;
        _pk.IsChecked = o.ShowPk;
        _comment.IsChecked = o.ShowComment;
        var hasAudit = TableLayoutAudit.Parse(o.AuditColumns).Count > 0;
        _audit.IsEnabled = hasAudit;
        _audit.IsChecked = hasAudit && o.HideAudit;
        ToolTip.SetTip(_audit, hasAudit ? "설정의 감사 컬럼 목록에 맞는 컬럼을 표에서 뺀다" : "설정에서 감사 컬럼을 정하세요");
        AutomationProperties.SetName(_audit, "감사 컬럼 빼기");
        Rebuild();

        _alias.TextChanged += (_, _) => OptionsChanged();
        _filter.TextChanged += (_, _) => Rebuild();
        foreach (var t in new[] { _comma, _lower, _pk, _comment })
        {
            t.IsCheckedChanged += (_, _) => OptionsChanged();
        }

        _audit.IsCheckedChanged += (_, _) => OptionsChanged();

        _list.SelectionChanged += OnSelectionChanged;
        _list.AddHandler(PointerPressedEvent, OnListPressed, RoutingStrategies.Tunnel);
        _list.DoubleTapped += OnDoubleTapped;
        _list.KeyUp += OnListKeyUp;
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

    /// <summary>한 줄 높이(DIP). Styles/GridC.axaml 의 ListBoxItem MinHeight 와 같다.</summary>
    internal const double RowHeight = 24;

    /// <summary>처음에 보이게 할 행 수.</summary>
    internal const int InitialRows = 30;

    // 표 밖의 높이(도구 막대 + 머리글 + 상태 줄 + 여유). 헤드리스 측정으로 맞춘 값(시험이 지킨다).
    internal const double ChromeHeight = 96;

    /// <summary>
    /// 첫 창 높이: 약 30행이 보이게 하되 컬럼이 적으면 그 행 수에 맞추고(최소 높이 아래로는 안 줄임), 화면 작업 영역 높이(<paramref name="workAreaHeight"/>, DIP)의 90% 를 넘지 않는다.
    /// 작업 영역을 모르면(0 이하) 줄이지 않는다.
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

    /// <summary>지금 표에 보이는 행(거르기 적용 뒤).</summary>
    internal IReadOnlyList<TableLayoutRow> VisibleRows => Items().Select(i => (TableLayoutRow)i.Tag!).ToList();

    /// <summary>고른 행(보이는 행만).</summary>
    internal IReadOnlyList<TableLayoutRow> SelectedRows => _list.SelectedItems?.OfType<ListBoxItem>().Select(i => i.Tag).OfType<TableLayoutRow>().ToList() ?? [];

    internal ListBox List => _list;

    /// <summary>현재 행(▶).</summary>
    internal TableLayoutRow? CurrentRow => _current;

    internal TextBox FilterBox => _filter;

    internal CheckBox AuditBox => _audit;

    /// <summary>상태 줄 글.</summary>
    internal string StatusText => _status.Text ?? string.Empty;

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

    /// <summary>고른 행을 편집기에 넣는다(Enter·「넣기」).</summary>
    internal bool PasteSelection() => SelectedRows is { Count: > 0 } rows && _session.Paste(rows);

    /// <summary>고른 행의 넣기 글을 클립보드에 둔다(⌘C·Ctrl+C).</summary>
    internal async Task<bool> CopySelectionAsync()
    {
        var text = _session.TextFor(SelectedRows);
        if (text.Length == 0 || Clipboard is not { } clipboard)
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

    /// <summary>두 번 누른 행: 누르기 전 선택 안이면 그 선택 전체를, 밖이면 그 행 하나를 넣는다.</summary>
    internal bool PasteDoubleClicked(TableLayoutRow row)
    {
        var rows = TableLayoutPaste.DoubleClickRows(row, _pressSelection);
        if (rows.Count > 1)
        {
            Select(rows); // 첫 클릭이 줄인 선택을 되돌린다.
        }

        return _session.Paste(rows);
    }

    internal void Select(IEnumerable<TableLayoutRow> rows)
    {
        var set = rows.ToHashSet();
        _list.SelectedItems?.Clear();
        foreach (var item in Items().Where(i => set.Contains((TableLayoutRow)i.Tag!)))
        {
            _list.SelectedItems?.Add(item);
        }
    }

    private IEnumerable<ListBoxItem> Items() => _list.ItemsSource?.OfType<ListBoxItem>() ?? [];

    private static TextBlock Glyph(string text) => new()
    {
        Text = text,
        Classes = { "mono" },
        FontSize = 12,
        FontWeight = FontWeight.SemiBold,
        MinWidth = 14,
        TextAlignment = TextAlignment.Center,
    };

    private static ToggleButton Toggle(Control content, string tip)
    {
        var t = new ToggleButton { Classes = { "toolbar" }, Content = content, Padding = new Thickness(5, 3), Focusable = false };
        ToolTip.SetTip(t, tip);
        AutomationProperties.SetName(t, tip);
        return t;
    }

    private static Button IconButton(string icon, string tip, Action click)
    {
        var b = new Button { Classes = { "toolbar" }, Content = tip, Focusable = false };
        ButtonIcon.SetKey(b, icon);
        ButtonIcon.SetIconOnly(b, true);
        b.Click += (_, _) => click();
        return b;
    }

    private DockPanel BuildContent()
    {
        _filter.InnerLeftContent = ShellIcons.Create("find", 14);

        // 도구 막대: 넣기·복사 | 옵션 토글 4개 | 별칭 칸 ………… 거르기 칸.
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(IconButton("add", "넣기", () => PasteSelection()));
        tools.Children.Add(IconButton("copy", "복사", () => _ = CopySelectionAsync()));
        tools.Children.Add(new Separator { Classes = { "toolbar" } });
        tools.Children.Add(_comma);
        tools.Children.Add(_lower);
        tools.Children.Add(_pk);
        tools.Children.Add(_comment);
        tools.Children.Add(_audit);
        tools.Children.Add(new Separator { Classes = { "toolbar" } });
        tools.Children.Add(new TextBlock { Text = "별칭", FontSize = 12, Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center });
        tools.Children.Add(_alias);
        var bar = new DockPanel();
        DockPanel.SetDock(_filter, Avalonia.Controls.Dock.Right);
        bar.Children.Add(_filter);
        bar.Children.Add(tools);

        var hint = new TextBlock
        {
            Text = (LiteralTranslator.IsEnglish ? "Enter · double-click = insert · {0} = copy" : "Enter·두 번 누르기 = 넣기 · {0} = 복사").Replace("{0}", OperatingSystem.IsMacOS() ? "⌘C" : "Ctrl+C", StringComparison.Ordinal),
        };

        // 격자 표(Styles/GridC): 머리글과 줄이 같은 칸 묶음 이름(tl)을 써서 너비가 맞는다.
        var body = new DockPanel();
        var header = GridC.Header(LiteralTranslator.IsEnglish ? HeadersEn : HeadersKo, Group);
        DockPanel.SetDock(header, Avalonia.Controls.Dock.Top);
        body.Children.Add(header);
        DockPanel.SetDock(_probeHost, Avalonia.Controls.Dock.Top);
        body.Children.Add(_probeHost);
        body.Children.Add(_list);
        Grid.SetIsSharedSizeScope(body, true);

        var root = GridC.Frame(bar, body, _status, hint);
        if (_session.Model.Comment is { } comment)
        {
            ToolTip.SetTip(_status, comment);
        }

        return root;
    }

    private void Rebuild()
    {
        var selected = SelectedRows.ToHashSet();
        var items = new List<ListBoxItem>();
        _markers.Clear();
        var options = _session.Options;
        var patterns = options.HideAudit ? TableLayoutAudit.Parse(options.AuditColumns) : [];
        var all = patterns.Count == 0 ? _session.Model.Rows : _session.Model.Rows.Where(r => !TableLayoutAudit.Matches(patterns, r.Name)).ToList();
        _hidden = _session.Model.Rows.Count - all.Count;
        foreach (var r in TableLayoutPaste.Filter(all, _filter.Text))
        {
            items.Add(new ListBoxItem { Tag = r, Content = GridC.Row(Cells(r, register: true), rowHead: true, Group) });
        }

        _list.ItemsSource = items;
        foreach (var item in items.Where(i => selected.Contains((TableLayoutRow)i.Tag!)))
        {
            _list.SelectedItems?.Add(item);
        }

        // 칸 너비는 처음부터 전체 컬럼 기준(거르기와 상관없이 감사 컬럼을 뺀 전체): 스크롤·거르기로 표가 흔들리지 않는다.
        _probeHost.Child = GridC.Probe(
            all,
            6,
            (r, c) => c switch
            {
                0 => all.Count.ToString(CultureInfo.InvariantCulture).Length,
                1 => GridC.TextWeight(r.Name),
                2 => GridC.TextWeight(r.TypeText),
                4 => GridC.TextWeight(r.DefaultValue),
                _ => 0,
            },
            r => Cells(r, register: false),
            Group);
        UpdateStatus();
    }

    private List<Control> Cells(TableLayoutRow r, bool register)
    {
        var showPk = _session.Options.ShowPk;
        var marker = new TextBlock { Text = register && ReferenceEquals(r, _current) ? "▶" : string.Empty, Classes = { "grid-c-marker" } };
        if (register)
        {
            _markers[r] = marker;
        }

        var head = new DockPanel { MinWidth = 28, Children = { marker, new TextBlock { Text = r.Index.ToString(CultureInfo.InvariantCulture), Classes = { "num", "dim" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center } } };
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (showPk)
        {
            // 열쇠 자리를 늘 비워 둬서 PK 가 아닌 이름도 같은 자리에서 시작한다.
            name.Children.Add(r.IsPk ? ShellIcons.Create("key", 12)! : new Control { Width = 12 });
        }

        name.Children.Add(new TextBlock { Text = r.Name });
        return
        [
            head,
            name,
            new TextBlock { Text = r.TypeText, Classes = { "mono", "dim" } },
            new TextBlock { Text = r.NotNull ? "N" : "Y", Classes = { r.NotNull ? "warning-text" : "dim" } },
            new TextBlock { Text = r.DefaultValue ?? string.Empty, Classes = { "mono" }, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 160 },
            new TextBlock { Text = r.Comment ?? string.Empty, Classes = { "dim" }, TextTrimming = TextTrimming.CharacterEllipsis },
        ];
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateStatus();

    // 현재 행 ▶ = 사용자가 마지막으로 누른 행·키보드로 옮겨 간 행(선택 순서가 아니라 포커스가 있는 줄).
    private void OnListKeyUp(object? sender, KeyEventArgs e)
    {
        if ((FocusManager?.GetFocusedElement() as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.Tag is TableLayoutRow row)
        {
            SetCurrent(row);
        }
    }

    private void SetCurrent(TableLayoutRow row)
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

    private void UpdateStatus()
    {
        var model = _session.Model;
        var shown = Items().Count();
        var total = model.Rows.Count;
        var visibleTotal = total - _hidden;
        var picked = _list.SelectedItems?.Count ?? 0;
        var parts = new List<string>
        {
            $"{model.Title} ({model.Kind})",
            LiteralTranslator.IsEnglish ? $"{total} columns" : $"{total} 컬럼",
        };
        if (_hidden > 0)
        {
            parts.Add(LiteralTranslator.IsEnglish ? $"{_hidden} hidden" : $"{_hidden}개 숨김");
        }

        if (shown != visibleTotal)
        {
            parts.Add(LiteralTranslator.IsEnglish ? $"{shown} shown" : $"{shown}개 보임");
        }

        if (picked > 0)
        {
            parts.Add(LiteralTranslator.IsEnglish ? $"{picked} selected" : $"{picked}개 선택");
        }

        var pk = model.Rows.Where(r => r.IsPk).Select(r => r.Name).ToList();
        if (pk.Count > 0)
        {
            parts.Add("PK: " + string.Join(", ", pk));
        }

        _status.Text = string.Join(" · ", parts);
    }

    private void OptionsChanged()
    {
        var before = (_session.Options.ShowPk, _session.Options.HideAudit);
        // 쉼표 위치(Comma)는 창의 입력이 아니므로 지금 옵션에서 그대로 이어 간다.
        _session.SetOptions(_session.Options with
        {
            Alias = _alias.Text ?? string.Empty,
            InsertComma = _comma.IsChecked == true,
            LowerCase = _lower.IsChecked == true,
            ShowPk = _pk.IsChecked == true,
            ShowComment = _comment.IsChecked == true,
            HideAudit = _audit.IsChecked == true,
        });
        if (before != (_session.Options.ShowPk, _session.Options.HideAudit))
        {
            Rebuild();
        }
    }

    private void OnListPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            _pressSelection = SelectedRows.ToList();
        }

        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.Tag is TableLayoutRow row)
        {
            SetCurrent(row);
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.Tag is TableLayoutRow row)
        {
            PasteDoubleClicked(row);
            e.Handled = true;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        // 별칭·거르기 칸에서는 Enter·복사·전체 선택이 그 칸의 글 편집이다. 거르기 칸의 ↓ 는 표로 옮겨 간다.
        if (e.Source is Visual v && v.FindAncestorOfType<TextBox>(includeSelf: true) is { } box)
        {
            if (ReferenceEquals(box, _filter) && e.Key == Key.Down)
            {
                e.Handled = true;
                _list.Focus();
                if (_list.SelectedItems is { Count: 0 })
                {
                    _list.SelectedIndex = 0;
                }
            }

            return;
        }

        var keys = Application.Current?.PlatformSettings?.HotkeyConfiguration;
        if (e.Key is Key.Enter)
        {
            e.Handled = PasteSelection();
        }
        else if (keys?.Copy.Any(g => g.Matches(e)) == true)
        {
            e.Handled = true;
            _ = CopySelectionAsync();
        }
        else if (keys?.SelectAll.Any(g => g.Matches(e)) == true)
        {
            e.Handled = true;
            _list.SelectAll();
        }
    }
}
