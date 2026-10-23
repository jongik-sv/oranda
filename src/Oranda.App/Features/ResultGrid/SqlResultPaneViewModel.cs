using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.Core.Execution;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

/// <summary>
/// SQL 편집기 문서 아래 결과 영역(도구 창 아님, 편집기 문서가 품는다) — TSK-06-03.
/// 결과 1개(<see cref="IResultSet"/>)를 가상 그리드로 보인다. 결과를 바꾸면 이전 것은 닫는다.
/// </summary>
public sealed partial class SqlResultPaneViewModel : ToolPanelViewModelBase
{
    private ResultGridModel? _model;
    private SqlStatement? _statement;
    private string _statusText = string.Empty;
    private bool _isFindVisible;
    private string _findText = string.Empty;
    private string _findMessage = string.Empty;
    private GridEditSession? _editSession;
    private string? _editHint;
    private string? _selectionSummary;
    private SelectionScope? _summaryScope;
    private long? _totalRows;
    private string? _countMessage;
    private bool _isCountingRows;
    private int _countGeneration;
    private Action? _cancelCount;
    private bool _isFilterBarVisible;
    private string _whereText = string.Empty;
    private string _orderByText = string.Empty;
    private bool _isFiltering;
    private ResultFilterRequest? _activeFilter;
    private string? _filterMessage;
    private int _resultGeneration;
    private int _filterBindCounter;
    private Action? _cancelFilter;
    private readonly Dictionary<string, BindValue> _filterBinds = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasStatementBinds;
    private bool _isOpeningRelated;
    private string? _relatedMessage;
    private string? _gridNotice;
    private bool _isQuickFilterVisible;
    private string _quickFilterText = string.Empty;
    private QuickFilterColumn? _quickFilterColumn;
    private CancellationTokenSource? _quickCts;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsFindVisible
    {
        get => _isFindVisible;
        set => SetProperty(ref _isFindVisible, value);
    }

    public string FindText
    {
        get => _findText;
        set => SetProperty(ref _findText, value);
    }

    public string FindMessage
    {
        get => _findMessage;
        private set => SetProperty(ref _findMessage, value);
    }

    public ResultGridModel? Model
    {
        get => _model;
        private set
        {
            if (SetProperty(ref _model, value))
            {
                OnPropertyChanged(nameof(CanCountRows));
            }
        }
    }

    /// <summary>
    /// 여러 칸을 고르면 개수·합계·평균·최소·최대(가져온 행만, <see cref="SelectionAggregate"/>). 한 칸 이하면 null.
    /// 상태 줄 오른쪽에 보이고, 누르면 이 글을 복사한다.
    /// </summary>
    public string? SelectionSummary
    {
        get => _selectionSummary;
        private set
        {
            if (SetProperty(ref _selectionSummary, value))
            {
                OnPropertyChanged(nameof(HasSelectionSummary));
            }
        }
    }

    public bool HasSelectionSummary => _selectionSummary is not null;

    /// <summary>「전체 행 수 세기」를 할 수 있다: 질의 문장이 만든 결과이고 오류가 없다(PL/SQL 의 REF CURSOR 결과는 아니다).</summary>
    public bool CanCountRows => Statement is { Kind: StatementKind.Query } && _model is { HasResult: true, Error: null };

    /// <summary>전체 행 수를 세는 중(상태 줄에 취소 단추를 보인다).</summary>
    public bool IsCountingRows
    {
        get => _isCountingRows;
        private set => SetProperty(ref _isCountingRows, value);
    }

    /// <summary>머리글 정렬하느라 가져오거나 계산하는 중(상태 줄에 진행과 취소 단추를 보인다, TSK-13-17).</summary>
    public bool IsSorting => _model?.IsSorting ?? false;

