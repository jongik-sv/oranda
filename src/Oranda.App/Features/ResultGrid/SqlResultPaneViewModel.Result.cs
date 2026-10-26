using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.Core.Execution;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

// SqlResultPaneViewModel 의 일부: 결과 바꾸기·행 수 세기(SqlResultPaneViewModel.cs 에서 나눔, 동작 불변).
public sealed partial class SqlResultPaneViewModel
{
    /// <summary>새 결과를 보인다. 호출한 쪽은 <paramref name="result"/> 의 수명을 이 영역에 넘긴다.</summary>
    public void SetResult(IResultSet result, GridDisplayOptions? display = null) => SetResultCore(result, display, keepLocalFilter: false);

    /// <summary>
    /// 결과를 바꾼다. <paramref name="keepLocalFilter"/> 이면 서버 거르기 줄·서버 정렬로 같은 질의를 다시 조회한 경우라 앱 안 거르기(빠른 거르기 글·열 필터)를 새 결과에 다시 적용한다.
    /// 새 문장의 결과면 앱 안 거르기를 비운다.
    /// </summary>
    private void SetResultCore(IResultSet result, GridDisplayOptions? display, bool keepLocalFilter)
    {
        ArgumentNullException.ThrowIfNull(result);
        LocalFilterSpec? carried = null;
        IReadOnlyList<ResultColumn>? oldColumns = null;
        var oldModel = keepLocalFilter ? _model : null;
        if (keepLocalFilter && _model is { } old && !old.LocalFilter.IsEmpty)
        {
            carried = old.LocalFilter;
            oldColumns = old.Columns;
        }

        _quickCts?.Cancel();
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
        m.QuickFilterRequested += OnQuickFilterRequested;
        m.ClearLocalFiltersRequested += OnClearLocalFiltersRequested;
        m.CellFilterRequested += OnCellFilterRequested;
        m.PinRequested += OnPinRequested;
        m.RelatedRowsRequested += OnRelatedRowsRequested;
        m.EditHintRequested += OnEditHintRequested;
        m.NoticeRequested += OnNoticeRequested;
        m.EditModeStarter = StartEditModeAsync;
        m.ServerSort = ServerSortAsync;
        m.ServerSortBlockedReason = ServerSortBlockedReason;
        if (oldModel is not null)
        {
            m.CarryLayoutFrom(oldModel);
        }

        Model = m;
        SetQuickTextSilently(string.Empty);
        FillQuickFilterColumns(m);
        if (carried is not null && oldColumns is not null)
        {
            var remapped = RemapFilter(carried, oldColumns, m.Columns);
            SetQuickTextSilently(remapped.QuickText);
            FillQuickFilterColumns(m);
            _ = m.SetLocalFilterAsync(remapped);
        }

        UpdateStatus();
    }

