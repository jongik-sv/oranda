using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Oranda.App.Shell.Controls;

/// <summary>
/// 열 이름 + 행으로 된 정보 표(DB 정보·객체 상세의 구역). Orange 의 Property 창처럼 머리글 띠·줄무늬 행·숫자 오른쪽 정렬로 그린다.
/// 칸은 골라서 복사할 수 있다. 행을 누르면 <see cref="RowActivated"/>(첫 두 칸을 이름·값으로 쓰는 화면이 받는다).
/// </summary>
public sealed class DetailTable : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<string>?> ColumnsProperty =
        AvaloniaProperty.Register<DetailTable, IReadOnlyList<string>?>(nameof(Columns));

    public static readonly StyledProperty<IReadOnlyList<IReadOnlyList<object?>>?> RowsProperty =
        AvaloniaProperty.Register<DetailTable, IReadOnlyList<IReadOnlyList<object?>>?>(nameof(Rows));

    /// <summary>한 번에 그릴 최대 행(그 뒤는 「…」 한 줄). 거르기로 좁혀 보게 한다.</summary>
    public const int MaxRows = 2000;

    private readonly ScrollViewer _scroll = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    public DetailTable()
    {
        Content = _scroll;
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ColumnsProperty || change.Property == RowsProperty)
        {
            Build();
        }
    }

    private void Build()
    {
        var columns = Columns ?? [];
        var rows = Rows ?? [];
        if (columns.Count == 0)
        {
            _scroll.Content = null;
            return;
        }

        var shown = Math.Min(rows.Count, MaxRows);
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        for (var c = 0; c < columns.Count; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(c == columns.Count - 1 ? GridLength.Star : GridLength.Auto));
        }

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var header = new Border { Classes = { "grid-header" } };
        Grid.SetColumnSpan(header, columns.Count);
        grid.Children.Add(header);
        for (var c = 0; c < columns.Count; c++)
        {
            var h = new TextBlock { Text = columns[c], Margin = new Thickness(8, 4, 16, 4), FontWeight = FontWeight.SemiBold };
            Grid.SetColumn(h, c);
            grid.Children.Add(h);
        }

        for (var r = 0; r < shown; r++)
        {
            var row = rows[r];
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var gridRow = r + 1;
            var band = new Border { Classes = { r % 2 == 1 ? "detail-row-alt" : "detail-row" } };
            Grid.SetRow(band, gridRow);
            Grid.SetColumnSpan(band, columns.Count);
            band.PointerPressed += (_, _) => RowActivated?.Invoke(this, row);
            grid.Children.Add(band);
            for (var c = 0; c < columns.Count; c++)
            {
                var value = c < row.Count ? row[c] : null;
                var cell = new SelectableTextBlock
                {
                    Text = Format(value),
                    Margin = new Thickness(8, 2, 16, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = IsNumber(value) && c < columns.Count - 1 ? TextAlignment.Right : TextAlignment.Left, // 늘어나는 마지막 열은 왼쪽
                    Opacity = value is null ? 0.45 : 1,
                };
                cell.PointerPressed += (_, _) => RowActivated?.Invoke(this, row);
                Grid.SetRow(cell, gridRow);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        if (rows.Count > shown)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var more = new TextBlock
            {
                Text = $"… {(rows.Count - shown).ToString("N0", CultureInfo.CurrentCulture)}행 더 있음 — 찾기로 좁혀 보세요",
                Margin = new Thickness(8, 4),
                Opacity = 0.6,
            };
            Grid.SetRow(more, shown + 1);
            Grid.SetColumnSpan(more, columns.Count);
            grid.Children.Add(more);
        }

        _scroll.Content = grid;
    }

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
