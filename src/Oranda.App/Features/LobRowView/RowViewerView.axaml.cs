using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Oranda.App.Features.Localization;
using Oranda.App.Features.SessionMonitor;
using Oranda.Core.Grid;

namespace Oranda.App.Features.LobRowView;

/// <summary>
/// 행 보기 창. 필드 목록은 조회 결과와 같은 그리드(<see cref="MonitorGrid"/>, TSK-14-16)로 보인다: 번호·열 이름·타입·값 네 열.
/// 결과 그리드의 현재 행이 바뀌어 다시 채워도 정렬·열 순서·너비·열 필터·고른 칸(열 키)·스크롤이 남는다(<see cref="MonitorGridBinder{T}"/>).
/// 두 번 누르기·F2·Enter: 고칠 수 있는 칸이면 아래 입력칸으로 값 고치기, 아니면(LOB·읽기 전용) 값 보기 문서를 연다.
/// </summary>
public partial class RowViewerView : UserControl
{
    /// <summary>값 열 번호(<see cref="Columns"/> 안). 편집 모드에서 바꾼 값은 이 칸을 강조색으로 그린다.</summary>
    internal const int ValueColumn = 3;

    private readonly MonitorGridBinder<RowField> _grid;
    private RowViewerViewModel? _bound;

    public RowViewerView()
    {
        InitializeComponent();
        FieldGrid.ExtraMenuItems = ExtraMenuItems;
        _grid = new MonitorGridBinder<RowField>(FieldGrid, Columns(), f => (f.Ordinal, f.Name))
        {
            CellEmphasis = (f, c) => c == ValueColumn && f.IsEdited,
            // NULL 은 조회 결과와 같은 글자(설정의 NULL 표시)로, 흐린 색으로 그린다.
            DisplayOptions = () => MonitorGridBinder<RowField>.Display with { NullText = _bound?.Model?.Display.NullText ?? GridDisplayOptions.Default.NullText },
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is RowViewerViewModel vm && !ReferenceEquals(_bound, vm))
            {
                _bound = vm;
                _grid.Attach(vm.Fields, vm, nameof(RowViewerViewModel.SelectedField), () => vm.SelectedField, f => vm.SelectedField = f);
            }
        };
        FirstButton.Click += (_, _) => Vm?.MoveFirst();
        PrevButton.Click += (_, _) => Vm?.MoveRow(-1);
        NextButton.Click += (_, _) => Vm?.MoveRow(1);
        LastButton.Click += (_, _) => Vm?.MoveLast();
        FieldGrid.RowDoubleTapped += (_, _) => Activate();
        FieldGrid.AddHandler(KeyDownEvent, OnGridKeyDown, RoutingStrategies.Tunnel);

        // 값 입력칸: Enter 반영, Esc 취소, 포커스를 잃으면 반영.
        EditBox.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        EditBox.LostFocus += (_, _) => Vm?.CommitEdit(Vm.SelectedField);
        EditPanel.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && EditPanel.IsVisible)
            {
                EditBox.Focus();
                EditBox.SelectAll();
            }
        };
    }

    /// <summary>행 보기 그리드의 열: 번호(숫자) · 열 이름 · 타입 · 값(NULL 이면 null 로 넘겨 그리드가 NULL 표시로 그린다).</summary>
    internal static MonitorColumn<RowField>[] Columns()
    {
        var en = LiteralTranslator.IsEnglish;
        return
        [
            new("#", ColumnDataKind.Number, f => f.Number),
            new(en ? "Column" : "열 이름", ColumnDataKind.Text, f => f.Name),
            new(en ? "Type" : "타입", ColumnDataKind.Text, f => f.TypeName),
            new(en ? "Value" : "값", ColumnDataKind.Text, f => f.IsNull ? null : f.Text),
        ];
    }

    /// <summary>안의 그리드(시험용).</summary>
    internal MonitorGrid Grid => FieldGrid;

    private RowViewerViewModel? Vm => DataContext as RowViewerViewModel;

    // 두 번 누르기·F2·Enter: 고칠 수 있으면 값 고치기, 아니면 값 보기.
    private void Activate()
    {
        if (Vm is not { SelectedField: { } f } vm)
        {
            return;
        }

        if (f.CanEdit)
        {
            RowViewerViewModel.BeginEdit(f);
        }
        else
        {
            _ = vm.OpenValueAsync(f);
        }
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        // 열 필터 창의 입력칸 등에서 누른 Enter 는 그대로 둔다.
        if (e.Source is not TextBox && e.KeyModifiers == KeyModifiers.None && e.Key is Key.F2 or Key.Enter && Vm?.SelectedField is not null)
        {
            e.Handled = true;
            Activate();
        }
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { SelectedField: { } f } vm)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            vm.CommitEdit(f);
            FieldGrid.GridControl.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            RowViewerViewModel.CancelEdit(f);
            FieldGrid.GridControl.Focus();
        }
    }

    // 그리드 복사 묶음·열 항목 아래: 값 보기 · 값 복사 · 이름 = 값 전체 복사(예전 행 보기 메뉴).
    private IReadOnlyList<object> ExtraMenuItems()
    {
        var en = LiteralTranslator.IsEnglish;
        var picked = Vm?.SelectedField is not null;
        var view = new MenuItem { Header = en ? "View value" : "값 보기", IsEnabled = picked };
        view.Click += (_, _) => _ = Vm?.OpenValueAsync(Vm.SelectedField);
        var copyValue = new MenuItem { Header = en ? "Copy value" : "값 복사", IsEnabled = picked };
        copyValue.Click += async (_, _) => await CopyAsync(Vm?.SelectedValueText());
        var copyAll = new MenuItem { Header = en ? "Copy all as name = value" : "이름 = 값 전체 복사" };
        copyAll.Click += async (_, _) => await CopyAsync(Vm?.AllFieldsText());
        return [view, copyValue, copyAll];
    }

    private async Task CopyAsync(string? text)
    {
        if (text is not null && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