    /// <summary>정렬 진행 글: 「정렬하려고 N행 가져오는 중…」·「N행 정렬 중…」. 정렬 중이 아니면 빈 글.</summary>
    public string SortStatusText
    {
        get
        {
            if (_model is not { IsSorting: true } m)
            {
                return string.Empty;
            }

            var rows = m.LoadedRowCount.ToString("N0", CultureInfo.CurrentCulture);
            return m.SortingPhase switch
            {
                SortPhase.Sorting => UiText.Get("LargeSort.status.sorting", "{0}행 정렬 중…").Replace("{0}", rows, StringComparison.Ordinal),
                SortPhase.Querying => UiText.Get("LargeSort.status.querying", "서버에서 정렬해 다시 조회하는 중…"),
                _ => UiText.Get("LargeSort.status.fetching", "정렬하려고 {0}행 가져오는 중…").Replace("{0}", rows, StringComparison.Ordinal),
            };
        }
    }

    /// <summary>정렬을 멈춘다(상태 줄 취소 단추). 원래 순서가 그대로다.</summary>
    public void CancelSort() => _model?.CancelSort();

    /// <summary>「전체 행 수 세기」로 센 전체 행 수. 세지 않았으면 null.</summary>
    public long? CountedRowCount => _totalRows;

    /// <summary>이 결과를 만든 문장(「편집 모드로 다시 실행」이 같은 문장을 다시 실행한다). 모르면 null(편집기 커서 문장).</summary>
    public SqlStatement? Statement
    {
        get => _statement;
        set
        {
            if (SetProperty(ref _statement, value))
            {
                OnPropertyChanged(nameof(CanCountRows));
                OnPropertyChanged(nameof(CanApplyFilter));
            }
        }
    }

    /// <summary>
    /// 편집기 밖에서 이 결과 영역을 쓰는 쪽(객체 상세 「데이터」 탭)의 실행 연결. null 이면 공용 연결(편집기의 활성 세션)을 쓴다.
    /// </summary>
    public ResultPaneRunner? Runner { get; init; }

    /// <summary>편집 모드(F9)로 열려 고칠 수 있는 결과. 결과 위에 편집 툴바를 보인다.</summary>
    public bool IsEditMode => _editSession is { CanEdit: true };

    /// <summary>반영하지 않은 바꾼 값·새 행·지울 행이 있다(반영·되돌리기 단추가 켜진다).</summary>
    public bool IsEditDirty => _editSession is { IsDirty: true };

    /// <summary>
    /// 편집할 수 없는 결과를 고치려 할 때 보이는 안내(오렌지처럼 조용히 무시하지 않는다). 없으면 null.
    /// 읽기 전용 결과면 편집 모드로 다시 실행하라고, 편집 모드인데 고칠 수 없으면 그 이유를 말한다.
    /// </summary>
    public string? EditHint
    {
        get => _editHint;
        private set
        {
            if (SetProperty(ref _editHint, value))
            {
                OnPropertyChanged(nameof(HasEditHint));
                OnPropertyChanged(nameof(EditHintAction));
                OnPropertyChanged(nameof(HasEditHintAction));
                OnPropertyChanged(nameof(OffersRerunForEdit));
                OnPropertyChanged(nameof(OffersRowIdRerun));
            }
        }
    }

    public bool HasEditHint => _editHint is not null;

    /// <summary>안내 막대 단추 글: 읽기 전용이면 「편집 모드로 다시 실행」, 키가 없어 못 고치면 「ROWID 추가 후 다시 실행」.</summary>
    public string? EditHintAction => _editHint is null ? null
        : _editSession is null ? (_activeFilter is null && !_hasStatementBinds ? "편집 모드로 다시 실행" : null)
        : _editSession.Capability.CanAddRowId ? "ROWID 추가 후 다시 실행"
        : null;

    public bool HasEditHintAction => EditHintAction is not null;

    /// <summary>안내 막대에 「편집 모드로 다시 실행」 단추를 보인다(읽기 전용 결과, 거른 결과·외래키로 연 결과는 빼고).</summary>
    public bool OffersRerunForEdit => _editHint is not null && _editSession is null && _activeFilter is null && !_hasStatementBinds;

    /// <summary>안내 막대에 「ROWID 추가 후 다시 실행」 단추를 보인다(키가 없어 고칠 수 없는 결과).</summary>
    public bool OffersRowIdRerun => _editHint is not null && _editSession is { CanEdit: false, Capability.CanAddRowId: true };

    /// <summary>결과 거르기 줄(GRID-10, <c>oranda.grid.toggleFilterBar</c>)을 보인다. 숨겨도 걸어 둔 거르기는 그대로다.</summary>
    public bool IsFilterBarVisible
    {
        get => _isFilterBarVisible;
        set => SetProperty(ref _isFilterBarVisible, value);
    }

