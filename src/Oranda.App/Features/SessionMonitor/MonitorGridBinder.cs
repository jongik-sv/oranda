using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Oranda.App.Features.ResultGrid;
using Oranda.Core.Grid;

namespace Oranda.App.Features.SessionMonitor;

/// <summary>모니터 표 열 하나: 머리글, 값 분류(숫자면 오른쪽 맞춤·수로 정렬), 항목 → 칸 값(원시 값, 표시는 그리드가 서식한다).</summary>
public sealed record MonitorColumn<T>(string Name, ColumnDataKind Kind, Func<T, object?> Value);

/// <summary>
/// 모니터 목록(<see cref="ObservableCollection{T}"/>)을 조회 결과 그리드(<see cref="MonitorGrid"/>)로 보이게 잇는 어댑터(TSK-14-15).
/// 목록이 바뀌면(자동 새로 고침) 한 번에 몰아 새 결과(<see cref="MemoryResultSet"/>)를 만들고, 이전 결과의 화면 상태를 그대로 이어받는다:
/// 정렬 열·방향, 열 순서·숨김·고정, 열 너비, 열 필터, 고른 행(키로 찾음)·현재 칸, 스크롤 위치. 새 결과는 정렬·거르기를 마친 뒤에 바꿔 끼워 깜박이지 않는다.
/// 현재 칸의 행(▶)을 화면 모델의 선택 항목과 맞춘다(양방향).
/// </summary>
public sealed class MonitorGridBinder<T> where T : class
{
    /// <summary>모니터 표 표시: NULL 은 빈 칸, 숫자는 천 단위 쉼표.</summary>
    public static readonly GridDisplayOptions Display = GridDisplayOptions.Default with { NullText = string.Empty, ThousandsSeparator = true };

    private readonly MonitorGrid _grid;
    private readonly IReadOnlyList<MonitorColumn<T>> _columns;
    private readonly ResultColumn[] _resultColumns;
    private readonly Func<T, object> _key;
    private ObservableCollection<T>? _items;
    private Func<T?>? _getSelected;
    private Action<T?>? _setSelected;
    private List<T> _shown = [];
    private int _generation;
    private bool _pending;
    private bool _building;
    private bool _syncing;
    private T? _lastActive;

