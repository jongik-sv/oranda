using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Shell;
using Oranda.Core.Grid;
using Oranda.Core.Shell;

namespace Oranda.App.Features.LobRowView;

/// <summary>행 보기의 필드 한 줄: 열 이름·형식·값 글자.</summary>
public sealed record RowField(int Ordinal, string Name, string TypeName, string Text, bool IsNull, bool IsLob);

/// <summary>
/// <c>oranda.panel.rowView</c> — 행 보기(GRID-07, TSK-06-05). 결과 그리드에서 고른 행 하나를 세로 필드 목록(열 이름 | 값)으로 보인다.
/// 그리드의 선택 행을 따라간다(그리드 <c>Changed</c> 이벤트). 값은 그리드 캐시에서 읽기만 한다.
/// </summary>
[OrandaToolPanel("oranda.panel.rowView", ToolPanelKind.Tool, DockLocation.Right, Order = 30)]
public sealed partial class RowViewerViewModel : ToolPanelViewModelBase, IDisposable
{
    private ResultGridModel? _model;
    private long _row = -1;

    public RowViewerViewModel()
    {
        Current = this;
    }

    /// <summary>가장 최근에 만든 행 보기 창(명령 처리기가 그리드를 붙이는 곳).</summary>
    public static RowViewerViewModel? Current { get; private set; }

    /// <summary>필드 목록(거름 적용).</summary>
    public ObservableCollection<RowField> Fields { get; } = [];

    /// <summary>선택한 행이 어느 그리드의 것인지 알려 주는 현재 그리드.</summary>
    public ResultGridModel? Model => _model;

    /// <summary>지금 보는 행 번호(0부터). 없으면 -1.</summary>
    public long Row => _row;

    [ObservableProperty]
    private string _headerText = "행 보기: 결과 그리드에서 행을 고르세요.";

    /// <summary>열 이름·값을 거른다(대소문자 무시).</summary>
    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private RowField? _selectedField;

    /// <summary>그리드를 붙이고 그 그리드의 선택 행을 보인다. 이전 그리드와는 떼어 낸다.</summary>
    public void Attach(ResultGridModel? model)
    {
        if (ReferenceEquals(model, _model))
        {
            Refresh();
            return;
        }

        if (_model is not null)
        {
            _model.Changed -= OnGridChanged;
        }

        _model = model;
        if (_model is not null)
        {
            _model.Changed += OnGridChanged;
        }

        Refresh();
    }

    /// <summary>그리드의 선택 행으로 다시 채운다. 선택이 없으면 첫 행.</summary>
    public void Refresh()
    {
        if (_model is not { HasResult: true } m || m.RowCount == 0)
        {
            ShowRow(-1);
            return;
        }

        ShowRow(m.HasSelection ? m.Active.Row : 0);
    }

    /// <summary>앞(-1)·뒤(+1) 행으로. 그리드 선택도 따라 옮긴다.</summary>
    public void MoveRow(int delta)
    {
        if (_model is not { HasResult: true } m || m.RowCount == 0 || _row < 0)
        {
            return;
        }

        var target = Math.Clamp(_row + delta, 0, m.RowCount - 1);
        if (target == _row)
        {
            return;
        }

        var col = m.HasSelection ? m.Active.Col : 0;
        m.SelectCell(new GridCell(target, col));
    }

    partial void OnFilterTextChanged(string value) => Rebuild();

    private void OnGridChanged(object? sender, EventArgs e)
    {
        if (_model is not { HasResult: true } m || m.RowCount == 0)
        {
            ShowRow(-1);
            return;
        }

        var row = m.HasSelection ? m.Active.Row : 0;
        // 같은 행이어도 값이 늦게 도착할 수 있어(페이지 가져오기) 다시 채운다.
        ShowRow(row);
    }

    private void ShowRow(long row)
    {
        _row = row;
        OnPropertyChanged(nameof(Row));
        Rebuild();
    }

    private void Rebuild()
    {
        Fields.Clear();
        if (_model is not { } m || _row < 0)
        {
            HeaderText = "행 보기: 결과 그리드에서 행을 고르세요.";
            return;
        }

        var loaded = true;
        var all = new List<RowField>(m.Columns.Count);
        foreach (var c in m.Columns)
        {
            if (!m.TryGetValue(_row, c.Ordinal, out var v))
            {
                loaded = false;
                m.Prefetch(_row, _row);
                all.Add(new RowField(c.Ordinal, c.Name, c.DbTypeName, string.Empty, false, false));
                continue;
            }

            var isNull = v is null or DBNull;
            all.Add(new RowField(c.Ordinal, c.Name, c.DbTypeName, CellValueFormatter.Format(c, v, m.Display), isNull, v is LobValue));
        }

        var filter = FilterText.Trim();
        foreach (var f in all)
        {
            if (filter.Length == 0
                || f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || f.Text.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                Fields.Add(f);
            }
        }

        var total = m.TotalRowCount is { } t ? t.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) : "?";
        HeaderText = $"행 {_row + 1:N0} / {total}" + (loaded ? string.Empty : " (불러오는 중…)");
    }

    public void Dispose()
    {
        Attach(null);
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
    }
}
