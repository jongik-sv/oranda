using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.GridEdit;
using Oranda.App.Shell;
using Oranda.Core.Execution;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

/// <summary>
/// SQL 편집기 문서 아래 결과 영역(도구 창 아님, 편집기 문서가 품는다) — TSK-06-03.
/// 결과 1개(<see cref="IResultSet"/>)를 가상 그리드로 보인다. 결과를 바꾸면 이전 것은 닫는다.
/// </summary>
public sealed class SqlResultPaneViewModel : ToolPanelViewModelBase
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
        : _editSession is null ? (_activeFilter is null ? "편집 모드로 다시 실행" : null)
        : _editSession.Capability.CanAddRowId ? "ROWID 추가 후 다시 실행"
        : null;

    public bool HasEditHintAction => EditHintAction is not null;

    /// <summary>안내 막대에 「편집 모드로 다시 실행」 단추를 보인다(읽기 전용 결과, 거른 결과는 빼고).</summary>
    public bool OffersRerunForEdit => _editHint is not null && _editSession is null && _activeFilter is null;

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

    /// <summary>최근에 적용한 조건(새것부터, 최대 <see cref="ResultFilter.HistorySize"/>개). 이 결과 영역 안에서만 남는다.</summary>
    public ObservableCollection<string> FilterHistory { get; } = [];

    public bool HasFilterHistory => FilterHistory.Count > 0;

    /// <summary>결과 탭을 닫아 달라는 요청(편집기 문서가 받는다).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>거르기 줄을 열었으니 WHERE 칸으로 포커스를 옮겨 달라는 요청(셀 값 거르기는 그리드에 포커스를 둔다).</summary>
    public event EventHandler? FilterFocusRequested;

    /// <summary>
    /// 거르기로 결과를 바꿨다. 편집 모드(F9)였으면 편집 상태는 풀렸다 — 결과 탭이 「(편집)」 표시와 편집 상태를 지운다.
    /// </summary>
    public event EventHandler? FilterApplied;

    /// <summary>새 결과를 보인다. 호출한 쪽은 <paramref name="result"/> 의 수명을 이 영역에 넘긴다.</summary>
    public void SetResult(IResultSet result, GridDisplayOptions? display = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        Detach();
        var m = new ResultGridModel(result);
        if (display is not null)
        {
            m.Display = display;
        }

        m.Changed += OnModelChanged;
        m.FindRequested += OnFindRequested;
        m.CloseRequested += OnCloseRequested;
        m.CountRowsRequested += OnCountRowsRequested;
        m.FilterBarRequested += OnFilterBarRequested;
        m.CellFilterRequested += OnCellFilterRequested;
        Model = m;
        UpdateStatus();
    }

    /// <summary>편집 모드(F9)로 다시 연 결과의 편집 상태를 붙인다. 고칠 수 없는 결과면 이유를 안내 막대에 보인다.</summary>
    public void SetEditSession(GridEditSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        DetachEditSession();
        _editSession = session;
        session.Changed += OnEditChanged;
        EditHint = null;
        OnEditChanged(session, EventArgs.Empty);
        if (!session.CanEdit)
        {
            ShowEditHint();
        }
    }

    /// <summary>셀을 고치려 했는데 고칠 수 없을 때 부른다(두 번 누르기·F2·글자 입력).</summary>
    public void ShowEditHint()
    {
        EditHint = _editSession switch
        {
            null when _activeFilter is not null => "거른 결과는 편집할 수 없습니다. 값을 고치려면 거르기를 지운 뒤 결과 편집 모드로 다시 실행하세요(F9).",
            null => "읽기 전용 결과입니다. 값을 고치려면 결과 편집 모드로 다시 실행하세요(F9).",
            { CanEdit: false } es => "이 결과는 편집할 수 없습니다: " + (es.Capability.Message ?? es.Capability.Reason.ToString()),
            _ => null,
        };
    }

    public void HideEditHint() => EditHint = null;

    /// <summary>안내 막대 단추: 같은 문장을 편집 모드로 다시 실행한다(키가 없으면 ROWID 를 더해서).</summary>
    public Task RunEditHintActionAsync()
    {
        var addRowId = _editSession is { CanEdit: false, Capability.CanAddRowId: true };
        HideEditHint();
        if (addRowId)
        {
            return ShellCommandRunner.RunAsync("oranda.grid.edit.addRowidAndRerun");
        }

        return GridEditCommands.Rerun?.Invoke(Statement, CancellationToken.None) ?? Task.CompletedTask;
    }

    public void Clear()
    {
        Detach();
        Model = null;
        StatusText = string.Empty;
        SelectionSummary = null;
    }

    /// <summary>
    /// 「전체 행 수 세기」(<c>oranda.grid.countRows</c>): 이 결과를 만든 질의의 전체 행 수를 세어 상태 줄에 「가져온 N행 / 전체 M행」으로 보인다.
    /// 실패·취소도 상태 줄에 보이고 밖으로 던지지 않는다. 세는 사이 결과가 바뀌면 그 값은 버린다.
    /// </summary>
    public async Task CountAllRowsAsync(CancellationToken ct = default)
    {
        if (_isCountingRows || !CanCountRows || Statement is not { } stmt)
        {
            return;
        }

        // 거른 결과면 거른 질의(셀 값 바인드 포함, 정렬은 빼고)를 센다 — 「전체 M행」이 거르기 전 행 수가 되지 않게.
        Func<SqlStatement, CancellationToken, Task<long?>>? count = _activeFilter is { } filter
            ? ResultGridCommands.CountFiltered is { } countFiltered ? (_, token) => countFiltered(filter with { OrderBy = string.Empty }, token) : null
            : ResultGridCommands.CountRows;
        if (count is null)
        {
            _countMessage = "전체 행 수를 셀 수 없습니다(실행 엔진이 연결되지 않았습니다).";
            UpdateStatus();
            return;
        }

        var generation = ++_countGeneration;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cancelCount = cts.Cancel;
        _totalRows = null;
        _countMessage = "전체 행 수 세는 중…";
        IsCountingRows = true;
        UpdateStatus();

        long? total = null;
        string? message;
        try
        {
            total = await count(stmt, cts.Token).ConfigureAwait(true);
            message = total is null ? "전체 행 수를 읽지 못했습니다." : null;
        }
        catch (OperationCanceledException)
        {
            message = "전체 행 수 세기를 취소했습니다.";
        }
        catch (Exception e)
        {
            message = "전체 행 수를 세지 못했습니다: " + e.Message.ReplaceLineEndings(" ");
        }

        if (generation != _countGeneration)
        {
            return; // 그새 결과가 바뀌었다(Detach 가 상태를 이미 비웠다)
        }

        _cancelCount = null;
        _totalRows = total;
        _countMessage = message;
        IsCountingRows = false;
        UpdateStatus();
    }

    /// <summary>세는 중인 「전체 행 수 세기」를 취소한다(서버 중단은 실행 엔진 몫).</summary>
    public void CancelCountRows() => _cancelCount?.Invoke();

    // ---- 결과 거르기(GRID-10) ---------------------------------------------------------------

    /// <summary>거르기 줄 보이기/숨기기(<c>oranda.grid.toggleFilterBar</c>). 질의 결과가 아니면 열지 않는다.</summary>
    public void ToggleFilterBar()
    {
        if (!_isFilterBarVisible && Statement is not { Kind: StatementKind.Query })
        {
            return;
        }

        IsFilterBarVisible = !_isFilterBarVisible;
        if (_isFilterBarVisible)
        {
            FilterFocusRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 거르기 줄의 조건·정렬로 결과를 만든 질의를 감싸 다시 실행하고, 이 결과 영역의 결과를 바꾼다(줄의 글은 남는다).
    /// 편집 모드였으면 편집 없이 다시 연다(감싼 질의는 키·ROWID 를 알아낼 수 없다). 반영하지 않은 편집이 있으면 거르지 않는다.
    /// 실패·취소는 상태 줄에 보이고 지금 결과를 그대로 둔다. 밖으로 던지지 않는다.
    /// </summary>
    public async Task ApplyFilterAsync(CancellationToken ct = default)
    {
        if (_isFiltering || Statement is not { Kind: StatementKind.Query } query)
        {
            return;
        }

        var request = new ResultFilterRequest(query, WhereText.Trim(), OrderByText.Trim())
        {
            Binds = new Dictionary<string, BindValue>(_filterBinds, StringComparer.OrdinalIgnoreCase),
        };
        if (request.IsEmpty && _activeFilter is null)
        {
            ShowFilterMessage(null); // 거른 적이 없고 조건도 없다: 다시 실행할 까닭이 없다
            return;
        }

        if (IsEditDirty)
        {
            ShowFilterMessage("반영하지 않은 편집이 있습니다. 반영하거나 되돌린 뒤 거르세요.");
            return;
        }

        if (ResultGridCommands.Filter is not { } run)
        {
            ShowFilterMessage("거를 수 없습니다(실행 엔진이 연결되지 않았습니다).");
            return;
        }

        var generation = _resultGeneration;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cancelFilter = cts.Cancel;
        IsFiltering = true;
        ShowFilterMessage(request.IsEmpty ? "거르기를 지우는 중…" : "거르는 중…");

        IResultSet? result = null;
        string? message = null;
        try
        {
            result = await run(request, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            message = "거르기를 취소했습니다.";
        }
        catch (Exception e)
        {
            message = "거르지 못했습니다: " + e.Message.ReplaceLineEndings(" ");
        }

        _cancelFilter = null;
        IsFiltering = false;
        if (generation != _resultGeneration)
        {
            // 그새 결과 영역이 비었거나 다른 결과로 바뀌었다: 늦게 온 결과는 버린다.
            if (result is not null)
            {
                await result.DisposeAsync().ConfigureAwait(true);
            }

            return;
        }

        if (result is null)
        {
            ShowFilterMessage(message);
            return;
        }

        if (_editSession is { } editing && ReferenceEquals(GridEditCommands.Active, editing))
        {
            GridEditCommands.Active = null; // 편집 명령이 버린 편집 상태를 가리키지 않게
        }

        SetResult(result, _model?.Display);
        _activeFilter = request.IsEmpty ? null : request;
        _filterMessage = null;
        Remember(request.Where);
        OnPropertyChanged(nameof(IsFiltered));
        UpdateStatus();
        FilterApplied?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 거르기 지우기: 조건·정렬 칸을 비우고, 거른 결과였으면 원래 질의를 그대로 다시 실행한다(편집 모드는 F9 로 다시).
    /// 셀 값 바인드는 남긴다(최근 조건 목록의 조건이 그 이름을 쓴다).
    /// </summary>
    public Task ClearFilterAsync(CancellationToken ct = default)
    {
        WhereText = string.Empty;
        OrderByText = string.Empty;
        return ApplyFilterAsync(ct);
    }

    /// <summary>거르는 중인 질의를 취소한다(서버 중단은 실행 엔진 몫).</summary>
    public void CancelFilter() => _cancelFilter?.Invoke();

    /// <summary>
    /// 셀 값으로 거르기(「이 값으로 거르기」·「이 값 빼고 거르기」·「NULL 인 행만」): 활성 칸의 열과 값으로 조건을 만들어
    /// WHERE 칸 뒤에 <c>AND</c> 로 잇고 바로 거른다. 값은 바인드(<c>:oranda_fN</c>)로만 넘기고 본문에 이어 붙이지 않는다.
    /// </summary>
    public Task FilterByCellAsync(CellFilterKind kind, CancellationToken ct = default)
    {
        if (!CanApplyFilter || Statement is not { } query || _model is not { HasResult: true, HasSelection: true } m)
        {
            return Task.CompletedTask;
        }

        if (IsEditDirty)
        {
            ShowFilterMessage("반영하지 않은 편집이 있습니다. 반영하거나 되돌린 뒤 거르세요.");
            return Task.CompletedTask;
        }

        var cell = m.Active;
        if (cell.Col < 0 || cell.Col >= m.VisibleColumns.Count)
        {
            return Task.CompletedTask;
        }

        var ordinal = m.VisibleColumns[cell.Col];
        if (!m.TryGetValue(cell.Row, ordinal, out var value))
        {
            ShowFilterMessage("칸 값을 아직 가져오지 않았습니다. 잠시 뒤 다시 거르세요.");
            return Task.CompletedTask;
        }

        var name = ResultFilter.NextBindName(query.BindNames.Concat(_filterBinds.Keys), ref _filterBindCounter);
        if (!ResultFilter.TryCellCondition(m.Columns[ordinal], value, kind, name, out var condition, out var bind, out var error))
        {
            ShowFilterMessage(error);
            return Task.CompletedTask;
        }

        if (bind is not null)
        {
            _filterBinds[name] = bind;
        }

        WhereText = ResultFilter.Combine(WhereText, condition);
        IsFilterBarVisible = true;
        return ApplyFilterAsync(ct);
    }

    private void ShowFilterMessage(string? message)
    {
        _filterMessage = message;
        UpdateStatus();
    }

    /// <summary>적용한 조건을 최근 목록 맨 앞에 둔다(같은 글은 하나만, 최대 <see cref="ResultFilter.HistorySize"/>개).</summary>
    private void Remember(string condition)
    {
        if (condition.Length == 0)
        {
            return;
        }

        var at = FilterHistory.IndexOf(condition);
        if (at == 0)
        {
            return;
        }

        if (at > 0)
        {
            FilterHistory.RemoveAt(at);
        }

        FilterHistory.Insert(0, condition);
        while (FilterHistory.Count > ResultFilter.HistorySize)
        {
            FilterHistory.RemoveAt(FilterHistory.Count - 1);
        }

        OnPropertyChanged(nameof(HasFilterHistory));
    }

    /// <summary>상태 줄의 선택 집계 글을 클립보드에 쓴다(그리드의 복사와 같은 창구).</summary>
    public Task CopySelectionSummaryAsync() =>
        _selectionSummary is { } text && _model?.ClipboardWriter is { } write ? write(text) : Task.CompletedTask;

    public async Task FindNextAsync()
    {
        if (_model is null)
        {
            return;
        }

        FindMessage = await _model.FindNextAsync(FindText).ConfigureAwait(true) ? string.Empty : "찾지 못했습니다.";
    }

    private void OnEditChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsEditMode));
        OnPropertyChanged(nameof(IsEditDirty));
    }

    private void DetachEditSession()
    {
        if (_editSession is not null)
        {
            _editSession.Changed -= OnEditChanged;
            _editSession = null;
            OnEditChanged(this, EventArgs.Empty);
        }

        EditHint = null;
    }

    private void Detach()
    {
        DetachEditSession();
        ResetCount();
        ResetFilter();
        _summaryScope = null;
        if (_model is null)
        {
            return;
        }

        _model.Changed -= OnModelChanged;
        _model.FindRequested -= OnFindRequested;
        _model.CloseRequested -= OnCloseRequested;
        _model.CountRowsRequested -= OnCountRowsRequested;
        _model.FilterBarRequested -= OnFilterBarRequested;
        _model.CellFilterRequested -= OnCellFilterRequested;
        if (ReferenceEquals(ResultGridCommands.Active, _model))
        {
            ResultGridCommands.Active = null;
        }

        _ = _model.DisposeAsync().AsTask();
        _model = null;
    }

    private void OnModelChanged(object? sender, EventArgs e) => UpdateStatus();

    private void OnFindRequested(object? sender, EventArgs e) => IsFindVisible = true;

    private void OnCloseRequested(object? sender, EventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnCountRowsRequested(object? sender, EventArgs e) => _ = CountAllRowsAsync();

    private void OnFilterBarRequested(object? sender, EventArgs e) => ToggleFilterBar();

    private void OnCellFilterRequested(object? sender, CellFilterKind kind) => _ = FilterByCellAsync(kind);

    /// <summary>
    /// 결과를 바꾸거나 닫을 때: 거르는 중이면 취소하고(늦게 온 결과는 버린다) 「거른 결과」 표시를 지운다.
    /// 거르기 줄의 글·최근 조건·셀 값 바인드는 남긴다(같은 결과 영역에서 이어 쓴다).
    /// </summary>
    private void ResetFilter()
    {
        _resultGeneration++;
        _cancelFilter?.Invoke();
        _cancelFilter = null;
        if (_activeFilter is not null)
        {
            _activeFilter = null;
            OnPropertyChanged(nameof(IsFiltered));
        }
    }

    /// <summary>결과를 바꾸거나 닫을 때: 세는 중이면 취소하고 센 값·안내를 비운다.</summary>
    private void ResetCount()
    {
        _countGeneration++;
        _cancelCount?.Invoke();
        _cancelCount = null;
        _totalRows = null;
        _countMessage = null;
        IsCountingRows = false;
    }

    private void UpdateStatus()
    {
        if (_model is null)
        {
            StatusText = string.Empty;
            SelectionSummary = null;
            return;
        }

        if (_model.Error is { } err)
        {
            StatusText = "오류: " + err.Message + (_filterMessage is { } failed ? " · " + failed : string.Empty);
            SelectionSummary = null;
            OnPropertyChanged(nameof(CanCountRows));
            return;
        }

        var culture = CultureInfo.CurrentCulture;
        var loaded = _model.LoadedRowCount.ToString("N0", culture);
        var text = _totalRows is { } total
            ? $"가져온 {loaded}행 / 전체 {total.ToString("N0", culture)}행"
            : _model.HasMoreRows ? $"{loaded} 행 (더 있음)" : $"{loaded} 행";
        if (_model.EditOverlay?.ExtraRows is > 0 and var added)
        {
            text += $" · 새 행 {added}"; // 편집 모드에서 아직 반영하지 않은 새 행
        }

        if (_activeFilter is not null)
        {
            text += " · 거른 결과";
        }

        if (_countMessage is { } message)
        {
            text += " · " + message;
        }

        if (_filterMessage is { } filterMessage)
        {
            text += " · " + filterMessage;
        }

        StatusText = text;
        UpdateSelectionSummary(_model);
    }

    /// <summary>
    /// 선택 집계를 다시 센다. 칸 범위·열·정렬이 그대로면 건너뛴다(페이지가 올 때마다 Changed 가 오므로).
    /// 편집 모드에서는 값이 바뀌어도 범위가 같을 수 있어 매번 센다.
    /// </summary>
    private void UpdateSelectionSummary(ResultGridModel m)
    {
        if (SelectionAggregate.ScopeOf(m) is not { } scope)
        {
            _summaryScope = null;
            SelectionSummary = null;
            return;
        }

        if (m.EditOverlay is null && _summaryScope == scope)
        {
            return;
        }

        _summaryScope = scope;
        SelectionSummary = SelectionAggregate.Compute(m)?.ToText(CultureInfo.CurrentCulture);
    }
}