    /// <summary>거르기 줄의 WHERE 칸(조건 글). 결과를 바꿔도 남는다.</summary>
    public string WhereText
    {
        get => _whereText;
        set => SetProperty(ref _whereText, value ?? string.Empty);
    }

    /// <summary>거르기 줄의 ORDER BY 칸(정렬 글). 결과를 바꿔도 남는다.</summary>
    public string OrderByText
    {
        get => _orderByText;
        set => SetProperty(ref _orderByText, value ?? string.Empty);
    }

    /// <summary>거른 질의를 실행하는 중(거르기 줄에 취소 단추를 보인다).</summary>
    public bool IsFiltering
    {
        get => _isFiltering;
        private set
        {
            if (SetProperty(ref _isFiltering, value))
            {
                OnPropertyChanged(nameof(CanApplyFilter));
            }
        }
    }

    /// <summary>
    /// 거를 수 있다: 질의 문장이 만든 결과이고(PL/SQL 의 REF CURSOR 결과는 감쌀 질의가 없다) 거르는 중이 아니다.
    /// 거른 결과가 가져오다 오류가 나도 조건을 고쳐 다시 거를 수 있게 결과의 오류는 따지지 않는다.
    /// </summary>
    public bool CanApplyFilter => Statement is { Kind: StatementKind.Query } && !_isFiltering;

    /// <summary>지금 결과가 거른 결과다(편집 모드를 쓸 수 없다: 감싼 질의는 키·ROWID 를 알아낼 수 없다).</summary>
    public bool IsFiltered => _activeFilter is not null;

    /// <summary>
    /// 결과를 만든 문장의 바인드 값을 이 결과 영역이 가진다(외래키 따라가기로 연 결과, GRID-11). 거르기·전체 행 수 세기가 이 값을 넘기고,
    /// 「바인드 변수」 목록에서 읽지 않는다. 편집 모드로 다시 실행할 수는 없다(실행 화면은 목록에서 바인드를 읽는다).
    /// </summary>
    public bool HasStatementBinds => _hasStatementBinds;

    /// <summary>외래키 따라가기로 부모·자식 행을 여는 중.</summary>
    public bool IsOpeningRelatedRows => _isOpeningRelated;

    /// <summary>최근에 적용한 조건(새것부터, 최대 <see cref="ResultFilter.HistorySize"/>개). 이 결과 영역 안에서만 남는다.</summary>
    public ObservableCollection<string> FilterHistory { get; } = [];

    public bool HasFilterHistory => FilterHistory.Count > 0;

    /// <summary>결과 탭을 닫아 달라는 요청(편집기 문서가 받는다).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>이 결과의 탭을 고정하거나 풀어 달라는 요청(<c>oranda.grid.pinResultTab</c>, 결과 탭 묶음이 받는다).</summary>
    public event EventHandler? PinRequested;

    /// <summary>거르기 줄을 열었으니 WHERE 칸으로 포커스를 옮겨 달라는 요청(셀 값 거르기는 그리드에 포커스를 둔다).</summary>
    public event EventHandler? FilterFocusRequested;

    /// <summary>
    /// 거르기로 결과를 바꿨다. 편집 모드(F9)였으면 편집 상태는 풀렸다 — 결과 탭이 「(편집)」 표시와 편집 상태를 지운다.
    /// </summary>
    public event EventHandler? FilterApplied;

    /// <summary>
    /// 외래키 따라가기(GRID-11)로 부모·자식 행 결과를 받았다. 결과 탭 묶음이 받아 새 결과 탭에 연다(결과 집합의 수명도 넘긴다).
    /// 받는 곳이 없으면 이 결과 영역은 따라가기를 하지 않는다.
    /// </summary>
    public event EventHandler<RelatedRowsResult>? RelatedRowsOpened;
}

/// <summary>빠른 거르기 칸 옆 열 고르기의 항목. <paramref name="Ordinal"/> 이 -1 이면 「모든 열」.</summary>
public sealed record QuickFilterColumn(int Ordinal, string Name)
{
    public override string ToString() => Name;
}
