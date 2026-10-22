using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia.Threading;
using Oranda.App.Shell.Controls;

namespace Oranda.App.Features.SessionMonitor;

/// <summary>
/// 세션 모니터·SQL 모니터가 목록(<see cref="ObservableCollection{T}"/>)을 C안 격자(<see cref="DetailTable"/>)로 보이게 잇는 어댑터.
/// 칸 값 뒤에 항목 자체를 숨겨 붙인 행으로 표를 채우고, 현재 행(▶)을 화면 모델의 선택 항목과 맞춘다.
/// 목록이 바뀌면(자동 새로 고침) 한 번에 몰아 다시 그리며, 고른 행(키로 찾음)·현재 행·세로 스크롤 위치를 남긴다.
/// </summary>
public sealed class MonitorTableBinder<T> where T : class
{
    private readonly DetailTable _table;
    private readonly Func<T, IReadOnlyList<object?>> _cells;
    private readonly Func<T, object> _key;
    private ObservableCollection<T>? _items;
    private Func<T?>? _getSelected;
    private Action<T?>? _setSelected;
    private bool _pending;
    private bool _syncing;

    public MonitorTableBinder(DetailTable table, IReadOnlyList<string> columns, Func<T, IReadOnlyList<object?>> cells, Func<T, object> key)
    {
        _table = table;
        _cells = cells;
        _key = key;
        table.Columns = columns;
        table.KeepScroll = true;
        table.CurrentRowChanged += (_, row) =>
        {
            if (_pending || _setSelected is null || row.Count == 0 || row[^1] is not T item)
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
        };
    }

    /// <summary>항목 목록과 선택 항목을 잇는다. <paramref name="owner"/> 의 <paramref name="selectedProperty"/> 가 바뀌면 표의 현재 행도 따라간다.</summary>
    public void Attach(ObservableCollection<T> items, INotifyPropertyChanged owner, string selectedProperty, Func<T?> getSelected, Action<T?> setSelected)
    {
        _items = items;
        _getSelected = getSelected;
        _setSelected = setSelected;
        items.CollectionChanged += (_, _) => Schedule();
        owner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == selectedProperty && !_syncing && !_pending)
            {
                SyncSelection();
            }
        };
        Rebuild();
    }

    /// <summary>칸 글이 바뀌었을 때(델타 보기 전환 등) 다시 그린다.</summary>
    public void Refresh() => Schedule();

    private void Schedule()
    {
        if (_pending)
        {
            return;
        }

        _pending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _pending = false;
            Rebuild();
        }, DispatcherPriority.Loaded);
    }

    private void Rebuild()
    {
        if (_items is null || _getSelected is null)
        {
            return;
        }

        var selected = _getSelected();
        var picked = selected is null
            ? []
            : _table.SelectedRows.Where(r => r.Count > 0 && r[^1] is T).Select(r => _key((T)r[^1]!)).ToHashSet();
        var rows = _items.Select(i => (IReadOnlyList<object?>)[.. _cells(i), i]).ToList();
        _table.Rows = rows;
        if (selected is null)
        {
            return;
        }

        var selectedKey = _key(selected);
        picked.Add(selectedKey);
        _table.SelectRows(r => r[^1] is T t && picked.Contains(_key(t)), r => r[^1] is T t && _key(t).Equals(selectedKey));
    }

    // 화면 모델이 선택 항목을 바꿨다(목록이 그대로일 때) — 그 행만 고르고 ▶ 를 옮긴다.
    private void SyncSelection()
    {
        var selected = _getSelected?.Invoke();
        if (selected is null)
        {
            _table.ClearSelection();
            return;
        }

        var key = _key(selected);
        if (_table.CurrentRow is { Count: > 0 } cur && cur[^1] is T t && _key(t).Equals(key))
        {
            return;
        }

        _table.SelectRows(r => r[^1] is T x && _key(x).Equals(key), r => r[^1] is T x && _key(x).Equals(key));
    }
}
