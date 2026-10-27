using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Oranda.App.Features.ResultGrid;
using Oranda.Core.Grid;

namespace Oranda.App.Features.SessionMonitor;

/// <summary>
/// 모니터(세션·SQL)의 목록 표. 조회 결과와 같은 그리드(<see cref="ResultGridControl"/>)를 쓴다(TSK-14-15): 머리글을 눌러 정렬(오름 → 내림 → 해제),
/// 머리글을 끌어 열 순서 바꾸기, 경계를 끌어 너비 조절, ▾ 열 자동 필터(<see cref="ColumnFilterPanel"/>), 칸·행 선택(Shift·Ctrl·행 머리), Ctrl+휠 글자 크기,
/// 현재 행 ▶, 그리드 글꼴·색. 읽기 전용이라 편집·플래시백·외래키 따라가기·서버 거르기 줄은 없다.
/// 오른쪽 클릭 메뉴 = 복사 묶음(복사·머리글 포함·행 복사·CSV) + 열(고정·숨기기·모두 보이기·필터 지우기) + 모니터 명령(<see cref="ExtraMenuItems"/>).
/// 키: Ctrl+C 복사, Ctrl+Shift+C 머리글 포함, Ctrl+Alt+C 행 복사(조회 결과 키맵과 같은 키).
/// 행은 <see cref="MonitorGridBinder{T}"/> 가 채운다.
/// </summary>
public sealed class MonitorGrid : UserControl
{
    private readonly ResultGridControl _grid = new() { IsCommandTarget = false };
    private readonly ScrollBar _vbar = new() { Orientation = Orientation.Vertical, AllowAutoHide = false };
    private readonly ScrollBar _hbar = new() { Orientation = Orientation.Horizontal, AllowAutoHide = false };
    private readonly Popup _filterHost = new() { IsLightDismissEnabled = true, Placement = PlacementMode.BottomEdgeAlignedLeft };
    private bool _syncing;

    public MonitorGrid()
    {
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("*,Auto") };
        root.Children.Add(_grid);
        _filterHost.PlacementTarget = _grid;
        root.Children.Add(_filterHost);
        Grid.SetColumn(_vbar, 1);
        root.Children.Add(_vbar);
        Grid.SetRow(_hbar, 1);
        root.Children.Add(_hbar);
        Content = root;

