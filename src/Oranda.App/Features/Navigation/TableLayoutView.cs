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
/// 테이블 레이아웃 탭 하나의 본문(Orange 의 Alt+C). 도구 막대(넣기·복사 | 옵션 토글 4개 | 별칭 · 거르기) / 격자 표(행 머리 칸·현재 행 ▶) /
/// 상태 줄(객체·종류·컬럼 수·선택 수·PK | 키 힌트).
/// 행은 클릭·Shift(범위)·⌘ 또는 Ctrl(개별)·⌘A 로 고르고, Enter·「넣기」·두 번 누르기로 편집기에 넣고 ⌘C 로 같은 글을 복사한다.
/// 넣는 글은 늘 표 순서다(<see cref="TableLayoutPaste.Build"/>). 이 컨트롤은 창에 매이지 않아 탭을 다른 창으로 옮겨도 선택·거르기·확인란·별칭 상태가 그대로 남는다.
/// Esc(탭 닫기)는 이 컨트롤이 아니라 담는 창(<see cref="TableLayoutWindow"/>)이 처리한다.
/// </summary>
public sealed class TableLayoutView : UserControl
{
    // 「형식」「NULL」은 literals-en.json 의 기존 번역(Format·Nullable)과 달라서 언어를 보고 직접 고른다.
    private static readonly string[] HeadersKo = ["", "이름", "형식", "Null", "기본값", "주석"];
    private static readonly string[] HeadersEn = ["", "Name", "Type", "Null", "Default", "Comment"];
    private const string Group = "tl";

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

    private readonly Button _insert;
    private readonly Border _probeHost = new(); // 전체 컬럼 기준 칸 너비를 고정하는 숨은 줄(GridC.Probe)
    private int _hidden;
    private bool _syncing; // 옵션 값을 화면 칸에 옮기는 중: 칸의 변경 알림이 옵션을 되덮어 쓰거나 저장하지 않게 한다
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

    public TableLayoutView(TableLayoutSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
        _insert = IconButton("add", "넣기", () => PasteSelection());
        Content = BuildContent();

        var o = session.Options;
        _alias.Text = o.Alias;
        _comma.IsChecked = o.InsertComma;
        _lower.IsChecked = o.LowerCase;
        _pk.IsChecked = o.ShowPk;
        _comment.IsChecked = o.ShowComment;
        AuditStateFromOptions();
        Rebuild();
        UpdateInsertState();

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
        session.TargetChanged += OnTargetChanged;
        session.ModelChanged += OnModelChanged;
    }

    public TableLayoutSession Session { get; }

    /// <summary>탭 제목 = 「소유자.표」.</summary>
    public string TabTitle => Session.Model.Title;

    /// <summary>지금 표에 보이는 행(거르기 적용 뒤).</summary>
    internal IReadOnlyList<TableLayoutRow> VisibleRows => Items().Select(i => (TableLayoutRow)i.Tag!).ToList();

    /// <summary>고른 행(보이는 행만).</summary>
    internal IReadOnlyList<TableLayoutRow> SelectedRows => _list.SelectedItems?.OfType<ListBoxItem>().Select(i => i.Tag).OfType<TableLayoutRow>().ToList() ?? [];

    internal ListBox List => _list;

    /// <summary>현재 행(▶).</summary>
    internal TableLayoutRow? CurrentRow => _current;

    internal TextBox FilterBox => _filter;

    internal TextBox AliasBox => _alias;

    internal CheckBox AuditBox => _audit;

    /// <summary>넣기 단추.</summary>
    internal Button InsertButton => _insert;

    /// <summary>상태 줄 글.</summary>
    internal string StatusText => _status.Text ?? string.Empty;

    /// <summary>표 목록에 포커스를 둔다(탭을 열거나 옮긴 뒤).</summary>
    internal void FocusList() => _list.Focus();

    /// <summary>고른 행을 편집기에 넣는다(Enter·「넣기」).</summary>
    internal bool PasteSelection() => SelectedRows is { Count: > 0 } rows && !Blocked() && Session.Paste(rows);

    // 넣을 수 없으면(편집기가 열 블록·여러 커서 선택이거나, 이 탭을 연 편집기 탭이 닫혔으면) 넣지 않고 상태 줄로 알린다.
    private bool Blocked()
    {
        if (Session.IsTargetClosed)
        {
            UpdateStatus(); // 닫힘 안내는 상태 줄에 이미 붙어 있다.
            return true;
        }

        if (!Session.IsBlocked)
        {
            return false;
        }

        _status.Text = SqlEditor.SqlEditorViewModel.BlockSelectionNotice;
        return true;
    }

