using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.Grid;
using Oranda.Core.Shell;

namespace Oranda.App.Features.LobRowView;

/// <summary>행 보기의 필드 한 줄: 번호·열 이름·형식·값 글자. 편집 모드 결과면 값 칸을 고칠 수 있다.</summary>
public sealed partial class RowField(int ordinal, string name, string typeName, string text, bool isNull, bool isLob) : ObservableObject
{
    public int Ordinal { get; } = ordinal;

    /// <summary>이 표에서의 순서(0부터). 그리드에서 열을 끌어 옮겼으면 그 화면 순서다(<see cref="Ordinal"/> 은 원래 열 번호로 편집·값 읽기에 쓴다).</summary>
    public int Position { get; init; } = ordinal;

    /// <summary>화면에 보이는 번호(1부터).</summary>
    public int Number => Position + 1;

    /// <summary>줄무늬용: 홀수 번째 열.</summary>
    public bool IsAlt => Position % 2 == 1;

    public string Name { get; } = name;

    public string TypeName { get; } = typeName;

    /// <summary>값 표시 글자(NULL 이면 그리드 NULL 표시 설정의 글자).</summary>
    public string Text { get; } = text;

    public bool IsNull { get; } = isNull;

    public bool IsLob { get; } = isLob;

    /// <summary>편집 모드에서 바꾼 값이면 true(그리드의 편집 표시와 같다).</summary>
    public bool IsEdited { get; init; }

    /// <summary>이 값 칸을 고칠 수 있으면 true.</summary>
    public bool CanEdit { get; init; }

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editText = string.Empty;
}

/// <summary>
/// <c>oranda.panel.rowView</c> — 행 보기(GRID-07, TSK-06-05). 결과 그리드에서 고른 행 하나를 세로 필드 목록(열 이름 | 값)으로 보인다.
/// 그리드의 선택 행을 따라간다(그리드 <c>Changed</c> 이벤트). 값은 그리드 캐시에서 읽기만 한다.
/// 화면은 조회 결과와 같은 그리드(번호·열 이름·타입·값 세로 표, TSK-14-16)이고, 값 고치기는 표 아래 입력칸, 긴 값·LOB 는 값 보기 문서로 연다.
/// </summary>
[OrandaToolPanel("oranda.panel.rowView", ToolPanelKind.Tool, DockLocation.Right, Order = 30)]
public sealed partial class RowViewerViewModel : ToolPanelViewModelBase, IDisposable
{
    private ResultGridModel? _model;
    private long _row = -1;

    /// <summary>앱이 도구 창을 만들 때 쓰는 생성자: 값 보기(LOB 편집기) 문서를 여는 처리기를 잇는다.</summary>
    public RowViewerViewModel(ServiceContainer container)
        : this() => ValueHandler = new LobRowViewCommandHandler(container);

    public RowViewerViewModel()
    {
        Current = this;

        // 창은 처음 보일 때 만들어진다. 그 전에 명령이 고른 그리드가 있으면 여기서 붙인다.
        if (s_pending is not null)
        {
            Attach(s_pending);
        }
    }

    private static ResultGridModel? s_pending;

    /// <summary>값 보기 문서를 여는 처리기(앱은 생성자가, 시험은 직접 넣는다). 없으면 값 보기는 아무것도 하지 않는다.</summary>
    internal LobRowViewCommandHandler? ValueHandler { get; set; }

    /// <summary>가장 최근에 만든 행 보기 창(명령 처리기가 그리드를 붙이는 곳).</summary>
    public static RowViewerViewModel? Current { get; private set; }

    /// <summary>행 보기 창에 그리드를 붙인다. 창이 아직 없으면 만들어질 때 붙는다.</summary>
    public static void AttachActive(ResultGridModel? model)
    {
        s_pending = model;
        Current?.Attach(model);
    }

    /// <summary>필드 목록(거름 적용).</summary>
    public ObservableCollection<RowField> Fields { get; } = [];

    /// <summary>선택한 행이 어느 그리드의 것인지 알려 주는 현재 그리드.</summary>
    public ResultGridModel? Model => _model;

    /// <summary>지금 보는 행 번호(0부터). 없으면 -1.</summary>
    public long Row => _row;

    /// <summary>처음 행이 아니면 true(처음·이전 단추).</summary>
    public bool CanGoBack => _row > 0;

    /// <summary>마지막으로 가져온 행이 아니면 true(다음·마지막 단추).</summary>
    public bool CanGoForward => _model is { } m && _row >= 0 && _row < m.RowCount - 1;

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
    public void MoveRow(int delta) => MoveTo(_row + delta);