        _grid.ScrollChanged += (_, _) => SyncBars();
        _grid.SizeChanged += (_, _) => SyncBars();
        _vbar.Scroll += (_, e) =>
        {
            if (!_syncing)
            {
                _grid.ScrollTo(_grid.OffsetX, e.NewValue);
            }
        };
        _hbar.Scroll += (_, e) =>
        {
            if (!_syncing)
            {
                _grid.ScrollTo(e.NewValue, _grid.OffsetY);
            }
        };
        _grid.ColumnFilterRequested += async (_, e) => await OpenColumnFilterAsync(e);
        _grid.ContextMenu = BuildMenu();
        _grid.DoubleTapped += (_, e) =>
        {
            // 머리글을 두 번 누르면 정렬만 두 번 바뀐다: 행 위에서 두 번 누를 때만 알린다.
            if (Model is { HasSelection: true } && e.GetPosition(_grid).Y >= _grid.HeaderHeight)
            {
                RowDoubleTapped?.Invoke(this, EventArgs.Empty);
            }
        };
        AddHandler(KeyDownEvent, OnKeyDown);
        SyncBars();
    }

    /// <summary>안의 결과 그리드 컨트롤(시험·포커스용).</summary>
    public ResultGridControl GridControl => _grid;

    /// <summary>보이는 결과. 바꾸면 클립보드 쓰기를 이 화면의 클립보드로 잇는다.</summary>
    public ResultGridModel? Model
    {
        get => _grid.Model;
        set
        {
            if (value is not null)
            {
                value.ClipboardWriter = async text =>
                {
                    if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb)
                    {
                        try
                        {
                            await cb.SetTextAsync(text).ConfigureAwait(true);
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
                        {
                            // 다른 프로그램이 클립보드를 쥐고 있으면(Windows) 이번 복사만 건너뛴다.
                        }
                    }
                };
            }

            _grid.Model = value;
            SyncBars();
        }
    }

    /// <summary>오른쪽 클릭 메뉴에 복사·열 항목 아래로 덧붙일 항목을 메뉴가 열릴 때마다 돌려주는 함수(context:monitor/* 명령). 없으면 없다.</summary>
    public Func<IReadOnlyList<object>>? ExtraMenuItems { get; set; }

    /// <summary>행 위를 두 번 눌렀다(머리글은 빼고). 모니터가 상세·계획을 연다.</summary>
    public event EventHandler? RowDoubleTapped;

    private void SyncBars()
    {
        _syncing = true;
        try
        {
            _grid.SyncScrollBars(_vbar, _hbar);
        }
        finally
        {
            _syncing = false;
        }
    }

    private async Task OpenColumnFilterAsync(ColumnFilterRequestedEventArgs e)
    {
        if (Model is not { } m)
        {
            return;
        }

        var ordinal = e.Ordinal;
        ColumnValueList values;
        try
        {
            values = await m.CollectColumnValuesAsync(ordinal);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var panel = new ColumnFilterPanel(m.Columns[ordinal], values, m.LocalFilter.Columns.GetValueOrDefault(ordinal));
        panel.Applied += async (_, filter) =>
        {
            _filterHost.IsOpen = false;
            await m.SetLocalFilterAsync(m.LocalFilter.WithColumn(ordinal, filter));
            _grid.Focus();
        };
        panel.CloseRequested += (_, _) =>
        {
            _filterHost.IsOpen = false;
            _grid.Focus();
        };
        _filterHost.Child = new Border { Child = panel, Background = Avalonia.Media.Brushes.Transparent };
        _filterHost.PlacementRect = e.HeaderBounds;
        _filterHost.IsOpen = true;
    }

    private Func<List<object>>? _menuItems;

    /// <summary>오른쪽 클릭 메뉴가 열릴 때 보일 항목(지금 선택에 맞춘 활성 상태 포함). 시험용으로도 쓴다.</summary>
    internal IReadOnlyList<object> MenuItems() => _menuItems?.Invoke() ?? [];

    private ContextMenu BuildMenu()
    {
        MenuItem Item(string header, Func<ResultGridModel, Task> run, string? gesture = null)
        {
            var item = new MenuItem { Header = header, InputGesture = gesture is null ? null : KeyGesture.Parse(gesture) };
            item.Click += async (_, _) =>
            {
                if (Model is { } m)
                {
                    await run(m);
                }
            };
            return item;
        }

        var copy = Item("복사", m => m.CopyAsync(CopyFormat.Tsv, false), "Ctrl+C");
        var copyHeaders = Item("머리글 포함 복사", m => m.CopyAsync(CopyFormat.Tsv, true), "Ctrl+Shift+C");
        var copyRows = Item("행 복사", m => m.CopyRowsAsync(includeHeaders: false), "Ctrl+Alt+C");
        var copyRowsHeaders = Item("머리글 포함 행 복사", m => m.CopyRowsAsync(includeHeaders: true));
        var copyCsv = Item("CSV 로 복사", m => m.CopyAsync(CopyFormat.Csv, true));
        var freeze = Item("열 고정", m =>
        {
            m.FreezeUpToActive();
            return Task.CompletedTask;
        });
        var hide = Item("열 숨기기", m =>
        {
            if (m.HasSelection && m.Active.Col < m.VisibleColumns.Count)
            {
                m.HideColumn(m.VisibleColumns[m.Active.Col]);
            }

            return Task.CompletedTask;
        });
        var showAll = Item("숨긴 열 모두 보이기", m =>
        {
            m.ShowAllColumns();
            return Task.CompletedTask;
        });
        var clearFilters = Item("열 필터 모두 지우기", m => m.ClearLocalFilterAsync());
        var menu = new ContextMenu();
        _menuItems = () =>
        {
            var m = Model;
            var picked = m is { HasSelection: true };
            copy.IsEnabled = copyHeaders.IsEnabled = copyRows.IsEnabled = copyRowsHeaders.IsEnabled = copyCsv.IsEnabled = picked;
            freeze.IsEnabled = hide.IsEnabled = picked;
            showAll.IsEnabled = m is not null && m.VisibleColumns.Count < m.Columns.Count;
            clearFilters.IsEnabled = m is { LocalFilter.IsEmpty: false };
            var items = new List<object> { copy, copyHeaders, copyRows, copyRowsHeaders, copyCsv, new Separator(), freeze, hide, showAll, clearFilters };
            var extra = ExtraMenuItems?.Invoke() ?? [];
            if (extra.Count > 0)
            {
                items.Add(new Separator());
                items.AddRange(extra);
            }

            return items;
        };
        menu.Opening += (_, e) =>
        {
            menu.ItemsSource = MenuItems();
            e.Cancel = Model is not { HasResult: true };
        };
        return menu;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || Model is not { HasSelection: true } m)
        {
            return;
        }

        var primary = Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        if (e.Key != Key.C || !e.KeyModifiers.HasFlag(primary))
        {
            return;
        }

        e.Handled = true;
        var rest = e.KeyModifiers & ~primary;
        _ = rest switch
        {
            KeyModifiers.Shift => m.CopyAsync(CopyFormat.Tsv, true),
            KeyModifiers.Alt => m.CopyRowsAsync(includeHeaders: false),
            _ => m.CopyAsync(CopyFormat.Tsv, false),
        };
    }
}
