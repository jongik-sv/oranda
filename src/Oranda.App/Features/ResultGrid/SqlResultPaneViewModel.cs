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
        : _editSession is null ? "편집 모드로 다시 실행"
        : _editSession.Capability.CanAddRowId ? "ROWID 추가 후 다시 실행"
        : null;

    public bool HasEditHintAction => EditHintAction is not null;

    /// <summary>안내 막대에 「편집 모드로 다시 실행」 단추를 보인다(읽기 전용 결과).</summary>
    public bool OffersRerunForEdit => _editHint is not null && _editSession is null;

    /// <summary>안내 막대에 「ROWID 추가 후 다시 실행」 단추를 보인다(키가 없어 고칠 수 없는 결과).</summary>
    public bool OffersRowIdRerun => _editHint is not null && _editSession is { CanEdit: false, Capability.CanAddRowId: true };

    /// <summary>결과 탭을 닫아 달라는 요청(편집기 문서가 받는다).</summary>
    public event EventHandler? CloseRequested;

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

        if (ResultGridCommands.CountRows is not { } count)
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
        _summaryScope = null;
        if (_model is null)
        {
            return;
        }

        _model.Changed -= OnModelChanged;
        _model.FindRequested -= OnFindRequested;
        _model.CloseRequested -= OnCloseRequested;
        _model.CountRowsRequested -= OnCountRowsRequested;
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
            StatusText = "오류: " + err.Message;
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

        if (_countMessage is { } message)
        {
            text += " · " + message;
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
