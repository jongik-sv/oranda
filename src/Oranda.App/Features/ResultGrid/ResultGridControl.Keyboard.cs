using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;
using Oranda.App.Shell.Appearance;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

// ResultGridControl 의 일부: 키 입력·정렬(ResultGridControl.cs 에서 나눔, 동작 불변).
public sealed partial class ResultGridControl
{
    /// <summary>
    /// 포커스를 받으면 결과 명령(주 메뉴 「결과」·키맵)의 대상(<see cref="ResultGridCommands.Active"/>)이 되는가(기본 true).
    /// 모니터 표처럼 SQL 문서 밖의 읽기 전용 그리드는 끈다(편집기 결과 명령이 모니터 표를 고치거나 복사하지 않게).
    /// </summary>
    public bool IsCommandTarget { get; set; } = true;

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (IsCommandTarget)
        {
            ResultGridCommands.Active = _model;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_model is null || !_model.HasResult)
        {
            return;
        }

        if (e.Key == Key.Escape && _model.IsSorting)
        {
            _model.CancelSort(); // 정렬하느라 가져오거나 계산하는 중이면 멈춘다(원래 순서 유지)
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _headerPressCol >= 0)
        {
            CancelHeaderPress(); // 끌기를 취소하고, 놓아도 정렬하지 않는다
            e.Handled = true;
            return;
        }

        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var cur = _model.HasSelection ? _model.Active : new GridCell(0, 0);
        var page = Math.Max(1, (int)(ViewportHeight / RowHeight) - 1);
        GridCell? next = e.Key switch
        {
            Key.Up => cur with { Row = cur.Row - 1 },
            Key.Down => cur with { Row = cur.Row + 1 },
            Key.Left => cur with { Col = cur.Col - 1 },
            Key.Right => cur with { Col = cur.Col + 1 },
            Key.PageUp => cur with { Row = cur.Row - page },
            Key.PageDown => cur with { Row = cur.Row + page },
            Key.Home => ctrl ? new GridCell(0, 0) : cur with { Col = 0 },
            Key.End when !e.KeyModifiers.HasFlag(KeyModifiers.Alt) => ctrl ? new GridCell(_model.RowCount - 1, _model.VisibleColumns.Count - 1) : cur with { Col = _model.VisibleColumns.Count - 1 },
            _ => null,
        };

        if (next is { } n)
        {
            if (shift)
            {
                _model.ExtendTo(n);
            }
            else
            {
                _model.SelectCell(n);
            }

            EnsureVisible(_model.Active);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.A)
        {
            _model.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && _model.HasSelection && !_model.IsTextMode)
        {
            EditRequested?.Invoke(this, _model.Active);
            e.Handled = true;
        }
    }

    private async Task SortAsync(int ordinal)
    {
        try
        {
            await _model!.ToggleSortAsync(ordinal).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 취소·원본 오류는 Model.Error 로 보인다.
        }
    }

    /// <summary>색 묶음. 테마 리소스(<c>OrandaGrid*</c>)가 있으면 그것, 없으면 밝은/어두운 기본값.</summary>
}