    /// <summary>고른 행의 넣기 글을 클립보드에 둔다(⌘C·Ctrl+C).</summary>
    internal async Task<bool> CopySelectionAsync()
    {
        var text = Session.TextFor(SelectedRows);
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

    /// <summary>두 번 누른 행: 누르기 전 선택 안이면 그 선택 전체를, 밖이면 그 행 하나를 넣는다.</summary>
    internal bool PasteDoubleClicked(TableLayoutRow row)
    {
        var rows = TableLayoutPaste.DoubleClickRows(row, _pressSelection);
        if (rows.Count > 1)
        {
            Select(rows); // 첫 클릭이 줄인 선택을 되돌린다.
        }

        return !Blocked() && Session.Paste(rows);
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

    // 「감사 컬럼 빼기」: 설정에 감사 컬럼 목록이 있을 때만 켤 수 있다(목록이 사라졌으면 끈다).
    private void AuditStateFromOptions()
    {
        _syncing = true;
        try
        {
            ApplyAuditState(Session.Options);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ApplyAuditState(TableLayoutOptions o)
    {
        var hasAudit = TableLayoutAudit.Parse(o.AuditColumns).Count > 0;
        _audit.IsEnabled = hasAudit;
        _audit.IsChecked = hasAudit && o.HideAudit;
        ToolTip.SetTip(_audit, hasAudit ? "설정의 감사 컬럼 목록에 맞는 컬럼을 테이블에서 뺀다" : "설정에서 감사 컬럼을 정하세요");
        AutomationProperties.SetName(_audit, "감사 컬럼 빼기");
    }

    private DockPanel BuildContent()
    {
        _filter.InnerLeftContent = ShellIcons.Create("find", 14);

        // 도구 막대: 넣기·복사 | 옵션 토글 4개 | 별칭 칸 ………… 거르기 칸.
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(_insert);
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
        if (Session.Model.Comment is { } comment)
        {
            ToolTip.SetTip(_status, comment);
        }

        return root;
    }

    private void Rebuild()
    {
        var selected = SelectedRows.Select(r => r.Name).ToHashSet(StringComparer.Ordinal); // 컬럼 이름으로 맞춰 컬럼 목록이 새로 읽혀도 선택이 이어진다
        var items = new List<ListBoxItem>();
        _markers.Clear();
        var options = Session.Options;
        var patterns = options.HideAudit ? TableLayoutAudit.Parse(options.AuditColumns) : [];
        var all = patterns.Count == 0 ? Session.Model.Rows : Session.Model.Rows.Where(r => !TableLayoutAudit.Matches(patterns, r.Name)).ToList();
        _hidden = Session.Model.Rows.Count - all.Count;
        foreach (var r in TableLayoutPaste.Filter(all, _filter.Text))
        {
            items.Add(new ListBoxItem { Tag = r, Content = GridC.Row(Cells(r, register: true), rowHead: true, Group) });
        }

        _list.ItemsSource = items;
        foreach (var item in items.Where(i => selected.Contains(((TableLayoutRow)i.Tag!).Name)))
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
        var showPk = Session.Options.ShowPk;
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
        if ((TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.Tag is TableLayoutRow row)
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
        var model = Session.Model;
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

        var text = string.Join(" · ", parts);
        _status.Text = Session.IsTargetClosed ? TableLayoutSession.TargetClosedNotice + " · " + text : text; // 대상 편집기 탭이 닫힌 동안은 안내를 앞에 둔다
    }

    // 넣기를 연 편집기 탭이 바뀌었거나 닫혔다: 넣기 단추를 켜고 끄고 상태 줄을 새로 쓴다. 복사는 그대로 된다.
    private void OnTargetChanged(object? sender, EventArgs e)
    {
        UpdateInsertState();
        UpdateStatus();
    }

    // 같은 표를 다시 열어 컬럼 목록·설정이 새로 읽혔다: 표를 다시 그리되 거르기 글·확인란·별칭은 그대로 둔다.
    private void OnModelChanged(object? sender, EventArgs e)
    {
        if (_current is { } old)
        {
            _current = Session.Model.Rows.FirstOrDefault(r => r.Name == old.Name);
        }

        _pressSelection = [];
        ToolTip.SetTip(_status, Session.Model.Comment);
        AuditStateFromOptions();
        Rebuild();
        if (_current is { } now)
        {
            SetCurrent(now);
        }
    }

    private void UpdateInsertState()
    {
        var closed = Session.IsTargetClosed;
        _insert.IsEnabled = !closed;
        ToolTip.SetTip(_insert, closed ? TableLayoutSession.TargetClosedNotice : "넣기");
        AutomationProperties.SetName(_insert, "넣기");
    }

    private void OptionsChanged()
    {
        if (_syncing)
        {
            return;
        }

        var before = (Session.Options.ShowPk, Session.Options.HideAudit);
        // 쉼표 위치(Comma)는 창의 입력이 아니므로 지금 옵션에서 그대로 이어 간다.
        Session.SetOptions(Session.Options with
        {
            Alias = _alias.Text ?? string.Empty,
            InsertComma = _comma.IsChecked == true,
            LowerCase = _lower.IsChecked == true,
            ShowPk = _pk.IsChecked == true,
            ShowComment = _comment.IsChecked == true,
            HideAudit = _audit.IsEnabled ? _audit.IsChecked == true : Session.Options.HideAudit, // 감사 컬럼 목록이 없어 꺼진 확인란은 저장된 설정을 바꾸지 못한다
        });
        if (before != (Session.Options.ShowPk, Session.Options.HideAudit))
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

    /// <summary>
    /// 키 처리(Enter 넣기·복사·전체 선택·거르기 칸 ↓). 담는 창이 부른다: 표에 포커스가 없을 때 키가 창으로만 오므로 컨트롤 자신의 처리기로는 놓친다.
    /// </summary>
    internal void HandleKey(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
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