    /// <summary>다시 조회한 결과의 열에 거르기를 다시 맞춘다(열 이름으로 찾는다). 없어진 열의 조건은 버린다.</summary>
    internal static LocalFilterSpec RemapFilter(LocalFilterSpec spec, IReadOnlyList<ResultColumn> from, IReadOnlyList<ResultColumn> to)
    {
        int Map(int ordinal) => ordinal >= 0 && ordinal < from.Count
            ? to.ToList().FindIndex(c => string.Equals(c.Name, from[ordinal].Name, StringComparison.OrdinalIgnoreCase))
            : -1;

        var map = new Dictionary<int, ColumnFilter>();
        foreach (var (ordinal, filter) in spec.Columns)
        {
            if (Map(ordinal) is >= 0 and var mapped)
            {
                map[mapped] = filter;
            }
        }

        IReadOnlyList<int>? quick = null;
        if (spec.QuickColumns is { } qc)
        {
            quick = [.. qc.Select(Map).Where(o => o >= 0)];
            if (quick.Count == 0)
            {
                quick = null;
            }
        }

        return spec with { Columns = map, QuickColumns = quick };
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
            null when _activeFilter is not null => EditModeKey.Localize(
                "거른 결과는 편집할 수 없습니다. 값을 고치려면 거르기를 지운 뒤 결과 편집 모드로 다시 실행하세요{0}.",
                "A filtered result cannot be edited. To change values, clear the filter and rerun in result edit mode{0}."),
            null when _hasStatementBinds => EditModeKey.Localize(
                "외래키로 연 결과는 편집할 수 없습니다. 값을 고치려면 그 테이블을 편집기에서 조회해 결과 편집 모드로 실행하세요{0}.",
                "A result opened through a foreign key cannot be edited. To change values, query that table in the editor and run it in result edit mode{0}."),
            null => EditModeKey.Localize(
                "읽기 전용 결과입니다. 값을 고치려면 결과 편집 모드로 다시 실행하세요{0}.",
                "This result is read-only. To change values, rerun it in result edit mode{0}."),
            { CanEdit: false } es => "이 결과는 편집할 수 없습니다: " + (es.Capability.Message ?? es.Capability.Reason.ToString()),
            _ => null,
        };
    }

    public void HideEditHint() => EditHint = null;

    /// <summary>편집 모드가 아닌 이 결과를 편집 모드로 다시 실행할 수 있는지(거른 결과·외래키로 연 결과가 아니고, 다시 실행할 문장과 방법이 있다).</summary>
    public bool CanStartEditMode =>
        _editSession is null && _activeFilter is null && !_hasStatementBinds && Statement is not null
        && (Runner?.RerunForEdit ?? GridEditCommands.Rerun) is not null;

    /// <summary>
    /// 행 삭제·행 복제가 편집 모드가 아닌 결과에서 불렸을 때 편집 모드를 켠다(F9 와 같은 경로: 같은 문장을 편집 모드로 다시 실행, D-031).
    /// 거른 결과·외래키로 연 결과·조회 전용(R6, <c>readOnlySession</c>)이면 켜지 않고 false — 부른 쪽이 안내 막대를 띄운다.
    /// </summary>
    private async Task<bool> StartEditModeAsync(CancellationToken ct)
    {
        var keys = Oranda.App.Shell.ContextKeyService.Shared;
        if (!CanStartEditMode || (keys?.Get("readOnlySession") ?? false) || (keys?.Get("sessionBusy") ?? false)
            || Statement is not { } stmt || (Runner?.RerunForEdit ?? GridEditCommands.Rerun) is not { } rerun)
        {
            return false;
        }

        HideEditHint();
        await rerun(stmt, ct).ConfigureAwait(true);
        return true;
    }

    /// <summary>안내 막대 단추: 같은 문장을 편집 모드로 다시 실행한다(키가 없으면 ROWID 를 더해서).</summary>
    public Task RunEditHintActionAsync()
    {
        var addRowId = _editSession is { CanEdit: false, Capability.CanAddRowId: true };
        HideEditHint();
        if (addRowId)
        {
            return ShellCommandRunner.RunAsync("oranda.grid.edit.addRowidAndRerun");
        }

        return (Runner?.RerunForEdit ?? GridEditCommands.Rerun)?.Invoke(Statement, CancellationToken.None) ?? Task.CompletedTask;
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
        // 외래키로 연 결과는 이 결과 영역이 가진 바인드 값으로 센다(「바인드 변수」 목록에는 그 이름이 없다).
        Func<SqlStatement, CancellationToken, Task<long?>>? count = _activeFilter is { } filter
            ? (Runner?.CountFiltered ?? ResultGridCommands.CountFiltered) is { } countFiltered ? (_, token) => countFiltered(filter with { OrderBy = string.Empty }, token) : null
            : _hasStatementBinds
                ? (Runner?.CountFiltered ?? ResultGridCommands.CountFiltered) is { } countOwn ? (q, token) => countOwn(OwnBindsRequest(q), token) : null
                : RunBinds is not null && (Runner?.CountFiltered ?? ResultGridCommands.CountFiltered) is { } countRan
                    ? (q, token) => countRan(OwnBindsRequest(q), token) // 실행 때 쓴 값이 있으면 목록에 없는 변수도 센다
                    : Runner?.CountRows ?? ResultGridCommands.CountRows;
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
}
