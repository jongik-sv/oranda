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
    private string _statusText = string.Empty;
    private bool _isFindVisible;
    private string _findText = string.Empty;
    private string _findMessage = string.Empty;
    private GridEditSession? _editSession;
    private string? _editHint;

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
        private set => SetProperty(ref _model, value);
    }

    /// <summary>이 결과를 만든 문장(「편집 모드로 다시 실행」이 같은 문장을 다시 실행한다). 모르면 null(편집기 커서 문장).</summary>
    public SqlStatement? Statement { get; set; }

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
    }

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
        if (_model is null)
        {
            return;
        }

        _model.Changed -= OnModelChanged;
        _model.FindRequested -= OnFindRequested;
        _model.CloseRequested -= OnCloseRequested;
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

    private void UpdateStatus()
    {
        if (_model is null)
        {
            StatusText = string.Empty;
            return;
        }

        if (_model.Error is { } err)
        {
            StatusText = "오류: " + err.Message;
            return;
        }

        var loaded = _model.LoadedRowCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        StatusText = _model.HasMoreRows ? $"{loaded} 행 (더 있음)" : $"{loaded} 행";
        if (_model.EditOverlay?.ExtraRows is > 0 and var added)
        {
            StatusText += $" · 새 행 {added}"; // 편집 모드에서 아직 반영하지 않은 새 행
        }
    }
}