    /// <summary>처음 행으로.</summary>
    public void MoveFirst() => MoveTo(0);

    /// <summary>이미 가져온 마지막 행으로(남은 행을 가져오지 않는다).</summary>
    public void MoveLast() => MoveTo(long.MaxValue);

    private void MoveTo(long row)
    {
        if (_model is not { HasResult: true } m || m.RowCount == 0 || _row < 0)
        {
            return;
        }

        var target = Math.Clamp(row, 0, m.RowCount - 1);
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
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        Rebuild();
    }

    private void Rebuild()
    {
        var selected = SelectedField;
        Fields.Clear();
        if (_model is not { } m || _row < 0)
        {
            HeaderText = "행 보기: 결과 그리드에서 행을 고르세요.";
            return;
        }

        var loaded = true;
        var all = new List<RowField>(m.Columns.Count);
        var position = 0;
        foreach (var ordinal in m.ColumnsInDisplayOrder()) // 그리드에서 옮긴 열 순서를 따른다(숨긴 열은 뒤에)
        {
            var c = m.Columns[ordinal];
            var pos = position++;
            if (!m.TryGetValue(_row, c.Ordinal, out var v))
            {
                loaded = false;
                m.Prefetch(_row, _row);
                all.Add(new RowField(c.Ordinal, c.Name, c.DbTypeName, string.Empty, false, false) { Position = pos });
                continue;
            }

            var isNull = v is null or DBNull;
            all.Add(new RowField(c.Ordinal, c.Name, c.DbTypeName, CellValueFormatter.Format(c, v, m.Display), isNull, v is LobValue)
            {
                Position = pos,
                IsEdited = m.IsEdited(_row, c.Ordinal),
                CanEdit = v is not LobValue && GridEditBinding.CanEditCell(m, _row, c.Ordinal),
            });
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

        // 다시 채워도 같은 열(원래 열 번호·이름)을 계속 고른다: 행을 옮겨도 표의 현재 칸이 그 열에 머문다.
        if (selected is not null)
        {
            SelectedField = Fields.FirstOrDefault(f => f.Ordinal == selected.Ordinal && f.Name == selected.Name);
        }

        // 전체 수를 모르면 지금까지 가져온 수 뒤에 「+」.
        var total = m.RowCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + (m.TotalRowCount is null ? "+" : string.Empty);
        HeaderText = $"{_row + 1:N0} / {total}" + (loaded ? string.Empty : " (불러오는 중…)");
    }

    /// <summary>선택한 칸의 값(NULL 이면 빈 글자). 선택이 없으면 null.</summary>
    public string? SelectedValueText() => SelectedField is { } f ? (f.IsNull ? string.Empty : f.Text) : null;

    /// <summary>보이는 필드 전체를 줄마다 「이름 = 값」으로.</summary>
    public string AllFieldsText() => string.Join(Environment.NewLine, Fields.Select(f => $"{f.Name} = {f.Text}"));

    /// <summary>값 칸 편집을 연다. 고칠 수 없는 칸이면 무시한다.</summary>
    public static void BeginEdit(RowField? field)
    {
        if (field is not { CanEdit: true })
        {
            return;
        }

        field.EditText = field.IsNull ? string.Empty : field.Text;
        field.IsEditing = true;
    }

    /// <summary>입력한 값을 편집 세션에 반영한다. 그리드의 편집 표시도 따라 바뀐다(그리드 <c>Changed</c>).</summary>
    public void CommitEdit(RowField? field)
    {
        if (field is not { IsEditing: true })
        {
            return;
        }

        field.IsEditing = false;
        if (_model is { } m && _row >= 0)
        {
            GridEditBinding.TrySetCell(m, _row, field.Ordinal, field.EditText);
        }
    }

    /// <summary>필드 값을 값 보기(LOB 편집기) 문서로 연다: LOB 는 내용 전체, 긴 글자는 읽기 전용 글로. 처리기가 없으면 false.</summary>
    public async Task<bool> OpenValueAsync(RowField? field, CancellationToken ct = default)
    {
        if (field is null || _model is not { } m || _row < 0 || ValueHandler is not { } handler)
        {
            return false;
        }

        await handler.ViewValueAsync(m, _row, field.Ordinal, ct).ConfigureAwait(true);
        return true;
    }

    public static void CancelEdit(RowField? field)
    {
        if (field is not null)
        {
            field.IsEditing = false;
        }
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
