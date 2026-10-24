using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using static Oranda.App.Features.Tuning.Stats.TuningText;

namespace Oranda.App.Features.Tuning.Stats;

/// <summary>통계 보기 문서. 열·인덱스 표는 <c>DetailTable</c> 로 그리고, 단추는 <see cref="TableStatsViewModel"/> 의 동작을 부른다.</summary>
public partial class TableStatsView : UserControl
{
    /// <summary>인덱스 표에서 CF 막대를 그리는 칸.</summary>
    internal const int QualityColumn = 7;

    /// <summary>인덱스 표의 stale 칸.</summary>
    internal const int StaleColumn = 9;

    private readonly Dictionary<IReadOnlyList<object?>, ColumnStatsItem> _columnRows = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IReadOnlyList<object?>, IndexStatsItem> _indexRows = new(ReferenceEqualityComparer.Instance);
    private TableStatsViewModel? _bound;
    private bool _syncing;

    public TableStatsView()
    {
        InitializeComponent();
        ColumnTable.RightAlignedColumns = [1, 2, 3, 5];
        IndexTable.RightAlignedColumns = [3, 4, 5, 6];
        IndexTable.MonospaceColumns = [0, 1];
        IndexTable.CellEmphasis = (c, row) => c == StaleColumn && _indexRows.TryGetValue(row, out var i) && i.StaleDanger;
        IndexTable.CellWarning = (c, row) => c == 6 && _indexRows.TryGetValue(row, out var i) && i.Quality == ClusteringQuality.Poor;
        IndexTable.CellFactory = (c, row) => c == QualityColumn && _indexRows.TryGetValue(row, out var i) ? QualityCell(i) : null;
        ColumnTable.CurrentRowChanged += (_, row) =>
        {
            if (!_syncing && _bound is { } vm && _columnRows.TryGetValue(row, out var item))
            {
                vm.SelectedColumn = item;
            }
        };
        DataContextChanged += (_, _) => Bind(DataContext as TableStatsViewModel);
        EditorTableButton.Click += (_, _) => _ = _bound?.UseEditorTableAsync();
        RefreshButton.Click += (_, _) => _ = _bound?.LoadAsync();
        ScriptButton.Click += (_, _) => _bound?.OpenGatherScript();
        CopyButton.Click += async (_, _) =>
        {
            if (_bound?.BuildCopyText() is { Length: > 0 } text && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text).ConfigureAwait(true);
            }
        };
        ShareSwitch.SelectionChanged += (_, _) =>
        {
            if (_bound is { } vm)
            {
                vm.ShowShare = ShareSwitch.SelectedIndex == 1;
            }
        };
        TargetBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _bound is { } vm)
            {
                e.Handled = true;
                _ = vm.ApplyTargetTextAsync();
            }
        };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None && _bound is { CanRefresh: true } vm)
            {
                e.Handled = true;
                _ = vm.LoadAsync();
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void Bind(TableStatsViewModel? vm)
    {
        if (ReferenceEquals(_bound, vm))
        {
            return;
        }

        if (_bound is not null)
        {
            _bound.PropertyChanged -= OnViewModelChanged;
        }

        _bound = vm;
        if (vm is null)
        {
            return;
        }

        vm.PropertyChanged += OnViewModelChanged;
        FillColumns();
        FillIndexes();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TableStatsViewModel.Columns):
                FillColumns();
                break;
            case nameof(TableStatsViewModel.Indexes):
                FillIndexes();
                break;
            case nameof(TableStatsViewModel.SelectedColumn):
                SyncSelectedColumn();
                break;
        }
    }

    private void FillColumns()
    {
        _columnRows.Clear();
        var rows = new List<IReadOnlyList<object?>>();
        foreach (var item in _bound?.Columns ?? [])
        {
            var row = item.Cells;
            _columnRows[row] = item;
            rows.Add(row);
        }

        ColumnTable.Columns = TableStatsViewModel.ColumnHeaders;
        ColumnTable.Rows = rows;
        ColumnCount.Text = T($"{rows.Count}열", rows.Count == 1 ? "1 column" : $"{rows.Count} columns");
        SyncSelectedColumn();
    }

    private void FillIndexes()
    {
        _indexRows.Clear();
        var rows = new List<IReadOnlyList<object?>>();
        foreach (var item in _bound?.Indexes ?? [])
        {
            var row = item.Cells;
            _indexRows[row] = item;
            rows.Add(row);
        }

        IndexTable.Columns = TableStatsViewModel.IndexHeaders;
        IndexTable.Rows = rows;
        IndexCount.Text = rows.Count.ToString(CultureInfo.InvariantCulture) + T("개", rows.Count == 1 ? " index" : " indexes");
        NoIndexText.IsVisible = rows.Count == 0;
        IndexTable.IsVisible = rows.Count > 0;
    }

    private void SyncSelectedColumn()
    {
        if (_bound?.SelectedColumn is not { } selected)
        {
            return;
        }

        _syncing = true;
        try
        {
            ColumnTable.SelectRows(r => _columnRows.TryGetValue(r, out var i) && ReferenceEquals(i, selected),
                r => _columnRows.TryGetValue(r, out var i) && ReferenceEquals(i, selected));
        }
        finally
        {
            _syncing = false;
        }
    }

    // CF 판정 칸: 얇은 막대(좋음·보통·나쁨 색) + 판정 글.
    private static StackPanel QualityCell(IndexStatsItem item)
    {
        var bar = new Border { Width = 60 * item.BarFraction };
        bar.Classes.Add("bar");
        bar.Classes.Add(item.Quality switch
        {
            ClusteringQuality.Good => "good",
            ClusteringQuality.Fair => "fair",
            ClusteringQuality.Poor => "poor",
            _ => "unknown",
        });
        var track = new Border();
        track.Classes.Add("bar-track");
        var text = new TextBlock { Text = item.QualityText };
        text.Classes.Add("pct");
        if (item.Quality == ClusteringQuality.Poor)
        {
            text.Classes.Add("warning-text");
        }

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(6, 0),
            Children = { new Panel { Children = { track, bar }, VerticalAlignment = VerticalAlignment.Center }, text },
        };
    }
}