    public MonitorGridBinder(MonitorGrid grid, IReadOnlyList<MonitorColumn<T>> columns, Func<T, object> key)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(columns);
        _grid = grid;
        _columns = columns;
        _key = key;
        _resultColumns = [.. columns.Select((c, i) => new ResultColumn(i, c.Name, c.Kind == ColumnDataKind.Number ? "NUMBER" : "VARCHAR2", c.Kind))];
    }

    /// <summary>새 결과를 만들 때마다 부르는 표시 설정(NULL 글자·천 단위). 기본 <see cref="Display"/>. 행 보기는 원래 결과의 NULL 표시를 따른다.</summary>
    public Func<GridDisplayOptions> DisplayOptions { get; set; } = () => Display;

    /// <summary>(항목, 열 번호)를 받아 참이면 그 칸을 위험색 글자로 그린다(블로커 칸). 행을 채우기 전에 정한다.</summary>
    public Func<T, int, bool>? CellEmphasis { get; set; }

    /// <summary>지금 보이는 결과(시험용).</summary>
    public ResultGridModel? Model => _grid.Model;

    /// <summary>새로 고침해 결과를 바꿔 끼울 때마다 알린다(시험·상태 줄).</summary>
    public event EventHandler? Rebuilt;

    /// <summary>
    /// 표 아래 상태 줄 하나(<paramref name="line"/>)에 화면 모델의 상태 글과 고른 행 수를 합쳐 보인다(<see cref="MonitorStatusLine.Compose"/>).
    /// </summary>
    public void AttachStatusLine(Avalonia.Controls.TextBlock line, INotifyPropertyChanged owner, Func<string> getStatus)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(owner);
        var last = -1L;
        string? lastStatus = null;
        void Update(bool force)
        {
            var picked = _grid.Model is { HasSelection: true } m ? m.SelectedRowCount : 0;
            var status = getStatus();
            if (force || picked != last || !string.Equals(status, lastStatus, StringComparison.Ordinal))
            {
                last = picked;
                lastStatus = status;
                line.Text = MonitorStatusLine.Compose(status, (int)Math.Min(int.MaxValue, picked));
            }
        }

        owner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "StatusText")
            {
                Update(force: true);
            }
        };
        _modelChanged += () => Update(force: false);
        Update(force: true);
    }

    private Action? _modelChanged;

    /// <summary>항목 목록과 선택 항목을 잇는다. <paramref name="owner"/> 의 <paramref name="selectedProperty"/> 가 바뀌면 그리드의 현재 행도 따라간다.</summary>
    public void Attach(ObservableCollection<T> items, INotifyPropertyChanged owner, string selectedProperty, Func<T?> getSelected, Action<T?> setSelected)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(owner);
        _items = items;
        _getSelected = getSelected;
        _setSelected = setSelected;
        items.CollectionChanged += (_, _) => Refresh();
        owner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == selectedProperty && !_syncing && !_pending && !_building)
            {
                SelectFromOwner();
            }
        };
        Refresh();
    }

    /// <summary>칸 값이 바뀌었을 때(델타 보기 전환 등)·목록이 바뀌었을 때 다시 만든다(한 번에 몰아서).</summary>
    public void Refresh()
    {
        if (_pending)
        {
            return;
        }

        _pending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _pending = false;
            _ = RebuildAsync();
        }, DispatcherPriority.Loaded);
    }

    private async Task RebuildAsync()
    {
        if (_items is null)
        {
            return;
        }

        var generation = ++_generation;
        var items = _items.ToList();
        var rows = items.Select(i => (IReadOnlyList<object?>)[.. _columns.Select(c => c.Value(i))]).ToList();
        var m = new ResultGridModel(new MemoryResultSet(_resultColumns, rows), pageSize: Math.Max(1, rows.Count)) { Display = DisplayOptions() };
        if (CellEmphasis is { } emphasis)
        {
            m.CellEmphasis = (viewRow, ordinal) => m.SourceRowOf(viewRow) is var src && src >= 0 && src < items.Count && emphasis(items[(int)src], ordinal);
        }

        var old = _grid.Model;
        _building = true;
        try
        {
            if (old is not null)
            {
                m.CarryViewStateFrom(old);
            }

            await m.LoadFirstPageAsync().ConfigureAwait(true);
            if (old is not null)
            {
                await m.CarrySortAndFilterFromAsync(old).ConfigureAwait(true);
            }

            if (generation != _generation)
            {
                await m.DisposeAsync().ConfigureAwait(true);
                return;
            }

            RestoreSelection(old, m, items);
            m.SavedOffsetX = _grid.GridControl.OffsetX;
            m.SavedOffsetY = _grid.GridControl.OffsetY;
            if (old is not null)
            {
                old.Changed -= OnModelChanged;
            }

            _shown = items;
            m.Changed += OnModelChanged;
            _grid.Model = m;
        }
        finally
        {
            if (generation == _generation)
            {
                _building = false;
            }
        }

        if (old is not null)
        {
            _ = old.DisposeAsync().AsTask();
        }

        // 화면 모델이 고른 항목(새로 고침 뒤 같은 키, 다른 창이 고르게 한 세션)이 그리드의 현재 행과 다르면 화면 모델 쪽을 따른다.
        var want = _getSelected?.Invoke();
        if (want is not null && !Equals(ActiveItem(m) is { } a ? _key(a) : null, _key(want)))
        {
            SelectFromOwner();
        }
        else
        {
            PushSelection();
        }

        _modelChanged?.Invoke();
        Rebuilt?.Invoke(this, EventArgs.Empty);
    }

    // 이전 결과에서 고른 행(키)·현재 칸 열을 새 결과의 같은 키 행에 다시 건다.
    private void RestoreSelection(ResultGridModel? old, ResultGridModel m, List<T> items)
    {
        if (old is not { HasSelection: true } || m.RowCount == 0)
        {
            return;
        }

        var activeItem = ItemAt(old, _shown, old.Active.Row);
        var picked = new HashSet<object>();
        foreach (var (first, last) in old.SelectedRowRanges())
        {
            for (var r = first; r <= last; r++)
            {
                if (ItemAt(old, _shown, r) is { } it)
                {
                    picked.Add(_key(it));
                }
            }
        }

        var rowOf = new Dictionary<object, long>();
        for (var r = 0L; r < m.RowCount; r++)
        {
            if (ItemAt(m, items, r) is { } it)
            {
                rowOf.TryAdd(_key(it), r);
            }
        }

        if (activeItem is null || !rowOf.TryGetValue(_key(activeItem), out var activeRow))
        {
            return; // 고른 세션이 사라졌다: 선택 없음(화면 모델도 같은 키를 못 찾아 비운다)
        }

        var rows = picked.Where(rowOf.ContainsKey).Select(k => rowOf[k]).ToList();
        if (rows.Count <= 1)
        {
            m.SelectCell(new GridCell(activeRow, old.Anchor.Col));
            m.ExtendTo(new GridCell(activeRow, old.Active.Col));
        }
        else
        {
            m.SetSelectedRows(rows.Select(r => (r, r)), activeRow);
        }
    }

    private static T? ItemAt(ResultGridModel model, List<T> items, long viewRow)
    {
        var src = model.SourceRowOf(viewRow);
        return src >= 0 && src < items.Count ? items[(int)src] : null;
    }

    private T? ActiveItem(ResultGridModel m) => m.HasSelection ? ItemAt(m, _shown, m.Active.Row) : null;

    private void OnModelChanged(object? sender, EventArgs e)
    {
        if (_building || !ReferenceEquals(sender, _grid.Model))
        {
            return;
        }

        PushSelection();
        _modelChanged?.Invoke();
    }

    // 그리드 현재 행 → 화면 모델 선택.
    private void PushSelection()
    {
        if (_grid.Model is not { } m || _setSelected is null)
        {
            return;
        }

        var item = ActiveItem(m);
        if (ReferenceEquals(item, _lastActive) && ReferenceEquals(item, _getSelected?.Invoke()))
        {
            return;
        }

        _lastActive = item;
        if (ReferenceEquals(_getSelected?.Invoke(), item))
        {
            return;
        }

        _syncing = true;
        try
        {
            _setSelected(item);
        }
        finally
        {
            _syncing = false;
        }
    }

    // 화면 모델 선택 → 그리드 현재 행(목록이 그대로일 때, 또는 새로 고침 뒤 화면 모델이 다른 항목을 골랐을 때).
    private void SelectFromOwner()
    {
        if (_grid.Model is not { } m)
        {
            return;
        }

        var selected = _getSelected?.Invoke();
        if (selected is null)
        {
            if (m.HasSelection)
            {
                m.SetSelectedRows([]);
            }

            _lastActive = null;
            return;
        }

        var key = _key(selected);
        if (ActiveItem(m) is { } cur && Equals(_key(cur), key))
        {
            _lastActive = cur;
            return;
        }

        for (var r = 0L; r < m.RowCount; r++)
        {
            if (ItemAt(m, _shown, r) is { } it && Equals(_key(it), key))
            {
                _syncing = true;
                try
                {
                    m.SelectAndReveal(new GridCell(r, m.HasSelection ? m.Active.Col : 0));
                }
                finally
                {
                    _syncing = false;
                }

                _lastActive = it;
                return;
            }
        }
    }
}
